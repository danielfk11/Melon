using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace MelonMQ.Broker.Core;

public sealed record StreamEntry(
    long Offset,
    Guid MessageId,
    ReadOnlyMemory<byte> Body,
    long EnqueuedAt,
    long? ExpiresAt);

/// <summary>
/// Append-only, offset-based queue where messages are retained after consumers ACK them.
/// Multiple consumer groups each track their own offset independently, so different groups
/// all receive every message, while consumers within the same group share load.
/// </summary>
public sealed class StreamQueue : IDisposable
{
    public const string StreamMode = "stream";

    private readonly string _name;
    private readonly bool _durable;
    private readonly int _maxMessages;
    private readonly long? _maxAgeMs;
    private readonly ILogger _logger;
    private readonly string? _persistenceFilePath;

    // Entries are stored in arrival order; _baseOffset is the offset of _entries[0].
    private readonly List<StreamEntry> _entries = new();
    private long _baseOffset;
    private long _nextOffset;

    // Group/solo consumer committed offsets: consumerId → next-to-deliver offset
    private readonly ConcurrentDictionary<string, long> _consumerOffsets = new(StringComparer.Ordinal);

    // Signaled whenever a new entry is appended so waiting readers wake immediately.
    private volatile TaskCompletionSource _newEntrySignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public StreamQueue(
        string name,
        bool durable,
        int maxMessages,
        long? maxAgeMs,
        string? dataDirectory,
        ILogger logger)
    {
        _name = name;
        _durable = durable;
        _maxMessages = maxMessages > 0 ? maxMessages : 100_000;
        _maxAgeMs = maxAgeMs;
        _logger = logger;

        if (_durable && !string.IsNullOrEmpty(dataDirectory))
        {
            _persistenceFilePath = Path.Combine(dataDirectory, $"stream_{name}.log");
            _ = Task.Run(LoadPersistedEntriesAsync);
        }
    }

    public string Name => _name;
    public bool IsDurable => _durable;
    public long NextOffset => _nextOffset;
    public int Count { get { lock (_entries) { return _entries.Count; } } }

    // ── Write ────────────────────────────────────────────────────────────────

    public async Task<long> AppendAsync(
        ReadOnlyMemory<byte> body,
        Guid? messageId = null,
        long? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(_name);

        await _writeLock.WaitAsync(cancellationToken);
        StreamEntry entry;
        try
        {
            entry = new StreamEntry(
                _nextOffset++,
                messageId ?? Guid.NewGuid(),
                body,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                expiresAt);

            _entries.Add(entry);
            TrimRetention();

            if (_durable && _persistenceFilePath != null)
            {
                await PersistEntryAsync(entry);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        // Wake all waiting readers
        var old = Interlocked.Exchange(
            ref _newEntrySignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        old.TrySetResult();

        return entry.Offset;
    }

    // ── Consumer offset resolution ────────────────────────────────────────────

    /// <summary>
    /// Returns the offset at which <paramref name="consumerId"/> should start consuming.
    /// If the consumer has a committed offset, that takes precedence.
    /// Otherwise <paramref name="requestedOffset"/> is used: -1 = latest, 0 = beginning.
    /// </summary>
    public long ResolveStartOffset(string consumerId, long requestedOffset)
    {
        if (_consumerOffsets.TryGetValue(consumerId, out var committed))
            return committed;

        if (requestedOffset < 0)
            return _nextOffset; // latest — only new messages

        return Math.Max(0, requestedOffset);
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits until an entry at <paramref name="offset"/> is available and returns it.
    /// Returns null when cancelled or disconnected.
    /// Automatically skips expired entries.
    /// </summary>
    public async Task<StreamEntry?> ReadAtAsync(long offset, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _disposed) == 1)
                return null;

            StreamEntry? found = TryFindEntry(offset);

            if (found != null)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (found.ExpiresAt.HasValue && found.ExpiresAt <= now)
                {
                    // Skip expired; advance to next offset
                    return await ReadAtAsync(offset + 1, cancellationToken);
                }
                return found;
            }

            // Wait for the next append signal
            var signal = _newEntrySignal.Task;
            try
            {
                await signal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        return null;
    }

    // ── Commit ───────────────────────────────────────────────────────────────

    public void CommitOffset(string consumerId, long offset)
    {
        _consumerOffsets.AddOrUpdate(
            consumerId,
            offset + 1,
            (_, prev) => Math.Max(prev, offset + 1));
    }

    public long GetCommittedOffset(string consumerId) =>
        _consumerOffsets.TryGetValue(consumerId, out var v) ? v : -1;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private StreamEntry? TryFindEntry(long offset)
    {
        lock (_entries)
        {
            if (_entries.Count == 0) return null;
            var idx = (int)(offset - _baseOffset);
            if (idx < 0 || idx >= _entries.Count) return null;
            var candidate = _entries[idx];
            if (candidate.Offset == offset) return candidate;
            // Fallback (after trim gaps)
            foreach (var e in _entries)
                if (e.Offset == offset) return e;
            return null;
        }
    }

    private void TrimRetention()
    {
        // Already holding _writeLock — safe to mutate _entries
        // Trim by max count
        while (_entries.Count > _maxMessages)
        {
            _entries.RemoveAt(0);
            _baseOffset++;
        }

        // Trim by max age
        if (_maxAgeMs.HasValue)
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _maxAgeMs.Value;
            int i = 0;
            while (i < _entries.Count && _entries[i].EnqueuedAt < cutoff)
                i++;
            if (i > 0)
            {
                _entries.RemoveRange(0, i);
                _baseOffset += i;
            }
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task PersistEntryAsync(StreamEntry entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                op = "append",
                offset = entry.Offset,
                messageId = entry.MessageId,
                bodyBase64 = Convert.ToBase64String(entry.Body.Span),
                enqueuedAt = entry.EnqueuedAt,
                expiresAt = entry.ExpiresAt
            });
            await File.AppendAllTextAsync(_persistenceFilePath!, line + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist stream entry {Offset} for queue '{Name}'", entry.Offset, _name);
        }
    }

    private async Task LoadPersistedEntriesAsync()
    {
        if (_persistenceFilePath == null || !File.Exists(_persistenceFilePath))
            return;

        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lines = await File.ReadAllLinesAsync(_persistenceFilePath);

            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
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
                        continue; // expired

                    var entry = new StreamEntry(
                        root.GetProperty("offset").GetInt64(),
                        Guid.Parse(root.GetProperty("messageId").GetString()!),
                        Convert.FromBase64String(root.GetProperty("bodyBase64").GetString()!),
                        root.GetProperty("enqueuedAt").GetInt64(),
                        expiresAt);

                    lock (_entries)
                    {
                        _entries.Add(entry);
                        if (_nextOffset <= entry.Offset)
                            _nextOffset = entry.Offset + 1;
                    }
                }
                catch
                {
                    // Skip corrupt lines
                }
            }

            _logger.LogInformation(
                "Loaded {Count} stream entries for queue '{Name}'", _entries.Count, _name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load stream entries for queue '{Name}'", _name);
        }
    }

    public void DeletePersistenceFile()
    {
        if (_persistenceFilePath != null && File.Exists(_persistenceFilePath))
        {
            try { File.Delete(_persistenceFilePath); } catch { }
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        // Wake any readers so they exit
        _newEntrySignal.TrySetResult();
        _writeLock.Dispose();
    }
}
