using FluentAssertions;
using MelonMQ.Client;
using System.Text;
using Xunit;

namespace MelonMQ.Tests.Integration;

public class SimpleIntegrationTests
{
    /// <summary>
    /// Tests basic produce/consume flow. Requires a running MelonMQ broker on localhost:5672.
    /// Test is skipped (not falsely passed) when the broker is unavailable.
    /// </summary>
    [SkippableFact]
    public async Task BasicProducerConsumer_ShouldWork_WhenBrokerIsRunning()
    {
        // NOTE: This test requires MelonMQ broker running on localhost:5672
        // Run before testing: dotnet run --project src/MelonMQ.Broker
        
        MelonConnection connection;
        try
        {
            connection = await MelonConnection.ConnectAsync("melon://localhost:5672", null);
        }
        catch (Exception ex)
        {
            // Skip test if broker not running — don't falsely pass
            Skip.If(true, $"Broker not available: {ex.Message}");
            return;
        }

        using (connection)
        {
            using var channel = await connection.CreateChannelAsync();

            // Test basic operations
            await channel.DeclareQueueAsync("test-queue");
            
            var testMessage = "Hello Integration Test!";
            var messageBody = Encoding.UTF8.GetBytes(testMessage);
            await channel.PublishAsync("test-queue", messageBody);

            // Consume the message
            var messageReceived = false;
            string receivedContent = "";

            await foreach (var message in channel.ConsumeAsync("test-queue"))
            {
                receivedContent = Encoding.UTF8.GetString(message.Body.Span);
                await channel.AckAsync(message.DeliveryTag);
                messageReceived = true;
                break; // Exit after first message
            }

            // Verify
            messageReceived.Should().BeTrue();
            receivedContent.Should().Be(testMessage);
        }
    }
}