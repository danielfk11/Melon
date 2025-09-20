using System.Text.Json.Serialization;

namespace MelonMQ.Common;

/// <summary>
/// Message metadata and properties
/// </summary>
public class MessageProperties
{
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public DateTime? Expiration { get; set; }
    public byte Priority { get; set; } = 0;
    public string? GroupKey { get; set; }
    public string? ContentType { get; set; }
    public string? ContentEncoding { get; set; }
    public string? AppId { get; set; }
    public string? UserId { get; set; }
    public Guid? CorrelationId { get; set; }
    public string? ReplyTo { get; set; }
    public DeliveryMode DeliveryMode { get; set; } = DeliveryMode.NonPersistent;
    public Dictionary<string, object> Headers { get; set; } = new();
    
    // Delivery tracking
    public int DeliveryCount { get; set; } = 0;
    public DateTime? FirstDeliveryAttempt { get; set; }
    public DateTime? LastDeliveryAttempt { get; set; }
    public string? LastConsumerTag { get; set; }
}

/// <summary>
/// A message in the MelonMQ system
/// </summary>
public class Message
{
    public MessageProperties Properties { get; set; } = new();
    public ReadOnlyMemory<byte> Body { get; set; }
    public string Exchange { get; set; } = "";
    public string RoutingKey { get; set; } = "";
    
    // Internal tracking
    public long Offset { get; set; }
    public string? QueueName { get; set; }
    public ulong DeliveryTag { get; set; }
    public bool Redelivered => Properties.DeliveryCount > 1;
    
    public Message() { }
    
    public Message(ReadOnlyMemory<byte> body, string exchange = "", string routingKey = "")
    {
        Body = body;
        Exchange = exchange;
        RoutingKey = routingKey;
    }
}

/// <summary>
/// Exchange configuration
/// </summary>
public class ExchangeConfig
{
    public string Name { get; set; } = "";
    public ExchangeType Type { get; set; } = ExchangeType.Direct;
    public bool Durable { get; set; } = false;
    public bool AutoDelete { get; set; } = false;
    public bool Internal { get; set; } = false;
    public Dictionary<string, object> Arguments { get; set; } = new();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
}

/// <summary>
/// Queue configuration
/// </summary>
public class QueueConfig
{
    public string Name { get; set; } = "";
    public bool Durable { get; set; } = false;
    public bool AutoDelete { get; set; } = false;
    public bool Exclusive { get; set; } = false;
    public QueueType Type { get; set; } = QueueType.Classic;
    
    // Queue limits
    public int? MaxLength { get; set; }
    public long? MaxLengthBytes { get; set; }
    public TimeSpan? MessageTtl { get; set; }
    public TimeSpan? QueueTtl { get; set; }
    public byte? MaxPriority { get; set; }
    
    // Dead letter configuration
    public string? DeadLetterExchange { get; set; }
    public string? DeadLetterRoutingKey { get; set; }
    public int MaxDeliveryCount { get; set; } = 5;
    
    // Flow control
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);
    
    public Dictionary<string, object> Arguments { get; set; } = new();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
}

/// <summary>
/// Binding between exchange and queue
/// </summary>
public class BindingConfig
{
    public string Exchange { get; set; } = "";
    public string Queue { get; set; } = "";
    public string RoutingKey { get; set; } = "";
    public Dictionary<string, object> Arguments { get; set; } = new();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
}

/// <summary>
/// Consumer configuration
/// </summary>
public class ConsumerConfig
{
    public string ConsumerTag { get; set; } = "";
    public string QueueName { get; set; } = "";
    public bool NoAck { get; set; } = false;
    public bool Exclusive { get; set; } = false;
    public int Prefetch { get; set; } = ProtocolConstants.DefaultPrefetch;
    public Dictionary<string, object> Arguments { get; set; } = new();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
}

/// <summary>
/// User credentials and permissions
/// </summary>
public class UserConfig
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = 4096;
    public AuthMethod AuthMethod { get; set; } = AuthMethod.ScramSha256;
    public string[] Roles { get; set; } = Array.Empty<string>();
    public Dictionary<string, string[]> VHostPermissions { get; set; } = new();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Virtual host configuration
/// </summary>
public class VHostConfig
{
    public string Name { get; set; } = ProtocolConstants.DefaultVHost;
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
    
    // Resource limits
    public int? MaxConnections { get; set; }
    public int? MaxChannels { get; set; }
    public int? MaxQueues { get; set; }
    public int? MaxExchanges { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
}

/// <summary>
/// System statistics
/// </summary>
public class BrokerStats
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public TimeSpan Uptime { get; set; }
    
    // Connection stats
    public int TotalConnections { get; set; }
    public int ActiveConnections { get; set; }
    public int TotalChannels { get; set; }
    public int ActiveChannels { get; set; }
    
    // Message stats
    public long TotalMessagesPublished { get; set; }
    public long TotalMessagesDelivered { get; set; }
    public long TotalMessagesAcked { get; set; }
    public long TotalMessagesRejected { get; set; }
    public long MessagesInFlight { get; set; }
    
    // Resource stats
    public int TotalExchanges { get; set; }
    public int TotalQueues { get; set; }
    public int TotalBindings { get; set; }
    public int TotalConsumers { get; set; }
    
    // Performance metrics
    public double PublishRate { get; set; } // messages/second
    public double DeliveryRate { get; set; } // messages/second
    public double AckRate { get; set; } // messages/second
    
    // Memory usage
    public long MemoryUsed { get; set; }
    public long DiskUsed { get; set; }
    
    // Per-queue stats
    public Dictionary<string, QueueStats> QueueStats { get; set; } = new();
}

/// <summary>
/// Queue-specific statistics
/// </summary>
public class QueueStats
{
    public string Name { get; set; } = "";
    public int MessageCount { get; set; }
    public int ConsumerCount { get; set; }
    public long TotalPublished { get; set; }
    public long TotalDelivered { get; set; }
    public long TotalAcked { get; set; }
    public long TotalRejected { get; set; }
    public double PublishRate { get; set; }
    public double DeliveryRate { get; set; }
    public double AckRate { get; set; }
    public long MemoryUsed { get; set; }
    public DateTime LastActivity { get; set; }
}