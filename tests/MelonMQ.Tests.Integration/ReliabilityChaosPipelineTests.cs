using FluentAssertions;
using MelonMQ.Client;
using System.Text;

namespace MelonMQ.Tests.Integration;

public class ReliabilityChaosPipelineTests
{
    [Fact]
    [Trait("Category", "Chaos")]
    public async Task ChaosKill9_ShouldRecoverDurableMessages_AfterHardCrash()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-chaos-kill9", Guid.NewGuid().ToString("N"));
        var queueName = $"chaos-kill9-{Guid.NewGuid():N}";
        const int totalMessages = 220;
        var expected = Enumerable.Range(0, totalMessages).Select(i => $"kill9-{i:D4}").ToArray();

        await using (var host = new ExternalBrokerProcessHost(dataDir, batchFlushMs: 10))
        {
            await host.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            foreach (var payload in expected)
            {
                await channel.PublishAsync(
                    queueName,
                    Encoding.UTF8.GetBytes(payload),
                    persistent: true,
                    messageId: Guid.NewGuid());
            }

            await host.KillHardAsync();
        }

        await using (var restarted = new ExternalBrokerProcessHost(dataDir, batchFlushMs: 10))
        {
            await restarted.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{restarted.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            var consumed = await ConsumeExactAsync(channel, queueName, totalMessages, TimeSpan.FromSeconds(30));
            consumed.Should().BeEquivalentTo(expected);
        }
    }

    [Fact]
    [Trait("Category", "Chaos")]
    public async Task ChaosSlowDisk_ShouldPreserveDurableQueue_AfterRestart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-chaos-slowdisk", Guid.NewGuid().ToString("N"));
        var queueName = $"chaos-slowdisk-{Guid.NewGuid():N}";
        const int totalMessages = 180;
        var expected = Enumerable.Range(0, totalMessages).Select(i => $"slowdisk-{i:D4}").ToArray();

        await using (var host = new TestBrokerHost(dataDir, cfg =>
        {
            cfg.BatchFlushMs = 750;
            cfg.CompactionThresholdMB = 8;
        }))
        {
            await host.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            foreach (var payload in expected)
            {
                await channel.PublishAsync(
                    queueName,
                    Encoding.UTF8.GetBytes(payload),
                    persistent: true,
                    messageId: Guid.NewGuid());
            }
        }

        await using (var restarted = new TestBrokerHost(dataDir, cfg =>
        {
            cfg.BatchFlushMs = 750;
            cfg.CompactionThresholdMB = 8;
        }))
        {
            await restarted.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{restarted.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            var consumed = await ConsumeExactAsync(channel, queueName, totalMessages, TimeSpan.FromSeconds(35));
            consumed.Should().BeEquivalentTo(expected);
        }
    }

    private static async Task<List<string>> ConsumeExactAsync(MelonChannel channel, string queueName, int expected, TimeSpan timeout)
    {
        var consumed = new List<string>(expected);
        using var cts = new CancellationTokenSource(timeout);

        await foreach (var message in channel.ConsumeAsync(queueName, prefetch: 40, cancellationToken: cts.Token))
        {
            consumed.Add(Encoding.UTF8.GetString(message.Body.Span));
            await channel.AckAsync(message.DeliveryTag);
            if (consumed.Count >= expected)
            {
                break;
            }
        }

        consumed.Count.Should().Be(expected);
        return consumed;
    }
}
