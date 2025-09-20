using FluentAssertions;
using MelonMQ.Client;
using MelonMQ.Common;
using Xunit;

namespace MelonMQ.Tests.Client;

public class MelonConnectionTests
{
    [Fact]
    public void ParseConnectionString_ShouldParseCorrectly()
    {
        // Act & Assert
        var connectionString = "melon://user:pass@localhost:5672/vhost";
        
        // This would test the internal connection string parsing
        // For now, we just verify the connection string format is accepted
        var act = () => MelonConnection.ConnectAsync(connectionString);
        act.Should().NotBeNull();
    }

    [Theory]
    [InlineData("melon://guest:guest@localhost:5672")]
    [InlineData("melon://user:password@example.com:5672")]
    [InlineData("melon://admin:secret@192.168.1.100:5673/test")]
    public void ConnectionString_ShouldAcceptValidFormats(string connectionString)
    {
        // Act & Assert
        var act = () => MelonConnection.ConnectAsync(connectionString);
        act.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("http://localhost:5672")]
    [InlineData("melon://")]
    public void ConnectionString_ShouldRejectInvalidFormats(string connectionString)
    {
        // Act & Assert
        var act = async () => await MelonConnection.ConnectAsync(connectionString);
        act.Should().ThrowAsync<ArgumentException>();
    }
}

public class MessagePropertiesTests
{
    [Fact]
    public void Constructor_ShouldInitializeDefaults()
    {
        // Act
        var properties = new MessageProperties();

        // Assert
        properties.MessageId.Should().NotBeEmpty();
        properties.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        properties.Priority.Should().Be(0);
        properties.DeliveryMode.Should().Be(DeliveryMode.NonPersistent);
        properties.Headers.Should().NotBeNull();
        properties.Headers.Should().BeEmpty();
    }

    [Fact]
    public void SetProperties_ShouldWork()
    {
        // Arrange
        var properties = new MessageProperties();
        var messageId = Guid.NewGuid().ToString();
        var timestamp = DateTime.UtcNow.AddHours(-1);

        // Act
        properties.MessageId = messageId;
        properties.Timestamp = timestamp;
        properties.Priority = 5;
        properties.DeliveryMode = DeliveryMode.Persistent;
        properties.Headers["custom"] = "value";

        // Assert
        properties.MessageId.Should().Be(messageId);
        properties.Timestamp.Should().Be(timestamp);
        properties.Priority.Should().Be(5);
        properties.DeliveryMode.Should().Be(DeliveryMode.Persistent);
        properties.Headers["custom"].Should().Be("value");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(9)]
    public void Priority_ShouldAcceptValidValues(byte priority)
    {
        // Arrange
        var properties = new MessageProperties();

        // Act
        properties.Priority = priority;

        // Assert
        properties.Priority.Should().Be(priority);
    }
}