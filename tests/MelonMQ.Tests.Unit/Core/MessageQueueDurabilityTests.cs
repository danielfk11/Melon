using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MelonMQ.Broker.Core;
using System.Text;

namespace MelonMQ.Tests.Unit.Core;

public class MessageQueueDurabilityTests : IDisposable
{
    private readonly string _tempDataDirectory;

    public MessageQueueDurabilityTests()
    {
        _tempDataDirectory = Path.Combine(Path.GetTempPath(), "melonmq-unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDirectory);
    }

    [Fact]
    public async Task AckTombstone_ShouldPreventReplay_AfterRestart()
    {
        var config = new QueueConfiguration
        {
            Name = "durable-ack",
            Durable = true
        };

        using (var queue = CreateQueue(config))
        {
            await queue.EnqueueAsync(CreateMessage("payload"));
            var delivery = await queue.DequeueAsync("conn-1");
            delivery.Should().NotBeNull();

            var acked = await queue.AckAsync(delivery!.Value.DeliveryTag);
            acked.Should().BeTrue();
        }

        using (var reloaded = CreateQueue(config))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAsync<OperationCanceledException>(() => reloaded.DequeueAsync("conn-2", cts.Token));
        }
    }

    [Fact]
    public async Task AckTombstone_ShouldStayConsistent_WhenBatchFlushIsDelayed()
    {
        var config = new QueueConfiguration
        {
            Name = "durable-ack-delayed",
            Durable = true
        };

        using (var queue = CreateQueue(config, batchFlushMs: 250))
        {
            await queue.EnqueueAsync(CreateMessage("payload"));
            var delivery = await queue.DequeueAsync("conn-1");
            delivery.Should().NotBeNull();

            var acked = await queue.AckAsync(delivery!.Value.DeliveryTag);
            acked.Should().BeTrue();
        }

        using (var reloaded = CreateQueue(config, batchFlushMs: 250))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAsync<OperationCanceledException>(() => reloaded.DequeueAsync("conn-2", cts.Token));
        }
    }

    [Fact]
    public async Task DurablePurge_ShouldPreventReplay_AfterRestart()
    {
        var config = new QueueConfiguration
        {
            Name = "durable-purge",
            Durable = true
        };

        using (var queue = CreateQueue(config))
        {
            await queue.EnqueueAsync(CreateMessage("payload"));
            await queue.PurgeAsync();
        }

        using (var reloaded = CreateQueue(config))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAsync<OperationCanceledException>(() => reloaded.DequeueAsync("conn-2", cts.Token));
        }
    }

    [Fact]
    public async Task DurablePurge_ShouldStayConsistent_WhenBatchFlushIsDelayed()
    {
        var config = new QueueConfiguration
        {
            Name = "durable-purge-delayed",
            Durable = true
        };

        using (var queue = CreateQueue(config, batchFlushMs: 250))
        {
            await queue.EnqueueAsync(CreateMessage("payload"));
            await queue.PurgeAsync();
        }

        using (var reloaded = CreateQueue(config, batchFlushMs: 250))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAsync<OperationCanceledException>(() => reloaded.DequeueAsync("conn-2", cts.Token));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataDirectory))
        {
            Directory.Delete(_tempDataDirectory, recursive: true);
        }
    }

    private MessageQueue CreateQueue(QueueConfiguration config, int batchFlushMs = 1)
    {
        return new MessageQueue(
            config,
            _tempDataDirectory,
            new NullLogger<MessageQueue>(),
            queueResolver: null,
            channelCapacity: 100,
            compactionThresholdMB: 1,
            batchFlushMs: batchFlushMs);
    }

    private static QueueMessage CreateMessage(string payload)
    {
        return new QueueMessage
        {
            MessageId = Guid.NewGuid(),
            Body = Encoding.UTF8.GetBytes(payload),
            EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Persistent = true
        };
    }
}
