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

        try
        {
            var socket = tcpClient.Client;
            var stream = tcpClient.GetStream();
            var pipe = new Pipe();
            
            connection = new ClientConnection(
                connectionId, 
                socket, 
                pipe.Reader, 
                pipe.Writer);

            _connectionManager.AddConnection(connection);

            // Start reading from network into pipe
            var readTask = ReadFromNetworkAsync(stream, pipe.Writer, cancellationToken);
            
            // Start processing messages from pipe
            var processTask = ProcessMessagesAsync(connection, cancellationToken);

            await Task.WhenAny(readTask, processTask);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling client {ConnectionId}", connectionId);
        }
        finally
        {
            if (connection != null)
            {
                // Requeue any in-flight messages
                foreach (var queue in _queueManager.GetAllQueues())
                {
                    await queue.RequeuePendingMessagesForConnection(connectionId);
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
        
        FrameSerializer.WriteFrame(connection.Writer, response);
        await connection.Writer.FlushAsync();
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
        
        FrameSerializer.WriteFrame(connection.Writer, response);
        await connection.Writer.FlushAsync();
    }

    private async Task HandlePublish(ClientConnection connection, Frame frame)
    {
        var payload = (PublishPayload)frame.Payload!;
        var queue = _queueManager.GetQueue(payload.Queue);
        
        if (queue == null)
        {
            await SendError(connection, frame.CorrelationId, $"Queue '{payload.Queue}' does not exist");
            return;
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

        var response = new Frame
        {
            Type = MessageType.Publish,
            CorrelationId = frame.CorrelationId,
            Payload = new { success = true, messageId = payload.MessageId }
        };
        
        FrameSerializer.WriteFrame(connection.Writer, response);
        await connection.Writer.FlushAsync();
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

                    FrameSerializer.WriteFrame(connection.Writer, deliverFrame);
                    await connection.Writer.FlushAsync();
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
        
        FrameSerializer.WriteFrame(connection.Writer, response);
        await connection.Writer.FlushAsync();
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
        
        FrameSerializer.WriteFrame(connection.Writer, response);
        await connection.Writer.FlushAsync();
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
        
        FrameSerializer.WriteFrame(connection.Writer, response);
        await connection.Writer.FlushAsync();
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
        
        FrameSerializer.WriteFrame(connection.Writer, response);
        await connection.Writer.FlushAsync();
    }

    private async Task HandleHeartbeat(ClientConnection connection, Frame frame)
    {
        var response = new Frame
        {
            Type = MessageType.Heartbeat,
            CorrelationId = frame.CorrelationId,
            Payload = new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        };
        
        FrameSerializer.WriteFrame(connection.Writer, response);
        await connection.Writer.FlushAsync();
    }

    private async Task SendError(ClientConnection connection, ulong correlationId, string message)
    {
        var errorFrame = new Frame
        {
            Type = MessageType.Error,
            CorrelationId = correlationId,
            Payload = new ErrorPayload { Message = message }
        };
        
        FrameSerializer.WriteFrame(connection.Writer, errorFrame);
        await connection.Writer.FlushAsync();
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