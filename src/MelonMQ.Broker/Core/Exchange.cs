using MelonMQ.Common;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MelonMQ.Broker.Core;

/// <summary>
/// Exchange interface for routing messages
/// </summary>
public interface IExchange
{
    string Name { get; }
    ExchangeType Type { get; }
    ExchangeConfig Config { get; }
    
    Task PublishAsync(Message message, CancellationToken cancellationToken = default);
    Task BindAsync(string queueName, string routingKey, Dictionary<string, object>? arguments = null);
    Task UnbindAsync(string queueName, string routingKey);
    Task<string[]> GetBoundQueuesAsync(string routingKey);
}

/// <summary>
/// Base exchange implementation
/// </summary>
public abstract class ExchangeBase : IExchange
{
    protected readonly ExchangeConfig _config;
    protected readonly ILogger _logger;
    protected readonly IQueueManager _queueManager;
    protected readonly ReaderWriterLockSlim _lock = new();
    protected readonly Dictionary<string, List<BindingConfig>> _bindings = new();
    
    public string Name => _config.Name;
    public ExchangeType Type => _config.Type;
    public ExchangeConfig Config => _config;
    
    protected ExchangeBase(ExchangeConfig config, IQueueManager queueManager, ILogger logger)
    {
        _config = config;
        _queueManager = queueManager;
        _logger = logger;
    }
    
    public abstract Task PublishAsync(Message message, CancellationToken cancellationToken = default);
    
    public virtual Task BindAsync(string queueName, string routingKey, Dictionary<string, object>? arguments = null)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_bindings.ContainsKey(routingKey))
            {
                _bindings[routingKey] = new List<BindingConfig>();
            }
            
            var binding = new BindingConfig
            {
                Exchange = Name,
                Queue = queueName,
                RoutingKey = routingKey,
                Arguments = arguments ?? new Dictionary<string, object>()
            };
            
            _bindings[routingKey].Add(binding);
            
            _logger.LogDebug("Bound queue {QueueName} to exchange {ExchangeName} with routing key {RoutingKey}", 
                queueName, Name, routingKey);
            
            return Task.CompletedTask;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public virtual Task UnbindAsync(string queueName, string routingKey)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_bindings.TryGetValue(routingKey, out var bindings))
            {
                bindings.RemoveAll(b => b.Queue == queueName);
                
                if (bindings.Count == 0)
                {
                    _bindings.Remove(routingKey);
                }
            }
            
            _logger.LogDebug("Unbound queue {QueueName} from exchange {ExchangeName} with routing key {RoutingKey}", 
                queueName, Name, routingKey);
            
            return Task.CompletedTask;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public virtual Task<string[]> GetBoundQueuesAsync(string routingKey)
    {
        _lock.EnterReadLock();
        try
        {
            if (_bindings.TryGetValue(routingKey, out var bindings))
            {
                return Task.FromResult(bindings.Select(b => b.Queue).ToArray());
            }
            
            return Task.FromResult(Array.Empty<string>());
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    protected async Task RouteToQueuesAsync(IEnumerable<string> queueNames, Message message, CancellationToken cancellationToken)
    {
        var routedToAny = false;
        
        foreach (var queueName in queueNames)
        {
            try
            {
                var queue = await _queueManager.GetQueueAsync(queueName);
                if (queue != null)
                {
                    await queue.PublishAsync(message, cancellationToken);
                    routedToAny = true;
                    
                    _logger.LogDebug("Routed message {MessageId} to queue {QueueName}", 
                        message.Properties.MessageId, queueName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to route message {MessageId} to queue {QueueName}", 
                    message.Properties.MessageId, queueName);
            }
        }
        
        // Check mandatory flag
        if (!routedToAny && message.Properties.Headers.GetValueOrDefault("mandatory", false) is true)
        {
            throw new NoRouteException(message.Exchange, message.RoutingKey);
        }
    }
}

/// <summary>
/// Direct exchange - routes messages to queues with exact routing key match
/// </summary>
public class DirectExchange : ExchangeBase
{
    public DirectExchange(ExchangeConfig config, IQueueManager queueManager, ILogger<DirectExchange> logger)
        : base(config, queueManager, logger) { }
    
    public override async Task PublishAsync(Message message, CancellationToken cancellationToken = default)
    {
        var queueNames = new List<string>();
        
        _lock.EnterReadLock();
        try
        {
            if (_bindings.TryGetValue(message.RoutingKey, out var bindings))
            {
                queueNames.AddRange(bindings.Select(b => b.Queue));
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
        
        await RouteToQueuesAsync(queueNames, message, cancellationToken);
    }
}

/// <summary>
/// Fanout exchange - routes messages to all bound queues regardless of routing key
/// </summary>
public class FanoutExchange : ExchangeBase
{
    public FanoutExchange(ExchangeConfig config, IQueueManager queueManager, ILogger<FanoutExchange> logger)
        : base(config, queueManager, logger) { }
    
    public override async Task PublishAsync(Message message, CancellationToken cancellationToken = default)
    {
        var queueNames = new List<string>();
        
        _lock.EnterReadLock();
        try
        {
            foreach (var bindings in _bindings.Values)
            {
                queueNames.AddRange(bindings.Select(b => b.Queue));
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
        
        // Remove duplicates
        queueNames = queueNames.Distinct().ToList();
        
        await RouteToQueuesAsync(queueNames, message, cancellationToken);
    }
}

/// <summary>
/// Topic exchange - routes messages using pattern matching (* and # wildcards)
/// </summary>
public class TopicExchange : ExchangeBase
{
    public TopicExchange(ExchangeConfig config, IQueueManager queueManager, ILogger<TopicExchange> logger)
        : base(config, queueManager, logger) { }
    
    public override async Task PublishAsync(Message message, CancellationToken cancellationToken = default)
    {
        var queueNames = new List<string>();
        
        _lock.EnterReadLock();
        try
        {
            foreach (var (pattern, bindings) in _bindings)
            {
                if (TopicMatcher.IsMatch(pattern, message.RoutingKey))
                {
                    queueNames.AddRange(bindings.Select(b => b.Queue));
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
        
        // Remove duplicates
        queueNames = queueNames.Distinct().ToList();
        
        await RouteToQueuesAsync(queueNames, message, cancellationToken);
    }
}

/// <summary>
/// Headers exchange - routes messages based on header matching
/// </summary>
public class HeadersExchange : ExchangeBase
{
    public HeadersExchange(ExchangeConfig config, IQueueManager queueManager, ILogger<HeadersExchange> logger)
        : base(config, queueManager, logger) { }
    
    public override async Task PublishAsync(Message message, CancellationToken cancellationToken = default)
    {
        var queueNames = new List<string>();
        
        _lock.EnterReadLock();
        try
        {
            foreach (var (routingKey, bindings) in _bindings)
            {
                foreach (var binding in bindings)
                {
                    if (MatchesHeaders(message.Properties.Headers, binding.Arguments))
                    {
                        queueNames.Add(binding.Queue);
                    }
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
        
        // Remove duplicates
        queueNames = queueNames.Distinct().ToList();
        
        await RouteToQueuesAsync(queueNames, message, cancellationToken);
    }
    
    private static bool MatchesHeaders(Dictionary<string, object> messageHeaders, Dictionary<string, object> bindingArgs)
    {
        if (!bindingArgs.TryGetValue("x-match", out var matchType))
            return false;
        
        var isMatchAll = matchType.ToString() == "all";
        var requiredHeaders = bindingArgs.Where(kvp => kvp.Key != "x-match").ToList();
        
        if (requiredHeaders.Count == 0)
            return true;
        
        int matchCount = 0;
        
        foreach (var (key, expectedValue) in requiredHeaders)
        {
            if (messageHeaders.TryGetValue(key, out var actualValue))
            {
                // Simple string comparison for now
                if (expectedValue.ToString() == actualValue.ToString())
                {
                    matchCount++;
                }
            }
        }
        
        return isMatchAll ? matchCount == requiredHeaders.Count : matchCount > 0;
    }
}

/// <summary>
/// Queue manager interface
/// </summary>
public interface IQueueManager
{
    Task<IQueue?> GetQueueAsync(string queueName);
    Task<IQueue> CreateQueueAsync(QueueConfig config);
    Task DeleteQueueAsync(string queueName);
    Task<IQueue[]> GetAllQueuesAsync();
}

/// <summary>
/// Exchange manager interface
/// </summary>
public interface IExchangeManager
{
    Task<IExchange?> GetExchangeAsync(string exchangeName);
    Task<IExchange> CreateExchangeAsync(ExchangeConfig config);
    Task DeleteExchangeAsync(string exchangeName);
    Task<IExchange[]> GetAllExchangesAsync();
}

/// <summary>
/// Default queue manager implementation
/// </summary>
public class QueueManager : IQueueManager
{
    private readonly ConcurrentDictionary<string, IQueue> _queues = new();
    private readonly ILogger<QueueManager> _logger;
    
    public QueueManager(ILogger<QueueManager> logger)
    {
        _logger = logger;
    }
    
    public Task<IQueue?> GetQueueAsync(string queueName)
    {
        _queues.TryGetValue(queueName, out var queue);
        return Task.FromResult(queue);
    }
    
    public Task<IQueue> CreateQueueAsync(QueueConfig config)
    {
        var queue = new PriorityQueue(config, _logger.CreateLogger<PriorityQueue>());
        
        if (!_queues.TryAdd(config.Name, queue))
        {
            queue.Dispose();
            throw new ResourceExistsException("Queue", config.Name);
        }
        
        _logger.LogInformation("Created queue {QueueName} with config {@Config}", config.Name, config);
        return Task.FromResult<IQueue>(queue);
    }
    
    public Task DeleteQueueAsync(string queueName)
    {
        if (_queues.TryRemove(queueName, out var queue))
        {
            queue.Dispose();
            _logger.LogInformation("Deleted queue {QueueName}", queueName);
        }
        
        return Task.CompletedTask;
    }
    
    public Task<IQueue[]> GetAllQueuesAsync()
    {
        return Task.FromResult(_queues.Values.ToArray());
    }
}

/// <summary>
/// Default exchange manager implementation
/// </summary>
public class ExchangeManager : IExchangeManager
{
    private readonly ConcurrentDictionary<string, IExchange> _exchanges = new();
    private readonly IQueueManager _queueManager;
    private readonly ILogger<ExchangeManager> _logger;
    
    public ExchangeManager(IQueueManager queueManager, ILogger<ExchangeManager> logger)
    {
        _queueManager = queueManager;
        _logger = logger;
        
        // Create default exchange
        CreateDefaultExchange();
    }
    
    private void CreateDefaultExchange()
    {
        var defaultConfig = new ExchangeConfig
        {
            Name = "",
            Type = ExchangeType.Direct,
            Durable = true
        };
        
        var defaultExchange = new DirectExchange(defaultConfig, _queueManager, _logger.CreateLogger<DirectExchange>());
        _exchanges.TryAdd("", defaultExchange);
    }
    
    public Task<IExchange?> GetExchangeAsync(string exchangeName)
    {
        _exchanges.TryGetValue(exchangeName, out var exchange);
        return Task.FromResult(exchange);
    }
    
    public Task<IExchange> CreateExchangeAsync(ExchangeConfig config)
    {
        var exchange = config.Type switch
        {
            ExchangeType.Direct => new DirectExchange(config, _queueManager, _logger.CreateLogger<DirectExchange>()),
            ExchangeType.Fanout => new FanoutExchange(config, _queueManager, _logger.CreateLogger<FanoutExchange>()),
            ExchangeType.Topic => new TopicExchange(config, _queueManager, _logger.CreateLogger<TopicExchange>()),
            ExchangeType.Headers => new HeadersExchange(config, _queueManager, _logger.CreateLogger<HeadersExchange>()),
            _ => throw new NotSupportedException($"Exchange type {config.Type} is not supported")
        };
        
        if (!_exchanges.TryAdd(config.Name, exchange))
        {
            throw new ResourceExistsException("Exchange", config.Name);
        }
        
        _logger.LogInformation("Created exchange {ExchangeName} of type {ExchangeType}", config.Name, config.Type);
        return Task.FromResult<IExchange>(exchange);
    }
    
    public Task DeleteExchangeAsync(string exchangeName)
    {
        if (exchangeName == "")
        {
            throw new InvalidOperationException("Cannot delete default exchange");
        }
        
        if (_exchanges.TryRemove(exchangeName, out var exchange))
        {
            _logger.LogInformation("Deleted exchange {ExchangeName}", exchangeName);
        }
        
        return Task.CompletedTask;
    }
    
    public Task<IExchange[]> GetAllExchangesAsync()
    {
        return Task.FromResult(_exchanges.Values.ToArray());
    }
}