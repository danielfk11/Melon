using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace MelonMQ.Client.Protocol;

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
    public JsonElement? Payload { get; set; }
}

public static class FrameSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void WriteFrame(PipeWriter writer, MessageType type, ulong correlationId, object? payload = null)
    {
        var frameJson = new
        {
            type = type.ToString().ToUpperInvariant(),
            corrId = correlationId,
            payload = payload
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(frameJson, JsonOptions);
        var jsonString = Encoding.UTF8.GetString(jsonBytes);
        Console.WriteLine($"[CLIENT FrameSerializer] Sending JSON: {jsonString}");
        
        var lengthBytes = BitConverter.GetBytes(jsonBytes.Length);
        
        if (BitConverter.IsLittleEndian == false)
            Array.Reverse(lengthBytes);

        writer.Write(lengthBytes);
        writer.Write(jsonBytes);
    }

    public static async ValueTask<Frame?> ReadFrameAsync(PipeReader reader, CancellationToken cancellationToken = default)
    {
        // Read length prefix (4 bytes)
        var lengthResult = await reader.ReadAtLeastAsync(4, cancellationToken);
        if (lengthResult.IsCanceled || lengthResult.IsCompleted && lengthResult.Buffer.Length < 4)
            return null;

        var lengthBuffer = lengthResult.Buffer.Slice(0, 4);
        var lengthBytes = lengthBuffer.ToArray();
        
        Console.WriteLine($"[FrameSerializer] Raw length bytes: {string.Join(" ", lengthBytes.Select(b => b.ToString("X2")))}");
        
        if (BitConverter.IsLittleEndian == false)
            Array.Reverse(lengthBytes);
            
        var messageLength = BitConverter.ToInt32(lengthBytes);
        Console.WriteLine($"[FrameSerializer] Parsed message length: {messageLength}");
        reader.AdvanceTo(lengthBuffer.End);

        if (messageLength <= 0 || messageLength > 1024 * 1024) // Max 1MB per message
        {
            Console.WriteLine($"[FrameSerializer] INVALID LENGTH DETECTED: {messageLength}");
            Console.WriteLine($"[FrameSerializer] Available data in buffer: {lengthResult.Buffer.Length} bytes");
            if (lengthResult.Buffer.Length > 4)
            {
                var extraBytes = lengthResult.Buffer.Slice(4, Math.Min(20, lengthResult.Buffer.Length - 4)).ToArray();
                Console.WriteLine($"[FrameSerializer] Next 20 bytes: {string.Join(" ", extraBytes.Select(b => b.ToString("X2")))}");
            }
            throw new InvalidDataException($"Invalid message length: {messageLength}");
        }

        // Read message content
        var messageResult = await reader.ReadAtLeastAsync(messageLength, cancellationToken);
        if (messageResult.IsCanceled || messageResult.IsCompleted && messageResult.Buffer.Length < messageLength)
            return null;

        var messageBuffer = messageResult.Buffer.Slice(0, messageLength);
        var jsonBytes = messageBuffer.ToArray();
        reader.AdvanceTo(messageBuffer.End);

        var jsonString = Encoding.UTF8.GetString(jsonBytes);
        Console.WriteLine($"[CLIENT FrameSerializer] Received JSON: {jsonString}");
        var document = JsonDocument.Parse(jsonString);
        
        var typeString = document.RootElement.GetProperty("type").GetString()!;
        var corrId = document.RootElement.GetProperty("corrId").GetUInt64();
        
        Console.WriteLine($"[CLIENT FrameSerializer] Parsed type: {typeString}, corrId: {corrId}");
        
        if (!Enum.TryParse<MessageType>(typeString, true, out var messageType))
        {
            Console.WriteLine($"[CLIENT FrameSerializer] ERROR: Unknown message type: {typeString}");
            throw new InvalidDataException($"Unknown message type: {typeString}");
        }

        JsonElement? payload = null;
        if (document.RootElement.TryGetProperty("payload", out var payloadElement))
        {
            payload = payloadElement;
        }

        return new Frame
        {
            Type = messageType,
            CorrelationId = corrId,
            Payload = payload
        };
    }
}