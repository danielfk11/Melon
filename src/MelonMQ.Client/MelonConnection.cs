using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MelonMQ.Common;
using Microsoft.Extensions.Logging;

namespace MelonMQ.Client;

/// <summary>
/// Connection interface for MelonMQ client
/// </summary>
public interface IMelonConnection : IAsyncDisposable
{
    string ConnectionId { get; }
    bool IsConnected { get; }
    ConnectionState State { get; }
    
    Task<IMelonChannel> CreateChannelAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Channel interface for logical operations
/// </summary>
public interface IMelonChannel : IAsyncDisposable
{
    string ChannelId { get; }
    bool IsOpen { get; }
    
    // Exchange operations
    Task DeclareExchangeAsync(string name, ExchangeType type, bool durable = false, Dictionary<string, object>? arguments = null, CancellationToken cancellationToken = default);
    Task DeleteExchangeAsync(string name, bool ifUnused = false, CancellationToken cancellationToken = default);
    
    // Queue operations
    Task DeclareQueueAsync(string name, bool durable = false, bool exclusive = false, bool autoDelete = false, Dictionary<string, object>? arguments = null, CancellationToken cancellationToken = default);
    Task DeleteQueueAsync(string name, bool ifUnused = false, bool ifEmpty = false, CancellationToken cancellationToken = default);
    Task PurgeQueueAsync(string name, CancellationToken cancellationToken = default);
    
    // Binding operations
    Task BindQueueAsync(string queue, string exchange, string routingKey, Dictionary<string, object>? arguments = null, CancellationToken cancellationToken = default);
    Task UnbindQueueAsync(string queue, string exchange, string routingKey, CancellationToken cancellationToken = default);
    
    // Publishing
    Task PublishAsync(string exchange, string routingKey, BinaryData body, MessageProperties? properties = null, bool persistent = false, byte priority = 0, Guid? messageId = null, CancellationToken cancellationToken = default);
    Task<PublishConfirmation> PublishWithConfirmationAsync(string exchange, string routingKey, BinaryData body, MessageProperties? properties = null, bool persistent = false, byte priority = 0, Guid? messageId = null, CancellationToken cancellationToken = default);
    
    // Consuming
    IAsyncEnumerable<MessageDelivery> ConsumeAsync(string queue, string? consumerTag = null, int prefetch = 100, bool noAck = false, CancellationToken cancellationToken = default);
    Task SetPrefetchAsync(int prefetch, CancellationToken cancellationToken = default);
    
    // Flow control
    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Connection state enumeration
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    Closing,
    Closed
}

/// <summary>
/// Message delivery with acknowledgment support
/// </summary>
public class MessageDelivery
{
    public required Message Message { get; init; }
    public required string ConsumerTag { get; init; }
    public required ulong DeliveryTag { get; init; }
    public required IMelonChannel Channel { get; init; }
    
    public async Task AckAsync(CancellationToken cancellationToken = default)
    {
        await ((MelonChannel)Channel).AckAsync(DeliveryTag, cancellationToken);
    }
    
    public async Task NackAsync(bool requeue = true, CancellationToken cancellationToken = default)
    {
        await ((MelonChannel)Channel).NackAsync(DeliveryTag, requeue, cancellationToken);
    }
    
    public async Task RejectAsync(bool requeue = true, CancellationToken cancellationToken = default)
    {
        await ((MelonChannel)Channel).RejectAsync(DeliveryTag, requeue, cancellationToken);
    }
}

/// <summary>
/// Publish confirmation
/// </summary>
public class PublishConfirmation
{
    public bool IsConfirmed { get; init; }
    public string? ErrorMessage { get; init; }
    public ulong CorrelationId { get; init; }
}

/// <summary>
/// Connection factory for creating MelonMQ connections
/// </summary>
public static class MelonConnection
{
    public static async Task<IMelonConnection> ConnectAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(connectionString);
        var host = uri.Host;
        var port = uri.Port != -1 ? uri.Port : 5672;
        
        string? username = null;
        string? password = null;
        
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':');
            username = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        
        return await ConnectAsync(host, port, username, password, cancellationToken);
    }
    
    public static async Task<IMelonConnection> ConnectAsync(string host, int port = 5672, string? username = null, string? password = null, CancellationToken cancellationToken = default)
    {
        var connection = new MelonConnectionImpl(host, port, username, password);
        await connection.ConnectAsync(cancellationToken);
        return connection;
    }
}

/// <summary>
/// Connection implementation
/// </summary>
internal class MelonConnectionImpl : IMelonConnection
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _username;
    private readonly string? _password;
    private readonly ILogger<MelonConnectionImpl> _logger;
    private readonly ConcurrentDictionary<string, MelonChannel> _channels = new();
    
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private PipeReader? _reader;
    private PipeWriter? _writer;
    private Task? _readTask;
    private Task? _heartbeatTask;
    private readonly CancellationTokenSource _connectionCts = new();
    private readonly AtomicCounter _correlationCounter = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<ProtocolFrame>> _pendingRequests = new();
    
    public string ConnectionId { get; } = Guid.NewGuid().ToString();
    public bool IsConnected => State == ConnectionState.Connected;
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    
    public MelonConnectionImpl(string host, int port, string? username, string? password)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
        _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<MelonConnectionImpl>();
    }
    
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Disconnected)
            throw new InvalidOperationException($"Cannot connect in state {State}");
        
        State = ConnectionState.Connecting;
        
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_host, _port, cancellationToken);
            
            _networkStream = _tcpClient.GetStream();
            
            // Create pipes for async I/O
            var inputPipe = new Pipe();
            var outputPipe = new Pipe();
            
            _reader = inputPipe.Reader;
            _writer = outputPipe.Writer;
            
            // Start read/write tasks
            _readTask = ReadFromNetworkAsync(_networkStream, inputPipe.Writer, _connectionCts.Token);
            _ = WriteToNetworkAsync(_networkStream, outputPipe.Reader, _connectionCts.Token);
            
            // Authenticate
            if (!string.IsNullOrEmpty(_username))
            {
                await AuthenticateAsync(cancellationToken);
            }
            
            State = ConnectionState.Connected;
            
            // Start heartbeat
            _heartbeatTask = SendHeartbeatsAsync(_connectionCts.Token);
            
            _logger.LogInformation("Connected to MelonMQ broker at {Host}:{Port}", _host, _port);
        }
        catch
        {
            State = ConnectionState.Disconnected;
            await DisposeResourcesAsync();
            throw;
        }
    }
    
    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        State = ConnectionState.Authenticating;
        
        var correlationId = (ulong)_correlationCounter.Next();
        var tcs = new TaskCompletionSource<ProtocolFrame>();
        _pendingRequests[correlationId] = tcs;
        
        try
        {
            var writer = new ArrayBufferWriter<byte>();
            var tlvWriter = new TlvWriter(writer);
            
            tlvWriter.WriteString(_username!);
            tlvWriter.WriteString(_password!);
            
            FrameCodec.WriteFrame(_writer!, FrameType.Auth, FrameFlags.Request, correlationId, writer.WrittenMemory.Span);
            await _writer!.FlushAsync(cancellationToken);
            
            var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            
            if (response.Type == FrameType.AuthOk)
            {
                _logger.LogDebug("Authentication successful for user {Username}", _username);
            }
            else if (response.Type == FrameType.Error)
            {
                var reader = new TlvReader(response.Payload.Span);
                reader.TryReadInt32(out var errorCode);
                reader.TryReadString(out var errorMessage);
                throw new AuthenticationException($"Authentication failed: {errorMessage}");
            }
            else
            {
                throw new AuthenticationException("Unexpected authentication response");
            }
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }
    
    public async Task<IMelonChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Connection is not established");
        
        var channel = new MelonChannel(this, _logger.CreateLogger<MelonChannel>());
        _channels[channel.ChannelId] = channel;
        
        return channel;
    }
    
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (State == ConnectionState.Closed || State == ConnectionState.Closing)
            return;
        
        State = ConnectionState.Closing;
        
        try
        {
            // Close all channels
            var closeChannelTasks = _channels.Values.Select(c => c.CloseAsync().AsTask());
            await Task.WhenAll(closeChannelTasks);
            
            // Send close frame
            if (_writer != null)
            {
                var correlationId = (ulong)_correlationCounter.Next();
                FrameCodec.WriteFrame(_writer, FrameType.Close, FrameFlags.Request, correlationId, ReadOnlySpan<byte>.Empty);
                await _writer.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during connection close");
        }
        finally
        {
            State = ConnectionState.Closed;
            _connectionCts.Cancel();
            await DisposeResourcesAsync();
        }
    }
    
    private async Task ReadFromNetworkAsync(NetworkStream networkStream, PipeWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var memory = writer.GetMemory(65536);
                var bytesRead = await networkStream.ReadAsync(memory, cancellationToken);
                
                if (bytesRead == 0)
                    break;
                
                writer.Advance(bytesRead);
                
                var result = await writer.FlushAsync(cancellationToken);
                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogError(ex, "Error reading from network");
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }
    
    private async Task WriteToNetworkAsync(NetworkStream networkStream, PipeReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var buffer = result.Buffer;
                
                if (buffer.IsEmpty && result.IsCompleted)
                    break;
                
                try
                {
                    foreach (var segment in buffer)
                    {
                        await networkStream.WriteAsync(segment, cancellationToken);
                    }
                    
                    await networkStream.FlushAsync(cancellationToken);
                }
                finally
                {
                    reader.AdvanceTo(buffer.End);
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogError(ex, "Error writing to network");
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }
    
    private async Task SendHeartbeatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                await Task.Delay(ProtocolConstants.DefaultHeartbeatIntervalMs, cancellationToken);
                
                if (_writer != null)
                {
                    var correlationId = (ulong)_correlationCounter.Next();
                    FrameCodec.WriteFrame(_writer, FrameType.Heartbeat, FrameFlags.Request, correlationId, ReadOnlySpan<byte>.Empty);
                    await _writer.FlushAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogError(ex, "Error sending heartbeats");
        }
    }
    
    internal async Task<ProtocolFrame> SendRequestAsync(FrameType frameType, FrameFlags flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Connection is not established");
        
        var correlationId = (ulong)_correlationCounter.Next();
        var tcs = new TaskCompletionSource<ProtocolFrame>();
        _pendingRequests[correlationId] = tcs;
        
        try
        {
            FrameCodec.WriteFrame(_writer!, frameType, flags | FrameFlags.Request, correlationId, payload.Span);
            await _writer!.FlushAsync(cancellationToken);
            
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }
    
    internal void RemoveChannel(string channelId)
    {
        _channels.TryRemove(channelId, out _);
    }
    
    private async Task DisposeResourcesAsync()
    {
        _connectionCts.Cancel();
        
        if (_readTask != null)
        {
            try { await _readTask; } catch { }
        }
        
        if (_heartbeatTask != null)
        {
            try { await _heartbeatTask; } catch { }
        }
        
        _reader?.Complete();
        _writer?.Complete();
        _networkStream?.Dispose();
        _tcpClient?.Dispose();
    }
    
    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _connectionCts.Dispose();
    }
}