using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MelonMQ.Broker.Core;

public class QueueManager : IQueueManager
{
    private readonly ConcurrentDictionary<string, MessageQueue> _queues = new();
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
            // Clean up persistence file
            queue.DeletePersistenceFile();
            _logger.LogInformation("Deleted queue {QueueName}", name);
            return true;
        }
        return false;
    }

    public async Task CleanupExpiredMessages()
    {
        var tasks = _queues.Values.Select(q => q.CleanupExpiredInFlightMessages());
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
}