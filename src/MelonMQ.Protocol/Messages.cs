namespace MelonMQ.Protocol;

public enum MessageType
{
    Auth,
    DeclareQueue,
    Publish,
    ConsumeSubscribe,
    Deliver,
    Ack,
    Nack,
    SetPrefetch,
    Heartbeat,
    Error,
    Success,
    DeclareExchange,
    BindQueue,
    UnbindQueue,
    StreamAck,
}

public class AuthPayload
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class DeclareQueuePayload
{
    public string Queue { get; set; } = string.Empty;
    public bool Durable { get; set; }
    public string? DeadLetterQueue { get; set; }
    public int? DefaultTtlMs { get; set; }
    /// <summary>"classic" (default) or "stream"</summary>
    public string Mode { get; set; } = "classic";
    /// <summary>Stream retention: max number of messages to keep (stream mode only)</summary>
    public int? StreamMaxLengthMessages { get; set; }
    /// <summary>Stream retention: max age in milliseconds (stream mode only)</summary>
    public long? StreamMaxAgeMs { get; set; }
}

public class PublishPayload
{
    public string Queue { get; set; } = string.Empty;
    public string BodyBase64 { get; set; } = string.Empty;
    public int? TtlMs { get; set; }
    public bool Persistent { get; set; }
    public Guid MessageId { get; set; }
    /// <summary>Exchange name for exchange-based routing. When set, Queue is ignored.</summary>
    public string? Exchange { get; set; }
    /// <summary>Routing key used with exchange routing.</summary>
    public string? RoutingKey { get; set; }
}

public class ConsumeSubscribePayload
{
    public string Queue { get; set; } = string.Empty;
    /// <summary>Consumer group name. Consumers in the same group share messages; different groups each receive all messages independently.</summary>
    public string? Group { get; set; }
    /// <summary>Stream offset to start consuming from: -1 = latest (default), 0 = beginning, N = specific offset. Only used for stream queues.</summary>
    public long? Offset { get; set; }
}

public class DeliverPayload
{
    public string Queue { get; set; } = string.Empty;
    public ulong DeliveryTag { get; set; }
    public string BodyBase64 { get; set; } = string.Empty;
    public bool Redelivered { get; set; }
    public Guid MessageId { get; set; }
    /// <summary>Stream offset. Only populated for stream queue deliveries.</summary>
    public long? Offset { get; set; }
}

public class AckPayload
{
    public ulong DeliveryTag { get; set; }
}

public class NackPayload
{
    public ulong DeliveryTag { get; set; }
    public bool Requeue { get; set; } = true;
}

public class SetPrefetchPayload
{
    public int Prefetch { get; set; }
}

public class ErrorPayload
{
    public string Message { get; set; } = string.Empty;
    public string? Code { get; set; }
}

public class DeclareExchangePayload
{
    public string Exchange { get; set; } = string.Empty;
    /// <summary>"direct", "fanout", or "topic"</summary>
    public string Type { get; set; } = "direct";
    public bool Durable { get; set; }
}

public class BindQueuePayload
{
    public string Exchange { get; set; } = string.Empty;
    public string Queue { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
}

public class UnbindQueuePayload
{
    public string Exchange { get; set; } = string.Empty;
    public string Queue { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
}

public class StreamAckPayload
{
    public string Queue { get; set; } = string.Empty;
    public long Offset { get; set; }
    /// <summary>Group name (if consuming as part of a consumer group).</summary>
    public string? Group { get; set; }
}
