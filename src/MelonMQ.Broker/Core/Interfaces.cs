namespace MelonMQ.Broker.Core;

public interface IQueueManager
{
    MessageQueue DeclareQueue(string name, bool durable = false, string? deadLetterQueue = null, int? defaultTtlMs = null, bool exactlyOnce = false);
    MessageQueue? GetQueue(string name);
    IEnumerable<MessageQueue> GetAllQueues();
    bool DeleteQueue(string name);
    Task CleanupExpiredMessages();
}

public interface IConnectionManager
{
    void AddConnection(ClientConnection connection);
    void RemoveConnection(string connectionId);
    ClientConnection? GetConnection(string connectionId);
    IEnumerable<ClientConnection> GetAllConnections();
    int ConnectionCount { get; }
    Task CleanupStaleConnections();
}
