using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MelonMQ.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MelonMQ.Broker.Network;

/// <summary>
/// TCP server for handling MelonMQ protocol connections
/// </summary>
public class TcpServer : BackgroundService
{
    private readonly TcpListener _listener;
    private readonly IConnectionHandler _connectionHandler;
    private readonly ILogger<TcpServer> _logger;
    private readonly CancellationTokenSource _stoppingCts = new();
    
    public TcpServer(IPEndPoint endpoint, IConnectionHandler connectionHandler, ILogger<TcpServer> logger)
    {
        _listener = new TcpListener(endpoint);
        _connectionHandler = connectionHandler;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start();
        _logger.LogInformation("TCP server started on {Endpoint}", _listener.LocalEndpoint);
        
        var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _stoppingCts.Token).Token;
        
        try
        {
            while (!combinedToken.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    
                    // Handle connection on background task
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleConnectionAsync(tcpClient, combinedToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error handling connection from {RemoteEndpoint}", 
                                tcpClient.Client.RemoteEndPoint);
                        }
                        finally
                        {
                            tcpClient.Close();
                        }
                    }, combinedToken);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!combinedToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Error accepting TCP connection");
                        await Task.Delay(1000, combinedToken);
                    }
                }
            }
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("TCP server stopped");
        }
    }
    
    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var connection = new TcpConnection(tcpClient, _logger);
        await _connectionHandler.HandleConnectionAsync(connection, cancellationToken);
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts.Cancel();
        await base.StopAsync(cancellationToken);
    }
    
    public override void Dispose()
    {
        _stoppingCts.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Represents a TCP connection using System.IO.Pipelines for high performance
/// </summary>
public class TcpConnection : IConnection
{
    private readonly TcpClient _tcpClient;
    private readonly ILogger _logger;
    private readonly Pipe _inputPipe;
    private readonly Pipe _outputPipe;
    private readonly Task _sendTask;
    private readonly Task _receiveTask;
    private readonly CancellationTokenSource _connectionCts = new();
    
    public string Id { get; } = Guid.NewGuid().ToString();
    public EndPoint? RemoteEndPoint => _tcpClient.Client.RemoteEndPoint;
    public EndPoint? LocalEndPoint => _tcpClient.Client.LocalEndPoint;
    public bool IsConnected => _tcpClient.Connected && !_connectionCts.Token.IsCancellationRequested;
    
    public PipeReader Input => _inputPipe.Reader;
    public PipeWriter Output => _outputPipe.Writer;
    
    public TcpConnection(TcpClient tcpClient, ILogger logger)
    {
        _tcpClient = tcpClient;
        _logger = logger;
        
        // Configure TCP settings for low latency
        _tcpClient.NoDelay = true;
        _tcpClient.ReceiveBufferSize = 65536;
        _tcpClient.SendBufferSize = 65536;
        
        // Create pipes for buffering
        _inputPipe = new Pipe();
        _outputPipe = new Pipe();
        
        // Start send/receive tasks
        _sendTask = SendLoopAsync(_connectionCts.Token);
        _receiveTask = ReceiveLoopAsync(_connectionCts.Token);
        
        _logger.LogDebug("TCP connection {ConnectionId} established from {RemoteEndpoint}", 
            Id, RemoteEndPoint);
    }
    
    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var networkStream = _tcpClient.GetStream();
            
            while (!cancellationToken.IsCancellationRequested && _tcpClient.Connected)
            {
                var result = await _outputPipe.Reader.ReadAsync(cancellationToken);
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
                    _outputPipe.Reader.AdvanceTo(buffer.End);
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogError(ex, "Error in send loop for connection {ConnectionId}", Id);
        }
        finally
        {
            await _outputPipe.Reader.CompleteAsync();
        }
    }
    
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var networkStream = _tcpClient.GetStream();
            
            while (!cancellationToken.IsCancellationRequested && _tcpClient.Connected)
            {
                var memory = _inputPipe.Writer.GetMemory(65536);
                var bytesRead = await networkStream.ReadAsync(memory, cancellationToken);
                
                if (bytesRead == 0)
                    break;
                
                _inputPipe.Writer.Advance(bytesRead);
                
                var result = await _inputPipe.Writer.FlushAsync(cancellationToken);
                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.LogError(ex, "Error in receive loop for connection {ConnectionId}", Id);
        }
        finally
        {
            await _inputPipe.Writer.CompleteAsync();
        }
    }
    
    public async ValueTask CloseAsync()
    {
        if (!_connectionCts.IsCancellationRequested)
        {
            _connectionCts.Cancel();
            
            try
            {
                await Task.WhenAll(_sendTask, _receiveTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing connection {ConnectionId}", Id);
            }
            finally
            {
                _tcpClient.Close();
                _logger.LogDebug("TCP connection {ConnectionId} closed", Id);
            }
        }
    }
    
    public void Dispose()
    {
        _connectionCts.Cancel();
        _connectionCts.Dispose();
        _tcpClient.Dispose();
    }
}

/// <summary>
/// Connection interface
/// </summary>
public interface IConnection : IDisposable
{
    string Id { get; }
    EndPoint? RemoteEndPoint { get; }
    EndPoint? LocalEndPoint { get; }
    bool IsConnected { get; }
    
    PipeReader Input { get; }
    PipeWriter Output { get; }
    
    ValueTask CloseAsync();
}

/// <summary>
/// Connection handler interface
/// </summary>
public interface IConnectionHandler
{
    Task HandleConnectionAsync(IConnection connection, CancellationToken cancellationToken);
}

/// <summary>
/// Protocol handler for processing MelonMQ frames
/// </summary>
public class ProtocolHandler : IConnectionHandler
{
    private readonly IChannelManager _channelManager;
    private readonly ILogger<ProtocolHandler> _logger;
    
    public ProtocolHandler(IChannelManager channelManager, ILogger<ProtocolHandler> logger)
    {
        _channelManager = channelManager;
        _logger = logger;
    }
    
    public async Task HandleConnectionAsync(IConnection connection, CancellationToken cancellationToken)
    {
        var channel = await _channelManager.CreateChannelAsync(connection);
        
        try
        {
            await ProcessFramesAsync(channel, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing frames for connection {ConnectionId}", connection.Id);
        }
        finally
        {
            await _channelManager.CloseChannelAsync(channel.Id);
            await connection.CloseAsync();
        }
    }
    
    private async Task ProcessFramesAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var reader = new SequenceReader<byte>(ReadOnlySequence<byte>.Empty);
        
        while (!cancellationToken.IsCancellationRequested && channel.Connection.IsConnected)
        {
            var result = await channel.Connection.Input.ReadAsync(cancellationToken);
            var buffer = result.Buffer;
            
            if (buffer.IsEmpty && result.IsCompleted)
                break;
            
            reader = new SequenceReader<byte>(buffer);
            
            // Process complete frames
            while (FrameCodec.TryReadFrame(ref reader, out var frame))
            {
                try
                {
                    await channel.ProcessFrameAsync(frame, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing frame {FrameType} for channel {ChannelId}", 
                        frame.Type, channel.Id);
                    
                    // Send error response
                    await channel.SendErrorAsync(ErrorCode.InternalError, ex.Message, frame.CorrelationId, cancellationToken);
                }
            }
            
            // Mark consumed data
            var consumed = buffer.GetPosition(reader.Position.GetInteger());
            channel.Connection.Input.AdvanceTo(consumed, buffer.End);
            
            if (result.IsCompleted)
                break;
        }
    }
}

/// <summary>
/// Channel manager interface
/// </summary>
public interface IChannelManager
{
    Task<IChannel> CreateChannelAsync(IConnection connection);
    Task<IChannel?> GetChannelAsync(string channelId);
    Task CloseChannelAsync(string channelId);
    Task<IChannel[]> GetAllChannelsAsync();
}

/// <summary>
/// Channel interface representing a logical connection
/// </summary>
public interface IChannel
{
    string Id { get; }
    IConnection Connection { get; }
    bool IsAuthenticated { get; }
    string? Username { get; }
    string VHost { get; }
    
    Task ProcessFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken);
    Task SendFrameAsync(FrameType frameType, FrameFlags flags, ulong correlationId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    Task SendErrorAsync(ErrorCode errorCode, string message, ulong correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// Default channel manager implementation
/// </summary>
public class ChannelManager : IChannelManager
{
    private readonly ConcurrentDictionary<string, IChannel> _channels = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChannelManager> _logger;
    
    public ChannelManager(IServiceProvider serviceProvider, ILogger<ChannelManager> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }
    
    public Task<IChannel> CreateChannelAsync(IConnection connection)
    {
        var channel = new Channel(connection, _serviceProvider, _logger.CreateLogger<Channel>());
        
        if (!_channels.TryAdd(channel.Id, channel))
        {
            throw new InvalidOperationException($"Channel {channel.Id} already exists");
        }
        
        _logger.LogDebug("Created channel {ChannelId} for connection {ConnectionId}", channel.Id, connection.Id);
        return Task.FromResult<IChannel>(channel);
    }
    
    public Task<IChannel?> GetChannelAsync(string channelId)
    {
        _channels.TryGetValue(channelId, out var channel);
        return Task.FromResult(channel);
    }
    
    public Task CloseChannelAsync(string channelId)
    {
        if (_channels.TryRemove(channelId, out var channel))
        {
            channel.Dispose();
            _logger.LogDebug("Closed channel {ChannelId}", channelId);
        }
        
        return Task.CompletedTask;
    }
    
    public Task<IChannel[]> GetAllChannelsAsync()
    {
        return Task.FromResult(_channels.Values.ToArray());
    }
}