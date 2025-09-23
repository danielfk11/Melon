using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MelonMQ.Broker.Core;
using Xunit;

namespace MelonMQ.Tests.Unit.Core;

public class QueueManagerTests : IDisposable
{
    private readonly QueueManager _queueManager;
    private readonly string _tempDataDirectory;

    public QueueManagerTests()
    {
        _tempDataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var loggerFactory = new NullLoggerFactory();
        _queueManager = new QueueManager(_tempDataDirectory, loggerFactory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataDirectory))
        {
            Directory.Delete(_tempDataDirectory, true);
        }
    }

    [Fact]
    public void DeclareQueue_ShouldCreateNewQueue()
    {
        // Act
        var queue = _queueManager.DeclareQueue("test-queue");

        // Assert
        queue.Should().NotBeNull();
        queue.Name.Should().Be("test-queue");
    }

    [Fact]
    public void DeclareQueue_ShouldReturnSameQueue_WhenCalledTwice()
    {
        // Arrange
        var queue1 = _queueManager.DeclareQueue("same-queue");

        // Act
        var queue2 = _queueManager.DeclareQueue("same-queue");

        // Assert
        queue2.Should().BeSameAs(queue1);
    }

    [Fact]
    public void GetQueue_ShouldReturnQueue_WhenExists()
    {
        // Arrange
        var originalQueue = _queueManager.DeclareQueue("existing-queue");

        // Act
        var retrievedQueue = _queueManager.GetQueue("existing-queue");

        // Assert
        retrievedQueue.Should().BeSameAs(originalQueue);
    }

    [Fact]
    public void GetQueue_ShouldReturnNull_WhenNotExists()
    {
        // Act
        var queue = _queueManager.GetQueue("non-existing-queue");

        // Assert
        queue.Should().BeNull();
    }

    [Fact]
    public void DeleteQueue_ShouldRemoveQueue()
    {
        // Arrange
        _queueManager.DeclareQueue("queue-to-delete");

        // Act
        var result = _queueManager.DeleteQueue("queue-to-delete");

        // Assert
        result.Should().BeTrue();
        _queueManager.GetQueue("queue-to-delete").Should().BeNull();
    }
}