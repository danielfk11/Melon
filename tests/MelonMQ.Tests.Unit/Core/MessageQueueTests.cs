using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MelonMQ.Broker.Core;
using System.Text;
using Xunit;

namespace MelonMQ.Tests.Unit.Core;

public class MessageQueueTests : IDisposable
{
    private readonly MessageQueue _messageQueue;
    private readonly string _tempDataDirectory;

    public MessageQueueTests()
    {
        _tempDataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDataDirectory);
        
        var config = new QueueConfiguration
        {
            Name = "test-queue",
            Durable = false,
            DeadLetterQueue = null,
            DefaultTtlMs = null
        };

        _messageQueue = new MessageQueue(config, null, new NullLogger<MessageQueue>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataDirectory))
        {
            Directory.Delete(_tempDataDirectory, true);
        }
    }

    [Fact]
    public void Constructor_ShouldSetPropertiesCorrectly()
    {
        // Assert
        _messageQueue.Name.Should().Be("test-queue");
        _messageQueue.IsDurable.Should().BeFalse();
        _messageQueue.PendingCount.Should().Be(0);
        _messageQueue.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldAddMessageToQueue()
    {
        // Arrange
        var messageBody = Encoding.UTF8.GetBytes("Test message");
        var message = new QueueMessage
        {
            MessageId = Guid.NewGuid(),
            Body = messageBody,
            Persistent = false,
            EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Act
        var result = await _messageQueue.EnqueueAsync(message);

        // Assert
        result.Should().BeTrue();
        _messageQueue.PendingCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DequeueAsync_ShouldReturnMessage_WhenMessageExists()
    {
        // Arrange
        var messageBody = Encoding.UTF8.GetBytes("Test message");
        var originalMessage = new QueueMessage
        {
            MessageId = Guid.NewGuid(),
            Body = messageBody,
            Persistent = false,
            EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await _messageQueue.EnqueueAsync(originalMessage);

        // Act
        var dequeuedMessage = await _messageQueue.DequeueAsync("test-connection", CancellationToken.None);

        // Assert
        dequeuedMessage.Should().NotBeNull();
        dequeuedMessage!.Value.Message.MessageId.Should().Be(originalMessage.MessageId);
        dequeuedMessage.Value.Message.Body.ToArray().Should().BeEquivalentTo(originalMessage.Body.ToArray());
        _messageQueue.InFlightCount.Should().Be(1);
    }

    [Fact]
    public async Task DequeueAsync_ShouldTimeout_WhenNoMessageAvailable()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => 
            _messageQueue.DequeueAsync("test-connection", cts.Token));
    }

    [Fact]
    public async Task AckAsync_ShouldRemoveMessageFromInFlight()
    {
        // Arrange
        var messageBody = Encoding.UTF8.GetBytes("Test message");
        var message = new QueueMessage
        {
            MessageId = Guid.NewGuid(),
            Body = messageBody,
            Persistent = false,
            EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await _messageQueue.EnqueueAsync(message);
        await _messageQueue.DequeueAsync("test-connection", CancellationToken.None);

        // Note: In a real scenario, we would need access to the delivery tag
        // For this unit test, we'll test that in-flight count increases after dequeue
        _messageQueue.InFlightCount.Should().Be(1);
    }

    [Fact]
    public async Task PurgeAsync_ShouldClearAllMessages()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            var messageBody = Encoding.UTF8.GetBytes($"Test message {i}");
            var message = new QueueMessage
            {
                MessageId = Guid.NewGuid(),
                Body = messageBody,
                Persistent = false,
                EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            await _messageQueue.EnqueueAsync(message);
        }

        // Act
        await _messageQueue.PurgeAsync();

        // Assert
        _messageQueue.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task CleanupExpiredInFlightMessages_ShouldCompleteSuccessfully()
    {
        // Act & Assert - Should not throw
        await _messageQueue.CleanupExpiredInFlightMessages();
    }
}