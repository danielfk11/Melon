using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using MelonMQ.Broker.Protocol;

namespace MelonMQ.Broker.Core;

public class ClientConnection : IDisposable
{
    public string Id { get; }
    public Socket Socket { get; }
    public PipeReader Reader { get; }
    public Stream Stream { get; }
    public bool IsAuthenticated { get; set; }
    public int Prefetch { get; set; } = 100;
    public ConcurrentDictionary<string, CancellationTokenSource> ActiveConsumers { get; } = new();
    public long LastHeartbeat { get; set; }
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
    /// <summary>
    /// Semaphore used to notify consumer loops when prefetch slots become available.
    /// Released on Ack/Nack, waited on in consumer loop instead of busy-wait polling.
    /// </summary>
    public SemaphoreSlim PrefetchSlotAvailable { get; } = new(0, int.MaxValue);
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public ClientConnection(string id, Socket socket, PipeReader reader, Stream stream)
    {
        Id = id;
        Socket = socket;
        Reader = reader;
        Stream = stream;
        LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        foreach (var consumer in ActiveConsumers.Values)
        {
            try { consumer.Cancel(); consumer.Dispose(); } catch { }
        }
        ActiveConsumers.Clear();

        try { Reader.Complete(); } catch { }
        try { Stream.Dispose(); } catch { }
        try { Socket.Dispose(); } catch { }
        WriteLock.Dispose();
        PrefetchSlotAvailable.Dispose();
    }
}

public class ConnectionManager : IConnectionManager
{
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly ILogger<ConnectionManager> _logger;
    private readonly int _connectionTimeoutMs;

    public ConnectionManager(ILogger<ConnectionManager> logger, MelonMQConfiguration config)
    {
        _logger = logger;
        _connectionTimeoutMs = config.ConnectionTimeout;
    }

    public void AddConnection(ClientConnection connection)
    {
        _connections[connection.Id] = connection;
        _logger.LogInformation("Added connection {ConnectionId}", connection.Id);
    }

    public void RemoveConnection(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            connection.Dispose();
            _logger.LogInformation("Removed connection {ConnectionId}", connectionId);
        }
    }

    public ClientConnection? GetConnection(string connectionId)
    {
        return _connections.TryGetValue(connectionId, out var connection) ? connection : null;
    }

    public IEnumerable<ClientConnection> GetAllConnections()
    {
        return _connections.Values;
    }

    public int ConnectionCount => _connections.Count;

    public Task CleanupStaleConnections()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleConnections = _connections.Values
            .Where(c => now - c.LastHeartbeat > _connectionTimeoutMs)
            .ToList();

        foreach (var connection in staleConnections)
        {
            _logger.LogWarning("Removing stale connection {ConnectionId}", connection.Id);
            RemoveConnection(connection.Id);
        }
        
        return Task.CompletedTask;
    }
}