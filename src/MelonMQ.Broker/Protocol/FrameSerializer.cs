using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using MelonMQ.Protocol;

namespace MelonMQ.Broker.Protocol;

public static class FrameSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void WriteFrame(PipeWriter writer, Frame frame)
    {
        var frameJson = new
        {
            type = frame.Type.ToString().ToUpperInvariant(),
            corrId = frame.CorrelationId,
            payload = frame.Payload
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(frameJson, JsonOptions);
        var lengthBytes = BitConverter.GetBytes(jsonBytes.Length);
        
        if (BitConverter.IsLittleEndian == false)
            Array.Reverse(lengthBytes);

        writer.Write(lengthBytes);
        writer.Write(jsonBytes);
    }

    public static async Task WriteFrameToStreamAsync(Stream stream, Frame frame)
    {
        var frameJson = new
        {
            type = frame.Type.ToString().ToUpperInvariant(),
            corrId = frame.CorrelationId,
            payload = frame.Payload
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(frameJson, JsonOptions);
        var lengthBytes = BitConverter.GetBytes(jsonBytes.Length);
        
        if (BitConverter.IsLittleEndian == false)
            Array.Reverse(lengthBytes);

        await stream.WriteAsync(lengthBytes);
        await stream.WriteAsync(jsonBytes);
        await stream.FlushAsync();
    }

    public static async ValueTask<Frame?> ReadFrameAsync(PipeReader reader, CancellationToken cancellationToken = default)
    {
        // Read length prefix (4 bytes)
        var lengthResult = await reader.ReadAtLeastAsync(4, cancellationToken);
        if (lengthResult.IsCanceled || lengthResult.IsCompleted && lengthResult.Buffer.Length < 4)
            return null;

        var lengthBuffer = lengthResult.Buffer.Slice(0, 4);
        var lengthBytes = lengthBuffer.ToArray();
        
        if (BitConverter.IsLittleEndian == false)
            Array.Reverse(lengthBytes);
            
        var messageLength = BitConverter.ToInt32(lengthBytes);
        reader.AdvanceTo(lengthBuffer.End);

        if (messageLength <= 0 || messageLength > 1024 * 1024) // Max 1MB per message
        {
            throw new InvalidDataException($"Invalid message length: {messageLength}");
        }

        // Read message content
        var messageResult = await reader.ReadAtLeastAsync(messageLength, cancellationToken);
        if (messageResult.IsCanceled || messageResult.IsCompleted && messageResult.Buffer.Length < messageLength)
            return null;

        var messageBuffer = messageResult.Buffer.Slice(0, messageLength);
        var jsonBytes = messageBuffer.ToArray();
        reader.AdvanceTo(messageBuffer.End);

        var document = JsonDocument.Parse(jsonBytes);
        
        var typeString = document.RootElement.GetProperty("type").GetString()!;
        var corrId = document.RootElement.GetProperty("corrId").GetUInt64();
        
        if (!Enum.TryParse<MessageType>(typeString, true, out var messageType))
            throw new InvalidDataException($"Unknown message type: {typeString}");

        object? payload = null;
        if (document.RootElement.TryGetProperty("payload", out var payloadElement))
        {
            payload = messageType switch
            {
                MessageType.Auth => JsonSerializer.Deserialize<AuthPayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.DeclareQueue => JsonSerializer.Deserialize<DeclareQueuePayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.Publish => JsonSerializer.Deserialize<PublishPayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.ConsumeSubscribe => JsonSerializer.Deserialize<ConsumeSubscribePayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.Deliver => JsonSerializer.Deserialize<DeliverPayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.Ack => JsonSerializer.Deserialize<AckPayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.Nack => JsonSerializer.Deserialize<NackPayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.SetPrefetch => JsonSerializer.Deserialize<SetPrefetchPayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.Error => JsonSerializer.Deserialize<ErrorPayload>(payloadElement.GetRawText(), JsonOptions),
                MessageType.Success => payloadElement.GetRawText(), // Success responses can be raw JSON
                _ => null
            };
        }

        return new Frame
        {
            Type = messageType,
            CorrelationId = corrId,
            Payload = payload
        };
    }
}