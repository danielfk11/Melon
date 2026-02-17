using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using MelonMQ.Broker.Protocol;

namespace MelonMQ.Broker.Core;

public class ClientConnection
{
    public string Id { get; }
    public Socket Socket { get; }
    public PipeReader Reader { get; }
    public Stream Stream { get; } // Changed from PipeWriter to Stream
    public bool IsAuthenticated { get; set; }
    public int Prefetch { get; set; } = 100;
    public ConcurrentDictionary<string, CancellationTokenSource> ActiveConsumers { get; } = new();
    public long LastHeartbeat { get; set; }

    public ClientConnection(string id, Socket socket, PipeReader reader, Stream stream)
    {
        Id = id;
        Socket = socket;
        Reader = reader;
        Stream = stream; // Store stream instead of PipeWriter
        LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}

public class ConnectionManager : IConnectionManager
{
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly ILogger<ConnectionManager> _logger;

    public ConnectionManager(ILogger<ConnectionManager> logger)
    {
        _logger = logger;
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
            // Cancel all active consumers
            foreach (var consumer in connection.ActiveConsumers.Values)
            {
                consumer.Cancel();
            }

            try
            {
                connection.Socket.Close();
            }
            catch { }

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
            .Where(c => now - c.LastHeartbeat > 30000) // 30 seconds timeout
            .ToList();

        foreach (var connection in staleConnections)
        {
            _logger.LogWarning("Removing stale connection {ConnectionId}", connection.Id);
            RemoveConnection(connection.Id);
        }
        
        return Task.CompletedTask;
    }
}