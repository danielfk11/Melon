using FluentAssertions;
using MelonMQ.Client;
using System.Text;

namespace MelonMQ.Tests.Integration;

public class RollingUpgradeCompatibilityTests
{
    [Fact]
    [Trait("Category", "RollingUpgrade")]
    public async Task RollingUpgradeNToNPlus1_WithRollback_ShouldPreserveDurableData()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-rolling-upgrade-tests", Guid.NewGuid().ToString("N"));
        var queueName = $"rolling-upgrade-{Guid.NewGuid():N}";
        const int nPhaseTotal = 160;
        const int ackedBeforeUpgrade = 50;
        const int nPlus1PhaseTotal = 160;
        const int rollbackPhaseTotal = 80;

        var pendingExpected = new HashSet<string>(StringComparer.Ordinal);

        await using (var hostN = new TestBrokerHost(dataDir, cfg =>
        {
            cfg.BatchFlushMs = 10;
            cfg.CompactionThresholdMB = 100;
        }))
        {
            await hostN.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{hostN.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            foreach (var payload in BuildPayloads("n", nPhaseTotal))
            {
                pendingExpected.Add(payload);
                await channel.PublishAsync(queueName, Encoding.UTF8.GetBytes(payload), persistent: true, messageId: Guid.NewGuid());
            }

            var consumedBeforeUpgrade = await ConsumeExactAsync(channel, queueName, ackedBeforeUpgrade, TimeSpan.FromSeconds(20));
            consumedBeforeUpgrade.Should().OnlyHaveUniqueItems();
            foreach (var consumed in consumedBeforeUpgrade)
            {
                pendingExpected.Remove(consumed);
            }
        }

        await using (var hostNPlus1 = new TestBrokerHost(dataDir, cfg =>
        {
            cfg.BatchFlushMs = 1;
            cfg.CompactionThresholdMB = 32;
        }))
        {
            await hostNPlus1.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{hostNPlus1.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            foreach (var payload in BuildPayloads("nplus1", nPlus1PhaseTotal))
            {
                pendingExpected.Add(payload);
                await channel.PublishAsync(queueName, Encoding.UTF8.GetBytes(payload), persistent: true, messageId: Guid.NewGuid());
            }

            var drainedAfterUpgrade = await ConsumeExactAsync(channel, queueName, pendingExpected.Count, TimeSpan.FromSeconds(30));
            drainedAfterUpgrade.Should().BeEquivalentTo(pendingExpected);

            var replayAfterDrain = await TryConsumeSingleAsync(channel, queueName, TimeSpan.FromMilliseconds(800));
            replayAfterDrain.Should().BeNull("all persisted data must be drained exactly once after N -> N+1");
        }

        await using (var hostRollback = new TestBrokerHost(dataDir, cfg =>
        {
            cfg.BatchFlushMs = 10;
            cfg.CompactionThresholdMB = 100;
        }))
        {
            await hostRollback.StartAsync();
            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{hostRollback.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            var rollbackPayloads = BuildPayloads("rollback", rollbackPhaseTotal).ToArray();
            foreach (var payload in rollbackPayloads)
            {
                await channel.PublishAsync(queueName, Encoding.UTF8.GetBytes(payload), persistent: true, messageId: Guid.NewGuid());
            }

            var drainedAfterRollback = await ConsumeExactAsync(channel, queueName, rollbackPayloads.Length, TimeSpan.FromSeconds(20));
            drainedAfterRollback.Should().BeEquivalentTo(rollbackPayloads);

            var replayAfterRollback = await TryConsumeSingleAsync(channel, queueName, TimeSpan.FromMilliseconds(800));
            replayAfterRollback.Should().BeNull("rollback must not resurrect already ACKed durable messages");
        }
    }

    private static IEnumerable<string> BuildPayloads(string phase, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return $"{phase}-msg-{i:D5}";
        }
    }

    private static async Task<List<string>> ConsumeExactAsync(MelonChannel channel, string queueName, int expected, TimeSpan timeout)
    {
        var consumed = new List<string>(expected);
        using var cts = new CancellationTokenSource(timeout);

        await foreach (var msg in channel.ConsumeAsync(queueName, prefetch: 50, cancellationToken: cts.Token))
        {
            consumed.Add(Encoding.UTF8.GetString(msg.Body.Span));
            await channel.AckAsync(msg.DeliveryTag);

            if (consumed.Count >= expected)
            {
                break;
            }
        }

        consumed.Count.Should().Be(expected, $"expected to consume exactly {expected} messages from '{queueName}'");
        return consumed;
    }

    private static async Task<string?> TryConsumeSingleAsync(MelonChannel channel, string queueName, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var msg in channel.ConsumeAsync(queueName, prefetch: 1, cancellationToken: cts.Token))
            {
                await channel.AckAsync(msg.DeliveryTag);
                return Encoding.UTF8.GetString(msg.Body.Span);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return null;
    }
}
