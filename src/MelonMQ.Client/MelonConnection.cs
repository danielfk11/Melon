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
    private readonly PipeReader _incomingReader;
    private readonly PipeWriter _outgoingWriter;
    private readonly PipeReader _outgoingReader;
    private readonly PipeWriter _incomingWriter;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<Frame>> _pendingRequests = new();
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private readonly Task _networkReaderTask;
    private ulong _nextCorrelationId = 1;
    private bool _disposed;

    private MelonConnection(TcpClient tcpClient, NetworkStream stream, PipeReader incomingReader, PipeWriter outgoingWriter, PipeReader outgoingReader, PipeWriter incomingWriter)
    {
        _tcpClient = tcpClient;
        _stream = stream;
        _incomingReader = incomingReader;
        _outgoingWriter = outgoingWriter;
        _outgoingReader = outgoingReader;
        _incomingWriter = incomingWriter;
        _cancellationTokenSource = new CancellationTokenSource();
        
        _readerTask = Task.Run(ProcessIncomingFrames);
        _writerTask = Task.Run(() => WriteToNetworkAsync(_cancellationTokenSource.Token));
        _networkReaderTask = Task.Run(() => ReadFromNetworkAsync(_cancellationTokenSource.Token));
        
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
        var incomingPipe = new Pipe(); // For data from server to client
        var outgoingPipe = new Pipe(); // For data from client to server
        
        var connection = new MelonConnection(tcpClient, stream, incomingPipe.Reader, outgoingPipe.Writer, outgoingPipe.Reader, incomingPipe.Writer);
        
        // Give pipes time to initialize properly
        await Task.Delay(100, cancellationToken);
        
        return connection;
    }

    public Task<MelonChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new MelonChannel(this));
    }

    internal async Task<Frame> SendRequestAsync(MessageType type, object? payload = null, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MelonConnection));

        var correlationId = Interlocked.Increment(ref _nextCorrelationId);
        var tcs = new TaskCompletionSource<Frame>();
        
        _pendingRequests[correlationId] = tcs;

        try
        {
            FrameSerializer.WriteFrame(_outgoingWriter, type, correlationId, payload);
            await _outgoingWriter.FlushAsync(cancellationToken);

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }

    internal void SendFrame(MessageType type, object? payload = null)
    {
        if (_disposed)
            return;

        var correlationId = Interlocked.Increment(ref _nextCorrelationId);
        try
        {
            FrameSerializer.WriteFrame(_outgoingWriter, type, correlationId, payload);
            _ = _outgoingWriter.FlushAsync();
        }
        catch
        {
            // Ignore send errors for fire-and-forget messages
        }
    }

    private async Task ReadFromNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                var memory = _incomingWriter.GetMemory(4096);
                var bytesRead = await _stream.ReadAsync(memory, cancellationToken);
                
                if (bytesRead == 0)
                    break;

                _incomingWriter.Advance(bytesRead);
                await _incomingWriter.FlushAsync(cancellationToken);
            }
        }
        catch (Exception)
        {
            // Connection closed
        }
        finally
        {
            await _incomingWriter.CompleteAsync();
        }
    }

    private async Task WriteToNetworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                var result = await _outgoingReader.ReadAsync(cancellationToken);
                if (result.IsCanceled)
                    break;

                var buffer = result.Buffer;
                if (buffer.IsEmpty && result.IsCompleted)
                    break;

                foreach (var segment in buffer)
                {
                    await _stream.WriteAsync(segment, cancellationToken);
                }

                await _stream.FlushAsync(cancellationToken);
                _outgoingReader.AdvanceTo(buffer.End);
            }
        }
        catch (Exception)
        {
            // Connection closed
        }
    }

    private async Task ProcessIncomingFrames()
    {
        Console.WriteLine("[CLIENT] ProcessIncomingFrames started");
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && !_disposed)
            {
                Console.WriteLine("[CLIENT] Waiting for frame...");
                var frame = await FrameSerializer.ReadFrameAsync(_incomingReader, _cancellationTokenSource.Token);
                if (frame == null)
                {
                    Console.WriteLine("[CLIENT] Frame is null, breaking");
                    break;
                }

                Console.WriteLine($"[CLIENT] Received frame type: {frame.Type}, correlationId: {frame.CorrelationId}");

                if (_pendingRequests.TryRemove(frame.CorrelationId, out var tcs))
                {
                    Console.WriteLine($"[CLIENT] Completing pending request for correlationId: {frame.CorrelationId}");
                    tcs.SetResult(frame);
                }
                else if (frame.Type == MessageType.Deliver)
                {
                    Console.WriteLine("[CLIENT] Handling delivery");
                    // Handle incoming delivery
                    await HandleDelivery(frame);
                }
                else
                {
                    Console.WriteLine($"[CLIENT] No handler for frame type: {frame.Type}, correlationId: {frame.CorrelationId}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CLIENT] Error in ProcessIncomingFrames: {ex.Message}");
            // Connection error - complete all pending requests
            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetException(new InvalidOperationException("Connection lost"));
            }
        }
        finally
        {
            Console.WriteLine("[CLIENT] ProcessIncomingFrames ended");
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
            await _writerTask;
            await _networkReaderTask;
        }
        catch { }

        _tcpClient.Close();
        _cancellationTokenSource.Dispose();
    }
}