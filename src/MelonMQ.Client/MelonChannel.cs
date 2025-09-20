using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MelonMQ.Common;
using Microsoft.Extensions.Logging;

namespace MelonMQ.Client;

/// <summary>
/// Channel implementation for MelonMQ client
/// </summary>
internal class MelonChannel : IMelonChannel
{
    private readonly MelonConnectionImpl _connection;
    private readonly ILogger<MelonChannel> _logger;
    private readonly ConcurrentDictionary<string, ConsumerContext> _consumers = new();
    private readonly AtomicCounter _deliveryTagCounter = new();
    private volatile bool _disposed;
    
    public string ChannelId { get; } = Guid.NewGuid().ToString();
    public bool IsOpen => !_disposed && _connection.IsConnected;
    
    public MelonChannel(MelonConnectionImpl connection, ILogger<MelonChannel> logger)
    {
        _connection = connection;
        _logger = logger;
    }
    
    public async Task DeclareExchangeAsync(string name, ExchangeType type, bool durable = false, Dictionary<string, object>? arguments = null, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(name);
        tlvWriter.WriteInt32((int)type);
        tlvWriter.WriteString(JsonSerializer.Serialize(arguments ?? new Dictionary<string, object>()));
        
        var flags = FrameFlags.Request;
        if (durable) flags |= FrameFlags.Durable;
        
        var response = await _connection.SendRequestAsync(FrameType.DeclareExchange, flags, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Declared exchange {ExchangeName} of type {ExchangeType}", name, type);
    }
    
    public async Task DeleteExchangeAsync(string name, bool ifUnused = false, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(name);
        tlvWriter.WriteInt32(ifUnused ? 1 : 0);
        
        var response = await _connection.SendRequestAsync(FrameType.Delete, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Deleted exchange {ExchangeName}", name);
    }
    
    public async Task DeclareQueueAsync(string name, bool durable = false, bool exclusive = false, bool autoDelete = false, Dictionary<string, object>? arguments = null, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        var args = arguments ?? new Dictionary<string, object>();
        if (exclusive) args["x-exclusive"] = true;
        if (autoDelete) args["x-auto-delete"] = true;
        
        tlvWriter.WriteString(name);
        tlvWriter.WriteString(JsonSerializer.Serialize(args));
        
        var flags = FrameFlags.Request;
        if (durable) flags |= FrameFlags.Durable;
        
        var response = await _connection.SendRequestAsync(FrameType.DeclareQueue, flags, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Declared queue {QueueName}", name);
    }
    
    public async Task DeleteQueueAsync(string name, bool ifUnused = false, bool ifEmpty = false, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(name);
        tlvWriter.WriteInt32(ifUnused ? 1 : 0);
        tlvWriter.WriteInt32(ifEmpty ? 1 : 0);
        
        var response = await _connection.SendRequestAsync(FrameType.Delete, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Deleted queue {QueueName}", name);
    }
    
    public async Task PurgeQueueAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(name);
        
        var response = await _connection.SendRequestAsync(FrameType.Purge, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Purged queue {QueueName}", name);
    }
    
    public async Task BindQueueAsync(string queue, string exchange, string routingKey, Dictionary<string, object>? arguments = null, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(queue);
        tlvWriter.WriteString(exchange);
        tlvWriter.WriteString(routingKey);
        tlvWriter.WriteString(JsonSerializer.Serialize(arguments ?? new Dictionary<string, object>()));
        
        var response = await _connection.SendRequestAsync(FrameType.Bind, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Bound queue {QueueName} to exchange {ExchangeName} with routing key {RoutingKey}", queue, exchange, routingKey);
    }
    
    public async Task UnbindQueueAsync(string queue, string exchange, string routingKey, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(queue);
        tlvWriter.WriteString(exchange);
        tlvWriter.WriteString(routingKey);
        
        var response = await _connection.SendRequestAsync(FrameType.Unbind, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Unbound queue {QueueName} from exchange {ExchangeName} with routing key {RoutingKey}", queue, exchange, routingKey);
    }
    
    public async Task PublishAsync(string exchange, string routingKey, BinaryData body, MessageProperties? properties = null, bool persistent = false, byte priority = 0, Guid? messageId = null, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var props = properties ?? new MessageProperties();
        props.MessageId = messageId ?? Guid.NewGuid();
        props.Priority = priority;
        if (persistent) props.DeliveryMode = DeliveryMode.Persistent;
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(exchange);
        tlvWriter.WriteString(routingKey);
        tlvWriter.WriteString(JsonSerializer.Serialize(props));
        tlvWriter.WriteBytes(body.ToMemory().Span);
        
        var flags = FrameFlags.Request;
        if (persistent) flags |= FrameFlags.Durable;
        
        var response = await _connection.SendRequestAsync(FrameType.Publish, flags, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Published message {MessageId} to exchange {ExchangeName} with routing key {RoutingKey}", props.MessageId, exchange, routingKey);
    }
    
    public async Task<PublishConfirmation> PublishWithConfirmationAsync(string exchange, string routingKey, BinaryData body, MessageProperties? properties = null, bool persistent = false, byte priority = 0, Guid? messageId = null, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var props = properties ?? new MessageProperties();
        props.MessageId = messageId ?? Guid.NewGuid();
        props.Priority = priority;
        if (persistent) props.DeliveryMode = DeliveryMode.Persistent;
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(exchange);
        tlvWriter.WriteString(routingKey);
        tlvWriter.WriteString(JsonSerializer.Serialize(props));
        tlvWriter.WriteBytes(body.ToMemory().Span);
        
        var flags = FrameFlags.Request;
        if (persistent) flags |= FrameFlags.Durable;
        
        var response = await _connection.SendRequestAsync(FrameType.Publish, flags, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Confirm)
        {
            _logger.LogDebug("Published and confirmed message {MessageId} to exchange {ExchangeName}", props.MessageId, exchange);
            return new PublishConfirmation { IsConfirmed = true, CorrelationId = response.CorrelationId };
        }
        else if (response.Type == FrameType.Error)
        {
            var reader = new TlvReader(response.Payload.Span);
            reader.TryReadInt32(out var errorCode);
            reader.TryReadString(out var errorMessage);
            
            return new PublishConfirmation 
            { 
                IsConfirmed = false, 
                ErrorMessage = errorMessage,
                CorrelationId = response.CorrelationId 
            };
        }
        else
        {
            return new PublishConfirmation 
            { 
                IsConfirmed = false, 
                ErrorMessage = "Unexpected response",
                CorrelationId = response.CorrelationId 
            };
        }
    }
    
    public async IAsyncEnumerable<MessageDelivery> ConsumeAsync(string queue, string? consumerTag = null, int prefetch = 100, bool noAck = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        consumerTag ??= $"consumer-{ChannelId}-{Guid.NewGuid():N}";
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteString(queue);
        tlvWriter.WriteString(consumerTag);
        tlvWriter.WriteInt32(prefetch);
        
        var response = await _connection.SendRequestAsync(FrameType.Consume, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        // Create consumer context
        var consumerContext = new ConsumerContext(consumerTag, queue, prefetch, noAck);
        _consumers[consumerTag] = consumerContext;
        
        try
        {
            _logger.LogDebug("Started consuming from queue {QueueName} with consumer tag {ConsumerTag}", queue, consumerTag);
            
            // This is a simplified implementation - in a real implementation,
            // you'd have a background task listening for DELIVER frames
            // and yielding them through this async enumerable
            
            while (!cancellationToken.IsCancellationRequested && IsOpen)
            {
                // TODO: Implement actual message delivery from background task
                // For now, just yield a delay to prevent tight loop
                await Task.Delay(100, cancellationToken);
                
                // Example of yielding a message (this would come from actual delivery)
                // yield return new MessageDelivery { ... };
            }
        }
        finally
        {
            _consumers.TryRemove(consumerTag, out _);
            _logger.LogDebug("Stopped consuming from queue {QueueName} with consumer tag {ConsumerTag}", queue, consumerTag);
        }
    }
    
    public async Task SetPrefetchAsync(int prefetch, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteInt32(prefetch);
        
        var response = await _connection.SendRequestAsync(FrameType.SetPrefetch, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Set prefetch to {Prefetch}", prefetch);
    }
    
    internal async Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteInt64((long)deliveryTag);
        
        var response = await _connection.SendRequestAsync(FrameType.Ack, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Acknowledged message with delivery tag {DeliveryTag}", deliveryTag);
    }
    
    internal async Task NackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteInt64((long)deliveryTag);
        tlvWriter.WriteInt32(requeue ? 1 : 0);
        
        var response = await _connection.SendRequestAsync(FrameType.Nack, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Nacked message with delivery tag {DeliveryTag}, requeue: {Requeue}", deliveryTag, requeue);
    }
    
    internal async Task RejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteInt64((long)deliveryTag);
        tlvWriter.WriteInt32(requeue ? 1 : 0);
        
        var response = await _connection.SendRequestAsync(FrameType.Reject, FrameFlags.Request, writer.WrittenMemory, cancellationToken);
        
        if (response.Type == FrameType.Error)
        {
            ThrowError(response);
        }
        
        _logger.LogDebug("Rejected message with delivery tag {DeliveryTag}, requeue: {Requeue}", deliveryTag, requeue);
    }
    
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            // Cancel all consumers
            foreach (var consumer in _consumers.Values)
            {
                consumer.Cancel();
            }
            _consumers.Clear();
            
            // Send close frame
            var response = await _connection.SendRequestAsync(FrameType.Close, FrameFlags.Request, ReadOnlyMemory<byte>.Empty, cancellationToken);
            
            _logger.LogDebug("Closed channel {ChannelId}", ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing channel {ChannelId}", ChannelId);
        }
        finally
        {
            _connection.RemoveChannel(ChannelId);
        }
    }
    
    private void EnsureOpen()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MelonChannel));
        
        if (!_connection.IsConnected)
            throw new InvalidOperationException("Connection is not established");
    }
    
    private static void ThrowError(ProtocolFrame response)
    {
        var reader = new TlvReader(response.Payload.Span);
        reader.TryReadInt32(out var errorCode);
        reader.TryReadString(out var errorMessage);
        
        throw new MelonMqException((ErrorCode)errorCode, errorMessage ?? "Unknown error");
    }
    
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await CloseAsync();
        }
    }
}

/// <summary>
/// Consumer context for tracking active consumers
/// </summary>
internal class ConsumerContext
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    public string ConsumerTag { get; }
    public string QueueName { get; }
    public int Prefetch { get; }
    public bool NoAck { get; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;
    
    public ConsumerContext(string consumerTag, string queueName, int prefetch, bool noAck)
    {
        ConsumerTag = consumerTag;
        QueueName = queueName;
        Prefetch = prefetch;
        NoAck = noAck;
    }
    
    public void Cancel()
    {
        _cancellationTokenSource.Cancel();
    }
}