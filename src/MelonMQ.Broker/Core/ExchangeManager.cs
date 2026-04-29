using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MelonMQ.Broker.Core;

public enum ExchangeType { Direct, Fanout, Topic }

public sealed record ExchangeInfo(string Name, ExchangeType Type, bool Durable);

public sealed record ExchangeBinding(string QueueName, string RoutingKey);

public class ExchangeManager
{
    private readonly ConcurrentDictionary<string, ExchangeInfo> _exchanges =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<ExchangeBinding>> _bindings =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<ExchangeManager> _logger;
    private readonly string? _topologyFilePath;
    private readonly object _topologyLock = new();

    private sealed record PersistedExchangeTopology(IReadOnlyList<PersistedExchange> Exchanges);

    private sealed record PersistedExchange(
        string Name,
        string Type,
        bool Durable,
        IReadOnlyList<ExchangeBinding> Bindings);

    public ExchangeManager(ILogger<ExchangeManager> logger, string? dataDirectory = null)
    {
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
            _topologyFilePath = Path.Combine(dataDirectory, "exchange_topology.json");
            LoadPersistedTopology();
        }
    }

    public void DeclareExchange(string name, ExchangeType type, bool durable)
    {
        ValidateExchangeName(name);

        lock (_topologyLock)
        {
            _exchanges[name] = new ExchangeInfo(name, type, durable);
            _bindings.TryAdd(name, new List<ExchangeBinding>());
            PersistDurableTopologyLocked();
        }

        _logger.LogInformation("Declared exchange '{Exchange}' type={Type}", name, type);
    }

    public bool ExchangeExists(string name) => _exchanges.ContainsKey(name);

    public ExchangeInfo? GetExchange(string name) =>
        _exchanges.TryGetValue(name, out var e) ? e : null;

    public void BindQueue(string exchangeName, string queueName, string routingKey)
    {
        lock (_topologyLock)
        {
            if (!_exchanges.ContainsKey(exchangeName))
                throw new InvalidOperationException($"Exchange '{exchangeName}' does not exist.");

            var bindings = _bindings.GetOrAdd(exchangeName, _ => new List<ExchangeBinding>());
            lock (bindings)
            {
                if (!bindings.Any(b =>
                        string.Equals(b.QueueName, queueName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(b.RoutingKey, routingKey, StringComparison.OrdinalIgnoreCase)))
                {
                    bindings.Add(new ExchangeBinding(queueName, routingKey));
                    PersistDurableTopologyLocked();
                    _logger.LogInformation(
                        "Bound queue '{Queue}' to exchange '{Exchange}' with routing key '{Key}'",
                        queueName, exchangeName, routingKey);
                }
            }
        }
    }

    public void UnbindQueue(string exchangeName, string queueName, string routingKey)
    {
        lock (_topologyLock)
        {
            if (_bindings.TryGetValue(exchangeName, out var bindings))
            {
                lock (bindings)
                {
                    var removed = bindings.RemoveAll(b =>
                        string.Equals(b.QueueName, queueName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(b.RoutingKey, routingKey, StringComparison.OrdinalIgnoreCase));

                    if (removed > 0)
                    {
                        PersistDurableTopologyLocked();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns the distinct queue names that should receive a message published to
    /// <paramref name="exchangeName"/> with the given <paramref name="routingKey"/>.
    /// </summary>
    public IReadOnlyList<string> ResolveQueues(string exchangeName, string routingKey)
    {
        if (!_exchanges.TryGetValue(exchangeName, out var exchange))
            return Array.Empty<string>();

        if (!_bindings.TryGetValue(exchangeName, out var bindings))
            return Array.Empty<string>();

        List<ExchangeBinding> snapshot;
        lock (bindings) { snapshot = bindings.ToList(); }

        return exchange.Type switch
        {
            ExchangeType.Fanout =>
                snapshot
                    .Select(b => b.QueueName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),

            ExchangeType.Direct =>
                snapshot
                    .Where(b => string.Equals(b.RoutingKey, routingKey, StringComparison.OrdinalIgnoreCase))
                    .Select(b => b.QueueName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),

            ExchangeType.Topic =>
                snapshot
                    .Where(b => TopicMatches(b.RoutingKey, routingKey))
                    .Select(b => b.QueueName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),

            _ => Array.Empty<string>()
        };
    }

    public IReadOnlyList<ExchangeInfo> GetAllExchanges() => _exchanges.Values.ToList();

    public IReadOnlyList<ExchangeBinding> GetBindings(string exchangeName)
    {
        if (_bindings.TryGetValue(exchangeName, out var bindings))
        {
            lock (bindings) { return bindings.ToList(); }
        }
        return Array.Empty<ExchangeBinding>();
    }

    public bool DeleteExchange(string name)
    {
        lock (_topologyLock)
        {
            _bindings.TryRemove(name, out _);
            var removed = _exchanges.TryRemove(name, out _);
            if (removed)
            {
                PersistDurableTopologyLocked();
            }

            return removed;
        }
    }

    private void LoadPersistedTopology()
    {
        if (_topologyFilePath == null || !File.Exists(_topologyFilePath))
            return;

        try
        {
            using var stream = File.OpenRead(_topologyFilePath);
            var topology = JsonSerializer.Deserialize<PersistedExchangeTopology>(stream);
            if (topology?.Exchanges == null)
                return;

            lock (_topologyLock)
            {
                foreach (var exchange in topology.Exchanges)
                {
                    if (!Enum.TryParse<ExchangeType>(exchange.Type, ignoreCase: true, out var exchangeType))
                    {
                        continue;
                    }

                    _exchanges[exchange.Name] = new ExchangeInfo(exchange.Name, exchangeType, exchange.Durable);
                    var bindings = exchange.Bindings?.ToList() ?? new List<ExchangeBinding>();
                    _bindings[exchange.Name] = bindings;
                }
            }

            _logger.LogInformation("Loaded {Count} durable exchanges from persisted topology", topology.Exchanges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted exchange topology");
        }
    }

    private void PersistDurableTopologyLocked()
    {
        if (_topologyFilePath == null)
            return;

        try
        {
            var exchanges = _exchanges.Values
                .Where(e => e.Durable)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(exchange => new PersistedExchange(
                    exchange.Name,
                    exchange.Type.ToString(),
                    exchange.Durable,
                    GetBindingsSnapshotLocked(exchange.Name)))
                .ToList();

            if (exchanges.Count == 0)
            {
                if (File.Exists(_topologyFilePath))
                {
                    File.Delete(_topologyFilePath);
                }
                return;
            }

            var topology = new PersistedExchangeTopology(exchanges);
            var tempPath = _topologyFilePath + ".tmp";

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, topology, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _topologyFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist exchange topology");
        }
    }

    private IReadOnlyList<ExchangeBinding> GetBindingsSnapshotLocked(string exchangeName)
    {
        if (_bindings.TryGetValue(exchangeName, out var bindings))
        {
            lock (bindings)
            {
                return bindings.ToList();
            }
        }

        return Array.Empty<ExchangeBinding>();
    }

    // ── Topic routing ────────────────────────────────────────────────────────
    // Pattern words: '*' matches exactly one word, '#' matches zero or more words.
    // Words are separated by '.'.

    internal static bool TopicMatches(string pattern, string routingKey)
    {
        var patternWords = pattern.Split('.');
        var keyWords = routingKey.Split('.');
        return MatchWords(patternWords, 0, keyWords, 0);
    }

    private static bool MatchWords(string[] pattern, int pi, string[] key, int ki)
    {
        while (pi < pattern.Length)
        {
            if (pattern[pi] == "#")
            {
                // '#' can match zero or more words — try every possible length
                for (var i = ki; i <= key.Length; i++)
                {
                    if (MatchWords(pattern, pi + 1, key, i))
                        return true;
                }
                return false;
            }

            if (ki >= key.Length)
                return false;

            if (pattern[pi] != "*" &&
                !string.Equals(pattern[pi], key[ki], StringComparison.OrdinalIgnoreCase))
                return false;

            pi++;
            ki++;
        }
        return ki == key.Length;
    }

    private static readonly Regex _validName =
        new(@"^[a-zA-Z0-9\-_.]+$", RegexOptions.Compiled);

    private static void ValidateExchangeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 ||
            name.Contains("..") || !_validName.IsMatch(name))
        {
            throw new ArgumentException($"Invalid exchange name: '{name}'");
        }
    }
}
