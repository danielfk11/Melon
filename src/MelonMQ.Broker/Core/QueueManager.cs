using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MelonMQ.Broker.Core;

public class QueueManager : IQueueManager, IDisposable
{
    private readonly ConcurrentDictionary<string, MessageQueue> _queues = new();
    private readonly ConcurrentDictionary<string, StreamQueue> _streamQueues = new();
    // sourceQueueName → set of group-queue names (e.g. "_grp:tasks:workers")
    private readonly ConcurrentDictionary<string, HashSet<string>> _groupQueuesBySource = new();
    private readonly string? _dataDirectory;
    private readonly ILogger<QueueManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly int _maxConnections;
    private readonly int _channelCapacity;
    private readonly long _compactionThresholdMB;
    private readonly int _batchFlushMs;
    private readonly int _maxQueues;

    public QueueManager(string? dataDirectory, ILoggerFactory loggerFactory, int maxConnections = 1000, int channelCapacity = 10000, long compactionThresholdMB = 100, int batchFlushMs = 10, int maxQueues = 0)
    {
        _dataDirectory = dataDirectory;
        _loggerFactory = loggerFactory;
        _maxConnections = maxConnections;
        _channelCapacity = channelCapacity;
        _compactionThresholdMB = compactionThresholdMB;
        _batchFlushMs = batchFlushMs;
        _maxQueues = maxQueues;
        _logger = loggerFactory.CreateLogger<QueueManager>();

        if (!string.IsNullOrEmpty(_dataDirectory))
        {
            Directory.CreateDirectory(_dataDirectory);
        }
    }

    public int QueueCount => _queues.Count;

    private static readonly Regex ValidQueueNamePattern = new(@"^[a-zA-Z0-9\-_.]+$", RegexOptions.Compiled);

    private static void ValidateQueueName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Queue name cannot be empty.");

        if (name.Length > 255)
            throw new ArgumentException("Queue name cannot exceed 255 characters.");

        if (name.Contains(".."))
            throw new ArgumentException("Queue name cannot contain '..'.");

        if (!ValidQueueNamePattern.IsMatch(name))
            throw new ArgumentException($"Queue name '{name}' contains invalid characters. Only alphanumeric, hyphens, underscores, and dots are allowed.");
    }

    public MessageQueue DeclareQueue(string name, bool durable = false, string? deadLetterQueue = null, int? defaultTtlMs = null)
    {
        ValidateQueueName(name);
        if (deadLetterQueue != null) ValidateQueueName(deadLetterQueue);

        // Enforce max queues limit (check before creating)
        if (_maxQueues > 0 && _queues.Count >= _maxQueues && !_queues.ContainsKey(name))
        {
            throw new InvalidOperationException($"Maximum queue limit ({_maxQueues}) reached. Delete unused queues first.");
        }

        return _queues.GetOrAdd(name, _ =>
        {
            var config = new QueueConfiguration
            {
                Name = name,
                Durable = durable,
                DeadLetterQueue = deadLetterQueue,
                DefaultTtlMs = defaultTtlMs
            };

            // Pass queue resolver so DLQ routing works
            var queue = new MessageQueue(
                config, 
                _dataDirectory, 
                _loggerFactory.CreateLogger<MessageQueue>(),
                queueResolver: GetQueue,
                channelCapacity: _channelCapacity,
                compactionThresholdMB: _compactionThresholdMB,
                batchFlushMs: _batchFlushMs);
            _logger.LogInformation("Declared queue {QueueName} (durable: {Durable})", name, durable);
            return queue;
        });
    }

    public MessageQueue? GetQueue(string name)
    {
        return _queues.TryGetValue(name, out var queue) ? queue : null;
    }

    public IEnumerable<MessageQueue> GetAllQueues()
    {
        return _queues.Values;
    }

    public bool DeleteQueue(string name)
    {
        if (_queues.TryRemove(name, out var queue))
        {
            queue.Dispose();
            // Clean up persistence file
            queue.DeletePersistenceFile();
            _logger.LogInformation("Deleted queue {QueueName}", name);
            return true;
        }
        return false;
    }

    public async Task CleanupExpiredMessages()
    {
        var tasks = _queues.Values.Select(async q =>
        {
            await q.CleanupExpiredInFlightMessages();
            await q.DrainExpiredPendingAsync();
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Removes empty queues that have been idle for longer than the specified threshold.
    /// </summary>
    public int CleanupInactiveQueues(int inactiveThresholdSeconds, bool onlyNonDurable = false)
    {
        var thresholdMs = (long)inactiveThresholdSeconds * 1000;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var removedCount = 0;

        var candidates = _queues.Values
            .Where(q => q.IsEmpty && (now - q.LastActivityAt) > thresholdMs)
            .Where(q => !onlyNonDurable || !q.IsDurable)
            .Select(q => q.Name)
            .ToList();

        foreach (var queueName in candidates)
        {
            if (_queues.TryRemove(queueName, out var queue))
            {
                queue.Dispose();
                queue.DeletePersistenceFile();
                var idleMinutes = (now - queue.LastActivityAt) / 60000.0;
                _logger.LogInformation(
                    "GC: Deleted inactive queue '{QueueName}' (idle for {IdleMinutes:F1} minutes, durable: {Durable})",
                    queueName, idleMinutes, queue.IsDurable);
                removedCount++;
            }
        }

        if (removedCount > 0)
        {
            _logger.LogInformation("GC: Cleaned up {Count} inactive queues. Remaining: {Remaining}", 
                removedCount, _queues.Count);
        }

        return removedCount;
    }

    /// <summary>
    /// Returns queues that are empty and have been idle for the specified threshold.
    /// </summary>
    public IEnumerable<(string Name, bool Durable, long IdleTimeMs)> GetInactiveQueues(int inactiveThresholdSeconds)
    {
        var thresholdMs = (long)inactiveThresholdSeconds * 1000;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _queues.Values
            .Where(q => q.IsEmpty && (now - q.LastActivityAt) > thresholdMs)
            .Select(q => (q.Name, q.IsDurable, q.IdleTimeMs))
            .OrderByDescending(q => q.IdleTimeMs);
    }

    // ── Stream queues ─────────────────────────────────────────────────────────

    public StreamQueue DeclareStreamQueue(
        string name,
        bool durable,
        int maxMessages = 100_000,
        long? maxAgeMs = null)
    {
        ValidateQueueName(name);
        return _streamQueues.GetOrAdd(name, _ =>
        {
            var q = new StreamQueue(
                name,
                durable,
                maxMessages,
                maxAgeMs,
                _dataDirectory,
                _loggerFactory.CreateLogger<StreamQueue>());
            _logger.LogInformation("Declared stream queue '{Name}' (durable: {Durable})", name, durable);
            return q;
        });
    }

    public StreamQueue? GetStreamQueue(string name) =>
        _streamQueues.TryGetValue(name, out var q) ? q : null;

    public bool IsStreamQueue(string name) => _streamQueues.ContainsKey(name);

    public bool DeleteStreamQueue(string name)
    {
        if (_streamQueues.TryRemove(name, out var q))
        {
            q.DeletePersistenceFile();
            q.Dispose();
            _logger.LogInformation("Deleted stream queue '{Name}'", name);
            return true;
        }
        return false;
    }

    // ── Consumer group queues ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the internal name used for a group queue derived from a source queue.
    /// </summary>
    public static string GetGroupQueueName(string sourceQueue, string groupName) =>
        $"_grp:{sourceQueue}:{groupName}";

    /// <summary>
    /// Creates (or returns) the virtual queue used by <paramref name="groupName"/>
    /// when consuming from <paramref name="sourceQueue"/>.
    /// The group queue is auto-durable when the source queue is durable.
    /// </summary>
    public MessageQueue DeclareGroupQueue(string sourceQueue, string groupName, bool durable)
    {
        var groupQueueName = GetGroupQueueName(sourceQueue, groupName);

        // Internal group queue — bypass public name validation (colons are intentional separators)
        var queue = _queues.GetOrAdd(groupQueueName, _ =>
        {
            var config = new QueueConfiguration { Name = groupQueueName, Durable = durable };
            return new MessageQueue(
                config,
                _dataDirectory,
                _loggerFactory.CreateLogger<MessageQueue>(),
                queueResolver: GetQueue,
                channelCapacity: _channelCapacity,
                compactionThresholdMB: _compactionThresholdMB,
                batchFlushMs: _batchFlushMs);
        });

        _groupQueuesBySource.AddOrUpdate(
            sourceQueue,
            _ => new HashSet<string>(StringComparer.Ordinal) { groupQueueName },
            (_, set) =>
            {
                lock (set) { set.Add(groupQueueName); }
                return set;
            });

        return queue;
    }

    /// <summary>
    /// Returns all active group queues fanning out from <paramref name="sourceQueue"/>.
    /// </summary>
    public IEnumerable<MessageQueue> GetGroupQueues(string sourceQueue)
    {
        if (!_groupQueuesBySource.TryGetValue(sourceQueue, out var names))
            return Enumerable.Empty<MessageQueue>();

        HashSet<string> snapshot;
        lock (names) { snapshot = new HashSet<string>(names, StringComparer.Ordinal); }

        return snapshot
            .Select(n => _queues.TryGetValue(n, out var q) ? q : null)
            .Where(q => q != null)!;
    }

    public void Dispose()
    {
        foreach (var queue in _queues.Values)
            queue.Dispose();
        foreach (var q in _streamQueues.Values)
            q.Dispose();
    }
}