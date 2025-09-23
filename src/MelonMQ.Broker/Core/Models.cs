namespace MelonMQ.Broker.Core;

public class QueueMessage
{
    public Guid MessageId { get; set; }
    public ReadOnlyMemory<byte> Body { get; set; }
    public long EnqueuedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public bool Persistent { get; set; }
    public bool Redelivered { get; set; }
    public int DeliveryCount { get; set; }
}

public class InFlightMessage
{
    public QueueMessage Message { get; set; } = null!;
    public ulong DeliveryTag { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public long DeliveredAt { get; set; }
    public long ExpiresAt { get; set; }
}

public class QueueConfiguration
{
    public string Name { get; set; } = string.Empty;
    public bool Durable { get; set; }
    public string? DeadLetterQueue { get; set; }
    public int? DefaultTtlMs { get; set; }
}