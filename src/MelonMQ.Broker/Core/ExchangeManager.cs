using System.Collections.Concurrent;
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

    public ExchangeManager(ILogger<ExchangeManager> logger)
    {
        _logger = logger;
    }

    public void DeclareExchange(string name, ExchangeType type, bool durable)
    {
        ValidateExchangeName(name);
        _exchanges[name] = new ExchangeInfo(name, type, durable);
        _bindings.TryAdd(name, new List<ExchangeBinding>());
        _logger.LogInformation("Declared exchange '{Exchange}' type={Type}", name, type);
    }

    public bool ExchangeExists(string name) => _exchanges.ContainsKey(name);

    public ExchangeInfo? GetExchange(string name) =>
        _exchanges.TryGetValue(name, out var e) ? e : null;

    public void BindQueue(string exchangeName, string queueName, string routingKey)
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
                _logger.LogInformation(
                    "Bound queue '{Queue}' to exchange '{Exchange}' with routing key '{Key}'",
                    queueName, exchangeName, routingKey);
            }
        }
    }

    public void UnbindQueue(string exchangeName, string queueName, string routingKey)
    {
        if (_bindings.TryGetValue(exchangeName, out var bindings))
        {
            lock (bindings)
            {
                bindings.RemoveAll(b =>
                    string.Equals(b.QueueName, queueName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(b.RoutingKey, routingKey, StringComparison.OrdinalIgnoreCase));
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
        _bindings.TryRemove(name, out _);
        return _exchanges.TryRemove(name, out _);
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
