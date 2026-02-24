using System.Collections.Concurrent;
using System.Diagnostics;

namespace MelonMQ.Broker.Core;

public class MelonMetrics
{
    private static readonly ActivitySource ActivitySource = new("MelonMQ.Broker");
    
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

    public Activity? StartActivity(string name)
    {
        return ActivitySource.StartActivity(name);
    }

    // Common metrics
    public void RecordMessagePublished(string queueName, TimeSpan duration)
    {
        IncrementCounter("messages.published.total");
        IncrementCounter($"messages.published.queue.{queueName}");
        RecordTiming("message.publish.duration", duration);
    }

    public void RecordMessageConsumed(string queueName, TimeSpan duration)
    {
        IncrementCounter("messages.consumed.total");
        IncrementCounter($"messages.consumed.queue.{queueName}");
        RecordTiming("message.consume.duration", duration);
    }

    public void RecordConnectionOpened()
    {
        IncrementCounter("connections.opened");
    }

    public void RecordConnectionClosed()
    {
        IncrementCounter("connections.closed");
    }

    public void RecordError(string operation, string errorType)
    {
        IncrementCounter("errors.total");
        IncrementCounter($"errors.operation.{operation}");
        IncrementCounter($"errors.type.{errorType}");
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