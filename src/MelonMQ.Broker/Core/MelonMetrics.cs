using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MelonMQ.Broker.Core;

public class MelonMetrics
{
    public const string MeterName = "MelonMQ.Broker";
    public const string ActivitySourceName = "MelonMQ.Broker";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> MessagesPublishedCounter = Meter.CreateCounter<long>(
        "melonmq_messages_published_total",
        unit: "messages",
        description: "Total number of messages published.");

    private static readonly Counter<long> MessagesConsumedCounter = Meter.CreateCounter<long>(
        "melonmq_messages_consumed_total",
        unit: "messages",
        description: "Total number of messages consumed.");

    private static readonly Counter<long> ConnectionOpenedCounter = Meter.CreateCounter<long>(
        "melonmq_connections_opened_total",
        unit: "connections",
        description: "Total opened connections.");

    private static readonly Counter<long> ConnectionClosedCounter = Meter.CreateCounter<long>(
        "melonmq_connections_closed_total",
        unit: "connections",
        description: "Total closed connections.");

    private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
        "melonmq_errors_total",
        unit: "errors",
        description: "Total processing errors.");

    private static readonly Counter<long> MessagesRejectedCounter = Meter.CreateCounter<long>(
        "melonmq_messages_rejected_total",
        unit: "messages",
        description: "Total messages rejected before enqueue.");

    private static readonly Counter<long> ClusterReplicationCounter = Meter.CreateCounter<long>(
        "melonmq_cluster_replication_total",
        unit: "operations",
        description: "Total cluster replication operations.");

    private static readonly Histogram<double> OperationDurationHistogram = Meter.CreateHistogram<double>(
        "melonmq_operation_duration_ms",
        unit: "ms",
        description: "Operation execution duration in milliseconds.");

    private static readonly Histogram<long> MessageSizeHistogram = Meter.CreateHistogram<long>(
        "melonmq_message_size_bytes",
        unit: "bytes",
        description: "Message payload size in bytes.");
    
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, (long Total, int Count)> _timings = new();
    private readonly ConcurrentDictionary<string, long> _streamGroupPartitionLag = new();
    private readonly ConcurrentDictionary<string, long> _streamPartitionLag = new();
    private readonly QueueManager? _queueManager;
    private readonly ObservableGauge<long> _streamGroupPartitionLagGauge;
    private readonly ObservableGauge<long> _streamPartitionLagGauge;
    private readonly ObservableGauge<long>? _queuePendingMessagesGauge;
    private readonly ObservableGauge<long>? _queueInFlightMessagesGauge;
    private readonly ObservableGauge<long>? _queuesTotalGauge;
    
    public static MelonMetrics Instance { get; } = new();

    public MelonMetrics(QueueManager? queueManager = null)
    {
        _queueManager = queueManager;

        _streamGroupPartitionLagGauge = Meter.CreateObservableGauge<long>(
            "melonmq_stream_group_partition_lag",
            ObserveStreamGroupPartitionLag,
            unit: "messages",
            description: "Current lag by stream queue/group/partition.");

        _streamPartitionLagGauge = Meter.CreateObservableGauge<long>(
            "melonmq_stream_partition_lag_max",
            ObserveStreamPartitionLag,
            unit: "messages",
            description: "Current max lag by stream queue partition across groups.");

        if (_queueManager != null)
        {
            _queuePendingMessagesGauge = Meter.CreateObservableGauge<long>(
                "melonmq_queue_pending_messages",
                ObserveQueuePendingMessages,
                unit: "messages",
                description: "Current pending messages by queue.");

            _queueInFlightMessagesGauge = Meter.CreateObservableGauge<long>(
                "melonmq_queue_inflight_messages",
                ObserveQueueInFlightMessages,
                unit: "messages",
                description: "Current in-flight messages by queue.");

            _queuesTotalGauge = Meter.CreateObservableGauge<long>(
                "melonmq_queues_total",
                ObserveQueuesTotal,
                unit: "queues",
                description: "Current number of classic queues.");
        }
    }

    public void IncrementCounter(string name, long value = 1)
    {
        _counters.AddOrUpdate(name, value, (key, current) => current + value);
    }

    public void RecordTiming(string name, TimeSpan duration)
    {
        var ms = (long)duration.TotalMilliseconds;
        _timings.AddOrUpdate(name, 
            (ms, 1), 
            (key, current) => (current.Total + ms, current.Count + 1));
    }

    public long GetCounter(string name)
    {
        return _counters.GetValueOrDefault(name, 0);
    }

    public double GetAverageTiming(string name)
    {
        if (_timings.TryGetValue(name, out var timing) && timing.Count > 0)
        {
            return (double)timing.Total / timing.Count;
        }
        return 0;
    }

    public Dictionary<string, object> GetAllMetrics()
    {
        var result = new Dictionary<string, object>();

        // Add counters
        foreach (var counter in _counters)
        {
            result[$"counter.{counter.Key}"] = counter.Value;
        }

        // Add average timings
        foreach (var timing in _timings)
        {
            if (timing.Value.Count > 0)
            {
                result[$"timing.{timing.Key}.avg_ms"] = (double)timing.Value.Total / timing.Value.Count;
                result[$"timing.{timing.Key}.count"] = timing.Value.Count;
            }
        }

        foreach (var lag in _streamGroupPartitionLag)
        {
            result[$"lag.stream.group_partition.{lag.Key}"] = lag.Value;
        }

        foreach (var lag in _streamPartitionLag)
        {
            result[$"lag.stream.partition.{lag.Key}"] = lag.Value;
        }

        return result;
    }

    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return ActivitySource.StartActivity(name, kind);
    }

    // Common metrics
    public void RecordMessagePublished(string queueName, TimeSpan duration, int payloadSizeBytes = 0, string source = "tcp", bool replicated = false)
    {
        IncrementCounter("messages.published.total");
        IncrementCounter($"messages.published.queue.{queueName}");
        RecordTiming("message.publish.duration", duration);

        var tags = new TagList
        {
            { "queue", queueName },
            { "source", source },
            { "replicated", replicated }
        };

        MessagesPublishedCounter.Add(1, tags);
        OperationDurationHistogram.Record(duration.TotalMilliseconds, tags);
        if (payloadSizeBytes > 0)
        {
            MessageSizeHistogram.Record(payloadSizeBytes, tags);
        }
    }

    public void RecordMessageConsumed(string queueName, TimeSpan duration, bool redelivered = false, string source = "tcp")
    {
        IncrementCounter("messages.consumed.total");
        IncrementCounter($"messages.consumed.queue.{queueName}");
        RecordTiming("message.consume.duration", duration);

        var tags = new TagList
        {
            { "queue", queueName },
            { "source", source },
            { "redelivered", redelivered }
        };

        MessagesConsumedCounter.Add(1, tags);
        OperationDurationHistogram.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordMessageRejected(string queueName, string reason, string source = "tcp")
    {
        IncrementCounter("messages.rejected.total");
        IncrementCounter($"messages.rejected.reason.{reason}");
        IncrementCounter($"messages.rejected.queue.{queueName}");

        var tags = new TagList
        {
            { "queue", queueName },
            { "reason", reason },
            { "source", source }
        };

        MessagesRejectedCounter.Add(1, tags);
    }

    public void RecordConnectionOpened(string transport = "tcp")
    {
        IncrementCounter("connections.opened");
        ConnectionOpenedCounter.Add(1, new TagList { { "transport", transport } });
    }

    public void RecordConnectionClosed(string transport = "tcp")
    {
        IncrementCounter("connections.closed");
        ConnectionClosedCounter.Add(1, new TagList { { "transport", transport } });
    }

    public void RecordError(string operation, string errorType, string? queueName = null)
    {
        IncrementCounter("errors.total");
        IncrementCounter($"errors.operation.{operation}");
        IncrementCounter($"errors.type.{errorType}");

        var tags = new TagList
        {
            { "operation", operation },
            { "error_type", errorType }
        };

        if (!string.IsNullOrWhiteSpace(queueName))
        {
            tags.Add("queue", queueName);
        }

        ErrorCounter.Add(1, tags);
    }

    public void RecordClusterReplication(string operation, string targetNode, bool success, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "target_node", targetNode },
            { "status", success ? "success" : "failure" }
        };

        ClusterReplicationCounter.Add(1, tags);
        OperationDurationHistogram.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordClusterLeadership(bool isLeader, bool hasQuorum, int activeNodes)
    {
        IncrementCounter("cluster.leader", isLeader ? 1 : 0);
        IncrementCounter("cluster.quorum", hasQuorum ? 1 : 0);
        IncrementCounter("cluster.active_nodes", activeNodes);
    }

    public void ReplaceStreamLagMetrics(
        string queueName,
        IReadOnlyCollection<StreamGroupLag> groupLagMetrics,
        IReadOnlyCollection<StreamPartitionLag> partitionLagMetrics)
    {
        var groupPrefix = $"{queueName}|";
        foreach (var key in _streamGroupPartitionLag.Keys.Where(k => k.StartsWith(groupPrefix, StringComparison.Ordinal)).ToArray())
            _streamGroupPartitionLag.TryRemove(key, out _);

        foreach (var metric in groupLagMetrics)
            _streamGroupPartitionLag[$"{queueName}|{metric.Group}|{metric.Partition}"] = metric.Lag;

        foreach (var key in _streamPartitionLag.Keys.Where(k => k.StartsWith(groupPrefix, StringComparison.Ordinal)).ToArray())
            _streamPartitionLag.TryRemove(key, out _);

        foreach (var metric in partitionLagMetrics)
            _streamPartitionLag[$"{queueName}|{metric.Partition}"] = metric.MaxGroupLag;

        IncrementCounter($"streams.lag.updates.queue.{queueName}");
    }

    public IReadOnlyDictionary<string, long> GetStreamGroupPartitionLagSnapshot() =>
        new Dictionary<string, long>(_streamGroupPartitionLag);

    public IReadOnlyDictionary<string, long> GetStreamPartitionLagSnapshot() =>
        new Dictionary<string, long>(_streamPartitionLag);

    private IEnumerable<Measurement<long>> ObserveStreamGroupPartitionLag()
    {
        foreach (var item in _streamGroupPartitionLag)
        {
            var parts = item.Key.Split('|');
            if (parts.Length != 3) continue;
            if (!int.TryParse(parts[2], out var partition)) continue;
            yield return new Measurement<long>(
                item.Value,
                new TagList
                {
                    { "queue", parts[0] },
                    { "group", parts[1] },
                    { "partition", partition }
                });
        }
    }

    private IEnumerable<Measurement<long>> ObserveStreamPartitionLag()
    {
        foreach (var item in _streamPartitionLag)
        {
            var parts = item.Key.Split('|');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[1], out var partition)) continue;
            yield return new Measurement<long>(
                item.Value,
                new TagList
                {
                    { "queue", parts[0] },
                    { "partition", partition }
                });
        }
    }

    private IEnumerable<Measurement<long>> ObserveQueuePendingMessages()
    {
        if (_queueManager == null)
            yield break;

        foreach (var queue in _queueManager.GetAllQueues())
        {
            yield return new Measurement<long>(
                queue.PendingCount,
                CreateQueueTagList(queue));
        }
    }

    private IEnumerable<Measurement<long>> ObserveQueueInFlightMessages()
    {
        if (_queueManager == null)
            yield break;

        foreach (var queue in _queueManager.GetAllQueues())
        {
            yield return new Measurement<long>(
                queue.InFlightCount,
                CreateQueueTagList(queue));
        }
    }

    private Measurement<long> ObserveQueuesTotal()
    {
        return new Measurement<long>(_queueManager?.QueueCount ?? 0);
    }

    private static TagList CreateQueueTagList(MessageQueue queue)
    {
        return new TagList
        {
            { "queue", queue.Name },
            { "durable", queue.IsDurable.ToString().ToLowerInvariant() },
            { "exactly_once", queue.IsExactlyOnce.ToString().ToLowerInvariant() }
        };
    }
}

public static class MetricsExtensions
{
    public static IDisposable MeasureTime(this MelonMetrics metrics, string name)
    {
        return new TimingScope(metrics, name);
    }

    private class TimingScope : IDisposable
    {
        private readonly MelonMetrics _metrics;
        private readonly string _name;
        private readonly Stopwatch _stopwatch;

        public TimingScope(MelonMetrics metrics, string name)
        {
            _metrics = metrics;
            _name = name;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _metrics.RecordTiming(_name, _stopwatch.Elapsed);
        }
    }
}
