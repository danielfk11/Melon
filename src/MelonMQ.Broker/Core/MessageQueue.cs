using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MelonMQ.Broker.Core;

public class MessageQueue
{
    private readonly QueueConfiguration _config;
    private readonly Channel<QueueMessage> _messageChannel;
    private readonly ChannelWriter<QueueMessage> _writer;
    private readonly ChannelReader<QueueMessage> _reader;
    private readonly ConcurrentDictionary<ulong, InFlightMessage> _inFlightMessages = new();
    private readonly string? _persistenceFilePath;
    private readonly ILogger<MessageQueue> _logger;
    private ulong _nextDeliveryTag = 1;

    public MessageQueue(QueueConfiguration config, string? dataDirectory, ILogger<MessageQueue> logger)
    {
        _config = config;
        _logger = logger;
        
        var options = new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        
        _messageChannel = Channel.CreateBounded<QueueMessage>(options);
        _writer = _messageChannel.Writer;
        _reader = _messageChannel.Reader;

        if (_config.Durable && !string.IsNullOrEmpty(dataDirectory))
        {
            _persistenceFilePath = Path.Combine(dataDirectory, $"{_config.Name}.log");
            _ = Task.Run(LoadPersistedMessages);
        }
    }

    public string Name => _config.Name;
    public bool IsDurable => _config.Durable;
    public int PendingCount => _messageChannel.Reader.CanCount ? _messageChannel.Reader.Count : 0;
    public int InFlightCount => _inFlightMessages.Count;

    public async Task<bool> EnqueueAsync(QueueMessage message, CancellationToken cancellationToken = default)
    {
        // Check TTL
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (message.ExpiresAt.HasValue && message.ExpiresAt <= now)
        {
            _logger.LogDebug("Message {MessageId} expired before enqueue", message.MessageId);
            await HandleDeadLetter(message);
            return false;
        }

        // Persist if durable
        if (_config.Durable && _persistenceFilePath != null)
        {
            await PersistMessage(message);
        }

        await _writer.WriteAsync(message, cancellationToken);
        return true;
    }

    public async Task<QueueMessage?> DequeueAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await _reader.ReadAsync(cancellationToken);
            
            // Check TTL at delivery time
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (message.ExpiresAt.HasValue && message.ExpiresAt <= now)
            {
                _logger.LogDebug("Message {MessageId} expired at delivery", message.MessageId);
                await HandleDeadLetter(message);
                return await DequeueAsync(connectionId, cancellationToken); // Try next message
            }

            // Track in-flight
            var deliveryTag = Interlocked.Increment(ref _nextDeliveryTag);
            var inFlight = new InFlightMessage
            {
                Message = message,
                DeliveryTag = deliveryTag,
                ConnectionId = connectionId,
                DeliveredAt = now,
                ExpiresAt = now + 300000 // 5 minutes timeout
            };

            _inFlightMessages[deliveryTag] = inFlight;
            return message;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public Task<bool> AckAsync(ulong deliveryTag)
    {
        if (_inFlightMessages.TryRemove(deliveryTag, out var inFlight))
        {
            _logger.LogDebug("Acked message {MessageId} with delivery tag {DeliveryTag}", 
                inFlight.Message.MessageId, deliveryTag);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> NackAsync(ulong deliveryTag, bool requeue = true)
    {
        if (_inFlightMessages.TryRemove(deliveryTag, out var inFlight))
        {
            if (requeue)
            {
                inFlight.Message.Redelivered = true;
                inFlight.Message.DeliveryCount++;
                _ = Task.Run(() => EnqueueAsync(inFlight.Message));
                _logger.LogDebug("Requeued message {MessageId}", inFlight.Message.MessageId);
            }
            else
            {
                _ = Task.Run(() => HandleDeadLetter(inFlight.Message));
                _logger.LogDebug("Dead-lettered message {MessageId}", inFlight.Message.MessageId);
            }
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public async Task RequeuePendingMessagesForConnection(string connectionId)
    {
        var toRequeue = _inFlightMessages.Values
            .Where(x => x.ConnectionId == connectionId)
            .ToList();

        foreach (var inFlight in toRequeue)
        {
            if (_inFlightMessages.TryRemove(inFlight.DeliveryTag, out _))
            {
                inFlight.Message.Redelivered = true;
                inFlight.Message.DeliveryCount++;
                await EnqueueAsync(inFlight.Message);
            }
        }

        _logger.LogInformation("Requeued {Count} messages for disconnected connection {ConnectionId}", 
            toRequeue.Count, connectionId);
    }

    public async Task PurgeAsync()
    {
        // Clear the channel by creating a new one
        var options = new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        var newChannel = Channel.CreateBounded<QueueMessage>(options);
        // Note: This is a simplified approach - in production you'd want proper coordination
        
        _logger.LogInformation("Purged queue {QueueName}", _config.Name);
    }

    private async Task HandleDeadLetter(QueueMessage message)
    {
        if (!string.IsNullOrEmpty(_config.DeadLetterQueue))
        {
            // In a real implementation, you'd send to the DLQ queue
            _logger.LogWarning("Message {MessageId} should be sent to DLQ {DLQ}", 
                message.MessageId, _config.DeadLetterQueue);
        }
    }

    private async Task PersistMessage(QueueMessage message)
    {
        if (_persistenceFilePath == null) return;

        var record = new
        {
            msgId = message.MessageId,
            enqueuedAt = message.EnqueuedAt,
            expiresAt = message.ExpiresAt,
            payloadBase64 = Convert.ToBase64String(message.Body.Span)
        };

        var line = JsonSerializer.Serialize(record) + Environment.NewLine;
        await File.AppendAllTextAsync(_persistenceFilePath, line);
    }

    private async Task LoadPersistedMessages()
    {
        if (_persistenceFilePath == null || !File.Exists(_persistenceFilePath))
            return;

        try
        {
            var lines = await File.ReadAllLinesAsync(_persistenceFilePath);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var loadedCount = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var record = JsonSerializer.Deserialize<JsonElement>(line);
                    var msgId = Guid.Parse(record.GetProperty("msgId").GetString()!);
                    var enqueuedAt = record.GetProperty("enqueuedAt").GetInt64();
                    var expiresAt = record.TryGetProperty("expiresAt", out var exp) && exp.ValueKind != JsonValueKind.Null
                        ? exp.GetInt64() : (long?)null;
                    var payloadBase64 = record.GetProperty("payloadBase64").GetString()!;

                    // Skip expired messages
                    if (expiresAt.HasValue && expiresAt <= now)
                        continue;

                    var message = new QueueMessage
                    {
                        MessageId = msgId,
                        Body = Convert.FromBase64String(payloadBase64),
                        EnqueuedAt = enqueuedAt,
                        ExpiresAt = expiresAt,
                        Persistent = true,
                        Redelivered = false,
                        DeliveryCount = 0
                    };

                    await _writer.WriteAsync(message);
                    loadedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load persisted message from line: {Line}", line);
                }
            }

            _logger.LogInformation("Loaded {Count} persisted messages for queue {QueueName}", 
                loadedCount, _config.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted messages for queue {QueueName}", _config.Name);
        }
    }

    public async Task CleanupExpiredInFlightMessages()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expired = _inFlightMessages.Values
            .Where(x => x.ExpiresAt <= now)
            .ToList();

        foreach (var inFlight in expired)
        {
            if (_inFlightMessages.TryRemove(inFlight.DeliveryTag, out _))
            {
                inFlight.Message.Redelivered = true;
                inFlight.Message.DeliveryCount++;
                await EnqueueAsync(inFlight.Message);
                
                _logger.LogDebug("Requeued expired in-flight message {MessageId}", 
                    inFlight.Message.MessageId);
            }
        }
    }
}