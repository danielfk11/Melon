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
    
    public static MelonMetrics Instance { get; } = new();

    public MelonMetrics() { }

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