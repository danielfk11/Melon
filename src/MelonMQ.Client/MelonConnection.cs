using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using MelonMQ.Client.Protocol;
using MelonMQ.Protocol;

namespace MelonMQ.Client;

public class MelonConnection : IDisposable, IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly PipeReader _incomingReader;
    private readonly PipeWriter _outgoingWriter;
    private readonly PipeReader _outgoingReader;
    private readonly PipeWriter _incomingWriter;
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<Frame>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _readerTask;
    private readonly Task _writerTask;
    private readonly Task _networkReaderTask;
    private readonly TimeSpan _heartbeatInterval;
    private ulong _nextCorrelationId = 1;
    private bool _disposed;

    private MelonConnection(TcpClient tcpClient, Stream stream, PipeReader incomingReader, PipeWriter outgoingWriter, PipeReader outgoingReader, PipeWriter incomingWriter, TimeSpan heartbeatInterval)
    {
        _tcpClient = tcpClient;
        _stream = stream;
        _incomingReader = incomingReader;
        _outgoingWriter = outgoingWriter;
        _outgoingReader = outgoingReader;
        _incomingWriter = incomingWriter;
        _heartbeatInterval = heartbeatInterval > TimeSpan.Zero ? heartbeatInterval : TimeSpan.FromSeconds(10);
        _cancellationTokenSource = new CancellationTokenSource();
        
        _readerTask = Task.Run(ProcessIncomingFrames);
        _writerTask = Task.Run(() => WriteToNetworkAsync(_cancellationTokenSource.Token));
        _networkReaderTask = Task.Run(() => ReadFromNetworkAsync(_cancellationTokenSource.Token));
        
        // Start heartbeat task
        _ = Task.Run(HeartbeatTask);
    }

    public static async Task<MelonConnection> ConnectAsync(string connectionString, MelonConnectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MelonConnectionOptions();

        var uri = new Uri(connectionString);
        if (!string.Equals(uri.Scheme, "melon", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "melons", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported connection scheme '{uri.Scheme}'. Use melon:// or melons://.");
        }

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5672;
        var useTls = options.UseTls || string.Equals(uri.Scheme, "melons", StringComparison.OrdinalIgnoreCase);

        var retryPolicy = options.RetryPolicy;
        var tcpClient = await ConnectionHelper.ConnectWithRetryAsync(host, port, retryPolicy, cancellationToken);

        FrameSerializer.ConfigureMaxFrameSize(MessageSizePolicy.ComputeMaxFrameSizeBytes(options.MaxMessageSize));

        Stream stream = tcpClient.GetStream();
        if (useTls)
        {
            var sslStream = new SslStream(
                stream,
                leaveInnerStreamOpen: false,
                (_, _, _, sslPolicyErrors) =>
                {
                    if (options.AllowUntrustedServerCertificate)
                    {
                        return true;
                    }

                    return sslPolicyErrors == SslPolicyErrors.None;
                });

            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = options.TlsTargetHost ?? host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = options.CheckCertificateRevocation
                    ? X509RevocationMode.Online
                    : X509RevocationMode.NoCheck,
                ClientCertificates = options.ClientCertificates
            };

            await sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken);
            stream = sslStream;
        }

        var incomingPipe = new Pipe(); // For data from server to client
        var outgoingPipe = new Pipe(); // For data from client to server
        
        var connection = new MelonConnection(
            tcpClient,
            stream,
            incomingPipe.Reader,
            outgoingPipe.Writer,
            outgoingPipe.Reader,
            incomingPipe.Writer,
            options.HeartbeatInterval);
        
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
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        _pendingRequests[correlationId] = tcs;

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                FrameSerializer.WriteFrame(_outgoingWriter, type, correlationId, payload);
                await _outgoingWriter.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            if (!tcs.Task.IsCompleted)
            {
                tcs.TrySetException(ex);
            }
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
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeLock.WaitAsync(_cancellationTokenSource.Token);
                try
                {
                    FrameSerializer.WriteFrame(_outgoingWriter, type, correlationId, payload);
                    await _outgoingWriter.FlushAsync(_cancellationTokenSource.Token);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch
            {
                // Ignore send errors for fire-and-forget messages
            }
        });
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
        catch (Exception ex)
        {
            FailPendingRequests(new InvalidOperationException($"Connection lost while reading: {ex.Message}", ex));
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
        catch (Exception ex)
        {
            FailPendingRequests(new InvalidOperationException($"Connection lost while writing: {ex.Message}", ex));
        }
    }

    private async Task ProcessIncomingFrames()
    {
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && !_disposed)
            {
                var frame = await FrameSerializer.ReadFrameAsync(_incomingReader, _cancellationTokenSource.Token);
                if (frame == null)
                {
                    FailPendingRequests(new InvalidOperationException("Connection closed by remote host."));
                    break;
                }

                if (_pendingRequests.TryRemove(frame.CorrelationId, out var tcs))
                {
                    tcs.SetResult(frame);
                }
                else if (frame.Type == MessageType.Deliver)
                {
                    await HandleDelivery(frame);
                }
            }
        }
        catch (Exception ex)
        {
            FailPendingRequests(new InvalidOperationException($"Connection lost: {ex.Message}", ex));
        }
    }

    private void FailPendingRequests(Exception ex)
    {
        foreach (var pending in _pendingRequests.ToArray())
        {
            if (_pendingRequests.TryRemove(pending.Key, out var tcs))
            {
                tcs.TrySetException(ex);
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
        try
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
                    Queue = queue,
                    Offset = frame.Payload.Value.TryGetProperty("offset", out var offsetEl) &&
                             offsetEl.ValueKind != System.Text.Json.JsonValueKind.Null
                             ? offsetEl.GetInt64() : null
                };

                if (_deliveryChannels.TryGetValue(queue, out var channel))
                {
                    await channel.Writer.WriteAsync(message);
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't crash the processing loop
            System.Diagnostics.Debug.WriteLine($"Error processing delivery frame: {ex.Message}");
        }
    }

    private async Task HeartbeatTask()
    {
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested && !_disposed)
            {
                await Task.Delay(_heartbeatInterval, _cancellationTokenSource.Token);
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

        FailPendingRequests(new ObjectDisposedException(nameof(MelonConnection), "Connection was disposed."));
        _cancellationTokenSource.Cancel();
        _stream.Dispose();
        _tcpClient.Close();
        _cancellationTokenSource.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        FailPendingRequests(new ObjectDisposedException(nameof(MelonConnection), "Connection was disposed."));
        _cancellationTokenSource.Cancel();
        
        try
        {
            await _readerTask;
            await _writerTask;
            await _networkReaderTask;
        }
        catch { }

        await _stream.DisposeAsync();
        _tcpClient.Close();
        _cancellationTokenSource.Dispose();
    }
}