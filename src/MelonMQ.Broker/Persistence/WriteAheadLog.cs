using System.Buffers;
using System.Buffers.Binary;
using MelonMQ.Common;
using Microsoft.Extensions.Logging;

namespace MelonMQ.Broker.Persistence;

/// <summary>
/// Write-Ahead Log implementation for message persistence
/// </summary>
public interface IWriteAheadLog : IDisposable
{
    Task<long> AppendAsync(WalRecord record, CancellationToken cancellationToken = default);
    Task<WalRecord?> ReadAsync(long offset, CancellationToken cancellationToken = default);
    Task<IAsyncEnumerable<WalRecord>> ReadFromAsync(long offset, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task<long> GetCurrentOffsetAsync();
    Task CompactAsync(long beforeOffset, CancellationToken cancellationToken = default);
}

/// <summary>
/// WAL record types
/// </summary>
public enum WalRecordType : byte
{
    MessagePublish = 1,
    MessageAck = 2,
    MessageNack = 3,
    QueueDeclare = 10,
    QueueDelete = 11,
    ExchangeDeclare = 12,
    ExchangeDelete = 13,
    Binding = 14,
    Unbinding = 15,
    Checkpoint = 20
}

/// <summary>
/// A record in the Write-Ahead Log
/// </summary>
public readonly struct WalRecord
{
    public long Offset { get; init; }
    public WalRecordType Type { get; init; }
    public byte Flags { get; init; }
    public DateTime Timestamp { get; init; }
    public Guid MessageId { get; init; }
    public string QueueName { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }
    
    public WalRecord(WalRecordType type, Guid messageId, string queueName, ReadOnlyMemory<byte> data, byte flags = 0)
    {
        Offset = 0; // Set during append
        Type = type;
        Flags = flags;
        Timestamp = DateTime.UtcNow;
        MessageId = messageId;
        QueueName = queueName;
        Data = data;
    }
}

/// <summary>
/// Segmented Write-Ahead Log implementation
/// </summary>
public class SegmentedWal : IWriteAheadLog
{
    private const uint WalMagic = 0x4D57414C; // "MWAL"
    private const byte WalVersion = 1;
    private const int SegmentHeaderSize = 20; // Magic(4) + Version(1) + SegmentId(8) + Reserved(3) + HeaderCrc(4)
    private const int RecordHeaderSize = 45; // Length(4) + Crc(4) + Type(1) + Flags(1) + Timestamp(8) + MessageId(16) + QueueNameLen(1) + (QueueName) + Data
    
    private readonly string _dataDirectory;
    private readonly int _segmentSize;
    private readonly SyncPolicy _syncPolicy;
    private readonly ILogger<SegmentedWal> _logger;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<long, WalSegment> _segments = new();
    
    private long _currentSegmentId;
    private WalSegment? _currentSegment;
    private long _nextOffset = 1;
    private Timer? _flushTimer;
    
    public SegmentedWal(string dataDirectory, int segmentSize, SyncPolicy syncPolicy, ILogger<SegmentedWal> logger)
    {
        _dataDirectory = dataDirectory;
        _segmentSize = segmentSize;
        _syncPolicy = syncPolicy;
        _logger = logger;
        
        Directory.CreateDirectory(_dataDirectory);
        
        if (_syncPolicy == SyncPolicy.Batch)
        {
            _flushTimer = new Timer(async _ => await FlushAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }
    
    public async Task InitializeAsync()
    {
        _lock.EnterWriteLock();
        try
        {
            await RecoverAsync();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    private async Task RecoverAsync()
    {
        var segmentFiles = Directory.GetFiles(_dataDirectory, "*.wal")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(name => long.TryParse(name, out _))
            .Select(long.Parse)
            .OrderBy(id => id);
        
        foreach (var segmentId in segmentFiles)
        {
            try
            {
                var segment = await WalSegment.OpenAsync(GetSegmentPath(segmentId), segmentId, _logger);
                _segments[segmentId] = segment;
                _currentSegmentId = Math.Max(_currentSegmentId, segmentId);
                
                // Find the highest offset
                var lastRecord = await segment.GetLastRecordAsync();
                if (lastRecord.HasValue)
                {
                    _nextOffset = Math.Max(_nextOffset, lastRecord.Value.Offset + 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover segment {SegmentId}", segmentId);
            }
        }
        
        await EnsureCurrentSegmentAsync();
        
        _logger.LogInformation("WAL recovered with {SegmentCount} segments, next offset: {NextOffset}", 
            _segments.Count, _nextOffset);
    }
    
    private async Task EnsureCurrentSegmentAsync()
    {
        if (_currentSegment == null || _currentSegment.Size >= _segmentSize)
        {
            if (_currentSegment != null)
            {
                await _currentSegment.CloseAsync();
            }
            
            _currentSegmentId++;
            var segmentPath = GetSegmentPath(_currentSegmentId);
            _currentSegment = await WalSegment.CreateAsync(segmentPath, _currentSegmentId, _logger);
            _segments[_currentSegmentId] = _currentSegment;
        }
    }
    
    public async Task<long> AppendAsync(WalRecord record, CancellationToken cancellationToken = default)
    {
        _lock.EnterWriteLock();
        try
        {
            await EnsureCurrentSegmentAsync();
            
            var offset = _nextOffset++;
            var recordWithOffset = record with { Offset = offset };
            
            await _currentSegment!.AppendAsync(recordWithOffset, cancellationToken);
            
            if (_syncPolicy == SyncPolicy.Always)
            {
                await _currentSegment.FlushAsync(cancellationToken);
            }
            
            return offset;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public async Task<WalRecord?> ReadAsync(long offset, CancellationToken cancellationToken = default)
    {
        _lock.EnterReadLock();
        try
        {
            foreach (var segment in _segments.Values.OrderBy(s => s.SegmentId))
            {
                var record = await segment.ReadAsync(offset, cancellationToken);
                if (record.HasValue)
                    return record;
            }
            
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    public async Task<IAsyncEnumerable<WalRecord>> ReadFromAsync(long offset, CancellationToken cancellationToken = default)
    {
        var segments = new List<WalSegment>();
        
        _lock.EnterReadLock();
        try
        {
            segments.AddRange(_segments.Values.OrderBy(s => s.SegmentId));
        }
        finally
        {
            _lock.ExitReadLock();
        }
        
        return ReadFromSegmentsAsync(segments, offset, cancellationToken);
    }
    
    private async IAsyncEnumerable<WalRecord> ReadFromSegmentsAsync(
        IEnumerable<WalSegment> segments, 
        long fromOffset, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var segment in segments)
        {
            await foreach (var record in segment.ReadAllAsync(cancellationToken))
            {
                if (record.Offset >= fromOffset)
                    yield return record;
            }
        }
    }
    
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _lock.EnterReadLock();
        try
        {
            if (_currentSegment != null)
            {
                await _currentSegment.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    public Task<long> GetCurrentOffsetAsync()
    {
        _lock.EnterReadLock();
        try
        {
            return Task.FromResult(_nextOffset - 1);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    public async Task CompactAsync(long beforeOffset, CancellationToken cancellationToken = default)
    {
        var segmentsToDelete = new List<WalSegment>();
        
        _lock.EnterWriteLock();
        try
        {
            var segmentIds = _segments.Keys.OrderBy(id => id).ToList();
            
            foreach (var segmentId in segmentIds)
            {
                var segment = _segments[segmentId];
                var lastRecord = await segment.GetLastRecordAsync();
                
                if (lastRecord.HasValue && lastRecord.Value.Offset < beforeOffset && segment != _currentSegment)
                {
                    segmentsToDelete.Add(segment);
                    _segments.Remove(segmentId);
                }
                else
                {
                    break; // Can't delete segments with records >= beforeOffset
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        
        // Delete segments outside of lock
        foreach (var segment in segmentsToDelete)
        {
            try
            {
                await segment.DeleteAsync();
                _logger.LogInformation("Deleted WAL segment {SegmentId}", segment.SegmentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete WAL segment {SegmentId}", segment.SegmentId);
            }
        }
    }
    
    private string GetSegmentPath(long segmentId) => Path.Combine(_dataDirectory, $"{segmentId:D6}.wal");
    
    public void Dispose()
    {
        _flushTimer?.Dispose();
        
        _lock.EnterWriteLock();
        try
        {
            foreach (var segment in _segments.Values)
            {
                segment.Dispose();
            }
            _segments.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}

/// <summary>
/// Sync policy for WAL writes
/// </summary>
public enum SyncPolicy
{
    Never,      // No fsync (for development)
    Batch,      // Fsync every second
    Always      // Fsync on every write
}

/// <summary>
/// A single WAL segment file
/// </summary>
internal class WalSegment : IDisposable
{
    private readonly FileStream _fileStream;
    private readonly string _filePath;
    private readonly ILogger _logger;
    
    public long SegmentId { get; }
    public long Size => _fileStream.Length;
    
    private WalSegment(FileStream fileStream, string filePath, long segmentId, ILogger logger)
    {
        _fileStream = fileStream;
        _filePath = filePath;
        SegmentId = segmentId;
        _logger = logger;
    }
    
    public static async Task<WalSegment> CreateAsync(string filePath, long segmentId, ILogger logger)
    {
        var stream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 65536, FileOptions.Asynchronous);
        
        var segment = new WalSegment(stream, filePath, segmentId, logger);
        await segment.WriteHeaderAsync();
        
        return segment;
    }
    
    public static async Task<WalSegment> OpenAsync(string filePath, long segmentId, ILogger logger)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 65536, FileOptions.Asynchronous);
        
        var segment = new WalSegment(stream, filePath, segmentId, logger);
        await segment.ValidateHeaderAsync();
        
        return segment;
    }
    
    private async Task WriteHeaderAsync()
    {
        var headerBuffer = new byte[SegmentedWal.SegmentHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuffer, SegmentedWal.WalMagic);
        headerBuffer[4] = SegmentedWal.WalVersion;
        BinaryPrimitives.WriteInt64LittleEndian(headerBuffer.AsSpan(5), SegmentId);
        // Reserved bytes 13-15 are zero
        
        var headerCrc = Crc32C.Compute(headerBuffer.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(headerBuffer.AsSpan(16), headerCrc);
        
        await _fileStream.WriteAsync(headerBuffer);
        await _fileStream.FlushAsync();
    }
    
    private async Task ValidateHeaderAsync()
    {
        if (_fileStream.Length < SegmentedWal.SegmentHeaderSize)
            throw new InvalidDataException($"Invalid WAL segment header in {_filePath}");
        
        var headerBuffer = new byte[SegmentedWal.SegmentHeaderSize];
        _fileStream.Seek(0, SeekOrigin.Begin);
        await _fileStream.ReadExactlyAsync(headerBuffer);
        
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer);
        var version = headerBuffer[4];
        var fileSegmentId = BinaryPrimitives.ReadInt64LittleEndian(headerBuffer.AsSpan(5));
        var headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.AsSpan(16));
        
        if (magic != SegmentedWal.WalMagic)
            throw new InvalidDataException($"Invalid WAL magic in {_filePath}");
        
        if (version != SegmentedWal.WalVersion)
            throw new InvalidDataException($"Unsupported WAL version {version} in {_filePath}");
        
        if (fileSegmentId != SegmentId)
            throw new InvalidDataException($"Segment ID mismatch in {_filePath}");
        
        var computedCrc = Crc32C.Compute(headerBuffer.AsSpan(0, 16));
        if (computedCrc != headerCrc)
            throw new InvalidDataException($"Invalid header CRC in {_filePath}");
    }
    
    public async Task AppendAsync(WalRecord record, CancellationToken cancellationToken = default)
    {
        var queueNameBytes = System.Text.Encoding.UTF8.GetBytes(record.QueueName);
        var recordSize = SegmentedWal.RecordHeaderSize + queueNameBytes.Length + record.Data.Length;
        var buffer = new byte[recordSize];
        
        var span = buffer.AsSpan();
        var offset = 0;
        
        // Length (excluding length field itself)
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset), recordSize - 4);
        offset += 4;
        
        // CRC placeholder (will be filled later)
        offset += 4;
        
        // Type and Flags
        span[offset++] = (byte)record.Type;
        span[offset++] = record.Flags;
        
        // Timestamp
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), TimestampHelper.ToUnixTimestampMs(record.Timestamp));
        offset += 8;
        
        // Message ID
        record.MessageId.TryWriteBytes(span.Slice(offset));
        offset += 16;
        
        // Queue name
        span[offset++] = (byte)queueNameBytes.Length;
        queueNameBytes.CopyTo(span.Slice(offset));
        offset += queueNameBytes.Length;
        
        // Data
        record.Data.Span.CopyTo(span.Slice(offset));
        
        // Calculate and write CRC
        var crc = Crc32C.Compute(span.Slice(8)); // Skip length and CRC fields
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), crc);
        
        _fileStream.Seek(0, SeekOrigin.End);
        await _fileStream.WriteAsync(buffer, cancellationToken);
    }
    
    public async Task<WalRecord?> ReadAsync(long offset, CancellationToken cancellationToken = default)
    {
        await foreach (var record in ReadAllAsync(cancellationToken))
        {
            if (record.Offset == offset)
                return record;
        }
        return null;
    }
    
    public async IAsyncEnumerable<WalRecord> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _fileStream.Seek(SegmentedWal.SegmentHeaderSize, SeekOrigin.Begin);
        
        while (_fileStream.Position < _fileStream.Length)
        {
            var lengthBuffer = new byte[4];
            var bytesRead = await _fileStream.ReadAsync(lengthBuffer, cancellationToken);
            if (bytesRead < 4) break;
            
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length <= 0 || length > 16 * 1024 * 1024) break; // Sanity check
            
            var recordBuffer = new byte[length];
            lengthBuffer.CopyTo(recordBuffer, 0);
            
            bytesRead = await _fileStream.ReadAsync(recordBuffer.AsMemory(4), cancellationToken);
            if (bytesRead < length - 4) break;
            
            // Parse record
            var record = ParseRecord(recordBuffer);
            if (record.HasValue)
                yield return record.Value;
        }
    }
    
    private static WalRecord? ParseRecord(ReadOnlySpan<byte> buffer)
    {
        try
        {
            var offset = 0;
            
            // Skip length field
            offset += 4;
            
            // Verify CRC
            var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
            offset += 4;
            
            var computedCrc = Crc32C.Compute(buffer.Slice(offset));
            if (storedCrc != computedCrc)
                return null;
            
            // Parse fields
            var type = (WalRecordType)buffer[offset++];
            var flags = buffer[offset++];
            var timestamp = TimestampHelper.FromUnixTimestampMs(BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset)));
            offset += 8;
            
            var messageId = new Guid(buffer.Slice(offset, 16));
            offset += 16;
            
            var queueNameLen = buffer[offset++];
            var queueName = System.Text.Encoding.UTF8.GetString(buffer.Slice(offset, queueNameLen));
            offset += queueNameLen;
            
            var data = buffer.Slice(offset).ToArray();
            
            return new WalRecord
            {
                Type = type,
                Flags = flags,
                Timestamp = timestamp,
                MessageId = messageId,
                QueueName = queueName,
                Data = data
            };
        }
        catch
        {
            return null;
        }
    }
    
    public async Task<WalRecord?> GetLastRecordAsync()
    {
        WalRecord? lastRecord = null;
        
        await foreach (var record in ReadAllAsync())
        {
            lastRecord = record;
        }
        
        return lastRecord;
    }
    
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _fileStream.FlushAsync(cancellationToken);
    }
    
    public async Task CloseAsync()
    {
        await _fileStream.FlushAsync();
        _fileStream.Close();
    }
    
    public async Task DeleteAsync()
    {
        _fileStream.Close();
        File.Delete(_filePath);
    }
    
    public void Dispose()
    {
        _fileStream?.Dispose();
    }
}