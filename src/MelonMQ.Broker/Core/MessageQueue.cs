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
    private ulong _nextDeliveryTag = 0;
    private int _pendingCount = 0;
    private long _lastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long _createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Persistence batching
    private readonly Channel<string>? _persistenceBatchChannel;
    private readonly Task? _persistenceBatchTask;
    private readonly int _batchFlushMs;

    // Load gate: blocks operations until persisted messages are loaded
    private readonly TaskCompletionSource _loadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _needsLoad;

    public MessageQueue(QueueConfiguration config, string? dataDirectory, ILogger<MessageQueue> logger, Func<string, MessageQueue?>? queueResolver = null, int channelCapacity = 10000, long compactionThresholdMB = 100, int batchFlushMs = 10)
    {
        _config = config;
        _logger = logger;
        _queueResolver = queueResolver;
        _compactionThresholdBytes = compactionThresholdMB * 1024 * 1024;
        _batchFlushMs = batchFlushMs;
        
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

            // Setup persistence batching channel
            _persistenceBatchChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _persistenceBatchTask = Task.Run(PersistenceBatchLoop);

            _needsLoad = true;
            _ = Task.Run(LoadPersistedMessages);
        }
        else
        {
            _needsLoad = false;
            _loadGate.SetResult();
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
        // Wait for persisted messages to finish loading before accepting new messages
        await _loadGate.Task;

        // Check TTL
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (message.ExpiresAt.HasValue && message.ExpiresAt <= now)
        {
            _logger.LogDebug("Message {MessageId} expired before enqueue", message.MessageId);
            await HandleDeadLetter(message);
            return false;
        }

        // Persist if durable (batched)
        if (_config.Durable && _persistenceBatchChannel != null)
        {
            var record = new
            {
                msgId = message.MessageId,
                enqueuedAt = message.EnqueuedAt,
                expiresAt = message.ExpiresAt,
                payloadBase64 = Convert.ToBase64String(message.Body.Span)
            };
            var line = JsonSerializer.Serialize(record);
            await _persistenceBatchChannel.Writer.WriteAsync(line, cancellationToken);
        }

        await _writer.WriteAsync(message, cancellationToken);
        Interlocked.Increment(ref _pendingCount);
        TouchActivity();
        return true;
    }

    public async Task<(QueueMessage Message, ulong DeliveryTag)?> DequeueAsync(string connectionId, CancellationToken cancellationToken = default)
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
            return (message, deliveryTag);
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
            inFlight.Message.Redelivered = true;
            inFlight.Message.DeliveryCount++;

            // Enforce max delivery count — send to DLQ if exceeded
            if (_config.MaxDeliveryCount > 0 && inFlight.Message.DeliveryCount >= _config.MaxDeliveryCount)
            {
                _logger.LogWarning("Message {MessageId} exceeded max delivery count ({MaxDeliveryCount}), sending to DLQ",
                    inFlight.Message.MessageId, _config.MaxDeliveryCount);
                await HandleDeadLetter(inFlight.Message);
                return true;
            }

            if (requeue)
            {
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
        // Atomically ensure pending count doesn't go negative
        int current;
        do
        {
            current = Volatile.Read(ref _pendingCount);
            if (current >= 0) break;
        } while (Interlocked.CompareExchange(ref _pendingCount, 0, current) != current);

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

    /// <summary>
    /// Background loop that batches persistence writes for throughput.
    /// Accumulates lines and flushes every _batchFlushMs or when backlog is large.
    /// </summary>
    private async Task PersistenceBatchLoop()
    {
        if (_persistenceFilePath == null || _persistenceBatchChannel == null) return;

        var batch = new List<string>(64);
        try
        {
            while (await _persistenceBatchChannel.Reader.WaitToReadAsync())
            {
                batch.Clear();

                // Drain all available lines
                while (_persistenceBatchChannel.Reader.TryRead(out var line))
                {
                    batch.Add(line);
                }

                if (batch.Count == 0) continue;

                // Wait a small interval to gather more writes
                if (_batchFlushMs > 0)
                {
                    await Task.Delay(_batchFlushMs);
                    while (_persistenceBatchChannel.Reader.TryRead(out var line))
                    {
                        batch.Add(line);
                    }
                }

                // Flush batch to disk
                await _persistenceLock.WaitAsync();
                try
                {
                    var sb = new StringBuilder();
                    foreach (var l in batch)
                    {
                        sb.AppendLine(l);
                    }
                    var text = sb.ToString();
                    await File.AppendAllTextAsync(_persistenceFilePath, text);
                    _persistenceFileSize += Encoding.UTF8.GetByteCount(text);

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persistence batch loop error for queue {QueueName}", _config.Name);
        }
    }

    /// <summary>
    /// Compacts the persistence log by removing acked and expired messages.
    /// Uses streaming to avoid loading entire file into memory.
    /// </summary>
    public async Task CompactPersistenceLogAsync()
    {
        if (_persistenceFilePath == null) return;

        try
        {
            _logger.LogInformation("Starting persistence compaction for queue {QueueName}", _config.Name);
            
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var tempPath = _persistenceFilePath + ".tmp";
            var removedCount = 0;
            var survivingCount = 0;

            using (var reader = new StreamReader(_persistenceFilePath))
            using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
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

                        await writer.WriteLineAsync(line);
                        survivingCount++;
                    }
                    catch
                    {
                        // Skip corrupted lines
                        removedCount++;
                    }
                }
            }

            File.Move(tempPath, _persistenceFilePath, overwrite: true);

            // Clear acked IDs that were just compacted
            _ackedMessageIds.Clear();
            
            // Update tracked file size
            _persistenceFileSize = new FileInfo(_persistenceFilePath).Length;

            _logger.LogInformation("Compacted persistence log for queue {QueueName}: removed {Removed} entries, {Remaining} remaining", 
                _config.Name, removedCount, survivingCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error compacting persistence log for queue {QueueName}", _config.Name);
        }
    }

    private async Task LoadPersistedMessages()
    {
        if (_persistenceFilePath == null || !File.Exists(_persistenceFilePath))
        {
            _loadGate.TrySetResult();
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var loadedCount = 0;

            using var reader = new StreamReader(_persistenceFilePath);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
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
                    Interlocked.Increment(ref _pendingCount);
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
        finally
        {
            _loadGate.TrySetResult();
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

    /// <summary>
    /// Returns the set of in-flight delivery tags for cleanup by TcpServer.
    /// </summary>
    public IEnumerable<ulong> GetInFlightDeliveryTags()
    {
        return _inFlightMessages.Keys;
    }

    /// <summary>
    /// Deletes the persistence file for this queue (used when queue is deleted).
    /// </summary>
    public void DeletePersistenceFile()
    {
        if (_persistenceFilePath != null && File.Exists(_persistenceFilePath))
        {
            try
            {
                File.Delete(_persistenceFilePath);
                _logger.LogInformation("Deleted persistence file for queue {QueueName}", _config.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete persistence file for queue {QueueName}", _config.Name);
            }
        }
    }
}