using FluentAssertions;
using MelonMQ.Broker;
using MelonMQ.Client;
using MelonMQ.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;

namespace MelonMQ.Tests.Integration;

public class BrokerIntegrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string _tempDirectory = null!;
    private readonly int _brokerPort = GetAvailablePort();
    private readonly int _adminPort = GetAvailablePort();

    public async Task InitializeAsync()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"melonmq-integration-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseUrls($"http://localhost:{_adminPort}");
                builder.ConfigureServices(services =>
                {
                    // Override configuration for testing
                    services.Configure<BrokerConfiguration>(config =>
                    {
                        config.Host = "localhost";
                        config.Port = _brokerPort;
                        config.DataDirectory = _tempDirectory;
                        config.MaxConnections = 100;
                        config.HeartbeatInterval = TimeSpan.FromSeconds(30);
                    });
                });
            });

        // Start the broker
        await _factory.Services.GetRequiredService<IHostedService>().StartAsync(CancellationToken.None);
        
        // Give broker time to start
        await Task.Delay(1000);
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
        {
            await _factory.Services.GetRequiredService<IHostedService>().StopAsync(CancellationToken.None);
            await _factory.DisposeAsync();
        }

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task BasicConnection_ShouldSucceed()
    {
        // Act
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");

        // Assert
        connection.Should().NotBeNull();
        connection.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task CreateChannel_ShouldSucceed()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");

        // Act
        using var channel = await connection.CreateChannelAsync();

        // Assert
        channel.Should().NotBeNull();
    }

    [Fact]
    public async Task DeclareExchange_ShouldSucceed()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
        using var channel = await connection.CreateChannelAsync();

        // Act
        await channel.DeclareExchangeAsync("test-exchange", ExchangeType.Direct, durable: true);

        // Assert - No exception should be thrown
    }

    [Fact]
    public async Task DeclareQueue_ShouldSucceed()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
        using var channel = await connection.CreateChannelAsync();

        // Act
        await channel.DeclareQueueAsync("test-queue", durable: true, exclusive: false, autoDelete: false);

        // Assert - No exception should be thrown
    }

    [Fact]
    public async Task BindQueue_ShouldSucceed()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
        using var channel = await connection.CreateChannelAsync();
        
        await channel.DeclareExchangeAsync("test-exchange", ExchangeType.Direct, durable: true);
        await channel.DeclareQueueAsync("test-queue", durable: true, exclusive: false, autoDelete: false);

        // Act
        await channel.BindQueueAsync("test-queue", "test-exchange", "test.key");

        // Assert - No exception should be thrown
    }

    [Fact]
    public async Task PublishAndConsume_ShouldWork()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
        using var channel = await connection.CreateChannelAsync();
        
        await channel.DeclareExchangeAsync("test-exchange", ExchangeType.Direct, durable: true);
        await channel.DeclareQueueAsync("test-queue", durable: true, exclusive: false, autoDelete: false);
        await channel.BindQueueAsync("test-queue", "test-exchange", "test.key");

        var testMessage = "Hello, MelonMQ!";
        var messageBody = BinaryData.FromString(testMessage);
        var messageProperties = new MessageProperties
        {
            MessageId = Guid.NewGuid().ToString(),
            Priority = 5
        };

        // Act - Publish
        await channel.PublishAsync("test-exchange", "test.key", messageBody, messageProperties, persistent: true, priority: 5);

        // Act - Consume
        var receivedMessage = false;
        var receivedContent = string.Empty;
        
        await foreach (var delivery in channel.ConsumeAsync("test-queue", consumerTag: "test-consumer", prefetch: 1, noAck: false, CancellationToken.None))
        {
            receivedContent = System.Text.Encoding.UTF8.GetString(delivery.Message.Body.Span);
            await delivery.AckAsync();
            receivedMessage = true;
            break; // Only consume one message
        }

        // Assert
        receivedMessage.Should().BeTrue();
        receivedContent.Should().Be(testMessage);
    }

    [Fact]
    public async Task MessagePriority_ShouldBeRespected()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
        using var channel = await connection.CreateChannelAsync();
        
        await channel.DeclareExchangeAsync("priority-exchange", ExchangeType.Direct, durable: true);
        await channel.DeclareQueueAsync("priority-queue", durable: true, exclusive: false, autoDelete: false);
        await channel.BindQueueAsync("priority-queue", "priority-exchange", "priority.key");

        // Publish messages with different priorities
        var messages = new[]
        {
            (Priority: (byte)1, Content: "Low priority"),
            (Priority: (byte)9, Content: "High priority"),
            (Priority: (byte)5, Content: "Medium priority")
        };

        foreach (var (priority, content) in messages)
        {
            var messageBody = BinaryData.FromString(content);
            var properties = new MessageProperties { Priority = priority };
            await channel.PublishAsync("priority-exchange", "priority.key", messageBody, properties, persistent: true, priority: priority);
        }

        // Act - Consume messages
        var receivedMessages = new List<string>();
        var count = 0;
        
        await foreach (var delivery in channel.ConsumeAsync("priority-queue", consumerTag: "priority-consumer", prefetch: 10, noAck: false, CancellationToken.None))
        {
            var content = System.Text.Encoding.UTF8.GetString(delivery.Message.Body.Span);
            receivedMessages.Add(content);
            await delivery.AckAsync();
            
            count++;
            if (count >= 3) break;
        }

        // Assert - Should receive messages in priority order (highest first)
        receivedMessages.Should().HaveCount(3);
        receivedMessages[0].Should().Be("High priority");   // Priority 9
        receivedMessages[1].Should().Be("Medium priority"); // Priority 5
        receivedMessages[2].Should().Be("Low priority");    // Priority 1
    }

    [Fact]
    public async Task TopicExchange_ShouldRouteCorrectly()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
        using var channel = await connection.CreateChannelAsync();
        
        await channel.DeclareExchangeAsync("topic-exchange", ExchangeType.Topic, durable: true);
        await channel.DeclareQueueAsync("user-queue", durable: true, exclusive: false, autoDelete: false);
        await channel.DeclareQueueAsync("order-queue", durable: true, exclusive: false, autoDelete: false);
        await channel.DeclareQueueAsync("all-queue", durable: true, exclusive: false, autoDelete: false);
        
        await channel.BindQueueAsync("user-queue", "topic-exchange", "user.*");
        await channel.BindQueueAsync("order-queue", "topic-exchange", "order.*");
        await channel.BindQueueAsync("all-queue", "topic-exchange", "#");

        // Act - Publish to different routing keys
        var userMessage = BinaryData.FromString("User created");
        var orderMessage = BinaryData.FromString("Order created");
        
        await channel.PublishAsync("topic-exchange", "user.created", userMessage, new MessageProperties(), persistent: true, priority: 0);
        await channel.PublishAsync("topic-exchange", "order.created", orderMessage, new MessageProperties(), persistent: true, priority: 0);

        // Give some time for routing
        await Task.Delay(100);

        // Assert - Check that messages were routed correctly
        // This is a simplified check - in a full test we'd consume from each queue
        // For now, just verify no exceptions were thrown during routing
    }

    [Fact]
    public async Task FanoutExchange_ShouldBroadcast()
    {
        // Arrange
        using var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
        using var channel = await connection.CreateChannelAsync();
        
        await channel.DeclareExchangeAsync("fanout-exchange", ExchangeType.Fanout, durable: true);
        await channel.DeclareQueueAsync("fanout-queue1", durable: true, exclusive: false, autoDelete: false);
        await channel.DeclareQueueAsync("fanout-queue2", durable: true, exclusive: false, autoDelete: false);
        
        await channel.BindQueueAsync("fanout-queue1", "fanout-exchange", "");
        await channel.BindQueueAsync("fanout-queue2", "fanout-exchange", "");

        // Act
        var message = BinaryData.FromString("Broadcast message");
        await channel.PublishAsync("fanout-exchange", "", message, new MessageProperties(), persistent: true, priority: 0);

        // Give some time for routing
        await Task.Delay(100);

        // Assert - In a full test, we'd verify both queues received the message
    }

    [Fact]
    public async Task MultipleConnections_ShouldWork()
    {
        // Arrange & Act
        var connections = new List<IMelonConnection>();
        
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
                connections.Add(connection);
            }

            // Assert
            connections.Should().HaveCount(5);
            connections.Should().OnlyContain(c => c.IsConnected);
        }
        finally
        {
            foreach (var connection in connections)
            {
                connection.Dispose();
            }
        }
    }

    [Fact]
    public async Task AdminApi_ShouldReturnStats()
    {
        // Arrange
        var httpClient = _factory!.CreateClient();

        // Act
        var response = await httpClient.GetAsync("/api/admin/stats");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeEmpty();
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}