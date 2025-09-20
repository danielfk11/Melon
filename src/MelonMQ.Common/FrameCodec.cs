using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MelonMQ.Common;

/// <summary>
/// A frame in the MelonMQ protocol
/// </summary>
public readonly ref struct ProtocolFrame
{
    public ushort Magic { get; init; }
    public byte Version { get; init; }
    public FrameType Type { get; init; }
    public FrameFlags Flags { get; init; }
    public int Length { get; init; }
    public ulong CorrelationId { get; init; }
    public ReadOnlySpan<byte> Payload { get; init; }
    
    public bool IsRequest => (Flags & FrameFlags.Request) != 0;
    public bool IsDurable => (Flags & FrameFlags.Durable) != 0;
    public bool IsMandatory => (Flags & FrameFlags.Mandatory) != 0;
    public bool IsImmediate => (Flags & FrameFlags.Immediate) != 0;
    public bool IsCompressed => (Flags & FrameFlags.Compressed) != 0;
}

/// <summary>
/// Utility for reading and writing protocol frames
/// </summary>
public static class FrameCodec
{
    public static bool TryReadFrame(ref SequenceReader<byte> reader, out ProtocolFrame frame)
    {
        frame = default;
        
        if (reader.Remaining < ProtocolConstants.FrameHeaderSize)
            return false;
            
        var checkpoint = reader.Position;
        
        // Read header
        if (!reader.TryReadLittleEndian(out ushort magic) ||
            !reader.TryRead(out byte version) ||
            !reader.TryRead(out byte type) ||
            !reader.TryRead(out byte flags) ||
            !reader.TryReadLittleEndian(out int length) ||
            !reader.TryReadLittleEndian(out ulong correlationId))
        {
            reader.Rewind(reader.Position.GetInteger() - checkpoint.GetInteger());
            return false;
        }
        
        // Validate frame
        if (magic != ProtocolConstants.Magic || 
            version != ProtocolConstants.Version ||
            length < 0 || 
            length > ProtocolConstants.MaxFrameSize)
        {
            throw new InvalidOperationException($"Invalid frame: magic={magic:X4}, version={version}, length={length}");
        }
        
        // Check if we have the full payload
        if (reader.Remaining < length)
        {
            reader.Rewind(reader.Position.GetInteger() - checkpoint.GetInteger());
            return false;
        }
        
        // Read payload
        var payload = reader.UnreadSequence.Slice(0, length);
        reader.Advance(length);
        
        frame = new ProtocolFrame
        {
            Magic = magic,
            Version = version,
            Type = (FrameType)type,
            Flags = (FrameFlags)flags,
            Length = length,
            CorrelationId = correlationId,
            Payload = payload.IsSingleSegment ? payload.FirstSpan : payload.ToArray()
        };
        
        return true;
    }
    
    public static void WriteFrame(IBufferWriter<byte> writer, FrameType type, FrameFlags flags, 
        ulong correlationId, ReadOnlySpan<byte> payload)
    {
        var totalLength = ProtocolConstants.FrameHeaderSize + payload.Length;
        var buffer = writer.GetSpan(totalLength);
        
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, ProtocolConstants.Magic);
        buffer[2] = ProtocolConstants.Version;
        buffer[3] = (byte)type;
        buffer[4] = (byte)flags;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(5), payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(9), correlationId);
        
        payload.CopyTo(buffer.Slice(ProtocolConstants.FrameHeaderSize));
        
        writer.Advance(totalLength);
    }
}

/// <summary>
/// TLV (Type-Length-Value) reader for frame payloads
/// </summary>
public ref struct TlvReader
{
    private SequenceReader<byte> _reader;
    
    public TlvReader(ReadOnlySpan<byte> data)
    {
        _reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));
    }
    
    public bool TryReadString(out string value)
    {
        value = string.Empty;
        
        if (!_reader.TryRead(out byte type) || type != 1) // String type
            return false;
            
        if (!_reader.TryReadLittleEndian(out int length))
            return false;
            
        if (length < 0 || _reader.Remaining < length)
            return false;
            
        var stringBytes = _reader.UnreadSequence.Slice(0, length);
        value = stringBytes.IsSingleSegment 
            ? Encoding.UTF8.GetString(stringBytes.FirstSpan)
            : Encoding.UTF8.GetString(stringBytes.ToArray());
            
        _reader.Advance(length);
        return true;
    }
    
    public bool TryReadInt32(out int value)
    {
        value = 0;
        
        if (!_reader.TryRead(out byte type) || type != 2) // Int32 type
            return false;
            
        if (!_reader.TryReadLittleEndian(out int length) || length != 4)
            return false;
            
        return _reader.TryReadLittleEndian(out value);
    }
    
    public bool TryReadInt64(out long value)
    {
        value = 0;
        
        if (!_reader.TryRead(out byte type) || type != 3) // Int64 type
            return false;
            
        if (!_reader.TryReadLittleEndian(out int length) || length != 8)
            return false;
            
        return _reader.TryReadLittleEndian(out value);
    }
    
    public bool TryReadGuid(out Guid value)
    {
        value = default;
        
        if (!_reader.TryRead(out byte type) || type != 4) // Guid type
            return false;
            
        if (!_reader.TryReadLittleEndian(out int length) || length != 16)
            return false;
            
        Span<byte> guidBytes = stackalloc byte[16];
        if (!_reader.TryCopyTo(guidBytes))
            return false;
            
        value = new Guid(guidBytes);
        _reader.Advance(16);
        return true;
    }
    
    public bool TryReadBytes(out ReadOnlySpan<byte> value)
    {
        value = default;
        
        if (!_reader.TryRead(out byte type) || type != 5) // Bytes type
            return false;
            
        if (!_reader.TryReadLittleEndian(out int length))
            return false;
            
        if (length < 0 || _reader.Remaining < length)
            return false;
            
        var bytes = _reader.UnreadSequence.Slice(0, length);
        value = bytes.IsSingleSegment ? bytes.FirstSpan : bytes.ToArray();
        
        _reader.Advance(length);
        return true;
    }
}

/// <summary>
/// TLV writer for frame payloads
/// </summary>
public ref struct TlvWriter
{
    private readonly IBufferWriter<byte> _writer;
    
    public TlvWriter(IBufferWriter<byte> writer)
    {
        _writer = writer;
    }
    
    public void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var buffer = _writer.GetSpan(5 + bytes.Length);
        
        buffer[0] = 1; // String type
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1), bytes.Length);
        bytes.CopyTo(buffer.Slice(5));
        
        _writer.Advance(5 + bytes.Length);
    }
    
    public void WriteInt32(int value)
    {
        var buffer = _writer.GetSpan(9);
        
        buffer[0] = 2; // Int32 type
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1), 4);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(5), value);
        
        _writer.Advance(9);
    }
    
    public void WriteInt64(long value)
    {
        var buffer = _writer.GetSpan(13);
        
        buffer[0] = 3; // Int64 type
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1), 8);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(5), value);
        
        _writer.Advance(13);
    }
    
    public void WriteGuid(Guid value)
    {
        var buffer = _writer.GetSpan(21);
        
        buffer[0] = 4; // Guid type
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1), 16);
        value.TryWriteBytes(buffer.Slice(5));
        
        _writer.Advance(21);
    }
    
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        var buffer = _writer.GetSpan(5 + value.Length);
        
        buffer[0] = 5; // Bytes type
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1), value.Length);
        value.CopyTo(buffer.Slice(5));
        
        _writer.Advance(5 + value.Length);
    }
}