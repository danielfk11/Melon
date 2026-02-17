namespace MelonMQ.Broker.Core;

/// <summary>
/// Background service that periodically cleans up empty, inactive queues.
/// Prevents resource exhaustion from dynamically created queues that are no longer in use.
/// </summary>
public class QueueGarbageCollector : BackgroundService
{
    private readonly QueueManager _queueManager;
    private readonly MelonMQConfiguration _config;
    private readonly ILogger<QueueGarbageCollector> _logger;

    public QueueGarbageCollector(
        QueueManager queueManager,
        MelonMQConfiguration config,
        ILogger<QueueGarbageCollector> logger)
    {
        _queueManager = queueManager;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.QueueGC.Enabled)
        {
            _logger.LogInformation("Queue Garbage Collector is DISABLED");
            return;
        }

        var interval = TimeSpan.FromSeconds(_config.QueueGC.IntervalSeconds);
        
        _logger.LogInformation(
            "Queue Garbage Collector started (interval: {Interval}s, threshold: {Threshold}s, onlyNonDurable: {OnlyNonDurable}, maxQueues: {MaxQueues})",
            _config.QueueGC.IntervalSeconds,
            _config.QueueGC.InactiveThresholdSeconds,
            _config.QueueGC.OnlyNonDurable,
            _config.QueueGC.MaxQueues == 0 ? "unlimited" : _config.QueueGC.MaxQueues);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);

                // Cleanup expired in-flight messages  
                await _queueManager.CleanupExpiredMessages();

                // Cleanup inactive empty queues
                var removed = _queueManager.CleanupInactiveQueues(
                    _config.QueueGC.InactiveThresholdSeconds,
                    _config.QueueGC.OnlyNonDurable);

                if (removed > 0)
                {
                    MelonMetrics.Instance.IncrementCounter("gc.queues_removed", removed);
                }

                MelonMetrics.Instance.IncrementCounter("gc.runs");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Queue Garbage Collector");
            }
        }

        _logger.LogInformation("Queue Garbage Collector stopped");
    }
}
