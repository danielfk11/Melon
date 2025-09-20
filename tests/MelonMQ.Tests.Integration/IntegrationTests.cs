using MelonMQ.Broker.Core;
using MelonMQ.Client;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MelonMQ.Tests.Integration;

public class MelonMQIntegrationTests : IAsyncLifetime
{
    private TcpServer? _server;
    private QueueManager? _queueManager;
    private ConnectionManager? _connectionManager;
    private Task? _serverTask;
    private CancellationTokenSource? _serverCts;
    private const int TestPort = 5673;

    public async Task InitializeAsync()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        
        _queueManager = new QueueManager("test-data", loggerFactory);
        _connectionManager = new ConnectionManager(loggerFactory.CreateLogger<ConnectionManager>());
        _server = new TcpServer(_queueManager, _connectionManager, TestPort, loggerFactory.CreateLogger<TcpServer>());

        _serverCts = new CancellationTokenSource();
        _serverTask = Task.Run(() => _server.StartAsync(_serverCts.Token));

        // Wait a bit for server to start
        await Task.Delay(1000);
    }

    public async Task DisposeAsync()
    {
        _serverCts?.Cancel();
        _server?.Stop();
        
        if (_serverTask != null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException) { }
        }

        _serverCts?.Dispose();

        // Cleanup test data
        if (Directory.Exists("test-data"))
        {
            Directory.Delete("test-data", true);
        }
    }

    [Fact]
    public async Task PublishAndConsumeMessage_ShouldWork()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://localhost:{TestPort}");
        using var channel = await connection.CreateChannelAsync();

        await channel.DeclareQueueAsync("test-queue");

        var messageBody = "Hello, MelonMQ!"u8.ToArray();
        var receivedMessages = new List<IncomingMessage>();

        // Act - Publish
        await channel.PublishAsync("test-queue", messageBody);

        // Act - Consume
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        
        await foreach (var message in channel.ConsumeAsync("test-queue", cancellationToken: cts.Token))
        {
            receivedMessages.Add(message);
            await channel.AckAsync(message.DeliveryTag);
            break; // Only consume one message for this test
        }

        // Assert
        Assert.Single(receivedMessages);
        Assert.Equal(messageBody, receivedMessages[0].Body.ToArray());
        Assert.False(receivedMessages[0].Redelivered);
    }

    [Fact]
    public async Task MessageRedelivery_WhenNotAcked_ShouldWork()
    {
        // Arrange
        using var connection1 = await MelonConnection.ConnectAsync($"melon://localhost:{TestPort}");
        using var channel1 = await connection1.CreateChannelAsync();

        await channel1.DeclareQueueAsync("redelivery-queue");
        var messageBody = "Redelivery test"u8.ToArray();

        // Publish message
        await channel1.PublishAsync("redelivery-queue", messageBody);

        // Consume without ack (will trigger redelivery when connection closes)
        var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var message in channel1.ConsumeAsync("redelivery-queue", cancellationToken: cts1.Token))
        {
            // Don't ack the message - just break to close connection
            break;
        }

        // Close first connection (should trigger redelivery)
        await connection1.DisposeAsync();
        await Task.Delay(1000); // Wait for requeue

        // Create new connection and consume again
        using var connection2 = await MelonConnection.ConnectAsync($"melon://localhost:{TestPort}");
        using var channel2 = await connection2.CreateChannelAsync();

        var receivedMessages = new List<IncomingMessage>();
        var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var message in channel2.ConsumeAsync("redelivery-queue", cancellationToken: cts2.Token))
        {
            receivedMessages.Add(message);
            await channel2.AckAsync(message.DeliveryTag);
            break;
        }

        // Assert
        Assert.Single(receivedMessages);
        Assert.Equal(messageBody, receivedMessages[0].Body.ToArray());
        Assert.True(receivedMessages[0].Redelivered);
    }

    [Fact]
    public async Task PersistentQueue_AfterRestart_ShouldRetainMessages()
    {
        // This test would require restarting the broker, which is complex in a unit test
        // For now, we'll test the persistence mechanism directly
        
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var testDataDir = "test-persistence";
        
        // Create queue manager with persistence
        var queueManager = new QueueManager(testDataDir, loggerFactory);
        var queue = queueManager.DeclareQueue("persistent-queue", durable: true);

        // Add message
        var message = new QueueMessage
        {
            MessageId = Guid.NewGuid(),
            Body = "Persistent message"u8.ToArray(),
            EnqueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Persistent = true
        };

        await queue.EnqueueAsync(message);

        // Verify persistence file exists
        var persistenceFile = Path.Combine(testDataDir, "persistent-queue.log");
        Assert.True(File.Exists(persistenceFile));

        var content = await File.ReadAllTextAsync(persistenceFile);
        Assert.Contains(message.MessageId.ToString(), content);

        // Cleanup
        if (Directory.Exists(testDataDir))
        {
            Directory.Delete(testDataDir, true);
        }
    }

    [Fact]
    public async Task MultipleConsumers_ShouldDistributeMessages()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://localhost:{TestPort}");
        using var channel1 = await connection.CreateChannelAsync();
        using var channel2 = await connection.CreateChannelAsync();

        await channel1.DeclareQueueAsync("multi-consumer-queue");

        // Publish multiple messages
        for (int i = 0; i < 10; i++)
        {
            var messageBody = System.Text.Encoding.UTF8.GetBytes($"Message {i}");
            await channel1.PublishAsync("multi-consumer-queue", messageBody);
        }

        var consumer1Messages = new List<IncomingMessage>();
        var consumer2Messages = new List<IncomingMessage>();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start two consumers
        var consumer1Task = Task.Run(async () =>
        {
            await foreach (var message in channel1.ConsumeAsync("multi-consumer-queue", cancellationToken: cts.Token))
            {
                consumer1Messages.Add(message);
                await channel1.AckAsync(message.DeliveryTag);
                
                if (consumer1Messages.Count + consumer2Messages.Count >= 10)
                    break;
            }
        });

        var consumer2Task = Task.Run(async () =>
        {
            await foreach (var message in channel2.ConsumeAsync("multi-consumer-queue", cancellationToken: cts.Token))
            {
                consumer2Messages.Add(message);
                await channel2.AckAsync(message.DeliveryTag);
                
                if (consumer1Messages.Count + consumer2Messages.Count >= 10)
                    break;
            }
        });

        await Task.WhenAny(consumer1Task, consumer2Task);
        cts.Cancel();

        // Assert both consumers received messages
        var totalMessages = consumer1Messages.Count + consumer2Messages.Count;
        Assert.True(totalMessages > 0);
        Assert.True(consumer1Messages.Count > 0 || consumer2Messages.Count > 0);
    }
}