namespace MelonMQ.Client;

public class IncomingMessage
{
    public ulong DeliveryTag { get; set; }
    public ReadOnlyMemory<byte> Body { get; set; }
    public bool Redelivered { get; set; }
    public Guid MessageId { get; set; }
    public string Queue { get; set; } = string.Empty;
}