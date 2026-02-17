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
    private readonly Func<string, MessageQueue?>? _queueResolver;
    private readonly ConcurrentDictionary<Guid, bool> _ackedMessageIds = new();
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);
    private long _persistenceFileSize = 0;
    private readonly long _compactionThresholdBytes;
    private ulong _nextDeliveryTag = 1;
    private int _pendingCount = 0;
    private long _lastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long _createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public MessageQueue(QueueConfiguration config, string? dataDirectory, ILogger<MessageQueue> logger, Func<string, MessageQueue?>? queueResolver = null, int channelCapacity = 10000, long compactionThresholdMB = 100)
    {
        _config = config;
        _logger = logger;
        _queueResolver = queueResolver;
        _compactionThresholdBytes = compactionThresholdMB * 1024 * 1024;
        
        var options = new BoundedChannelOptions(channelCapacity)
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
            if (File.Exists(_persistenceFilePath))
            {
                _persistenceFileSize = new FileInfo(_persistenceFilePath).Length;
            }
            _ = Task.Run(LoadPersistedMessages);
        }
    }

    public string Name => _config.Name;
    public bool IsDurable => _config.Durable;
    public int PendingCount => _pendingCount;
    public int InFlightCount => _inFlightMessages.Count;
    public long LastActivityAt => _lastActivityAt;
    public long CreatedAt => _createdAt;

    /// <summary>
    /// Returns true if the queue has no pending or in-flight messages.
    /// </summary>
    public bool IsEmpty => PendingCount == 0 && InFlightCount == 0;

    /// <summary>
    /// Returns the duration in milliseconds since last activity.
    /// </summary>
    public long IdleTimeMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastActivityAt;

    private void TouchActivity()
    {
        Interlocked.Exchange(ref _lastActivityAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

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
        Interlocked.Increment(ref _pendingCount);
        TouchActivity();
        return true;
    }

    public async Task<QueueMessage?> DequeueAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await _reader.ReadAsync(cancellationToken);
            
            // Check TTL at delivery time
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (message.ExpiresAt.HasValue && message.ExpiresAt <= now)
            {
                _logger.LogDebug("Message {MessageId} expired at delivery", message.MessageId);
                await HandleDeadLetter(message);
                Interlocked.Decrement(ref _pendingCount);
                continue; // Try next message instead of recursion
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
            Interlocked.Decrement(ref _pendingCount);
            TouchActivity();
            return message;
        }

        return null;
    }

    public Task<bool> AckAsync(ulong deliveryTag)
    {
        if (_inFlightMessages.TryRemove(deliveryTag, out var inFlight))
        {
            // Track acked message for persistence compaction
            if (_config.Durable)
            {
                _ackedMessageIds[inFlight.Message.MessageId] = true;
            }

            _logger.LogDebug("Acked message {MessageId} with delivery tag {DeliveryTag}", 
                inFlight.Message.MessageId, deliveryTag);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public async Task<bool> NackAsync(ulong deliveryTag, bool requeue = true)
    {
        if (_inFlightMessages.TryRemove(deliveryTag, out var inFlight))
        {
            if (requeue)
            {
                inFlight.Message.Redelivered = true;
                inFlight.Message.DeliveryCount++;
                try
                {
                    await EnqueueAsync(inFlight.Message);
                    _logger.LogDebug("Requeued message {MessageId}", inFlight.Message.MessageId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to requeue message {MessageId}, sending to DLQ", inFlight.Message.MessageId);
                    await HandleDeadLetter(inFlight.Message);
                }
            }
            else
            {
                await HandleDeadLetter(inFlight.Message);
                _logger.LogDebug("Dead-lettered message {MessageId}", inFlight.Message.MessageId);
            }
            return true;
        }
        return false;
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

    public Task PurgeAsync()
    {
        // Read and discard all messages from the channel
        var count = 0;
        while (_reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            count++;
        }
        // Garante que não fique negativo
        Interlocked.Exchange(ref _pendingCount, Math.Max(0, _pendingCount));
        _logger.LogInformation("Purged {Count} messages from queue {QueueName}", count, _config.Name);
        return Task.CompletedTask;
    }

    private async Task HandleDeadLetter(QueueMessage message)
    {
        if (!string.IsNullOrEmpty(_config.DeadLetterQueue) && _queueResolver != null)
        {
            var dlq = _queueResolver(_config.DeadLetterQueue);
            if (dlq != null)
            {
                message.Redelivered = true;
                await dlq.EnqueueAsync(message);
                _logger.LogInformation("Message {MessageId} sent to DLQ {DLQ}", 
                    message.MessageId, _config.DeadLetterQueue);
                return;
            }
            
            _logger.LogWarning("DLQ '{DLQ}' not found for message {MessageId}. Message discarded.", 
                _config.DeadLetterQueue, message.MessageId);
        }
        else if (!string.IsNullOrEmpty(_config.DeadLetterQueue))
        {
            _logger.LogWarning("DLQ '{DLQ}' configured but queue resolver not available. Message {MessageId} discarded.", 
                _config.DeadLetterQueue, message.MessageId);
        }
        else
        {
            _logger.LogDebug("No DLQ configured for queue {QueueName}. Message {MessageId} discarded.", 
                _config.Name, message.MessageId);
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
        
        await _persistenceLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_persistenceFilePath, line);
            _persistenceFileSize += Encoding.UTF8.GetByteCount(line);
            
            // Trigger compaction if file exceeds threshold
            if (_persistenceFileSize > _compactionThresholdBytes)
            {
                await CompactPersistenceLogAsync();
            }
        }
        finally
        {
            _persistenceLock.Release();
        }
    }

    /// <summary>
    /// Compacts the persistence log by removing acked and expired messages.
    /// Only retains messages that are still pending or in-flight.
    /// </summary>
    public async Task CompactPersistenceLogAsync()
    {
        if (_persistenceFilePath == null) return;

        try
        {
            _logger.LogInformation("Starting persistence compaction for queue {QueueName}", _config.Name);
            
            var lines = await File.ReadAllLinesAsync(_persistenceFilePath);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var survivingLines = new List<string>();
            var removedCount = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var record = JsonSerializer.Deserialize<JsonElement>(line);
                    var msgId = Guid.Parse(record.GetProperty("msgId").GetString()!);
                    
                    // Skip acked messages
                    if (_ackedMessageIds.ContainsKey(msgId))
                    {
                        removedCount++;
                        continue;
                    }

                    // Skip expired messages
                    if (record.TryGetProperty("expiresAt", out var exp) && 
                        exp.ValueKind != JsonValueKind.Null &&
                        exp.GetInt64() <= now)
                    {
                        removedCount++;
                        continue;
                    }

                    survivingLines.Add(line);
                }
                catch
                {
                    // Skip corrupted lines
                    removedCount++;
                }
            }

            // Write compacted file atomically
            var tempPath = _persistenceFilePath + ".tmp";
            await File.WriteAllLinesAsync(tempPath, survivingLines);
            File.Move(tempPath, _persistenceFilePath, overwrite: true);

            // Clear acked IDs that were just compacted
            _ackedMessageIds.Clear();
            
            // Update tracked file size
            _persistenceFileSize = new FileInfo(_persistenceFilePath).Length;

            _logger.LogInformation("Compacted persistence log for queue {QueueName}: removed {Removed} entries, {Remaining} remaining", 
                _config.Name, removedCount, survivingLines.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error compacting persistence log for queue {QueueName}", _config.Name);
        }
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