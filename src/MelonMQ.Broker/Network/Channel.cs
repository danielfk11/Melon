using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using MelonMQ.Broker.Core;
using MelonMQ.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MelonMQ.Broker.Network;

/// <summary>
/// Channel implementation representing a logical connection session
/// </summary>
public class Channel : IChannel, IDisposable
{
    private readonly IConnection _connection;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Channel> _logger;
    private readonly Dictionary<string, IConsumer> _consumers = new();
    private readonly object _lock = new();
    
    private IQueueManager? _queueManager;
    private IExchangeManager? _exchangeManager;
    private volatile bool _disposed;
    
    public string Id { get; } = Guid.NewGuid().ToString();
    public IConnection Connection => _connection;
    public bool IsAuthenticated { get; private set; }
    public string? Username { get; private set; }
    public string VHost { get; private set; } = ProtocolConstants.DefaultVHost;
    
    public Channel(IConnection connection, IServiceProvider serviceProvider, ILogger<Channel> logger)
    {
        _connection = connection;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }
    
    public async Task ProcessFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        
        try
        {
            _logger.LogDebug("Processing frame {FrameType} with correlation {CorrelationId} on channel {ChannelId}", 
                frame.Type, frame.CorrelationId, Id);
            
            switch (frame.Type)
            {
                case FrameType.Auth:
                    await HandleAuthAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.DeclareExchange:
                    await HandleDeclareExchangeAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.DeclareQueue:
                    await HandleDeclareQueueAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Bind:
                    await HandleBindAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Unbind:
                    await HandleUnbindAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Publish:
                    await HandlePublishAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Consume:
                    await HandleConsumeAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Ack:
                    await HandleAckAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Nack:
                    await HandleNackAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Reject:
                    await HandleRejectAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.SetPrefetch:
                    await HandleSetPrefetchAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Heartbeat:
                    await HandleHeartbeatAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Close:
                    await HandleCloseAsync(frame, cancellationToken);
                    break;
                    
                case FrameType.Stats:
                    await HandleStatsAsync(frame, cancellationToken);
                    break;
                    
                default:
                    await SendErrorAsync(ErrorCode.NotImplemented, $"Frame type {frame.Type} is not implemented", 
                        frame.CorrelationId, cancellationToken);
                    break;
            }
        }
        catch (MelonMqException ex)
        {
            await SendErrorAsync(ex.ErrorCode, ex.Message, frame.CorrelationId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing frame {FrameType}", frame.Type);
            await SendErrorAsync(ErrorCode.InternalError, "Internal server error", frame.CorrelationId, cancellationToken);
        }
    }
    
    private async Task HandleAuthAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadString(out var username) ||
            !reader.TryReadString(out var password))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid auth frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        // TODO: Implement actual authentication
        if (username == "guest" && password == "guest")
        {
            IsAuthenticated = true;
            Username = username;
            
            await SendFrameAsync(FrameType.AuthOk, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
            
            // Get service dependencies after authentication
            _queueManager = _serviceProvider.GetRequiredService<IQueueManager>();
            _exchangeManager = _serviceProvider.GetRequiredService<IExchangeManager>();
            
            _logger.LogInformation("User {Username} authenticated on channel {ChannelId}", username, Id);
        }
        else
        {
            await SendErrorAsync(ErrorCode.InvalidCredentials, "Authentication failed", frame.CorrelationId, cancellationToken);
        }
    }
    
    private async Task HandleDeclareExchangeAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadString(out var exchangeName) ||
            !reader.TryReadInt32(out var exchangeTypeInt) ||
            !reader.TryReadString(out var argumentsJson))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid declare exchange frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var exchangeType = (ExchangeType)exchangeTypeInt;
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson) ?? new();
        
        var config = new ExchangeConfig
        {
            Name = exchangeName,
            Type = exchangeType,
            Durable = frame.IsDurable,
            Arguments = arguments,
            CreatedBy = Username!
        };
        
        try
        {
            await _exchangeManager!.CreateExchangeAsync(config);
            await SendFrameAsync(FrameType.DeclareExchange, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
        catch (ResourceExistsException)
        {
            // Exchange already exists, which is fine for declare
            await SendFrameAsync(FrameType.DeclareExchange, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
    }
    
    private async Task HandleDeclareQueueAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadString(out var queueName) ||
            !reader.TryReadString(out var argumentsJson))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid declare queue frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson) ?? new();
        
        var config = new QueueConfig
        {
            Name = queueName,
            Durable = frame.IsDurable,
            Arguments = arguments,
            CreatedBy = Username!
        };
        
        // Parse common queue arguments
        if (arguments.TryGetValue("x-max-length", out var maxLength))
            config.MaxLength = Convert.ToInt32(maxLength);
        
        if (arguments.TryGetValue("x-message-ttl", out var messageTtl))
            config.MessageTtl = TimeSpan.FromMilliseconds(Convert.ToInt64(messageTtl));
        
        if (arguments.TryGetValue("x-max-priority", out var maxPriority))
            config.MaxPriority = Convert.ToByte(maxPriority);
        
        if (arguments.TryGetValue("x-dead-letter-exchange", out var dlx))
            config.DeadLetterExchange = dlx.ToString();
        
        try
        {
            await _queueManager!.CreateQueueAsync(config);
            await SendFrameAsync(FrameType.DeclareQueue, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
        catch (ResourceExistsException)
        {
            // Queue already exists, which is fine for declare
            await SendFrameAsync(FrameType.DeclareQueue, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
    }
    
    private async Task HandleBindAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadString(out var queueName) ||
            !reader.TryReadString(out var exchangeName) ||
            !reader.TryReadString(out var routingKey) ||
            !reader.TryReadString(out var argumentsJson))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid bind frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson) ?? new();
        
        var exchange = await _exchangeManager!.GetExchangeAsync(exchangeName);
        if (exchange == null)
        {
            await SendErrorAsync(ErrorCode.ExchangeNotFound, $"Exchange '{exchangeName}' not found", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var queue = await _queueManager!.GetQueueAsync(queueName);
        if (queue == null)
        {
            await SendErrorAsync(ErrorCode.QueueNotFound, $"Queue '{queueName}' not found", frame.CorrelationId, cancellationToken);
            return;
        }
        
        await exchange.BindAsync(queueName, routingKey, arguments);
        await SendFrameAsync(FrameType.Bind, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
    
    private async Task HandleUnbindAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadString(out var queueName) ||
            !reader.TryReadString(out var exchangeName) ||
            !reader.TryReadString(out var routingKey))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid unbind frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var exchange = await _exchangeManager!.GetExchangeAsync(exchangeName);
        if (exchange == null)
        {
            await SendErrorAsync(ErrorCode.ExchangeNotFound, $"Exchange '{exchangeName}' not found", frame.CorrelationId, cancellationToken);
            return;
        }
        
        await exchange.UnbindAsync(queueName, routingKey);
        await SendFrameAsync(FrameType.Unbind, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
    
    private async Task HandlePublishAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadString(out var exchangeName) ||
            !reader.TryReadString(out var routingKey) ||
            !reader.TryReadString(out var propertiesJson) ||
            !reader.TryReadBytes(out var body))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid publish frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var properties = JsonSerializer.Deserialize<MessageProperties>(propertiesJson) ?? new MessageProperties();
        
        var message = new Message(body.ToArray(), exchangeName, routingKey)
        {
            Properties = properties
        };
        
        var exchange = await _exchangeManager!.GetExchangeAsync(exchangeName);
        if (exchange == null)
        {
            await SendErrorAsync(ErrorCode.ExchangeNotFound, $"Exchange '{exchangeName}' not found", frame.CorrelationId, cancellationToken);
            return;
        }
        
        await exchange.PublishAsync(message, cancellationToken);
        
        // Send confirmation if requested
        await SendFrameAsync(FrameType.Confirm, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
    
    private async Task HandleConsumeAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadString(out var queueName) ||
            !reader.TryReadString(out var consumerTag) ||
            !reader.TryReadInt32(out var prefetch))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid consume frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var queue = await _queueManager!.GetQueueAsync(queueName);
        if (queue == null)
        {
            await SendErrorAsync(ErrorCode.QueueNotFound, $"Queue '{queueName}' not found", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var consumer = new ChannelConsumer(consumerTag, queueName, prefetch, this, _logger);
        
        lock (_lock)
        {
            _consumers[consumerTag] = consumer;
        }
        
        queue.AddConsumer(consumer);
        
        await SendFrameAsync(FrameType.Consume, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
        
        _logger.LogDebug("Started consumer {ConsumerTag} on queue {QueueName} with prefetch {Prefetch}", 
            consumerTag, queueName, prefetch);
    }
    
    private async Task HandleAckAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadInt64(out var deliveryTag))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid ack frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        // Find the consumer and queue for this delivery tag
        // TODO: Implement proper delivery tag to queue mapping
        await SendFrameAsync(FrameType.Ack, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
    
    private async Task HandleNackAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadInt64(out var deliveryTag) ||
            !reader.TryReadInt32(out var requeueInt))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid nack frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var requeue = requeueInt != 0;
        
        // TODO: Implement proper nack handling
        await SendFrameAsync(FrameType.Nack, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
    
    private async Task HandleRejectAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadInt64(out var deliveryTag) ||
            !reader.TryReadInt32(out var requeueInt))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid reject frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        var requeue = requeueInt != 0;
        
        // TODO: Implement proper reject handling
        await SendFrameAsync(FrameType.Reject, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
    
    private async Task HandleSetPrefetchAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        var reader = new TlvReader(frame.Payload.Span);
        
        if (!reader.TryReadInt32(out var prefetch))
        {
            await SendErrorAsync(ErrorCode.SyntaxError, "Invalid set prefetch frame", frame.CorrelationId, cancellationToken);
            return;
        }
        
        // TODO: Update prefetch for all consumers on this channel
        await SendFrameAsync(FrameType.SetPrefetch, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
    
    private async Task HandleHeartbeatAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        // Echo back heartbeat
        await SendFrameAsync(FrameType.Heartbeat, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
    
    private async Task HandleCloseAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        await SendFrameAsync(FrameType.Close, FrameFlags.None, frame.CorrelationId, ReadOnlyMemory<byte>.Empty, cancellationToken);
        await _connection.CloseAsync();
    }
    
    private async Task HandleStatsAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        
        // TODO: Implement stats collection
        var stats = new BrokerStats();
        var statsJson = JsonSerializer.Serialize(stats);
        var statsBytes = System.Text.Encoding.UTF8.GetBytes(statsJson);
        
        await SendFrameAsync(FrameType.Stats, FrameFlags.None, frame.CorrelationId, statsBytes, cancellationToken);
    }
    
    public async Task SendFrameAsync(FrameType frameType, FrameFlags flags, ulong correlationId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        
        FrameCodec.WriteFrame(_connection.Output, frameType, flags, correlationId, payload.Span);
        await _connection.Output.FlushAsync(cancellationToken);
    }
    
    public async Task SendErrorAsync(ErrorCode errorCode, string message, ulong correlationId, CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        var tlvWriter = new TlvWriter(writer);
        
        tlvWriter.WriteInt32((int)errorCode);
        tlvWriter.WriteString(message);
        
        await SendFrameAsync(FrameType.Error, FrameFlags.None, correlationId, writer.WrittenMemory, cancellationToken);
        
        _logger.LogWarning("Sent error {ErrorCode}: {Message} for correlation {CorrelationId}", errorCode, message, correlationId);
    }
    
    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
        {
            throw new AuthenticationException("Channel is not authenticated");
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        lock (_lock)
        {
            foreach (var consumer in _consumers.Values)
            {
                consumer.Dispose();
            }
            _consumers.Clear();
        }
    }
}

/// <summary>
/// Consumer implementation for a channel
/// </summary>
public class ChannelConsumer : IConsumer, IDisposable
{
    private readonly Channel _channel;
    private readonly ILogger _logger;
    private readonly AtomicCounter _unackedCounter = new();
    private volatile bool _disposed;
    
    public string ConsumerTag { get; }
    public string QueueName { get; }
    public int Prefetch { get; }
    public int UnackedCount => (int)_unackedCounter.Current;
    public bool IsActive => !_disposed && _channel.Connection.IsConnected;
    
    public ChannelConsumer(string consumerTag, string queueName, int prefetch, Channel channel, ILogger logger)
    {
        ConsumerTag = consumerTag;
        QueueName = queueName;
        Prefetch = prefetch;
        _channel = channel;
        _logger = logger;
    }
    
    public Task<bool> CanDeliverAsync()
    {
        return Task.FromResult(IsActive && UnackedCount < Prefetch);
    }
    
    public async Task DeliverAsync(Message message, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        
        _unackedCounter.Next();
        
        try
        {
            var writer = new ArrayBufferWriter<byte>();
            var tlvWriter = new TlvWriter(writer);
            
            tlvWriter.WriteString(ConsumerTag);
            tlvWriter.WriteInt64((long)message.DeliveryTag);
            tlvWriter.WriteString(message.Exchange);
            tlvWriter.WriteString(message.RoutingKey);
            tlvWriter.WriteString(JsonSerializer.Serialize(message.Properties));
            tlvWriter.WriteBytes(message.Body.Span);
            
            await _channel.SendFrameAsync(FrameType.Deliver, FrameFlags.None, 0, writer.WrittenMemory, cancellationToken);
            
            _logger.LogDebug("Delivered message {MessageId} to consumer {ConsumerTag}", 
                message.Properties.MessageId, ConsumerTag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error delivering message {MessageId} to consumer {ConsumerTag}", 
                message.Properties.MessageId, ConsumerTag);
            
            // Decrease unacked count on delivery failure
            Interlocked.Decrement(ref _unackedCounter._value);
        }
    }
    
    public Task OnAckAsync(ulong deliveryTag)
    {
        Interlocked.Decrement(ref _unackedCounter._value);
        return Task.CompletedTask;
    }
    
    public Task OnNackAsync(ulong deliveryTag)
    {
        Interlocked.Decrement(ref _unackedCounter._value);
        return Task.CompletedTask;
    }
    
    public void Dispose()
    {
        _disposed = true;
    }
}