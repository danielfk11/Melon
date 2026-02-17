using MelonMQ.Protocol;

namespace MelonMQ.Broker.Protocol;

/// <summary>
/// Broker-side Frame uses object? for Payload since the broker deserializes
/// payloads into strongly-typed classes.
/// </summary>
public class Frame
{
    public MessageType Type { get; set; }
    public ulong CorrelationId { get; set; }
    public object? Payload { get; set; }
}