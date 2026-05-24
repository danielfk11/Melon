using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace MelonMQ.Broker.Core;

public sealed record StreamEntry(
    long Offset,
    int Partition,
    long PartitionOffset,
    Guid MessageId,
    ReadOnlyMemory<byte> Body,
    long EnqueuedAt,
    long? ExpiresAt);

public sealed record StreamGroupLag(string Group, int Partition, long Lag, long EndOffset, long CommittedOffset);
public sealed record StreamPartitionLag(int Partition, long MaxGroupLag, long EndOffset);

/// <summary>
/// Append-only stream queue with partitioned retention and per-group committed offsets.
/// Offsets sent over the wire are composite values: (partition << 48) | partitionOffset.
/// </summary>
public sealed class StreamQueue : IDisposable
{
    public const string StreamMode = "stream";

    private const int PartitionOffsetBits = 48;
    private const long PartitionOffsetMask = (1L << PartitionOffsetBits) - 1;

    private readonly string _name;
    private readonly bool _durable;
    private readonly int _maxMessages;
    private readonly long? _maxAgeMs;
    private readonly ILogger _logger;
    private readonly string? _persistenceFilePath;
    private readonly string? _offsetPersistenceFilePath;
    private readonly object _offsetPersistenceLock = new();
    private readonly int _partitionCount;

    private sealed class PartitionState
    {
        public readonly List<StreamEntry> Entries = new();
        public long BaseOffset;
        public long NextOffset;
    }

    private readonly PartitionState[] _partitions;

    // consumerId -> (partition -> next-to-deliver partition offset)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, long>> _consumerOffsets =
        new(StringComparer.Ordinal);

    // group -> active member connectionIds
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groupMembers =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _groupMemberJoinOrder =
        new(StringComparer.Ordinal);
    private long _memberJoinSequence;

    // group -> generation (incremented whenever membership changes)
    private readonly ConcurrentDictionary<string, long> _groupGenerations =
        new(StringComparer.Ordinal);

    private volatile TaskCompletionSource _newEntrySignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public StreamQueue(
        string name,
        bool durable,
        int maxMessages,
        long? maxAgeMs,
        int partitionCount,
        string? dataDirectory,
        ILogger logger)
    {
        _name = name;
        _durable = durable;
        _maxMessages = maxMessages > 0 ? maxMessages : 100_000;
        _maxAgeMs = maxAgeMs;
        _logger = logger;
        _partitionCount = partitionCount <= 0 ? 1 : partitionCount;
        _partitions = Enumerable.Range(0, _partitionCount).Select(_ => new PartitionState()).ToArray();

        if (_durable && !string.IsNullOrEmpty(dataDirectory))
        {
            _persistenceFilePath = Path.Combine(dataDirectory, $"stream_{name}.log");
            _offsetPersistenceFilePath = Path.Combine(dataDirectory, $"stream_{name}.offsets.log");
            LoadPersistedState();
        }
    }

    public string Name => _name;
    public bool IsDurable => _durable;
    public int PartitionCount => _partitionCount;
    public long NextOffset => _partitionCount == 1 ? _partitions[0].NextOffset : _partitions.Sum(p => p.NextOffset);
    public int Count => _partitions.Sum(p => p.Entries.Count);

    public async Task<long> AppendAsync(
        ReadOnlyMemory<byte> body,
        Guid? messageId = null,
        long? expiresAt = null,
        string? partitionKey = null,
        int? partition = null,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(_name);

        await _writeLock.WaitAsync(cancellationToken);
        StreamEntry entry;
        try
        {
            var targetPartition = ResolvePartition(partition, partitionKey, messageId);
            var state = _partitions[targetPartition];

            var partitionOffset = state.NextOffset++;
            entry = new StreamEntry(
                EncodeOffset(targetPartition, partitionOffset),
                targetPartition,
                partitionOffset,
                messageId ?? Guid.NewGuid(),
                body,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                expiresAt);

            state.Entries.Add(entry);
            TrimRetention(state);

            if (_durable && _persistenceFilePath != null)
            {
                await PersistEntryAsync(entry);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        var old = Interlocked.Exchange(
            ref _newEntrySignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        old.TrySetResult();

        return entry.Offset;
    }

    public long ResolveStartOffset(string consumerId, long requestedOffset) =>
        ResolveStartOffset(consumerId, requestedOffset, partition: 0);

    public long ResolveStartOffset(string consumerId, long requestedOffset, int partition)
    {
        if (_consumerOffsets.TryGetValue(consumerId, out var byPartition) &&
            byPartition.TryGetValue(partition, out var committed))
        {
            return committed;
        }

        if (requestedOffset < 0)
            return _partitions[partition].NextOffset;

        return Math.Max(0, requestedOffset);
    }

    public IReadOnlyDictionary<int, long> ResolveStartOffsets(
        string consumerId,
        long requestedOffset,
        IReadOnlyCollection<int>? partitions = null)
    {
        var selected = partitions ?? Enumerable.Range(0, _partitionCount).ToArray();
        var result = new Dictionary<int, long>(selected.Count);
        foreach (var partition in selected)
        {
            result[partition] = ResolveStartOffset(consumerId, requestedOffset, partition);
        }
        return result;
    }

    public StreamEntry? TryReadAt(int partition, long partitionOffset)
    {
        var state = _partitions[partition];
        if (state.Entries.Count == 0)
            return null;

        if (partitionOffset < state.BaseOffset)
            partitionOffset = state.BaseOffset;

        var idx = (int)(partitionOffset - state.BaseOffset);
        if (idx >= 0 && idx < state.Entries.Count)
        {
            var candidate = state.Entries[idx];
            if (candidate.PartitionOffset == partitionOffset && !IsExpired(candidate))
                return candidate;
        }

        foreach (var entry in state.Entries)
        {
            if (entry.PartitionOffset >= partitionOffset && !IsExpired(entry))
                return entry;
        }

        return null;
    }

    public async Task<bool> WaitForNewEntryAsync(CancellationToken cancellationToken, TimeSpan? maxWait = null)
    {
        var signal = _newEntrySignal.Task;
        if (maxWait is null)
        {
            await signal.WaitAsync(cancellationToken);
            return true;
        }

        using var timeoutCts = new CancellationTokenSource(maxWait.Value);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await signal.WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<StreamEntry?> ReadAtAsync(long offset, CancellationToken cancellationToken)
    {
        if (!TryDecodeOffset(offset, out var partition, out var partitionOffset))
            return null;
        return await ReadAtAsync(partition, partitionOffset, cancellationToken);
    }

    public async Task<StreamEntry?> ReadAtAsync(int partition, long partitionOffset, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return null;

            var found = TryReadAt(partition, partitionOffset);
            if (found != null)
                return found;

            try
            {
                await WaitForNewEntryAsync(cancellationToken, TimeSpan.FromMilliseconds(500));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    public void CommitOffset(string consumerId, long offset)
    {
        if (!TryDecodeOffset(offset, out var partition, out var partitionOffset))
            return;
        CommitOffset(consumerId, partition, partitionOffset);
    }

    public void CommitOffset(string consumerId, int partition, long partitionOffset)
    {
        var byPartition = _consumerOffsets.GetOrAdd(
            consumerId,
            _ => new ConcurrentDictionary<int, long>());

        var nextOffset = byPartition.AddOrUpdate(
            partition,
            partitionOffset + 1,
            (_, prev) => Math.Max(prev, partitionOffset + 1));

        if (_durable && _offsetPersistenceFilePath != null)
        {
            PersistCommittedOffset(consumerId, partition, nextOffset);
        }
    }

    public long GetCommittedOffset(string consumerId) =>
        GetCommittedOffset(consumerId, partition: 0);

    public long GetCommittedOffset(string consumerId, int partition)
    {
        if (_consumerOffsets.TryGetValue(consumerId, out var byPartition) &&
            byPartition.TryGetValue(partition, out var value))
        {
            return value;
        }
        return -1;
    }

    public long RegisterGroupMember(string group, string memberId)
    {
        var members = _groupMembers.GetOrAdd(group, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        var joinOrder = _groupMemberJoinOrder.GetOrAdd(group, _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal));
        members[memberId] = 0;
        joinOrder.TryAdd(memberId, Interlocked.Increment(ref _memberJoinSequence));
        return _groupGenerations.AddOrUpdate(group, 1, (_, current) => current + 1);
    }

    public long UnregisterGroupMember(string group, string memberId)
    {
        if (_groupMembers.TryGetValue(group, out var members))
        {
            members.TryRemove(memberId, out _);
            if (members.IsEmpty)
                _groupMembers.TryRemove(group, out _);
        }

        if (_groupMemberJoinOrder.TryGetValue(group, out var joinOrder))
        {
            joinOrder.TryRemove(memberId, out _);
            if (joinOrder.IsEmpty)
                _groupMemberJoinOrder.TryRemove(group, out _);
        }

        return _groupGenerations.AddOrUpdate(group, 1, (_, current) => current + 1);
    }

    public long GetGroupGeneration(string group) => _groupGenerations.GetValueOrDefault(group, 0);

    public IReadOnlyList<int> GetAssignedPartitions(string group, string memberId)
    {
        if (!_groupMembers.TryGetValue(group, out var members) || members.IsEmpty || !members.ContainsKey(memberId))
            return Array.Empty<int>();

        var memberOrder = _groupMemberJoinOrder.TryGetValue(group, out var joinOrder)
            ? joinOrder
            : new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var memberList = members.Keys
            .OrderByDescending(member => memberOrder.GetValueOrDefault(member, 0))
            .ThenBy(member => member, StringComparer.Ordinal)
            .ToArray();
        var assigned = new List<int>(_partitionCount);
        for (int partition = 0; partition < _partitionCount; partition++)
        {
            var selected = memberList[partition % memberList.Length];
            if (string.Equals(selected, memberId, StringComparison.Ordinal))
                assigned.Add(partition);
        }

        return assigned;
    }

    public IReadOnlyList<StreamGroupLag> GetGroupLagMetrics()
    {
        var result = new List<StreamGroupLag>();
        var groups = _consumerOffsets.Keys
            .Where(k => k.StartsWith("group:", StringComparison.Ordinal))
            .Select(k => k["group:".Length..])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var group in groups)
        {
            var consumerId = $"group:{group}";
            for (int partition = 0; partition < _partitionCount; partition++)
            {
                var end = _partitions[partition].NextOffset;
                var committed = Math.Max(0, GetCommittedOffset(consumerId, partition));
                var lag = Math.Max(0, end - committed);
                result.Add(new StreamGroupLag(group, partition, lag, end, committed));
            }
        }

        return result;
    }

    public IReadOnlyList<StreamPartitionLag> GetPartitionLagMetrics()
    {
        var groupLag = GetGroupLagMetrics();
        var result = new List<StreamPartitionLag>(_partitionCount);
        for (int partition = 0; partition < _partitionCount; partition++)
        {
            var end = _partitions[partition].NextOffset;
            var maxGroupLag = groupLag
                .Where(x => x.Partition == partition)
                .Select(x => x.Lag)
                .DefaultIfEmpty(0)
                .Max();
            result.Add(new StreamPartitionLag(partition, maxGroupLag, end));
        }
        return result;
    }

    public bool TryDecodeOffset(long offset, out int partition, out long partitionOffset)
    {
        if (_partitionCount == 1)
        {
            partition = 0;
            partitionOffset = offset;
            return true;
        }

        partition = (int)((ulong)offset >> PartitionOffsetBits);
        partitionOffset = offset & PartitionOffsetMask;
        if (partition < 0 || partition >= _partitionCount)
        {
            partition = 0;
            partitionOffset = offset;
            return true;
        }

        return true;
    }

    public void DeletePersistenceFile()
    {
        if (_persistenceFilePath != null && File.Exists(_persistenceFilePath))
        {
            try { File.Delete(_persistenceFilePath); } catch { }
        }

        if (_offsetPersistenceFilePath != null && File.Exists(_offsetPersistenceFilePath))
        {
            try { File.Delete(_offsetPersistenceFilePath); } catch { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        _newEntrySignal.TrySetResult();
        _writeLock.Dispose();
    }

    private static ulong StableHash(string value)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }

    private int ResolvePartition(int? partition, string? partitionKey, Guid? messageId)
    {
        if (_partitionCount == 1)
            return 0;

        if (partition.HasValue)
            return Math.Abs(partition.Value) % _partitionCount;

        var key = !string.IsNullOrWhiteSpace(partitionKey)
            ? partitionKey!
            : (messageId?.ToString() ?? Guid.NewGuid().ToString("N"));
        return (int)(StableHash(key) % (ulong)_partitionCount);
    }

    private static long EncodeOffset(int partition, long partitionOffset) =>
        ((long)partition << PartitionOffsetBits) | (partitionOffset & PartitionOffsetMask);

    private static bool IsExpired(StreamEntry entry)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return entry.ExpiresAt.HasValue && entry.ExpiresAt.Value <= now;
    }

    private void TrimRetention(PartitionState state)
    {
        while (state.Entries.Count > _maxMessages)
        {
            state.Entries.RemoveAt(0);
            state.BaseOffset++;
        }

        if (_maxAgeMs.HasValue)
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _maxAgeMs.Value;
            int i = 0;
            while (i < state.Entries.Count && state.Entries[i].EnqueuedAt < cutoff)
                i++;
            if (i > 0)
            {
                state.Entries.RemoveRange(0, i);
                state.BaseOffset += i;
            }
        }
    }

    private async Task PersistEntryAsync(StreamEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                op = "append",
                offset = entry.Offset,
                partition = entry.Partition,
                partitionOffset = entry.PartitionOffset,
                messageId = entry.MessageId,
                bodyBase64 = Convert.ToBase64String(entry.Body.Span),
                enqueuedAt = entry.EnqueuedAt,
                expiresAt = entry.ExpiresAt
            });
            await using var stream = new FileStream(
                _persistenceFilePath!,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist stream entry {Offset} for queue '{Name}'", entry.Offset, _name);
        }
    }

    private void LoadPersistedState()
    {
        try
        {
            LoadPersistedEntries();
            LoadPersistedOffsets();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted stream state for queue '{Name}'", _name);
        }
    }

    private void LoadPersistedEntries()
    {
        if (_persistenceFilePath == null || !File.Exists(_persistenceFilePath))
            return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var line in File.ReadLines(_persistenceFilePath).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.GetProperty("op").GetString() != "append") continue;

                long? expiresAt = null;
                if (root.TryGetProperty("expiresAt", out var expEl) &&
                    expEl.ValueKind != JsonValueKind.Null)
                {
                    expiresAt = expEl.GetInt64();
                }
                if (expiresAt.HasValue && expiresAt <= nowMs)
                    continue;

                int partition = 0;
                if (root.TryGetProperty("partition", out var partitionEl) && partitionEl.ValueKind != JsonValueKind.Null)
                    partition = partitionEl.GetInt32();

                long offset = root.GetProperty("offset").GetInt64();
                long partitionOffset;
                if (root.TryGetProperty("partitionOffset", out var partitionOffsetEl) && partitionOffsetEl.ValueKind != JsonValueKind.Null)
                {
                    partitionOffset = partitionOffsetEl.GetInt64();
                }
                else if (partition == 0)
                {
                    partitionOffset = offset;
                }
                else
                {
                    TryDecodeOffset(offset, out _, out partitionOffset);
                }

                if (partition < 0 || partition >= _partitionCount)
                    partition = 0;

                var entry = new StreamEntry(
                    EncodeOffset(partition, partitionOffset),
                    partition,
                    partitionOffset,
                    Guid.Parse(root.GetProperty("messageId").GetString()!),
                    Convert.FromBase64String(root.GetProperty("bodyBase64").GetString()!),
                    root.GetProperty("enqueuedAt").GetInt64(),
                    expiresAt);

                var state = _partitions[partition];
                state.Entries.Add(entry);
                if (state.NextOffset <= partitionOffset)
                    state.NextOffset = partitionOffset + 1;
            }
            catch
            {
                // Skip corrupt lines
            }
        }

        _logger.LogInformation("Loaded {Count} stream entries for queue '{Name}'", Count, _name);
    }

    private void LoadPersistedOffsets()
    {
        if (_offsetPersistenceFilePath == null || !File.Exists(_offsetPersistenceFilePath))
            return;

        foreach (var line in File.ReadLines(_offsetPersistenceFilePath).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.GetProperty("op").GetString() != "commit") continue;

                var consumerId = root.GetProperty("consumerId").GetString();
                if (string.IsNullOrWhiteSpace(consumerId)) continue;

                int partition = 0;
                if (root.TryGetProperty("partition", out var partitionEl) && partitionEl.ValueKind != JsonValueKind.Null)
                    partition = partitionEl.GetInt32();

                var nextOffset = root.GetProperty("nextOffset").GetInt64();
                var byPartition = _consumerOffsets.GetOrAdd(
                    consumerId,
                    _ => new ConcurrentDictionary<int, long>());
                byPartition.AddOrUpdate(partition, nextOffset, (_, previous) => Math.Max(previous, nextOffset));
            }
            catch
            {
                // Skip corrupt lines
            }
        }

        _logger.LogInformation("Loaded {Count} committed stream offsets for queue '{Name}'", _consumerOffsets.Count, _name);
    }

    private void PersistCommittedOffset(string consumerId, int partition, long nextOffset)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                op = "commit",
                consumerId,
                partition,
                nextOffset
            });

            lock (_offsetPersistenceLock)
            {
                File.AppendAllText(_offsetPersistenceFilePath!, line + "\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist committed offset {NextOffset} for consumer '{ConsumerId}' on queue '{Name}'",
                nextOffset,
                consumerId,
                _name);
        }
    }
}
