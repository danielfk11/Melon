using System.Text.Json.Serialization;

namespace MelonMQ.Cli;

/// <summary>
/// Broker statistics model for CLI display
/// </summary>
public record BrokerStats
{
    [JsonPropertyName("uptime")]
    public TimeSpan Uptime { get; init; }
    
    [JsonPropertyName("totalConnections")]
    public long TotalConnections { get; init; }
    
    [JsonPropertyName("activeConnections")]
    public int ActiveConnections { get; init; }
    
    [JsonPropertyName("activeChannels")]
    public int ActiveChannels { get; init; }
    
    [JsonPropertyName("totalQueues")]
    public int TotalQueues { get; init; }
    
    [JsonPropertyName("totalExchanges")]
    public int TotalExchanges { get; init; }
    
    [JsonPropertyName("messagesInFlight")]
    public long MessagesInFlight { get; init; }
    
    [JsonPropertyName("memoryUsed")]
    public long MemoryUsed { get; init; }
    
    [JsonPropertyName("queueStats")]
    public Dictionary<string, QueueStats> QueueStats { get; init; } = new();
}

/// <summary>
/// Queue statistics model
/// </summary>
public record QueueStats
{
    [JsonPropertyName("messageCount")]
    public long MessageCount { get; init; }
    
    [JsonPropertyName("consumerCount")]
    public int ConsumerCount { get; init; }
    
    [JsonPropertyName("publishRate")]
    public double PublishRate { get; init; }
    
    [JsonPropertyName("deliveryRate")]
    public double DeliveryRate { get; init; }
}