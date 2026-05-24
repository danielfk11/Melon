using FluentAssertions;
using MelonMQ.Client;
using System.Text;
using Xunit;

namespace MelonMQ.Tests.Integration;

public class ReliabilitySoakProfilesTests
{
    [SkippableFact]
    [Trait("Category", "Soak")]
    public async Task SoakDurableQueueAndStream_ShouldRunConfiguredProfile_WithoutMessageLoss()
    {
        var profile = (Environment.GetEnvironmentVariable("MELONMQ_SOAK_PROFILE") ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        var duration = profile switch
        {
            "24h" => TimeSpan.FromHours(24),
            "72h" => TimeSpan.FromHours(72),
            _ => TimeSpan.Zero
        };

        var overrideMinutesRaw = Environment.GetEnvironmentVariable("MELONMQ_SOAK_DURATION_MINUTES");
        if (int.TryParse(overrideMinutesRaw, out var overrideMinutes) && overrideMinutes > 0)
        {
            duration = TimeSpan.FromMinutes(overrideMinutes);
        }

        Skip.If(duration == TimeSpan.Zero, "Set MELONMQ_SOAK_PROFILE=24h|72h (or MELONMQ_SOAK_DURATION_MINUTES) to run soak profile.");

        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-soak-tests", Guid.NewGuid().ToString("N"));
        var queueName = $"soak.queue.{Guid.NewGuid():N}";
        var streamName = $"soak.stream.{Guid.NewGuid():N}";
        const int batchSize = 120;

        await using var host = new TestBrokerHost(dataDir, cfg =>
        {
            cfg.BatchFlushMs = 25;
            cfg.CompactionThresholdMB = 64;
        });
        await host.StartAsync();

        await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
        await using var channel = await conn.CreateChannelAsync();
        await channel.DeclareQueueAsync(queueName, durable: true);
        await channel.DeclareStreamQueueAsync(streamName, durable: true, maxMessages: 2_000_000);

        var deadline = DateTimeOffset.UtcNow + duration;
        var streamOffset = 0L;
        var cycle = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cycle++;
            var expectedBatch = Enumerable.Range(0, batchSize)
                .Select(i => $"soak-{cycle:D6}-{i:D4}")
                .ToArray();

            foreach (var payload in expectedBatch)
            {
                var body = Encoding.UTF8.GetBytes(payload);
                await channel.PublishAsync(queueName, body, persistent: true, messageId: Guid.NewGuid());
                await channel.PublishAsync(streamName, body, persistent: true, messageId: Guid.NewGuid());
            }

            var queueBatch = await ConsumeQueueBatchAsync(channel, queueName, batchSize, TimeSpan.FromSeconds(45));
            queueBatch.Should().BeEquivalentTo(expectedBatch);

            await using var streamConn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var streamChannel = await streamConn.CreateChannelAsync();
            var streamBatch = await ConsumeStreamBatchAsync(streamChannel, streamName, streamOffset, batchSize, TimeSpan.FromSeconds(45));
            streamBatch.Should().BeEquivalentTo(expectedBatch);
            streamOffset += batchSize;
        }
    }

    private static async Task<List<string>> ConsumeQueueBatchAsync(MelonChannel channel, string queue, int count, TimeSpan timeout)
    {
        var consumed = new List<string>(count);
        using var cts = new CancellationTokenSource(timeout);

        await foreach (var msg in channel.ConsumeAsync(queue, prefetch: 50, cancellationToken: cts.Token))
        {
            consumed.Add(Encoding.UTF8.GetString(msg.Body.Span));
            await channel.AckAsync(msg.DeliveryTag);
            if (consumed.Count >= count)
            {
                break;
            }
        }

        consumed.Count.Should().Be(count);
        return consumed;
    }

    private static async Task<List<string>> ConsumeStreamBatchAsync(
        MelonChannel channel,
        string queue,
        long startOffset,
        int count,
        TimeSpan timeout)
    {
        var consumed = new List<string>(count);
        using var cts = new CancellationTokenSource(timeout);

        await foreach (var msg in channel.ConsumeStreamAsync(queue, startOffset, cancellationToken: cts.Token))
        {
            consumed.Add(Encoding.UTF8.GetString(msg.Body.Span));
            if (consumed.Count >= count)
            {
                break;
            }
        }

        consumed.Count.Should().Be(count);
        return consumed;
    }
}
