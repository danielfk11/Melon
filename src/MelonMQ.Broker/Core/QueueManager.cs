using System.Collections.Concurrent;

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

    public QueueManager(string? dataDirectory, ILoggerFactory loggerFactory, int maxConnections = 1000, int channelCapacity = 10000, long compactionThresholdMB = 100)
    {
        _dataDirectory = dataDirectory;
        _loggerFactory = loggerFactory;
        _maxConnections = maxConnections;
        _channelCapacity = channelCapacity;
        _compactionThresholdMB = compactionThresholdMB;
        _logger = loggerFactory.CreateLogger<QueueManager>();

        if (!string.IsNullOrEmpty(_dataDirectory))
        {
            Directory.CreateDirectory(_dataDirectory);
        }
    }

    public MessageQueue DeclareQueue(string name, bool durable = false, string? deadLetterQueue = null, int? defaultTtlMs = null)
    {
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
                compactionThresholdMB: _compactionThresholdMB);
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
}