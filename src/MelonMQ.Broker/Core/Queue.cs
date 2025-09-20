using MelonMQ.Common;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MelonMQ.Broker.Core;

/// <summary>
/// Queue implementation with priority support and flow control
/// </summary>
public interface IQueue : IDisposable
{
    string Name { get; }
    QueueConfig Config { get; }
    int MessageCount { get; }
    int ConsumerCount { get; }
    
    Task PublishAsync(Message message, CancellationToken cancellationToken = default);
    Task<Message?> ConsumeAsync(string consumerTag, CancellationToken cancellationToken = default);
    Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken = default);
    Task NackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default);
    Task RejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default);
    
    Task PurgeAsync(CancellationToken cancellationToken = default);
    Task<QueueStats> GetStatsAsync();
    
    void AddConsumer(IConsumer consumer);
    void RemoveConsumer(string consumerTag);
}

/// <summary>
/// Consumer interface for queue delivery
/// </summary>
public interface IConsumer
{
    string ConsumerTag { get; }
    string QueueName { get; }
    int Prefetch { get; }
    int UnackedCount { get; }
    bool IsActive { get; }
    
    Task<bool> CanDeliverAsync();
    Task DeliverAsync(Message message, CancellationToken cancellationToken = default);
    Task OnAckAsync(ulong deliveryTag);
    Task OnNackAsync(ulong deliveryTag);
}

/// <summary>
/// Priority queue implementation
/// </summary>
public class PriorityQueue : IQueue
{
    private readonly QueueConfig _config;
    private readonly ILogger<PriorityQueue> _logger;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly AtomicCounter _deliveryTagGenerator = new();
    
    // Priority buckets (0-9, where 9 is highest priority)
    private readonly Queue<Message>[] _priorityBuckets = new Queue<Message>[10];
    private readonly Dictionary<ulong, PendingMessage> _pendingAcks = new();
    private readonly Dictionary<string, IConsumer> _consumers = new();
    
    private readonly Timer _redeliveryTimer;
    private readonly Timer _ttlTimer;
    
    private QueueStats _stats = new();
    private int _totalMessages;
    private bool _disposed;
    
    public string Name => _config.Name;
    public QueueConfig Config => _config;
    public int MessageCount => _totalMessages;
    public int ConsumerCount => _consumers.Count;
    
    public PriorityQueue(QueueConfig config, ILogger<PriorityQueue> logger)
    {
        _config = config;
        _logger = logger;
        
        // Initialize priority buckets
        for (int i = 0; i < _priorityBuckets.Length; i++)
        {
            _priorityBuckets[i] = new Queue<Message>();
        }
        
        // Setup timers for redelivery and TTL cleanup
        _redeliveryTimer = new Timer(ProcessRedeliveries, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _ttlTimer = new Timer(ProcessTtlExpiry, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        
        _stats.Name = config.Name;
    }
    
    public async Task PublishAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PriorityQueue));
        
        // Check queue limits
        CheckQueueLimits();
        
        // Set queue name and delivery tag
        message.QueueName = Name;
        message.DeliveryTag = (ulong)_deliveryTagGenerator.Next();
        
        // Determine priority bucket
        var priority = Math.Min(message.Properties.Priority, (byte)9);
        
        _lock.EnterWriteLock();
        try
        {
            _priorityBuckets[priority].Enqueue(message);
            Interlocked.Increment(ref _totalMessages);
            
            _stats.TotalPublished++;
            _stats.LastActivity = DateTime.UtcNow;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        
        // Try to deliver immediately to available consumers
        await TryDeliverToConsumersAsync(cancellationToken);
        
        _logger.LogDebug("Published message {MessageId} to queue {QueueName} with priority {Priority}", 
            message.Properties.MessageId, Name, priority);
    }
    
    public async Task<Message?> ConsumeAsync(string consumerTag, CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;
        
        Message? message = null;
        
        _lock.EnterWriteLock();
        try
        {
            // Check priority buckets from highest to lowest
            for (int priority = 9; priority >= 0; priority--)
            {
                if (_priorityBuckets[priority].Count > 0)
                {
                    message = _priorityBuckets[priority].Dequeue();
                    Interlocked.Decrement(ref _totalMessages);
                    break;
                }
            }
            
            if (message != null)
            {
                // Track for redelivery if not auto-ack
                var pendingMessage = new PendingMessage
                {
                    Message = message,
                    ConsumerTag = consumerTag,
                    DeliveryTime = DateTime.UtcNow,
                    RedeliveryCount = ++message.Properties.DeliveryCount
                };
                
                message.Properties.LastDeliveryAttempt = DateTime.UtcNow;
                message.Properties.LastConsumerTag = consumerTag;
                
                if (message.Properties.FirstDeliveryAttempt == null)
                    message.Properties.FirstDeliveryAttempt = DateTime.UtcNow;
                
                _pendingAcks[message.DeliveryTag] = pendingMessage;
                
                _stats.TotalDelivered++;
                _stats.LastActivity = DateTime.UtcNow;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        
        return message;
    }
    
    public async Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        
        _lock.EnterWriteLock();
        try
        {
            if (_pendingAcks.Remove(deliveryTag, out var pending))
            {
                _stats.TotalAcked++;
                _stats.LastActivity = DateTime.UtcNow;
                
                _logger.LogDebug("Acknowledged message {MessageId} with delivery tag {DeliveryTag}", 
                    pending.Message.Properties.MessageId, deliveryTag);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public async Task NackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        
        await HandleNackOrReject(deliveryTag, requeue, isReject: false, cancellationToken);
    }
    
    public async Task RejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        
        await HandleNackOrReject(deliveryTag, requeue, isReject: true, cancellationToken);
    }
    
    private async Task HandleNackOrReject(ulong deliveryTag, bool requeue, bool isReject, CancellationToken cancellationToken)
    {
        PendingMessage? pending = null;
        
        _lock.EnterWriteLock();
        try
        {
            if (_pendingAcks.Remove(deliveryTag, out pending))
            {
                _stats.TotalRejected++;
                _stats.LastActivity = DateTime.UtcNow;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        
        if (pending != null)
        {
            if (requeue && pending.RedeliveryCount < _config.MaxDeliveryCount)
            {
                // Re-queue the message
                await PublishAsync(pending.Message, cancellationToken);
            }
            else
            {
                // Send to dead letter queue if configured
                await SendToDeadLetterAsync(pending.Message, cancellationToken);
            }
            
            _logger.LogDebug("{Action} message {MessageId} with delivery tag {DeliveryTag}, requeue: {Requeue}", 
                isReject ? "Rejected" : "Nacked", pending.Message.Properties.MessageId, deliveryTag, requeue);
        }
    }
    
    public async Task PurgeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        
        int purgedCount = 0;
        
        _lock.EnterWriteLock();
        try
        {
            for (int i = 0; i < _priorityBuckets.Length; i++)
            {
                purgedCount += _priorityBuckets[i].Count;
                _priorityBuckets[i].Clear();
            }
            
            _totalMessages = 0;
            _pendingAcks.Clear();
            
            _stats.LastActivity = DateTime.UtcNow;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        
        _logger.LogInformation("Purged {MessageCount} messages from queue {QueueName}", purgedCount, Name);
    }
    
    public Task<QueueStats> GetStatsAsync()
    {
        _lock.EnterReadLock();
        try
        {
            _stats.MessageCount = _totalMessages;
            _stats.ConsumerCount = _consumers.Count;
            return Task.FromResult(_stats);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    public void AddConsumer(IConsumer consumer)
    {
        _lock.EnterWriteLock();
        try
        {
            _consumers[consumer.ConsumerTag] = consumer;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        
        _logger.LogDebug("Added consumer {ConsumerTag} to queue {QueueName}", consumer.ConsumerTag, Name);
    }
    
    public void RemoveConsumer(string consumerTag)
    {
        _lock.EnterWriteLock();
        try
        {
            _consumers.Remove(consumerTag);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        
        _logger.LogDebug("Removed consumer {ConsumerTag} from queue {QueueName}", consumerTag, Name);
    }
    
    private async Task TryDeliverToConsumersAsync(CancellationToken cancellationToken)
    {
        var availableConsumers = new List<IConsumer>();
        
        _lock.EnterReadLock();
        try
        {
            availableConsumers.AddRange(_consumers.Values.Where(c => c.IsActive));
        }
        finally
        {
            _lock.ExitReadLock();
        }
        
        foreach (var consumer in availableConsumers)
        {
            if (await consumer.CanDeliverAsync())
            {
                var message = await ConsumeAsync(consumer.ConsumerTag, cancellationToken);
                if (message != null)
                {
                    await consumer.DeliverAsync(message, cancellationToken);
                }
            }
        }
    }
    
    private void CheckQueueLimits()
    {
        if (_config.MaxLength.HasValue && _totalMessages >= _config.MaxLength.Value)
        {
            throw new QueueFullException(Name);
        }
        
        // TODO: Check MaxLengthBytes if needed
    }
    
    private async void ProcessRedeliveries(object? state)
    {
        if (_disposed) return;
        
        try
        {
            var expiredMessages = new List<PendingMessage>();
            var cutoffTime = DateTime.UtcNow - _config.VisibilityTimeout;
            
            _lock.EnterWriteLock();
            try
            {
                var expiredTags = _pendingAcks
                    .Where(kvp => kvp.Value.DeliveryTime < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var tag in expiredTags)
                {
                    if (_pendingAcks.Remove(tag, out var pending))
                    {
                        expiredMessages.Add(pending);
                    }
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            
            // Requeue expired messages
            foreach (var pending in expiredMessages)
            {
                if (pending.RedeliveryCount < _config.MaxDeliveryCount)
                {
                    await PublishAsync(pending.Message);
                }
                else
                {
                    await SendToDeadLetterAsync(pending.Message);
                }
            }
            
            if (expiredMessages.Count > 0)
            {
                _logger.LogDebug("Redelivered {Count} expired messages from queue {QueueName}", 
                    expiredMessages.Count, Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing redeliveries for queue {QueueName}", Name);
        }
    }
    
    private async void ProcessTtlExpiry(object? state)
    {
        if (_disposed) return;
        
        try
        {
            var expiredMessages = new List<Message>();
            var now = DateTime.UtcNow;
            
            _lock.EnterWriteLock();
            try
            {
                for (int priority = 0; priority < _priorityBuckets.Length; priority++)
                {
                    var bucket = _priorityBuckets[priority];
                    var remainingMessages = new Queue<Message>();
                    
                    while (bucket.Count > 0)
                    {
                        var message = bucket.Dequeue();
                        
                        if (IsExpired(message, now))
                        {
                            expiredMessages.Add(message);
                            Interlocked.Decrement(ref _totalMessages);
                        }
                        else
                        {
                            remainingMessages.Enqueue(message);
                        }
                    }
                    
                    _priorityBuckets[priority] = remainingMessages;
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            
            // Send expired messages to DLQ
            foreach (var message in expiredMessages)
            {
                await SendToDeadLetterAsync(message);
            }
            
            if (expiredMessages.Count > 0)
            {
                _logger.LogDebug("Expired {Count} messages from queue {QueueName}", expiredMessages.Count, Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing TTL expiry for queue {QueueName}", Name);
        }
    }
    
    private bool IsExpired(Message message, DateTime now)
    {
        // Check message-level TTL
        if (message.Properties.Expiration.HasValue && now > message.Properties.Expiration.Value)
            return true;
        
        // Check queue-level TTL
        if (_config.MessageTtl.HasValue && message.Properties.Timestamp.IsExpired(_config.MessageTtl))
            return true;
        
        return false;
    }
    
    private async Task SendToDeadLetterAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_config.DeadLetterExchange))
        {
            // TODO: Implement dead letter routing
            _logger.LogWarning("Dead letter exchange not implemented yet for message {MessageId}", 
                message.Properties.MessageId);
        }
        else
        {
            _logger.LogWarning("Message {MessageId} exceeded max delivery count but no DLQ configured", 
                message.Properties.MessageId);
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        _redeliveryTimer?.Dispose();
        _ttlTimer?.Dispose();
        
        _lock.EnterWriteLock();
        try
        {
            foreach (var bucket in _priorityBuckets)
            {
                bucket.Clear();
            }
            _pendingAcks.Clear();
            _consumers.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}

/// <summary>
/// Represents a message pending acknowledgment
/// </summary>
internal class PendingMessage
{
    public required Message Message { get; set; }
    public required string ConsumerTag { get; set; }
    public DateTime DeliveryTime { get; set; }
    public int RedeliveryCount { get; set; }
}