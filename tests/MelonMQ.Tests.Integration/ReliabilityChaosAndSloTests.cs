using FluentAssertions;
using MelonMQ.Client;
using System.Collections.Concurrent;
using System.Text;

namespace MelonMQ.Tests.Integration;

public class ReliabilityChaosAndSloTests
{
    [Fact]
    public async Task DurableQueue_ShouldRecoverMessages_AfterBrokerRestart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-chaos-tests", Guid.NewGuid().ToString("N"));
        const string queueName = "chaos-durable-recover";
        const int totalMessages = 120;

        await using (var host = new TestBrokerHost(dataDir))
        {
            await host.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();

            await channel.DeclareQueueAsync(queueName, durable: true);
            for (var i = 0; i < totalMessages; i++)
            {
                await channel.PublishAsync(
                    queueName,
                    Encoding.UTF8.GetBytes($"durable-{i}"),
                    persistent: true,
                    messageId: Guid.NewGuid());
            }
        }

        await using (var restarted = new TestBrokerHost(dataDir))
        {
            await restarted.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{restarted.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            var consumed = new ConcurrentDictionary<string, byte>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await foreach (var message in channel.ConsumeAsync(queueName, prefetch: 20, cancellationToken: cts.Token))
            {
                var body = Encoding.UTF8.GetString(message.Body.Span);
                consumed.TryAdd(body, 0);
                await channel.AckAsync(message.DeliveryTag);

                if (consumed.Count >= totalMessages)
                {
                    break;
                }
            }

            consumed.Count.Should().Be(totalMessages);
        }
    }

    [Fact]
    public async Task QueueRoundTrip_ShouldStayWithinLocalSloBudget()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-slo-tests", Guid.NewGuid().ToString("N"));
        const string queueName = "slo-roundtrip";
        const int totalMessages = 300;
        const double p95BudgetMs = 2500;

        await using var host = new TestBrokerHost(dataDir);
        await host.StartAsync();
        await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
        await using var channel = await conn.CreateChannelAsync();
        await channel.DeclareQueueAsync(queueName, durable: false);

        var publishTimes = new ConcurrentDictionary<Guid, long>();
        var latencies = new ConcurrentBag<long>();
        var consumed = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var consumer = Task.Run(async () =>
        {
            await foreach (var msg in channel.ConsumeAsync(queueName, prefetch: 50, cancellationToken: cts.Token))
            {
                if (publishTimes.TryGetValue(msg.MessageId, out var startedAt))
                {
                    var latencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt;
                    latencies.Add(latencyMs);
                }

                await channel.AckAsync(msg.DeliveryTag);

                if (Interlocked.Increment(ref consumed) >= totalMessages)
                {
                    cts.Cancel();
                    break;
                }
            }
        }, cts.Token);

        for (var i = 0; i < totalMessages; i++)
        {
            var id = Guid.NewGuid();
            publishTimes[id] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await channel.PublishAsync(queueName, Encoding.UTF8.GetBytes($"slo-{i}"), messageId: id);
        }

        try
        {
            await consumer;
        }
        catch (OperationCanceledException)
        {
            // Expected when target is reached.
        }

        latencies.Count.Should().Be(totalMessages);
        var sorted = latencies.OrderBy(x => x).ToArray();
        var p95Index = (int)Math.Ceiling(sorted.Length * 0.95) - 1;
        var p95 = sorted[Math.Max(0, p95Index)];

        p95.Should().BeLessThanOrEqualTo((long)p95BudgetMs);
    }
}
