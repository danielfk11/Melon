using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MelonMQ.Broker.Protocol;
using MelonMQ.Protocol;

namespace MelonMQ.Broker.Core;

public class TcpServer
{
    private readonly QueueManager _queueManager;
    private readonly ConnectionManager _connectionManager;
    private readonly MelonMetrics _metrics;
    private readonly ILogger<TcpServer> _logger;
    private readonly MelonMQConfiguration _config;
    private readonly ClusterCoordinator? _clusterCoordinator;
    private readonly ExchangeManager _exchangeManager;
    private readonly X509Certificate2? _serverCertificate;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ConcurrentDictionary<ulong, (string ConnectionId, string QueueName, ulong QueueDeliveryTag)> _deliveryTagMap = new();
    private readonly ConcurrentDictionary<string, int> _connectionInFlightCount = new();
    private readonly ConcurrentBag<Task> _activeClientTasks = new();

    public bool IsListening { get; private set; }

    public TcpServer(
        QueueManager queueManager,
        ConnectionManager connectionManager,
        MelonMQConfiguration config,
        ILogger<TcpServer> logger,
        MelonMetrics metrics,
        ClusterCoordinator? clusterCoordinator = null,
        ExchangeManager? exchangeManager = null)
    {
        _queueManager = queueManager;
        _connectionManager = connectionManager;
        _config = config;
        _logger = logger;
        _metrics = metrics;
        _clusterCoordinator = clusterCoordinator;
        _exchangeManager = exchangeManager ?? new ExchangeManager(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExchangeManager>.Instance);

        if (_config.TcpTls.Enabled)
        {
            _serverCertificate = LoadServerCertificate(_config.TcpTls);
        }

        FrameSerializer.ConfigureMaxFrameSize(MessageSizePolicy.ComputeMaxFrameSizeBytes(_config.MaxMessageSize));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bindAddress = ResolveBindAddress(_config.TcpBindAddress);
        _listener = new TcpListener(bindAddress, _config.TcpPort);
        _listener.Start();
        IsListening = true;

        _logger.LogInformation("TCP Server started on {Address}:{Port}", bindAddress, _config.TcpPort);

        // Start cleanup task
        _ = Task.Run(() => CleanupTask(_cancellationTokenSource.Token));

        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(_cancellationTokenSource.Token);
                
                // Enforce max connections
                if (_connectionManager.ConnectionCount >= _config.MaxConnections)
                {
                    _logger.LogWarning("Max connections ({MaxConnections}) reached, rejecting new connection", _config.MaxConnections);
                    tcpClient.Close();
                    continue;
                }
                
                var clientTask = Task.Run(() => HandleClientAsync(tcpClient, _cancellationTokenSource.Token), _cancellationTokenSource.Token);
                _activeClientTasks.Add(clientTask);
            }
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.Token.IsCancellationRequested)
        {
            // Server is stopping
        }
        catch (ObjectDisposedException)
        {
            // Server is stopping
        }
    }

    public async Task StopAsync(TimeSpan? gracefulTimeout = null)
    {
        _cancellationTokenSource?.Cancel();
        _listener?.Stop();
        IsListening = false;

        // Wait for active client tasks to finish gracefully
        var timeout = gracefulTimeout ?? TimeSpan.FromSeconds(10);
        var allTasks = _activeClientTasks.Where(t => !t.IsCompleted).ToArray();
        if (allTasks.Length > 0)
        {
            _logger.LogInformation("Waiting for {Count} active connections to close...", allTasks.Length);
            await Task.WhenAny(Task.WhenAll(allTasks), Task.Delay(timeout));
        }

        _logger.LogInformation("TCP Server stopped");
    }

    public void Stop()
    {
        _ = StopAsync();
    }

    /// <summary>
    /// Thread-safe frame write. Multiple tasks (consumer loops, ack/nack responses, heartbeat)
    /// may write to the same connection stream concurrently. NetworkStream.WriteAsync is NOT
    /// thread-safe, so we serialize writes with a per-connection SemaphoreSlim.
    /// </summary>
    private async Task WriteFrameAsync(ClientConnection connection, Frame frame)
    {
        await connection.WriteLock.WaitAsync();
        try
        {
            await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, frame);
        }
        finally
        {
            connection.WriteLock.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid().ToString();
        ClientConnection? connection = null;
        var connectionStartTime = DateTime.UtcNow;

        try
        {
            // Configure timeouts
            tcpClient.ReceiveTimeout = 30000; // 30 seconds
            tcpClient.SendTimeout = 10000;    // 10 seconds

            // Create linked cancellation token with connection lifetime timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromHours(24)); // Max connection time

            var socket = tcpClient.Client;
            var networkStream = tcpClient.GetStream();
            Stream stream = networkStream;
            if (_config.TcpTls.Enabled)
            {
                var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
                var sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _serverCertificate!,
                    ClientCertificateRequired = _config.TcpTls.ClientCertificateRequired,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = _config.TcpTls.CheckCertificateRevocation
                        ? X509RevocationMode.Online
                        : X509RevocationMode.NoCheck
                };

                using var tlsHandshakeCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                tlsHandshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
                await sslStream.AuthenticateAsServerAsync(sslOptions, tlsHandshakeCts.Token);
                stream = sslStream;
            }

            var pipe = new Pipe();
            
            connection = new ClientConnection(
                connectionId, 
                socket, 
                pipe.Reader, 
                stream);

            _connectionManager.AddConnection(connection);
            _metrics.RecordConnectionOpened();
            _logger.LogInformation("Client {ConnectionId} connected from {RemoteEndPoint}", 
                connectionId, tcpClient.Client.RemoteEndPoint);

            // Start reading from network into pipe
            var readTask = ReadFromNetworkAsync(stream, pipe.Writer, timeoutCts.Token);
            
            // Start processing messages from pipe
            var processTask = ProcessMessagesAsync(connection, timeoutCts.Token);

            // Wait for either task to complete or fail
            var completedTask = await Task.WhenAny(readTask, processTask);
            
            // Check if the completed task failed
            if (completedTask.IsFaulted)
            {
                _logger.LogWarning("Connection {ConnectionId} task failed: {Exception}", 
                    connectionId, completedTask.Exception?.GetBaseException()?.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Client {ConnectionId} connection cancelled due to server shutdown", connectionId);
        }
        catch (IOException ioEx)
        {
            _logger.LogWarning("IO error with client {ConnectionId}: {Message}", connectionId, ioEx.Message);
        }
        catch (AuthenticationException authEx)
        {
            _logger.LogWarning("TLS handshake failed for client {ConnectionId}: {Message}", connectionId, authEx.Message);
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("Client {ConnectionId} connection disposed", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling client {ConnectionId}", connectionId);
        }
        finally
        {
            var connectionDuration = DateTime.UtcNow - connectionStartTime;
            _logger.LogInformation("Client {ConnectionId} disconnected after {Duration}", 
                connectionId, connectionDuration);

            if (connection != null)
            {
                // 1. Signal all consumer CTSs to stop.
                foreach (var (cts, _) in connection.ActiveConsumers.Values)
                    try { cts.Cancel(); } catch { }

                // 2. Await all consumer tasks before touching _inFlightMessages.
                //    Without this wait a consumer task that already called DequeueAsync
                //    (message in _inFlightMessages) but hasn't checked CTS yet will race
                //    with RequeuePendingMessagesForConnection: requeue puts the msg back
                //    in the channel, the still-running consumer immediately dequeues it
                //    again, tries to write on the dead socket, and leaves it stuck forever.
                var pendingTasks = connection.ActiveConsumers.Values
                    .Select(x => x.Task.ContinueWith(_ => { }, TaskScheduler.Default))
                    .ToArray();
                if (pendingTasks.Length > 0)
                    await Task.WhenAll(pendingTasks);

                try
                {
                    // Clean up delivery tags for this connection
                    var tagsToRemove = _deliveryTagMap
                        .Where(kvp => kvp.Value.ConnectionId == connectionId)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    foreach (var tag in tagsToRemove)
                    {
                        _deliveryTagMap.TryRemove(tag, out _);
                    }

                    var inFlightKeysToRemove = _connectionInFlightCount.Keys
                        .Where(k => k.StartsWith(connectionId + ":", StringComparison.Ordinal))
                        .ToList();
                    foreach (var key in inFlightKeysToRemove)
                    {
                        _connectionInFlightCount.TryRemove(key, out _);
                    }

                    // Requeue any in-flight messages
                    foreach (var queue in _queueManager.GetAllQueues())
                    {
                        await queue.RequeuePendingMessagesForConnection(connectionId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cleaning up connection {ConnectionId}", connectionId);
                }

                _connectionManager.RemoveConnection(connectionId);
                _metrics.RecordConnectionClosed();
            }
            
            tcpClient.Close();
        }
    }

    private async Task ReadFromNetworkAsync(Stream stream, PipeWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var memory = writer.GetMemory(4096);
                var bytesRead = await stream.ReadAsync(memory, cancellationToken);
                
                if (bytesRead == 0)
                    break;

                writer.Advance(bytesRead);
                await writer.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading from network");
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private async Task ProcessMessagesAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await FrameSerializer.ReadFrameAsync(connection.Reader, cancellationToken);
                if (frame == null)
                    break;

                connection.LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await HandleFrame(connection, frame, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing messages for connection {ConnectionId}", connection.Id);
        }
    }

    private async Task HandleFrame(ClientConnection connection, Frame frame, CancellationToken cancellationToken)
    {
        using var activity = _metrics.StartActivity($"tcp.{frame.Type}", ActivityKind.Server);
        activity?.SetTag("messaging.system", "melonmq");
        activity?.SetTag("messaging.operation", frame.Type.ToString());
        activity?.SetTag("network.transport", "tcp");
        activity?.SetTag("net.peer.connection_id", connection.Id);

        try
        {
            // Auth frames are always allowed
            if (frame.Type == MessageType.Auth)
            {
                await HandleAuth(connection, frame);
                return;
            }

            // Heartbeat frames are always allowed
            if (frame.Type == MessageType.Heartbeat)
            {
                await HandleHeartbeat(connection, frame);
                return;
            }

            // Require authentication if configured
            if (_config.Security.RequireAuth && !connection.IsAuthenticated)
            {
                await SendError(connection, frame.CorrelationId, "Authentication required. Send Auth frame first.");
                return;
            }

            if (IsWriteFrameType(frame.Type) &&
                _clusterCoordinator?.Enabled == true &&
                !_clusterCoordinator.CanAcceptWrites(out var writeRejectionReason))
            {
                await SendError(
                    connection,
                    frame.CorrelationId,
                    writeRejectionReason ?? "This node is not allowed to accept write operations.");
                return;
            }

            switch (frame.Type)
            {
                case MessageType.DeclareQueue:
                    await HandleDeclareQueue(connection, frame);
                    break;
                
                case MessageType.Publish:
                    await HandlePublish(connection, frame, cancellationToken);
                    break;
                
                case MessageType.ConsumeSubscribe:
                    await HandleConsumeSubscribe(connection, frame, cancellationToken);
                    break;
                
                case MessageType.Ack:
                    await HandleAck(connection, frame);
                    break;
                
                case MessageType.Nack:
                    await HandleNack(connection, frame);
                    break;
                
                case MessageType.SetPrefetch:
                    await HandleSetPrefetch(connection, frame);
                    break;

                case MessageType.DeclareExchange:
                    await HandleDeclareExchange(connection, frame);
                    break;

                case MessageType.BindQueue:
                    await HandleBindQueue(connection, frame);
                    break;

                case MessageType.UnbindQueue:
                    await HandleUnbindQueue(connection, frame);
                    break;

                case MessageType.StreamAck:
                    await HandleStreamAck(connection, frame);
                    break;
                
                default:
                    await SendError(connection, frame.CorrelationId, $"Unknown message type: {frame.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Error handling frame {FrameType} for connection {ConnectionId}", 
                frame.Type, connection.Id);
            await SendError(connection, frame.CorrelationId, ex.Message);
        }
    }

    private async Task HandleAuth(ClientConnection connection, Frame frame)
    {
        if (_config.Security.RequireAuth)
        {
            var payload = (AuthPayload)frame.Payload!;
            
            // Validate credentials are provided
            if (string.IsNullOrEmpty(payload.Username) || string.IsNullOrEmpty(payload.Password))
            {
                var errorResponse = new Frame
                {
                    Type = MessageType.Error,
                    CorrelationId = frame.CorrelationId,
                    Payload = new { success = false, message = "Username and password are required" }
                };
                await WriteFrameAsync(connection, errorResponse);
                return;
            }
            
            // Validate against configured credential store
            if (_config.Security.Users.Count > 0)
            {
                if (!_config.Security.Users.TryGetValue(payload.Username, out var expectedPassword) 
                    || !PasswordHasher.VerifyPassword(payload.Password, expectedPassword))
                {
                    _logger.LogWarning("Auth failed for connection {ConnectionId} with user {Username}", 
                        connection.Id, payload.Username);
                    _metrics.RecordError("auth", "invalid_credentials");
                    var errorResponse = new Frame
                    {
                        Type = MessageType.Error,
                        CorrelationId = frame.CorrelationId,
                        Payload = new { success = false, message = "Invalid username or password" }
                    };
                    await WriteFrameAsync(connection, errorResponse);
                    return;
                }
            }
            else
            {
                _logger.LogError("Auth required but no users configured. Rejecting connection {ConnectionId} for user {Username}",
                    connection.Id, payload.Username);
                _metrics.RecordError("auth", "no_users_configured");

                var errorResponse = new Frame
                {
                    Type = MessageType.Error,
                    CorrelationId = frame.CorrelationId,
                    Payload = new { success = false, message = "Authentication is enabled but no users are configured" }
                };
                await WriteFrameAsync(connection, errorResponse);
                return;
            }
            
            _logger.LogInformation("Auth success for connection {ConnectionId} with user {Username}", 
                connection.Id, payload.Username);
        }
        
        connection.IsAuthenticated = true;
        
        var response = new Frame
        {
            Type = MessageType.Auth,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true }
        };
        
        await WriteFrameAsync(connection, response);
    }

    private async Task HandleDeclareQueue(ClientConnection connection, Frame frame)
    {
        var payload = (DeclareQueuePayload)frame.Payload!;
        
        try
        {
            // Stream queue mode
            if (string.Equals(payload.Mode, StreamQueue.StreamMode, StringComparison.OrdinalIgnoreCase))
            {
                _queueManager.DeclareStreamQueue(
                    payload.Queue,
                    payload.Durable,
                    payload.StreamMaxLengthMessages ?? 100_000,
                    payload.StreamMaxAgeMs);

                var streamResponse = new Frame
                {
                    Type = MessageType.DeclareQueue,
                    CorrelationId = frame.CorrelationId,
                    Payload = new { success = true, queue = payload.Queue, mode = "stream" }
                };
                await WriteFrameAsync(connection, streamResponse);
                return;
            }

            var alreadyExisted = _queueManager.GetQueue(payload.Queue) != null;
            var queue = _queueManager.DeclareQueue(
                payload.Queue, 
                payload.Durable, 
                payload.DeadLetterQueue, 
                payload.DefaultTtlMs);

            if (!alreadyExisted && _clusterCoordinator?.Enabled == true)
            {
                var replicated = await _clusterCoordinator.ReplicateDeclareQueueAsync(
                    payload.Queue,
                    payload.Durable,
                    payload.DeadLetterQueue,
                    payload.DefaultTtlMs);

                if (!replicated)
                {
                    _queueManager.DeleteQueue(payload.Queue);
                    await SendError(
                        connection,
                        frame.CorrelationId,
                        "Declare queue failed to meet cluster consistency policy.");
                    return;
                }
            }

            var response = new Frame
            {
                Type = MessageType.DeclareQueue,
                CorrelationId = frame.CorrelationId,
                Payload = new { success = true, queue = payload.Queue }
            };
            
            await WriteFrameAsync(connection, response);
        }
        catch (InvalidOperationException ex)
        {
            // MaxQueues limit exceeded
            await SendError(connection, frame.CorrelationId, ex.Message);
        }
    }

    private async Task HandlePublish(ClientConnection connection, Frame frame, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var payload = (PublishPayload)frame.Payload!;

        // Validation: need either exchange or queue, plus body
        var hasExchange = !string.IsNullOrWhiteSpace(payload.Exchange);
        var hasQueue = !string.IsNullOrWhiteSpace(payload.Queue);

        if (!hasExchange && !hasQueue)
        {
            await SendError(connection, frame.CorrelationId, "Publish payload must include either 'exchange' or 'queue'");
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.BodyBase64))
        {
            await SendError(connection, frame.CorrelationId, "Publish payload must include 'bodyBase64'");
            return;
        }

        byte[] bodyBytes;
        try { bodyBytes = Convert.FromBase64String(payload.BodyBase64); }
        catch (FormatException)
        {
            await SendError(connection, frame.CorrelationId, "Invalid Base64 in bodyBase64");
            return;
        }

        if (bodyBytes.Length > _config.MaxMessageSize)
        {
            await SendError(connection, frame.CorrelationId,
                $"Message size ({bodyBytes.Length} bytes) exceeds maximum allowed ({_config.MaxMessageSize} bytes)");
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var messageId = payload.MessageId == Guid.Empty ? Guid.NewGuid() : payload.MessageId;

        // ── Exchange routing ───────────────────────────────────────────────
        if (hasExchange)
        {
            var targetQueueNames = _exchangeManager.ResolveQueues(
                payload.Exchange!,
                payload.RoutingKey ?? string.Empty);

            if (targetQueueNames.Count == 0)
            {
                // Unroutable — silently drop (same as RabbitMQ default exchange behaviour)
                sw.Stop();
                await SendPublishSuccess(connection, frame.CorrelationId, messageId);
                return;
            }

            foreach (var queueName in targetQueueNames)
            {
                await PublishToQueueByName(
                    queueName, bodyBytes, messageId, now, payload.TtlMs, payload.Persistent, cancellationToken);
            }

            sw.Stop();
            _metrics.RecordMessagePublished(
                payload.Exchange!, sw.Elapsed, bodyBytes.Length, source: "tcp",
                replicated: _clusterCoordinator?.Enabled == true);
            await SendPublishSuccess(connection, frame.CorrelationId, messageId);
            return;
        }

        // ── Direct publish ─────────────────────────────────────────────────

        // Stream queue path
        var streamQueue = _queueManager.GetStreamQueue(payload.Queue);
        if (streamQueue != null)
        {
            long? expiresAt = payload.TtlMs.HasValue ? now + payload.TtlMs.Value : null;
            await streamQueue.AppendAsync(bodyBytes, messageId, expiresAt);
            sw.Stop();
            _metrics.RecordMessagePublished(
                payload.Queue, sw.Elapsed, bodyBytes.Length, source: "tcp",
                replicated: false);
            await SendPublishSuccess(connection, frame.CorrelationId, messageId);
            return;
        }

        // Classic queue path
        var queue = _queueManager.GetQueue(payload.Queue);
        if (queue == null)
        {
            _logger.LogDebug("Auto-creating queue '{QueueName}' for publish operation", payload.Queue);
            queue = _queueManager.DeclareQueue(payload.Queue, durable: false);
        }

        var effectiveTtlMs = payload.TtlMs ?? queue.DefaultTtlMs;
        var message = new QueueMessage
        {
            MessageId = messageId,
            Body = bodyBytes,
            EnqueuedAt = now,
            ExpiresAt = effectiveTtlMs.HasValue ? now + effectiveTtlMs.Value : null,
            Persistent = payload.Persistent,
            Redelivered = false,
            DeliveryCount = 0
        };

        await queue.EnqueueAsync(
            message,
            cancellationToken,
            waitForPersistenceFlush: queue.IsDurable);

        // Fan-out to consumer group queues
        foreach (var groupQueue in _queueManager.GetGroupQueues(payload.Queue))
        {
            var groupMessage = new QueueMessage
            {
                MessageId = Guid.NewGuid(), // distinct ID per group copy
                Body = message.Body,
                EnqueuedAt = message.EnqueuedAt,
                ExpiresAt = message.ExpiresAt,
                Persistent = message.Persistent,
                Redelivered = false,
                DeliveryCount = 0
            };
            await groupQueue.EnqueueAsync(
                groupMessage,
                cancellationToken,
                waitForPersistenceFlush: groupQueue.IsDurable);
        }

        if (_clusterCoordinator?.Enabled == true)
        {
            var replicated = await _clusterCoordinator.ReplicatePublishAsync(payload.Queue, message);
            if (!replicated)
            {
                await queue.AckByMessageIdAsync(message.MessageId);
                await SendError(connection, frame.CorrelationId,
                    "Publish failed to meet cluster consistency policy.");
                return;
            }
        }

        sw.Stop();
        _metrics.RecordMessagePublished(
            payload.Queue, sw.Elapsed, bodyBytes.Length, source: "tcp",
            replicated: _clusterCoordinator?.Enabled == true);

        await SendPublishSuccess(connection, frame.CorrelationId, messageId);
    }

    private async Task PublishToQueueByName(
        string queueName,
        byte[] bodyBytes,
        Guid messageId,
        long now,
        int? ttlMs,
        bool persistent,
        CancellationToken cancellationToken = default)
    {
        var streamQueue = _queueManager.GetStreamQueue(queueName);
        if (streamQueue != null)
        {
            long? expiresAt = ttlMs.HasValue ? now + ttlMs.Value : null;
            await streamQueue.AppendAsync(bodyBytes, Guid.NewGuid(), expiresAt);
            return;
        }

        var queue = _queueManager.GetQueue(queueName);
        if (queue == null)
        {
            _logger.LogDebug("Auto-creating queue '{QueueName}' for exchange routing", queueName);
            queue = _queueManager.DeclareQueue(queueName, durable: false);
        }

        var effectiveTtlMs = ttlMs ?? queue.DefaultTtlMs;
        var message = new QueueMessage
        {
            MessageId = Guid.NewGuid(),
            Body = bodyBytes,
            EnqueuedAt = now,
            ExpiresAt = effectiveTtlMs.HasValue ? now + effectiveTtlMs.Value : null,
            Persistent = persistent,
            Redelivered = false,
            DeliveryCount = 0
        };

        await queue.EnqueueAsync(
            message,
            cancellationToken,
            waitForPersistenceFlush: queue.IsDurable);

        foreach (var groupQueue in _queueManager.GetGroupQueues(queueName))
        {
            await groupQueue.EnqueueAsync(new QueueMessage
            {
                MessageId = Guid.NewGuid(),
                Body = message.Body,
                EnqueuedAt = message.EnqueuedAt,
                ExpiresAt = message.ExpiresAt,
                Persistent = message.Persistent
            }, cancellationToken, waitForPersistenceFlush: groupQueue.IsDurable);
        }
    }

    private async Task SendPublishSuccess(ClientConnection connection, ulong correlationId, Guid messageId)
    {
        try
        {
            await WriteFrameAsync(connection, new Frame
            {
                Type = MessageType.Publish,
                CorrelationId = correlationId,
                Payload = new { success = true, messageId }
            });
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("Could not send publish response to connection {Id} - already closed", connection.Id);
        }
    }

    private async Task HandleConsumeSubscribe(ClientConnection connection, Frame frame, CancellationToken cancellationToken)
    {
        if (_clusterCoordinator?.Enabled == true &&
            !_clusterCoordinator.CanAcceptWrites(out var reason))
        {
            await SendError(connection, frame.CorrelationId, reason ?? "Follower nodes do not serve consume subscriptions.");
            return;
        }

        var payload = (ConsumeSubscribePayload)frame.Payload!;

        // ── Stream queue path ─────────────────────────────────────────────
        var streamQueue = _queueManager.GetStreamQueue(payload.Queue);
        if (streamQueue != null)
        {
            await HandleStreamConsumeSubscribe(connection, frame, payload, streamQueue, cancellationToken);
            return;
        }

        // ── Classic queue path ────────────────────────────────────────────

        // Determine actual queue: group queue or source queue directly
        string actualQueueName;
        if (!string.IsNullOrEmpty(payload.Group))
        {
            var sourceQueue = _queueManager.GetQueue(payload.Queue);
            if (sourceQueue == null)
            {
                await SendError(connection, frame.CorrelationId, $"Queue '{payload.Queue}' does not exist");
                return;
            }
            var groupQueue = _queueManager.DeclareGroupQueue(payload.Queue, payload.Group, sourceQueue.IsDurable);
            actualQueueName = groupQueue.Name;
        }
        else
        {
            var q = _queueManager.GetQueue(payload.Queue);
            if (q == null)
            {
                await SendError(connection, frame.CorrelationId, $"Queue '{payload.Queue}' does not exist");
                return;
            }
            actualQueueName = payload.Queue;
        }

        var actualQueue = _queueManager.GetQueue(actualQueueName)!;

        // Consumer key includes group so two groups on the same queue+connection
        // can coexist without canceling each other.
        var consumerKey = !string.IsNullOrEmpty(payload.Group)
            ? $"{payload.Queue}::grp:{payload.Group}"
            : payload.Queue;

        // Cancel and await existing consumer for this consumer key if any
        if (connection.ActiveConsumers.TryRemove(consumerKey, out var existing))
        {
            try { existing.Cts.Cancel(); } catch { }
            try { await existing.Task.ConfigureAwait(false); } catch { }
        }

        // Immediately recover any in-flight messages for this queue that belong to
        // dead connections.
        var activeConnectionIds = _connectionManager
            .GetAllConnections()
            .Select(c => c.Id)
            .ToHashSet();
        await actualQueue.RequeuePendingMessagesForDeadConnections(activeConnectionIds);

        var consumerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var inFlightKey = $"{connection.Id}:{actualQueueName}";
        _connectionInFlightCount.TryAdd(inFlightKey, 0);

        // The "logical delivery queue" is what the client uses to route incoming
        // Deliver frames to the correct delivery channel.  When a group is active
        // we include it so two groups on the same source queue get separate channels.
        var logicalDeliveryQueue = consumerKey;

        // Send success BEFORE starting the consumer task.
        await WriteFrameAsync(connection, new Frame
        {
            Type = MessageType.ConsumeSubscribe,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true, queue = logicalDeliveryQueue }
        });

        var consumerTask = Task.Run(async () =>
        {
            try
            {
                while (!consumerCts.Token.IsCancellationRequested)
                {
                    var currentInFlight = _connectionInFlightCount.GetValueOrDefault(inFlightKey, 0);
                    if (currentInFlight >= connection.Prefetch)
                    {
                        try { await connection.PrefetchSlotAvailable.WaitAsync(consumerCts.Token); }
                        catch (OperationCanceledException) { break; }
                        catch (ObjectDisposedException) { break; }
                        continue;
                    }

                    var result = await actualQueue.DequeueAsync(connection.Id, consumerCts.Token);
                    if (result == null) continue;

                    var (message, queueDeliveryTag) = result.Value;
                    var clientDeliveryTag = connection.NextClientDeliveryTag();

                    _deliveryTagMap[clientDeliveryTag] = (connection.Id, actualQueueName, queueDeliveryTag);

                    await WriteFrameAsync(connection, new Frame
                    {
                        Type = MessageType.Deliver,
                        CorrelationId = 0,
                        Payload = new DeliverPayload
                        {
                            Queue = logicalDeliveryQueue,
                            DeliveryTag = clientDeliveryTag,
                            BodyBase64 = Convert.ToBase64String(message.Body.Span),
                            Redelivered = message.Redelivered,
                            MessageId = message.MessageId
                        }
                    });

                    _connectionInFlightCount.AddOrUpdate(inFlightKey, 1, (_, v) => v + 1);
                    _metrics.RecordMessageConsumed(payload.Queue, TimeSpan.Zero, message.Redelivered, source: "tcp");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in consumer for queue {Queue} on connection {ConnectionId}",
                    payload.Queue, connection.Id);
                _metrics.RecordError("consume", ex.GetType().Name);
            }
            finally
            {
                _connectionInFlightCount.TryRemove(inFlightKey, out _);
            }
        });

        connection.ActiveConsumers[consumerKey] = (consumerCts, consumerTask);
    }

    private async Task HandleStreamConsumeSubscribe(
        ClientConnection connection,
        Frame frame,
        ConsumeSubscribePayload payload,
        StreamQueue streamQueue,
        CancellationToken cancellationToken)
    {
        // Consumer identity: group members share offset; solo consumers track by connection
        var consumerId = !string.IsNullOrEmpty(payload.Group)
            ? $"group:{payload.Group}"
            : $"conn:{connection.Id}";

        var startOffset = streamQueue.ResolveStartOffset(consumerId, payload.Offset ?? -1);

        // Cancel existing consumer for this queue
        if (connection.ActiveConsumers.TryRemove(payload.Queue, out var existing))
        {
            try { existing.Cts.Cancel(); } catch { }
            try { await existing.Task.ConfigureAwait(false); } catch { }
        }

        var consumerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await WriteFrameAsync(connection, new Frame
        {
            Type = MessageType.ConsumeSubscribe,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true, queue = payload.Queue, offset = startOffset, mode = "stream" }
        });

        var consumerTask = Task.Run(async () =>
        {
            var currentOffset = startOffset;
            try
            {
                while (!consumerCts.Token.IsCancellationRequested)
                {
                    var entry = await streamQueue.ReadAtAsync(currentOffset, consumerCts.Token);
                    if (entry == null) break;

                    await WriteFrameAsync(connection, new Frame
                    {
                        Type = MessageType.Deliver,
                        CorrelationId = 0,
                        Payload = new DeliverPayload
                        {
                            Queue = payload.Queue,
                            DeliveryTag = 0,
                            BodyBase64 = Convert.ToBase64String(entry.Body.Span),
                            Redelivered = false,
                            MessageId = entry.MessageId,
                            Offset = entry.Offset
                        }
                    });

                    currentOffset = entry.Offset + 1;
                    _metrics.RecordMessageConsumed(payload.Queue, TimeSpan.Zero, false, source: "tcp-stream");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in stream consumer for queue {Queue} on connection {ConnectionId}",
                    payload.Queue, connection.Id);
            }
        });

        connection.ActiveConsumers[payload.Queue] = (consumerCts, consumerTask);
    }

    private async Task HandleAck(ClientConnection connection, Frame frame)
    {
        var payload = (AckPayload)frame.Payload!;

        if (!_deliveryTagMap.TryGetValue(payload.DeliveryTag, out var mapping))
        {
            _metrics.RecordError("ack", "unknown_delivery_tag");
            await SendError(connection, frame.CorrelationId, "Unknown delivery tag.");
            return;
        }
        
        var success = false;
        if (mapping.ConnectionId != connection.Id)
        {
            _metrics.RecordError("ack", "delivery_tag_ownership_violation");
            await SendError(connection, frame.CorrelationId, "Delivery tag does not belong to this connection.");
            return;
        }

        _deliveryTagMap.TryRemove(payload.DeliveryTag, out _);
        var queue = _queueManager.GetQueue(mapping.QueueName);
        Guid? ackedMessageId = null;
        if (queue != null)
        {
            if (queue.TryGetInFlightMessageId(mapping.QueueDeliveryTag, out var messageId))
            {
                ackedMessageId = messageId;
            }

            success = await queue.AckAsync(mapping.QueueDeliveryTag);

            if (success && ackedMessageId.HasValue && _clusterCoordinator?.Enabled == true)
            {
                var replicated = await _clusterCoordinator.ReplicateAckAsync(mapping.QueueName, ackedMessageId.Value);
                if (!replicated)
                {
                    _metrics.RecordError("ack", "cluster_replication_failed", mapping.QueueName);
                }
            }
        }

        // Decrement in-flight counter for prefetch control
        var inFlightKey = $"{connection.Id}:{mapping.QueueName}";
        _connectionInFlightCount.AddOrUpdate(inFlightKey, 0, (_, v) => Math.Max(0, v - 1));

        // Signal consumer loop that a prefetch slot is available
        if (!connection.IsDisposed)
        {
            try { connection.PrefetchSlotAvailable.Release(); } catch (ObjectDisposedException) { }
        }

        var response = new Frame
        {
            Type = MessageType.Ack,
            CorrelationId = frame.CorrelationId,
            Payload = new { success }
        };
        
        await WriteFrameAsync(connection, response);
    }

    private async Task HandleNack(ClientConnection connection, Frame frame)
    {
        var payload = (NackPayload)frame.Payload!;

        if (!_deliveryTagMap.TryGetValue(payload.DeliveryTag, out var mapping))
        {
            _metrics.RecordError("nack", "unknown_delivery_tag");
            await SendError(connection, frame.CorrelationId, "Unknown delivery tag.");
            return;
        }
        
        var success = false;
        if (mapping.ConnectionId != connection.Id)
        {
            _metrics.RecordError("nack", "delivery_tag_ownership_violation");
            await SendError(connection, frame.CorrelationId, "Delivery tag does not belong to this connection.");
            return;
        }

        _deliveryTagMap.TryRemove(payload.DeliveryTag, out _);
        var queue = _queueManager.GetQueue(mapping.QueueName);
        if (queue != null)
        {
            success = await queue.NackAsync(mapping.QueueDeliveryTag, payload.Requeue);
        }

        // Decrement in-flight counter for prefetch control
        var inFlightKey = $"{connection.Id}:{mapping.QueueName}";
        _connectionInFlightCount.AddOrUpdate(inFlightKey, 0, (_, v) => Math.Max(0, v - 1));

        // Signal consumer loop that a prefetch slot is available
        if (!connection.IsDisposed)
        {
            try { connection.PrefetchSlotAvailable.Release(); } catch (ObjectDisposedException) { }
        }

        var response = new Frame
        {
            Type = MessageType.Nack,
            CorrelationId = frame.CorrelationId,
            Payload = new { success }
        };
        
        await WriteFrameAsync(connection, response);
    }

    private async Task HandleDeclareExchange(ClientConnection connection, Frame frame)
    {
        var payload = (DeclareExchangePayload)frame.Payload!;

        if (!Enum.TryParse<ExchangeType>(payload.Type, ignoreCase: true, out var exchangeType))
        {
            await SendError(connection, frame.CorrelationId,
                $"Unknown exchange type '{payload.Type}'. Valid types: direct, fanout, topic.");
            return;
        }

        try
        {
            _exchangeManager.DeclareExchange(payload.Exchange, exchangeType, payload.Durable);
            await WriteFrameAsync(connection, new Frame
            {
                Type = MessageType.DeclareExchange,
                CorrelationId = frame.CorrelationId,
                Payload = new { success = true, exchange = payload.Exchange, type = payload.Type }
            });
        }
        catch (ArgumentException ex)
        {
            await SendError(connection, frame.CorrelationId, ex.Message);
        }
    }

    private async Task HandleBindQueue(ClientConnection connection, Frame frame)
    {
        var payload = (BindQueuePayload)frame.Payload!;

        try
        {
            _exchangeManager.BindQueue(payload.Exchange, payload.Queue, payload.RoutingKey);
            await WriteFrameAsync(connection, new Frame
            {
                Type = MessageType.BindQueue,
                CorrelationId = frame.CorrelationId,
                Payload = new { success = true, exchange = payload.Exchange, queue = payload.Queue, routingKey = payload.RoutingKey }
            });
        }
        catch (InvalidOperationException ex)
        {
            await SendError(connection, frame.CorrelationId, ex.Message);
        }
    }

    private async Task HandleUnbindQueue(ClientConnection connection, Frame frame)
    {
        var payload = (UnbindQueuePayload)frame.Payload!;

        _exchangeManager.UnbindQueue(payload.Exchange, payload.Queue, payload.RoutingKey);
        await WriteFrameAsync(connection, new Frame
        {
            Type = MessageType.UnbindQueue,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true }
        });
    }

    private async Task HandleStreamAck(ClientConnection connection, Frame frame)
    {
        var payload = (StreamAckPayload)frame.Payload!;

        var streamQueue = _queueManager.GetStreamQueue(payload.Queue);
        if (streamQueue == null)
        {
            await SendError(connection, frame.CorrelationId, $"Stream queue '{payload.Queue}' does not exist.");
            return;
        }

        var consumerId = !string.IsNullOrEmpty(payload.Group)
            ? $"group:{payload.Group}"
            : $"conn:{connection.Id}";

        streamQueue.CommitOffset(consumerId, payload.Offset);

        await WriteFrameAsync(connection, new Frame
        {
            Type = MessageType.StreamAck,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true, offset = payload.Offset }
        });
    }

    private static bool IsWriteFrameType(MessageType messageType)
    {
        return messageType is MessageType.DeclareQueue
            or MessageType.Publish
            or MessageType.Ack
            or MessageType.Nack
            or MessageType.DeclareExchange
            or MessageType.BindQueue
            or MessageType.UnbindQueue;
    }

    private async Task HandleSetPrefetch(ClientConnection connection, Frame frame)
    {
        var payload = (SetPrefetchPayload)frame.Payload!;

        // Validate prefetch value
        if (payload.Prefetch < 1)
        {
            await SendError(connection, frame.CorrelationId, "Prefetch must be at least 1");
            return;
        }
        if (payload.Prefetch > 10000)
        {
            await SendError(connection, frame.CorrelationId, "Prefetch cannot exceed 10000");
            return;
        }

        connection.Prefetch = payload.Prefetch;

        var response = new Frame
        {
            Type = MessageType.SetPrefetch,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true, prefetch = payload.Prefetch }
        };
        
        await WriteFrameAsync(connection, response);
    }

    private async Task HandleHeartbeat(ClientConnection connection, Frame frame)
    {
        var response = new Frame
        {
            Type = MessageType.Heartbeat,
            CorrelationId = frame.CorrelationId,
            Payload = new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        };
        
        await WriteFrameAsync(connection, response);
    }

    private async Task SendError(ClientConnection connection, ulong correlationId, string message)
    {
        try
        {
            var errorFrame = new Frame
            {
                Type = MessageType.Error,
                CorrelationId = correlationId,
                Payload = new ErrorPayload { Message = message }
            };
            
            await WriteFrameAsync(connection, errorFrame);
        }
        catch (InvalidOperationException)
        {
            // Connection already closed - ignore
            _logger.LogDebug("Could not send error to connection {ConnectionId} - connection already closed", connection.Id);
        }
    }

    private async Task CleanupTask(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _connectionManager.CleanupStaleConnections();
                await _queueManager.CleanupExpiredMessages();

                // Clean up stale delivery tags for expired in-flight messages
                CleanupStaleDeliveryTags();

                await Task.Delay(5000, cancellationToken); // Cleanup every 5 seconds
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup task");
            }
        }
    }

    /// <summary>
    /// Removes delivery tag mappings that reference expired/removed in-flight messages.
    /// Prevents memory leak when messages expire without explicit Ack/Nack.
    /// </summary>
    private void CleanupStaleDeliveryTags()
    {
        var tagsToRemove = new List<ulong>();
        foreach (var (clientTag, mapping) in _deliveryTagMap)
        {
            var queue = _queueManager.GetQueue(mapping.QueueName);
            if (queue == null)
            {
                tagsToRemove.Add(clientTag);
                continue;
            }

            // Check if the queue's in-flight message still exists
            var inFlightTags = queue.GetInFlightDeliveryTags();
            if (!inFlightTags.Contains(mapping.QueueDeliveryTag))
            {
                tagsToRemove.Add(clientTag);
            }
        }

        foreach (var tag in tagsToRemove)
        {
            _deliveryTagMap.TryRemove(tag, out _);
        }

        if (tagsToRemove.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} stale delivery tag mappings", tagsToRemove.Count);
        }
    }

    private static IPAddress ResolveBindAddress(string bindAddress)
    {
        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(bindAddress, out var address))
        {
            return address;
        }

        throw new InvalidOperationException($"Invalid TCP bind address: '{bindAddress}'");
    }

    private static X509Certificate2 LoadServerCertificate(TcpTlsConfiguration tlsConfig)
    {
        if (string.IsNullOrWhiteSpace(tlsConfig.CertificatePath))
        {
            throw new InvalidOperationException("TCP TLS certificate path is empty.");
        }

        return X509CertificateLoader.LoadPkcs12FromFile(
            tlsConfig.CertificatePath,
            tlsConfig.CertificatePassword,
            X509KeyStorageFlags.DefaultKeySet);
    }
}