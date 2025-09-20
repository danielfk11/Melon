using System.Collections.Concurrent;

namespace MelonMQ.Broker.Core;

public class QueueManager
{
    private readonly ConcurrentDictionary<string, MessageQueue> _queues = new();
    private readonly string? _dataDirectory;
    private readonly ILogger<QueueManager> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public QueueManager(string? dataDirectory, ILoggerFactory loggerFactory)
    {
        _dataDirectory = dataDirectory;
        _loggerFactory = loggerFactory;
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

            var queue = new MessageQueue(config, _dataDirectory, _loggerFactory.CreateLogger<MessageQueue>());
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