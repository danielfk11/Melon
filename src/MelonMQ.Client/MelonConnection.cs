using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MelonMQ.Client.Protocol;

namespace MelonMQ.Client;

public class MelonConnection : IDisposable, IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<Frame>> _pendingRequests = new();
    private readonly Task _readerTask;
    private ulong _nextCorrelationId = 1;
    private bool _disposed;

    private MelonConnection(TcpClient tcpClient, NetworkStream stream, PipeReader reader, PipeWriter writer)
    {
        _tcpClient = tcpClient;
        _stream = stream;
        _reader = reader;
        _writer = writer;
        _cancellationTokenSource = new CancellationTokenSource();
        
        _readerTask = Task.Run(ProcessIncomingFrames);
        
        // Start heartbeat task
        _ = Task.Run(HeartbeatTask);
    }

    public static async Task<MelonConnection> ConnectAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(connectionString);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5672;

        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, cancellationToken);
        
        var stream = tcpClient.GetStream();
        var pipe = new Pipe();
        
        var connection = new MelonConnection(tcpClient, stream, pipe.Reader, pipe.Writer);
        
        // Start network read task
        _ = Task.Run(() => connection.ReadFromNetworkAsync(cancellationToken));
        
        return connection;
    }

    public Task<MelonChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new MelonChannel(this));
    }

    internal async Task<Frame> SendRequestAsync(MessageType type, object? payload = null, CancellationToken cancellationToken = default)
    {
        var correlationId = Interlocked.Increment(ref _nextCorrelationId);
        var tcs = new TaskCompletionSource<Frame>();
        
        _pendingRequests[correlationId] = tcs;

        try
        {
            FrameSerializer.WriteFrame(_writer, type, correlationId, payload);
            await _writer.FlushAsync(cancellationToken);

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }

    internal void SendFrame(MessageType type, object? payload = null)
    {
        var correlationId = Interlocked.Increment(ref _nextCorrelationId);
        FrameSerializer.WriteFrame(_writer, type, correlationId, payload);
        _ = _writer.FlushAsync();
    }

    private async Task ReadFromNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                var memory = _writer.GetMemory(4096);
                var bytesRead = await _stream.ReadAsync(memory, cancellationToken);
                
                if (bytesRead == 0)
                    break;

                _writer.Advance(bytesRead);
                await _writer.FlushAsync(cancellationToken);
            }
        }
        catch (Exception)
        {
            // Connection closed
        }
        finally
        {
            await _writer.CompleteAsync();
        }
    }

    private async Task ProcessIncomingFrames()
    {
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && !_disposed)
            {
                var frame = await FrameSerializer.ReadFrameAsync(_reader, _cancellationTokenSource.Token);
                if (frame == null)
                    break;

                if (_pendingRequests.TryRemove(frame.CorrelationId, out var tcs))
                {
                    tcs.SetResult(frame);
                }
                else if (frame.Type == MessageType.Deliver)
                {
                    // Handle incoming delivery
                    await HandleDelivery(frame);
                }
            }
        }
        catch (Exception)
        {
            // Connection error - complete all pending requests
            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetException(new InvalidOperationException("Connection lost"));
            }
        }
    }

    private readonly ConcurrentDictionary<string, Channel<IncomingMessage>> _deliveryChannels = new();

    internal Channel<IncomingMessage> GetOrCreateDeliveryChannel(string queue)
    {
        return _deliveryChannels.GetOrAdd(queue, _ => 
            Channel.CreateUnbounded<IncomingMessage>());
    }

    private async Task HandleDelivery(Frame frame)
    {
        if (frame.Payload?.TryGetProperty("queue", out var queueElement) == true)
        {
            var queue = queueElement.GetString()!;
            var deliveryTag = frame.Payload.Value.GetProperty("deliveryTag").GetUInt64();
            var bodyBase64 = frame.Payload.Value.GetProperty("bodyBase64").GetString()!;
            var redelivered = frame.Payload.Value.GetProperty("redelivered").GetBoolean();
            var messageId = Guid.Parse(frame.Payload.Value.GetProperty("messageId").GetString()!);

            var message = new IncomingMessage
            {
                DeliveryTag = deliveryTag,
                Body = Convert.FromBase64String(bodyBase64),
                Redelivered = redelivered,
                MessageId = messageId,
                Queue = queue
            };

            if (_deliveryChannels.TryGetValue(queue, out var channel))
            {
                await channel.Writer.WriteAsync(message);
            }
        }
    }

    private async Task HeartbeatTask()
    {
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && !_disposed)
            {
                await Task.Delay(10000, _cancellationTokenSource.Token); // Every 10 seconds
                SendFrame(MessageType.Heartbeat);
            }
        }
        catch (OperationCanceledException)
        {
            // Task cancelled
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellationTokenSource.Cancel();
        _tcpClient.Close();
        _cancellationTokenSource.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellationTokenSource.Cancel();
        
        try
        {
            await _readerTask;
        }
        catch { }

        _tcpClient.Close();
        _cancellationTokenSource.Dispose();
    }
}