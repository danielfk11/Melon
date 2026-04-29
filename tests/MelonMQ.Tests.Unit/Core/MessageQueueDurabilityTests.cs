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
    public async Task DurableEnqueue_WithPublisherConfirm_ShouldFlushBeforeReturning()
    {
        var config = new QueueConfiguration
        {
            Name = "durable-confirm",
            Durable = true
        };

        var message = CreateMessage("payload");
        var logPath = Path.Combine(_tempDataDirectory, $"{config.Name}.log");

        using var queue = CreateQueue(config, batchFlushMs: 250);

        await queue.EnqueueAsync(message, waitForPersistenceFlush: true);

        File.Exists(logPath).Should().BeTrue();
        var persisted = await File.ReadAllTextAsync(logPath);
        persisted.Should().Contain(message.MessageId.ToString(), "publisher confirm should return only after the durable enqueue is flushed to disk");
    }

    [Fact]
    public async Task ExactlyOnceQueue_ShouldRejectDuplicateMessageId_AfterAckAndRestart()
    {
        var config = new QueueConfiguration
        {
            Name = "durable-exactly-once",
            Durable = true,
            ExactlyOnce = true
        };

        var messageId = Guid.NewGuid();

        using (var queue = CreateQueue(config))
        {
            var firstAccepted = await queue.EnqueueAsync(CreateMessage("payload", messageId), waitForPersistenceFlush: true);
            firstAccepted.Should().BeTrue();

            var delivery = await queue.DequeueAsync("conn-1", CancellationToken.None);
            delivery.Should().NotBeNull();

            var acked = await queue.AckAsync(delivery!.Value.DeliveryTag);
            acked.Should().BeTrue();
        }

        using (var reloaded = CreateQueue(config))
        {
            var duplicateAccepted = await reloaded.EnqueueAsync(CreateMessage("payload-duplicate", messageId), waitForPersistenceFlush: true);
            duplicateAccepted.Should().BeFalse("exactly-once queue should keep the messageId tombstone across restart");
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

    private static QueueMessage CreateMessage(string payload, Guid? messageId = null)
    {
        return new QueueMessage
        {
            MessageId = messageId ?? Guid.NewGuid(),
            Body = Encoding.UTF8.GetBytes(payload),
            EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Persistent = true
        };
    }
}
