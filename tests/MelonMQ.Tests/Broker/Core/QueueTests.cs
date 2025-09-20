using FluentAssertions;
using MelonMQ.Broker.Core;
using MelonMQ.Common;
using Xunit;

namespace MelonMQ.Tests.Broker.Core;

public class QueueTests
{
    [Fact]
    public void Constructor_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        var queue = new Queue("test-queue", durable: true, exclusive: false, autoDelete: false);

        // Assert
        queue.Name.Should().Be("test-queue");
        queue.Durable.Should().BeTrue();
        queue.Exclusive.Should().BeFalse();
        queue.AutoDelete.Should().BeFalse();
        queue.MessageCount.Should().Be(0);
        queue.ConsumerCount.Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_ShouldAddMessage()
    {
        // Arrange
        var queue = new Queue("test-queue");
        var message = CreateTestMessage();

        // Act
        await queue.EnqueueAsync(message);

        // Assert
        queue.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task EnqueueAsync_WithPriority_ShouldOrderCorrectly()
    {
        // Arrange
        var queue = new Queue("test-queue");
        var lowPriorityMessage = CreateTestMessage(priority: 1);
        var highPriorityMessage = CreateTestMessage(priority: 9);
        var mediumPriorityMessage = CreateTestMessage(priority: 5);

        // Act
        await queue.EnqueueAsync(lowPriorityMessage);
        await queue.EnqueueAsync(highPriorityMessage);
        await queue.EnqueueAsync(mediumPriorityMessage);

        // Assert
        queue.MessageCount.Should().Be(3);
        
        // Should dequeue in priority order
        var consumer = CreateTestConsumer();
        await queue.AddConsumerAsync(consumer);
        
        var messages = new List<QueuedMessage>();
        for (int i = 0; i < 3; i++)
        {
            var result = await queue.TryDequeueAsync(consumer.ConsumerTag, 100);
            result.Should().NotBeNull();
            messages.Add(result!);
        }
        
        messages[0].Priority.Should().Be(9); // Highest priority first
        messages[1].Priority.Should().Be(5);
        messages[2].Priority.Should().Be(1);
    }

    [Fact]
    public async Task TryDequeueAsync_WithNoMessages_ShouldReturnNull()
    {
        // Arrange
        var queue = new Queue("test-queue");
        var consumer = CreateTestConsumer();
        await queue.AddConsumerAsync(consumer);

        // Act
        var result = await queue.TryDequeueAsync(consumer.ConsumerTag, 100);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task TryDequeueAsync_WithPrefetch_ShouldRespectLimit()
    {
        // Arrange
        var queue = new Queue("test-queue");
        var consumer = CreateTestConsumer(prefetch: 2);
        await queue.AddConsumerAsync(consumer);
        
        // Add 5 messages
        for (int i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync(CreateTestMessage());
        }

        // Act - try to dequeue more than prefetch limit
        var messages = new List<QueuedMessage?>();
        for (int i = 0; i < 5; i++)
        {
            var result = await queue.TryDequeueAsync(consumer.ConsumerTag, 100);
            messages.Add(result);
        }

        // Assert
        messages.Count(m => m != null).Should().Be(2); // Only prefetch limit
        queue.MessageCount.Should().Be(3); // Remaining messages
    }

    [Fact]
    public async Task AckMessageAsync_ShouldRemoveFromPending()
    {
        // Arrange
        var queue = new Queue("test-queue");
        var consumer = CreateTestConsumer();
        await queue.AddConsumerAsync(consumer);
        
        await queue.EnqueueAsync(CreateTestMessage());
        var message = await queue.TryDequeueAsync(consumer.ConsumerTag, 100);
        message.Should().NotBeNull();

        // Act
        await queue.AckMessageAsync(message!.DeliveryTag);

        // Assert
        // Message should be removed from pending
        await queue.RemoveConsumerAsync(consumer.ConsumerTag);
        queue.MessageCount.Should().Be(0); // No requeued messages
    }

    [Fact]
    public async Task NackMessageAsync_WithRequeue_ShouldRequeueMessage()
    {
        // Arrange
        var queue = new Queue("test-queue");
        var consumer = CreateTestConsumer();
        await queue.AddConsumerAsync(consumer);
        
        await queue.EnqueueAsync(CreateTestMessage());
        var message = await queue.TryDequeueAsync(consumer.ConsumerTag, 100);
        message.Should().NotBeNull();

        // Act
        await queue.NackMessageAsync(message!.DeliveryTag, requeue: true);

        // Assert
        queue.MessageCount.Should().Be(1); // Message requeued
    }

    [Fact]
    public async Task NackMessageAsync_WithoutRequeue_ShouldDiscardMessage()
    {
        // Arrange
        var queue = new Queue("test-queue");
        var consumer = CreateTestConsumer();
        await queue.AddConsumerAsync(consumer);
        
        await queue.EnqueueAsync(CreateTestMessage());
        var message = await queue.TryDequeueAsync(consumer.ConsumerTag, 100);
        message.Should().NotBeNull();

        // Act
        await queue.NackMessageAsync(message!.DeliveryTag, requeue: false);

        // Assert
        queue.MessageCount.Should().Be(0); // Message discarded
    }

    [Fact]
    public async Task RemoveConsumerAsync_ShouldRequeueUnackedMessages()
    {
        // Arrange
        var queue = new Queue("test-queue");
        var consumer = CreateTestConsumer();
        await queue.AddConsumerAsync(consumer);
        
        await queue.EnqueueAsync(CreateTestMessage());
        await queue.EnqueueAsync(CreateTestMessage());
        
        // Dequeue but don't ack
        await queue.TryDequeueAsync(consumer.ConsumerTag, 100);
        await queue.TryDequeueAsync(consumer.ConsumerTag, 100);

        // Act
        await queue.RemoveConsumerAsync(consumer.ConsumerTag);

        // Assert
        queue.MessageCount.Should().Be(2); // Messages requeued
        queue.ConsumerCount.Should().Be(0);
    }

    [Fact]
    public async Task PurgeAsync_ShouldRemoveAllMessages()
    {
        // Arrange
        var queue = new Queue("test-queue");
        
        for (int i = 0; i < 10; i++)
        {
            await queue.EnqueueAsync(CreateTestMessage());
        }

        // Act
        await queue.PurgeAsync();

        // Assert
        queue.MessageCount.Should().Be(0);
    }

    private static QueuedMessage CreateTestMessage(byte priority = 0)
    {
        return new QueuedMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Exchange = "test-exchange",
            RoutingKey = "test.key",
            Body = BinaryData.FromString("Test message"),
            Properties = new MessageProperties { Priority = priority },
            Priority = priority,
            Timestamp = DateTime.UtcNow,
            DeliveryTag = 0 // Will be set by queue
        };
    }

    private static Consumer CreateTestConsumer(string tag = "test-consumer", int prefetch = 100)
    {
        return new Consumer
        {
            ConsumerTag = tag,
            Prefetch = prefetch,
            NoAck = false,
            Exclusive = false
        };
    }
}