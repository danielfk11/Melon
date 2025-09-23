using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MelonMQ.Broker.Protocol;

namespace MelonMQ.Broker.Core;

public class TcpServer
{
    private readonly QueueManager _queueManager;
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger<TcpServer> _logger;
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private long _nextDeliveryTag = 0;

    public TcpServer(QueueManager queueManager, ConnectionManager connectionManager, int port, ILogger<TcpServer> logger)
    {
        _queueManager = queueManager;
        _connectionManager = connectionManager;
        _port = port;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        _logger.LogInformation("TCP Server started on port {Port}", _port);

        // Start cleanup task
        _ = Task.Run(() => CleanupTask(_cancellationTokenSource.Token));

        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync();
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
            switch (frame.Type)
            {
                case MessageType.Auth:
                    await HandleAuth(connection, frame);
                    break;
                
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
                
                case MessageType.Heartbeat:
                    await HandleHeartbeat(connection, frame);
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
        // Simple auth - for now just mark as authenticated
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
        
        // DETECT ISSUE: If queue is empty but we got a success payload, something is wrong
        if (string.IsNullOrEmpty(payload.Queue) && string.IsNullOrEmpty(payload.BodyBase64))
        {
            _logger.LogError("DETECTED LOOP: Received PublishPayload with empty Queue and BodyBase64 - this looks like a response payload being re-sent as request!");
            _logger.LogError("Frame CorrelationId: {CorrelationId}, MessageId: {MessageId}", frame.CorrelationId, payload.MessageId);
            return; // Don't process this malformed request
        }
        
        _logger.LogInformation("HandlePublish called for queue '{QueueName}'", payload.Queue);
        
        var queue = _queueManager.GetQueue(payload.Queue);
        
        if (queue == null)
        {
            // Auto-create queue when it doesn't exist
            _logger.LogInformation("Auto-creating queue '{QueueName}' for publish operation", payload.Queue);
            queue = _queueManager.DeclareQueue(payload.Queue, durable: false);
        }
        else
        {
            _logger.LogInformation("Queue '{QueueName}' already exists, using existing queue", payload.Queue);
        }

        var body = Convert.FromBase64String(payload.BodyBase64);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        var message = new QueueMessage
        {
            MessageId = payload.MessageId,
            Body = body,
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
                Type = MessageType.Publish, // Keep consistent with other operations
                CorrelationId = frame.CorrelationId,
                Payload = new { success = true, messageId = payload.MessageId }
            };
            
            _logger.LogInformation("Sending publish success response with correlationId {CorrelationId}", frame.CorrelationId);
            await FrameSerializer.WriteFrameToStreamAsync(connection.Stream, response);
            _logger.LogInformation("Successfully sent publish response, now waiting for next request...");
        }
        catch (InvalidOperationException)
        {
            // Connection already closed - message was still processed successfully
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

        // Start consuming
        _ = Task.Run(async () =>
        {
            try
            {
                while (!consumerCts.Token.IsCancellationRequested)
                {
                    var message = await queue.DequeueAsync(connection.Id, consumerCts.Token);
                    if (message == null) continue;

                    var deliverPayload = new DeliverPayload
                    {
                        Queue = payload.Queue,
                        DeliveryTag = (ulong)Interlocked.Increment(ref _nextDeliveryTag),
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
        
        // Find the queue containing this delivery tag (simplified approach)
        var success = false;
        foreach (var queue in _queueManager.GetAllQueues())
        {
            if (await queue.AckAsync(payload.DeliveryTag))
            {
                success = true;
                break;
            }
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
        foreach (var queue in _queueManager.GetAllQueues())
        {
            if (await queue.NackAsync(payload.DeliveryTag, payload.Requeue))
            {
                success = true;
                break;
            }
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