namespace MelonMQ.Broker.Protocol;

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
    Success // Add success response type
}

public class Frame
{
    public MessageType Type { get; set; }
    public ulong CorrelationId { get; set; }
    public object? Payload { get; set; }
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
}

public class PublishPayload
{
    public string Queue { get; set; } = string.Empty;
    public string BodyBase64 { get; set; } = string.Empty;
    public int? TtlMs { get; set; }
    public bool Persistent { get; set; }
    public Guid MessageId { get; set; }
}

public class ConsumeSubscribePayload
{
    public string Queue { get; set; } = string.Empty;
}

public class DeliverPayload
{
    public string Queue { get; set; } = string.Empty;
    public ulong DeliveryTag { get; set; }
    public string BodyBase64 { get; set; } = string.Empty;
    public bool Redelivered { get; set; }
    public Guid MessageId { get; set; }
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