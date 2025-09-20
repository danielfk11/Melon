using FluentAssertions;
using MelonMQ.Broker.Core;
using MelonMQ.Common;
using Xunit;

namespace MelonMQ.Tests.Broker.Core;

public class ExchangeTests
{
    [Fact]
    public void DirectExchange_ShouldRouteByExactMatch()
    {
        // Arrange
        var exchange = new DirectExchange("test-direct", durable: true);
        exchange.BindQueue("queue1", "user.created");
        exchange.BindQueue("queue2", "user.updated");
        exchange.BindQueue("queue3", "order.created");

        // Act & Assert
        var routes1 = exchange.Route("user.created", CreateTestMessage());
        routes1.Should().ContainSingle().Which.Should().Be("queue1");

        var routes2 = exchange.Route("user.updated", CreateTestMessage());
        routes2.Should().ContainSingle().Which.Should().Be("queue2");

        var routes3 = exchange.Route("user.deleted", CreateTestMessage());
        routes3.Should().BeEmpty();
    }

    [Fact]
    public void FanoutExchange_ShouldRouteToAllQueues()
    {
        // Arrange
        var exchange = new FanoutExchange("test-fanout", durable: true);
        exchange.BindQueue("queue1", ""); // Routing key ignored in fanout
        exchange.BindQueue("queue2", "any.key");
        exchange.BindQueue("queue3", "another.key");

        // Act
        var routes = exchange.Route("any.routing.key", CreateTestMessage());

        // Assert
        routes.Should().HaveCount(3);
        routes.Should().Contain("queue1");
        routes.Should().Contain("queue2");
        routes.Should().Contain("queue3");
    }

    [Fact]
    public void TopicExchange_ShouldRouteByPattern()
    {
        // Arrange
        var exchange = new TopicExchange("test-topic", durable: true);
        exchange.BindQueue("queue1", "user.*");
        exchange.BindQueue("queue2", "user.#");
        exchange.BindQueue("queue3", "*.created");
        exchange.BindQueue("queue4", "order.updated");

        // Act & Assert
        var routes1 = exchange.Route("user.created", CreateTestMessage());
        routes1.Should().HaveCount(3); // Matches user.*, user.#, *.created
        routes1.Should().Contain("queue1");
        routes1.Should().Contain("queue2");
        routes1.Should().Contain("queue3");

        var routes2 = exchange.Route("user.profile.updated", CreateTestMessage());
        routes2.Should().ContainSingle().Which.Should().Be("queue2"); // Only matches user.#

        var routes3 = exchange.Route("order.updated", CreateTestMessage());
        routes3.Should().ContainSingle().Which.Should().Be("queue4"); // Exact match
    }

    [Fact]
    public void HeadersExchange_ShouldRouteByHeaders()
    {
        // Arrange
        var exchange = new HeadersExchange("test-headers", durable: true);
        
        // Bind with "all" match (default)
        exchange.BindQueue("queue1", "", new Dictionary<string, object>
        {
            ["type"] = "user",
            ["action"] = "created"
        });
        
        // Bind with "any" match
        exchange.BindQueue("queue2", "", new Dictionary<string, object>
        {
            ["x-match"] = "any",
            ["type"] = "user",
            ["priority"] = "high"
        });

        // Act & Assert
        var message1 = CreateTestMessage();
        message1.Properties.Headers["type"] = "user";
        message1.Properties.Headers["action"] = "created";
        
        var routes1 = exchange.Route("", message1);
        routes1.Should().HaveCount(2); // Matches both queues

        var message2 = CreateTestMessage();
        message2.Properties.Headers["type"] = "user";
        message2.Properties.Headers["priority"] = "low";
        
        var routes2 = exchange.Route("", message2);
        routes2.Should().ContainSingle().Which.Should().Be("queue2"); // Only matches "any" binding
    }

    [Fact]
    public void Exchange_UnbindQueue_ShouldRemoveBinding()
    {
        // Arrange
        var exchange = new DirectExchange("test-direct", durable: true);
        exchange.BindQueue("queue1", "test.key");
        
        // Verify binding exists
        var routesBefore = exchange.Route("test.key", CreateTestMessage());
        routesBefore.Should().ContainSingle().Which.Should().Be("queue1");

        // Act
        exchange.UnbindQueue("queue1", "test.key");

        // Assert
        var routesAfter = exchange.Route("test.key", CreateTestMessage());
        routesAfter.Should().BeEmpty();
    }

    [Fact]
    public void Exchange_GetBindings_ShouldReturnAllBindings()
    {
        // Arrange
        var exchange = new DirectExchange("test-direct", durable: true);
        exchange.BindQueue("queue1", "key1");
        exchange.BindQueue("queue2", "key2");
        exchange.BindQueue("queue1", "key3"); // Same queue, different key

        // Act
        var bindings = exchange.GetBindings();

        // Assert
        bindings.Should().HaveCount(3);
        bindings.Should().Contain(b => b.QueueName == "queue1" && b.RoutingKey == "key1");
        bindings.Should().Contain(b => b.QueueName == "queue2" && b.RoutingKey == "key2");
        bindings.Should().Contain(b => b.QueueName == "queue1" && b.RoutingKey == "key3");
    }

    [Theory]
    [InlineData("user.created", "user.*", true)]
    [InlineData("user.updated", "user.*", true)]
    [InlineData("order.created", "user.*", false)]
    [InlineData("user.profile.updated", "user.#", true)]
    [InlineData("user", "user.#", true)]
    [InlineData("profile.updated", "user.#", false)]
    public void TopicExchange_PatternMatching_ShouldWork(string routingKey, string pattern, bool shouldMatch)
    {
        // Arrange
        var exchange = new TopicExchange("test-topic", durable: true);
        exchange.BindQueue("test-queue", pattern);

        // Act
        var routes = exchange.Route(routingKey, CreateTestMessage());

        // Assert
        if (shouldMatch)
        {
            routes.Should().ContainSingle().Which.Should().Be("test-queue");
        }
        else
        {
            routes.Should().BeEmpty();
        }
    }

    [Fact]
    public void HeadersExchange_WithoutXMatch_ShouldDefaultToAll()
    {
        // Arrange
        var exchange = new HeadersExchange("test-headers", durable: true);
        exchange.BindQueue("queue1", "", new Dictionary<string, object>
        {
            ["type"] = "user",
            ["status"] = "active"
        });

        // Act
        var message = CreateTestMessage();
        message.Properties.Headers["type"] = "user";
        message.Properties.Headers["status"] = "active";
        message.Properties.Headers["extra"] = "value"; // Extra header should not prevent match
        
        var routes = exchange.Route("", message);

        // Assert
        routes.Should().ContainSingle().Which.Should().Be("queue1");
    }

    [Fact]
    public void HeadersExchange_AllMatch_RequiresAllHeaders()
    {
        // Arrange
        var exchange = new HeadersExchange("test-headers", durable: true);
        exchange.BindQueue("queue1", "", new Dictionary<string, object>
        {
            ["x-match"] = "all",
            ["type"] = "user",
            ["status"] = "active"
        });

        // Act & Assert
        var message1 = CreateTestMessage();
        message1.Properties.Headers["type"] = "user";
        // Missing "status" header
        
        var routes1 = exchange.Route("", message1);
        routes1.Should().BeEmpty(); // Should not match - missing required header

        var message2 = CreateTestMessage();
        message2.Properties.Headers["type"] = "user";
        message2.Properties.Headers["status"] = "active";
        
        var routes2 = exchange.Route("", message2);
        routes2.Should().ContainSingle(); // Should match - all headers present
    }

    private static QueuedMessage CreateTestMessage()
    {
        return new QueuedMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Exchange = "test-exchange",
            RoutingKey = "test.key",
            Body = BinaryData.FromString("Test message"),
            Properties = new MessageProperties(),
            Priority = 0,
            Timestamp = DateTime.UtcNow,
            DeliveryTag = 0
        };
    }
}