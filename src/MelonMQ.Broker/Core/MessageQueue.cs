using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MelonMQ.Broker.Core;

public class MessageQueue : IDisposable
{
    private const string EnqueueOperation = "enqueue";
    private const string AckOperation = "ack";
    private const string PurgeOperation = "purge";

    private readonly record struct PersistenceRecord(string Line, TaskCompletionSource<bool>? FlushCompletion);

    private readonly QueueConfiguration _config;
    private readonly Channel<QueueMessage> _messageChannel;
    private readonly ChannelWriter<QueueMessage> _writer;
    private readonly ChannelReader<QueueMessage> _reader;
    private readonly ConcurrentDictionary<ulong, InFlightMessage> _inFlightMessages = new();
    private readonly ConcurrentDictionary<Guid, byte> _ackedMessageIds = new();
    private readonly string? _persistenceFilePath;
    private readonly ILogger<MessageQueue> _logger;
    private readonly Func<string, MessageQueue?>? _queueResolver;
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);
    private long _persistenceFileSize;
    private readonly long _compactionThresholdBytes;
    private ulong _nextDeliveryTag;
    private int _pendingCount;
    private long _lastActivityAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private readonly Channel<PersistenceRecord>? _persistenceBatchChannel;
    private readonly Task? _persistenceBatchTask;
    private readonly int _batchFlushMs;

    private readonly TaskCompletionSource _loadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public MessageQueue(
        QueueConfiguration config,
        string? dataDirectory,
        ILogger<MessageQueue> logger,
        Func<string, MessageQueue?>? queueResolver = null,
        int channelCapacity = 10000,
        long compactionThresholdMB = 100,
        int batchFlushMs = 10)
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

            var persistenceCapacity = Math.Max(256, channelCapacity);
            _persistenceBatchChannel = Channel.CreateBounded<PersistenceRecord>(new BoundedChannelOptions(persistenceCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

            _persistenceBatchTask = Task.Run(PersistenceBatchLoop);
            _ = Task.Run(LoadPersistedMessages);
        }
        else
        {
            _loadGate.SetResult();
        }
    }

    public string Name => _config.Name;
    public bool IsDurable => _config.Durable;
    public int? DefaultTtlMs => _config.DefaultTtlMs;
    public int PendingCount => _pendingCount;
    public int InFlightCount => _inFlightMessages.Count;
    public long LastActivityAt => _lastActivityAt;
    public long CreatedAt => _createdAt;
    public bool IsEmpty => PendingCount == 0 && InFlightCount == 0;
    public long IdleTimeMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastActivityAt;

    private void TouchActivity()
    {
        Interlocked.Exchange(ref _lastActivityAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task<bool> EnqueueAsync(QueueMessage message, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(MessageQueue));
        }

        await _loadGate.Task;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!message.ExpiresAt.HasValue && _config.DefaultTtlMs.HasValue)
        {
            message.ExpiresAt = now + _config.DefaultTtlMs.Value;
        }

        if (message.ExpiresAt.HasValue && message.ExpiresAt <= now)
        {
            _logger.LogDebug("Message {MessageId} expired before enqueue", message.MessageId);
            await HandleDeadLetter(message);
            return false;
        }

        if (_ackedMessageIds.ContainsKey(message.MessageId))
        {
            _logger.LogDebug("Message {MessageId} is already acked and will not be enqueued", message.MessageId);
            return false;
        }

        if (_config.Durable)
        {
            await QueuePersistenceRecordAsync(CreateEnqueueRecord(message), waitForFlush: false, cancellationToken);
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

            if (_ackedMessageIds.ContainsKey(message.MessageId))
            {
                Interlocked.Decrement(ref _pendingCount);
                continue;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (message.ExpiresAt.HasValue && message.ExpiresAt <= now)
            {
                _logger.LogDebug("Message {MessageId} expired at delivery", message.MessageId);
                await HandleDeadLetter(message);
                Interlocked.Decrement(ref _pendingCount);
                continue;
            }

            var deliveryTag = Interlocked.Increment(ref _nextDeliveryTag);
            _inFlightMessages[deliveryTag] = new InFlightMessage
            {
                Message = message,
                DeliveryTag = deliveryTag,
                ConnectionId = connectionId,
                DeliveredAt = now,
                ExpiresAt = now + 300000
            };

            Interlocked.Decrement(ref _pendingCount);
            TouchActivity();
            return (message, deliveryTag);
        }

        return null;
    }

    public async Task<bool> AckAsync(ulong deliveryTag)
    {
        if (!_inFlightMessages.TryRemove(deliveryTag, out var inFlight))
        {
            return false;
        }

        _ackedMessageIds[inFlight.Message.MessageId] = 0;

        if (_config.Durable)
        {
            try
            {
                await AppendCriticalPersistenceRecordAsync(CreateAckRecord(inFlight.Message.MessageId));
            }
            catch (Exception ex)
            {
                _inFlightMessages[deliveryTag] = inFlight;
                _logger.LogError(ex, "Failed to persist ACK for message {MessageId}", inFlight.Message.MessageId);
                return false;
            }
        }

        _logger.LogDebug("Acked message {MessageId} with delivery tag {DeliveryTag}",
            inFlight.Message.MessageId, deliveryTag);
        TouchActivity();
        return true;
    }

    public async Task<bool> AckByMessageIdAsync(Guid messageId)
    {
        _ackedMessageIds[messageId] = 0;

        foreach (var kvp in _inFlightMessages.ToArray())
        {
            if (kvp.Value.Message.MessageId == messageId)
            {
                _inFlightMessages.TryRemove(kvp.Key, out _);
            }
        }

        if (_config.Durable)
        {
            await AppendCriticalPersistenceRecordAsync(CreateAckRecord(messageId));
        }

        return true;
    }

    public bool TryGetInFlightMessageId(ulong deliveryTag, out Guid messageId)
    {
        if (_inFlightMessages.TryGetValue(deliveryTag, out var inFlight))
        {
            messageId = inFlight.Message.MessageId;
            return true;
        }

        messageId = Guid.Empty;
        return false;
    }

    public async Task<bool> NackAsync(ulong deliveryTag, bool requeue = true)
    {
        if (!_inFlightMessages.TryRemove(deliveryTag, out var inFlight))
        {
            return false;
        }

        inFlight.Message.Redelivered = true;
        inFlight.Message.DeliveryCount++;

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

    /// <summary>
    /// Requeues all in-flight messages whose connection is no longer active.
    /// Called when a new consumer subscribes so stale in-flight messages from
    /// dead connections are recovered immediately instead of waiting for the
    /// 5-minute GC expiry window.
    /// </summary>
    public async Task RequeuePendingMessagesForDeadConnections(ISet<string> activeConnectionIds)
    {
        var toRequeue = _inFlightMessages.Values
            .Where(x => !activeConnectionIds.Contains(x.ConnectionId))
            .ToList();

        if (toRequeue.Count == 0) return;

        foreach (var inFlight in toRequeue)
        {
            if (_inFlightMessages.TryRemove(inFlight.DeliveryTag, out _))
            {
                inFlight.Message.Redelivered = true;
                inFlight.Message.DeliveryCount++;
                await EnqueueAsync(inFlight.Message);
            }
        }

        _logger.LogInformation(
            "Recovered {Count} in-flight messages from dead connections in queue '{Queue}'",
            toRequeue.Count, _config.Name);
    }

    public async Task PurgeAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.Task;

        var pendingPurged = 0;
        while (_reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            pendingPurged++;
        }

        var inFlightPurged = 0;
        foreach (var deliveryTag in _inFlightMessages.Keys.ToList())
        {
            if (_inFlightMessages.TryRemove(deliveryTag, out _))
            {
                inFlightPurged++;
            }
        }

        Interlocked.Exchange(ref _pendingCount, 0);

        if (_config.Durable)
        {
            await AppendCriticalPersistenceRecordAsync(
                CreatePurgeRecord(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                cancellationToken);
        }

        TouchActivity();

        _logger.LogInformation(
            "Purged queue {QueueName}: pending={PendingPurged}, inFlight={InFlightPurged}",
            _config.Name,
            pendingPurged,
            inFlightPurged);
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

    private async Task PersistenceBatchLoop()
    {
        if (_persistenceFilePath == null || _persistenceBatchChannel == null)
        {
            return;
        }

        var batch = new List<PersistenceRecord>(64);
        try
        {
            while (await _persistenceBatchChannel.Reader.WaitToReadAsync())
            {
                batch.Clear();
                while (_persistenceBatchChannel.Reader.TryRead(out var record))
                {
                    batch.Add(record);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                if (_batchFlushMs > 0)
                {
                    await Task.Delay(_batchFlushMs);
                    while (_persistenceBatchChannel.Reader.TryRead(out var record))
                    {
                        batch.Add(record);
                    }
                }

                var shouldCompact = false;
                await _persistenceLock.WaitAsync();
                try
                {
                    var sb = new StringBuilder();
                    foreach (var item in batch)
                    {
                        sb.AppendLine(item.Line);
                    }

                    var text = sb.ToString();
                    await File.AppendAllTextAsync(_persistenceFilePath, text);
                    _persistenceFileSize += Encoding.UTF8.GetByteCount(text);
                    shouldCompact = _persistenceFileSize > _compactionThresholdBytes;
                }
                finally
                {
                    _persistenceLock.Release();
                }

                foreach (var record in batch)
                {
                    record.FlushCompletion?.TrySetResult(true);
                }

                if (shouldCompact)
                {
                    await CompactPersistenceLogAsync();
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var record in batch)
            {
                record.FlushCompletion?.TrySetException(ex);
            }

            if (_persistenceBatchChannel != null)
            {
                while (_persistenceBatchChannel.Reader.TryRead(out var pendingRecord))
                {
                    pendingRecord.FlushCompletion?.TrySetException(ex);
                }
            }

            _logger.LogError(ex, "Persistence batch loop error for queue {QueueName}", _config.Name);
        }
    }

    public async Task CompactPersistenceLogAsync()
    {
        if (_persistenceFilePath == null || !File.Exists(_persistenceFilePath))
        {
            return;
        }

        await _persistenceLock.WaitAsync();
        try
        {
            _logger.LogInformation("Starting persistence compaction for queue {QueueName}", _config.Name);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var tempPath = _persistenceFilePath + ".tmp";
            var removedCount = 0;
            var sequence = 0L;
            var orderedMessages = new Dictionary<Guid, (QueueMessage Message, long Sequence)>();

            using (var reader = new StreamReader(_persistenceFilePath))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (!TryParsePersistenceRecord(line, out var op, out var msgId, out var enqueuedAt, out var expiresAt, out var payloadBase64))
                    {
                        removedCount++;
                        continue;
                    }

                    if (string.Equals(op, PurgeOperation, StringComparison.OrdinalIgnoreCase))
                    {
                        removedCount += orderedMessages.Count;
                        orderedMessages.Clear();
                        _ackedMessageIds.Clear();
                        continue;
                    }

                    if (msgId == Guid.Empty)
                    {
                        removedCount++;
                        continue;
                    }

                    if (string.Equals(op, AckOperation, StringComparison.OrdinalIgnoreCase))
                    {
                        _ackedMessageIds[msgId] = 0;
                        if (orderedMessages.Remove(msgId))
                        {
                            removedCount++;
                        }
                        continue;
                    }

                    if (expiresAt.HasValue && expiresAt <= now)
                    {
                        removedCount++;
                        continue;
                    }

                    if (string.IsNullOrEmpty(payloadBase64))
                    {
                        removedCount++;
                        continue;
                    }

                    try
                    {
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

                        if (orderedMessages.ContainsKey(msgId))
                        {
                            removedCount++;
                        }

                        orderedMessages[msgId] = (message, sequence++);
                    }
                    catch
                    {
                        removedCount++;
                    }
                }
            }

            var survivingCount = 0;
            using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
            {
                foreach (var entry in orderedMessages.Values.OrderBy(x => x.Sequence))
                {
                    await writer.WriteLineAsync(CreateEnqueueRecord(entry.Message));
                    survivingCount++;
                }
            }

            File.Move(tempPath, _persistenceFilePath, overwrite: true);
            _persistenceFileSize = new FileInfo(_persistenceFilePath).Length;

            _logger.LogInformation(
                "Compacted persistence log for queue {QueueName}: removed {Removed} entries, {Remaining} remaining",
                _config.Name,
                removedCount,
                survivingCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error compacting persistence log for queue {QueueName}", _config.Name);
        }
        finally
        {
            _persistenceLock.Release();
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
            var sequence = 0L;
            var orderedMessages = new Dictionary<Guid, (QueueMessage Message, long Sequence)>();

            using var reader = new StreamReader(_persistenceFilePath);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (!TryParsePersistenceRecord(line, out var op, out var msgId, out var enqueuedAt, out var expiresAt, out var payloadBase64))
                    {
                        continue;
                    }

                    if (string.Equals(op, PurgeOperation, StringComparison.OrdinalIgnoreCase))
                    {
                        orderedMessages.Clear();
                        _ackedMessageIds.Clear();
                        continue;
                    }

                    if (msgId == Guid.Empty)
                    {
                        continue;
                    }

                    if (string.Equals(op, AckOperation, StringComparison.OrdinalIgnoreCase))
                    {
                        _ackedMessageIds[msgId] = 0;
                        orderedMessages.Remove(msgId);
                        continue;
                    }

                    if (expiresAt.HasValue && expiresAt <= now)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(payloadBase64))
                    {
                        continue;
                    }

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

                    orderedMessages[msgId] = (message, sequence++);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load persisted message from line: {Line}", line);
                }
            }

            foreach (var entry in orderedMessages.Values.OrderBy(x => x.Sequence))
            {
                await _writer.WriteAsync(entry.Message);
                Interlocked.Increment(ref _pendingCount);
                loadedCount++;
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

    private static string CreateEnqueueRecord(QueueMessage message)
    {
        var record = new
        {
            op = EnqueueOperation,
            msgId = message.MessageId,
            enqueuedAt = message.EnqueuedAt,
            expiresAt = message.ExpiresAt,
            payloadBase64 = Convert.ToBase64String(message.Body.Span)
        };

        return JsonSerializer.Serialize(record);
    }

    private static string CreateAckRecord(Guid messageId)
    {
        var record = new
        {
            op = AckOperation,
            msgId = messageId,
            ackedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        return JsonSerializer.Serialize(record);
    }

    private static string CreatePurgeRecord(long purgedAt)
    {
        var record = new
        {
            op = PurgeOperation,
            purgedAt
        };

        return JsonSerializer.Serialize(record);
    }

    private async Task AppendCriticalPersistenceRecordAsync(string line, CancellationToken cancellationToken = default)
    {
        if (!_config.Durable)
        {
            return;
        }

        await QueuePersistenceRecordAsync(line, waitForFlush: true, cancellationToken);
    }

    private async Task QueuePersistenceRecordAsync(string line, bool waitForFlush, CancellationToken cancellationToken)
    {
        if (_persistenceBatchChannel == null)
        {
            return;
        }

        TaskCompletionSource<bool>? flushCompletion = null;
        if (waitForFlush)
        {
            flushCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var record = new PersistenceRecord(line, flushCompletion);
        await _persistenceBatchChannel.Writer.WriteAsync(record, cancellationToken);

        if (flushCompletion != null)
        {
            await flushCompletion.Task.WaitAsync(cancellationToken);
        }
    }

    private static bool TryParsePersistenceRecord(
        string line,
        out string operation,
        out Guid messageId,
        out long enqueuedAt,
        out long? expiresAt,
        out string? payloadBase64)
    {
        operation = EnqueueOperation;
        messageId = Guid.Empty;
        enqueuedAt = 0;
        expiresAt = null;
        payloadBase64 = null;

        var record = JsonSerializer.Deserialize<JsonElement>(line);

        if (record.TryGetProperty("op", out var opProperty) && opProperty.ValueKind == JsonValueKind.String)
        {
            operation = opProperty.GetString() ?? EnqueueOperation;
        }

        if (string.Equals(operation, PurgeOperation, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!record.TryGetProperty("msgId", out var msgIdProperty) || msgIdProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!Guid.TryParse(msgIdProperty.GetString(), out messageId))
        {
            return false;
        }

        if (string.Equals(operation, AckOperation, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!record.TryGetProperty("enqueuedAt", out var enqueuedAtProperty) ||
            enqueuedAtProperty.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        enqueuedAt = enqueuedAtProperty.GetInt64();

        if (record.TryGetProperty("expiresAt", out var expiresAtProperty) &&
            expiresAtProperty.ValueKind != JsonValueKind.Null)
        {
            expiresAt = expiresAtProperty.GetInt64();
        }

        if (!record.TryGetProperty("payloadBase64", out var payloadProperty) ||
            payloadProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        payloadBase64 = payloadProperty.GetString();
        return !string.IsNullOrEmpty(payloadBase64);
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
    /// Drains expired pending messages from the channel without an active consumer.
    /// This keeps PendingCount accurate even when no consumer is connected.
    /// Uses TryRead so it never blocks — stops as soon as the channel is empty
    /// or the next message is not yet expired.
    /// </summary>
    public async Task DrainExpiredPendingAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var drained = 0;

        // Peek at each message; if expired discard it, otherwise put it back.
        // Because Channel<T> has no peek, we read and re-enqueue non-expired ones.
        // To avoid draining the entire channel, we only process up to the current
        // PendingCount snapshot — messages published after this call are not touched.
        var limit = _pendingCount;
        for (var i = 0; i < limit; i++)
        {
            if (!_reader.TryRead(out var message))
                break;

            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_ackedMessageIds.ContainsKey(message.MessageId))
            {
                // Already processed — drop silently.
                Interlocked.Decrement(ref _pendingCount);
                drained++;
                continue;
            }

            if (message.ExpiresAt.HasValue && message.ExpiresAt <= now)
            {
                await HandleDeadLetter(message);
                Interlocked.Decrement(ref _pendingCount);
                drained++;
                continue;
            }

            // Message is valid — put it back at the end of the channel.
            // WriteAsync won't block in practice because we just freed a slot.
            await _writer.WriteAsync(message);
        }

        if (drained > 0)
            _logger.LogDebug("Drained {Count} expired/stale pending messages from queue {QueueName}", drained, _config.Name);
    }

    public IEnumerable<ulong> GetInFlightDeliveryTags()
    {
        return _inFlightMessages.Keys;
    }

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

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _persistenceBatchChannel?.Writer.TryComplete();
        }
        catch
        {
            // Ignore channel completion errors during shutdown.
        }

        if (_persistenceBatchTask != null)
        {
            try
            {
                _persistenceBatchTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore shutdown race conditions.
            }
        }

        try
        {
            _writer.TryComplete();
        }
        catch
        {
            // Ignore channel completion errors during shutdown.
        }

        _persistenceLock.Dispose();
    }
}
