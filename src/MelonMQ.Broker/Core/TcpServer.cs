using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MelonMQ.Broker.Protocol;
using MelonMQ.Protocol;

namespace MelonMQ.Broker.Core;

public class TcpServer
{
    private readonly QueueManager _queueManager;
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger<TcpServer> _logger;
    private readonly MelonMQConfiguration _config;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ConcurrentDictionary<ulong, (string QueueName, ulong QueueDeliveryTag)> _deliveryTagMap = new();
    private readonly ConcurrentDictionary<string, int> _connectionInFlightCount = new();
    private long _nextDeliveryTag = 0;

    public bool IsListening { get; private set; }

    public TcpServer(QueueManager queueManager, ConnectionManager connectionManager, MelonMQConfiguration config, ILogger<TcpServer> logger)
    {
        _queueManager = queueManager;
        _connectionManager = connectionManager;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, _config.TcpPort);
        _listener.Start();
        IsListening = true;

        _logger.LogInformation("TCP Server started on port {Port}", _config.TcpPort);

        // Start cleanup task
        _ = Task.Run(() => CleanupTask(_cancellationTokenSource.Token));

        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync();
                
                // Enforce max connections
                if (_connectionManager.ConnectionCount >= _config.MaxConnections)
                {
                    _logger.LogWarning("Max connections ({MaxConnections}) reached, rejecting new connection", _config.MaxConnections);
                    tcpClient.Close();
                    continue;
                }
                
                _ = Task.Run(() => HandleClientAsync(tcpClient, _cancellationTokenSource.Token));
            }
        }
        catch (ObjectDisposedException)
        {
            // Server is stopping
        }
    }

    public void Stop()
    {
        _cancellationTokenSource?.Cancel();
        _listener?.Stop();
        IsListening = false;
        _logger.LogInformation("TCP Server stopped");
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

            var socket = tcpClient.Client;
            var stream = tcpClient.GetStream();
            var pipe = new Pipe();
            
            connection = new ClientConnection(
                connectionId, 
                socket, 
                pipe.Reader, 
                stream);

            _connectionManager.AddConnection(connection);
            _logger.LogInformation("Client {ConnectionId} connected from {RemoteEndPoint}", 
                connectionId, tcpClient.Client.RemoteEndPoint);

            // Create linked cancellation token with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromHours(24)); // Max connection time

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
                try
                {
                    // Requeue any in-flight messages
                    foreach (var queue in _queueManager.GetAllQueues())
                    {
                        await queue.RequeuePendingMessagesForConnection(connectionId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error requeuing messages for connection {ConnectionId}", connectionId);
                }

                _connectionManager.RemoveConnection(connectionId);
            }
            
            tcpClient.Close();
        }
    }

    private async Task ReadFromNetworkAsync(NetworkStream stream, PipeWriter writer, CancellationToken cancellationToken)
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

            switch (frame.Type)
            {
                case MessageType.DeclareQueue:
                    await HandleDeclareQueue(connection, frame);
                    break;
                
                case MessageType.Publish:
                    await HandlePublish(connection, frame);
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
                
                default:
                    await SendError(connection, frame.CorrelationId, $"Unknown message type: {frame.Type}");
                    break;
            }
        }
        catch (Exception ex)
        {
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
            
            // Validate credentials (basic implementation — extend with JWT/token-based auth)
            if (string.IsNullOrEmpty(payload.Username) || string.IsNullOrEmpty(payload.Password))
            {
                var errorResponse = new Frame
                {
                    Type = MessageType.Error,
                    CorrelationId = frame.CorrelationId,
                    Payload = new { success = false, message = "Username and password are required" }
                };
                await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, errorResponse);
                return;
            }
            
            // TODO: Replace with real credential store (database, LDAP, etc.)
            _logger.LogInformation("Auth attempt from connection {ConnectionId} with user {Username}", 
                connection.Id, payload.Username);
        }
        
        connection.IsAuthenticated = true;
        
        var response = new Frame
        {
            Type = MessageType.Auth,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true }
        };
        
        await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
    }

    private async Task HandleDeclareQueue(ClientConnection connection, Frame frame)
    {
        var payload = (DeclareQueuePayload)frame.Payload!;
        
        var queue = _queueManager.DeclareQueue(
            payload.Queue, 
            payload.Durable, 
            payload.DeadLetterQueue, 
            payload.DefaultTtlMs);

        var response = new Frame
        {
            Type = MessageType.DeclareQueue,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true, queue = payload.Queue }
        };
        
        await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
    }

    private async Task HandlePublish(ClientConnection connection, Frame frame)
    {
        var payload = (PublishPayload)frame.Payload!;
        
        if (string.IsNullOrEmpty(payload.Queue) && string.IsNullOrEmpty(payload.BodyBase64))
        {
            _logger.LogError("Received malformed PublishPayload with empty Queue and BodyBase64 from connection {ConnectionId}", connection.Id);
            return;
        }
        
        // Enforce max message size
        var bodyBytes = Convert.FromBase64String(payload.BodyBase64);
        if (bodyBytes.Length > _config.MaxMessageSize)
        {
            await SendError(connection, frame.CorrelationId, 
                $"Message size ({bodyBytes.Length} bytes) exceeds maximum allowed ({_config.MaxMessageSize} bytes)");
            return;
        }
        
        var queue = _queueManager.GetQueue(payload.Queue);
        
        if (queue == null)
        {
            _logger.LogDebug("Auto-creating queue '{QueueName}' for publish operation", payload.Queue);
            queue = _queueManager.DeclareQueue(payload.Queue, durable: false);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        var message = new QueueMessage
        {
            MessageId = payload.MessageId,
            Body = bodyBytes,
            EnqueuedAt = now,
            ExpiresAt = payload.TtlMs.HasValue ? now + payload.TtlMs.Value : null,
            Persistent = payload.Persistent,
            Redelivered = false,
            DeliveryCount = 0
        };

        await queue.EnqueueAsync(message);

        try
        {
            var response = new Frame
            {
                Type = MessageType.Publish,
                CorrelationId = frame.CorrelationId,
                Payload = new { success = true, messageId = payload.MessageId }
            };
            
            await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("Could not send publish response to connection {ConnectionId} - connection already closed", connection.Id);
        }
    }

    private async Task HandleConsumeSubscribe(ClientConnection connection, Frame frame, CancellationToken cancellationToken)
    {
        var payload = (ConsumeSubscribePayload)frame.Payload!;
        var queue = _queueManager.GetQueue(payload.Queue);
        
        if (queue == null)
        {
            await SendError(connection, frame.CorrelationId, $"Queue '{payload.Queue}' does not exist");
            return;
        }

        // Cancel existing consumer for this queue if any
        if (connection.ActiveConsumers.TryRemove(payload.Queue, out var existingCts))
        {
            existingCts.Cancel();
        }

        var consumerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connection.ActiveConsumers[payload.Queue] = consumerCts;

        // Start consuming with prefetch control
        var inFlightKey = $"{connection.Id}:{payload.Queue}";
        _connectionInFlightCount.TryAdd(inFlightKey, 0);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!consumerCts.Token.IsCancellationRequested)
                {
                    // Enforce prefetch limit using shared atomic counter
                    var currentInFlight = _connectionInFlightCount.GetValueOrDefault(inFlightKey, 0);
                    if (currentInFlight >= connection.Prefetch)
                    {
                        await Task.Delay(10, consumerCts.Token);
                        continue;
                    }

                    var result = await queue.DequeueAsync(connection.Id, consumerCts.Token);
                    if (result == null) continue;

                    var (message, queueDeliveryTag) = result.Value;
                    var clientDeliveryTag = (ulong)Interlocked.Increment(ref _nextDeliveryTag);
                    
                    // Map client-facing tag to queue name + queue's internal tag
                    _deliveryTagMap[clientDeliveryTag] = (payload.Queue, queueDeliveryTag);

                    var deliverPayload = new DeliverPayload
                    {
                        Queue = payload.Queue,
                        DeliveryTag = clientDeliveryTag,
                        BodyBase64 = Convert.ToBase64String(message.Body.Span),
                        Redelivered = message.Redelivered,
                        MessageId = message.MessageId
                    };

                    var deliverFrame = new Frame
                    {
                        Type = MessageType.Deliver,
                        CorrelationId = 0,
                        Payload = deliverPayload
                    };

                    await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, deliverFrame);
                    _connectionInFlightCount.AddOrUpdate(inFlightKey, 1, (_, v) => v + 1);
                }
            }
            catch (OperationCanceledException)
            {
                // Consumer was cancelled
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in consumer for queue {Queue} on connection {ConnectionId}", 
                    payload.Queue, connection.Id);
            }
            finally
            {
                _connectionInFlightCount.TryRemove(inFlightKey, out _);
            }
        });

        var response = new Frame
        {
            Type = MessageType.ConsumeSubscribe,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true, queue = payload.Queue }
        };
        
        await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
    }

    private async Task HandleAck(ClientConnection connection, Frame frame)
    {
        var payload = (AckPayload)frame.Payload!;
        
        var success = false;
        if (_deliveryTagMap.TryRemove(payload.DeliveryTag, out var mapping))
        {
            var queue = _queueManager.GetQueue(mapping.QueueName);
            if (queue != null)
            {
                success = await queue.AckAsync(mapping.QueueDeliveryTag);
            }

            // Decrement in-flight counter for prefetch control
            var inFlightKey = $"{connection.Id}:{mapping.QueueName}";
            _connectionInFlightCount.AddOrUpdate(inFlightKey, 0, (_, v) => Math.Max(0, v - 1));
        }

        var response = new Frame
        {
            Type = MessageType.Ack,
            CorrelationId = frame.CorrelationId,
            Payload = new { success }
        };
        
        await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
    }

    private async Task HandleNack(ClientConnection connection, Frame frame)
    {
        var payload = (NackPayload)frame.Payload!;
        
        var success = false;
        if (_deliveryTagMap.TryRemove(payload.DeliveryTag, out var mapping))
        {
            var queue = _queueManager.GetQueue(mapping.QueueName);
            if (queue != null)
            {
                success = await queue.NackAsync(mapping.QueueDeliveryTag, payload.Requeue);
            }

            // Decrement in-flight counter for prefetch control
            var inFlightKey = $"{connection.Id}:{mapping.QueueName}";
            _connectionInFlightCount.AddOrUpdate(inFlightKey, 0, (_, v) => Math.Max(0, v - 1));
        }

        var response = new Frame
        {
            Type = MessageType.Nack,
            CorrelationId = frame.CorrelationId,
            Payload = new { success }
        };
        
        await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
    }

    private async Task HandleSetPrefetch(ClientConnection connection, Frame frame)
    {
        var payload = (SetPrefetchPayload)frame.Payload!;
        connection.Prefetch = payload.Prefetch;

        var response = new Frame
        {
            Type = MessageType.SetPrefetch,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true, prefetch = payload.Prefetch }
        };
        
        await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
    }

    private async Task HandleHeartbeat(ClientConnection connection, Frame frame)
    {
        var response = new Frame
        {
            Type = MessageType.Heartbeat,
            CorrelationId = frame.CorrelationId,
            Payload = new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        };
        
        await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
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
            
            await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, errorFrame);
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
}