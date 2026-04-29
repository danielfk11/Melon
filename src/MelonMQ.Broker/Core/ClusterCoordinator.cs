using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace MelonMQ.Broker.Core;

public sealed record ClusterNodeInfo(string NodeId, string NodeAddress, long LastSeenUnixMs);

public sealed record ClusterPingRequest(string NodeId, string NodeAddress);

public sealed record ClusterPingResponse(string NodeId, string NodeAddress, List<ClusterNodeInfo> KnownNodes);

public sealed record ClusterJoinRequest(string NodeId, string NodeAddress);

public sealed record ClusterLeaveRequest(string NodeId);

public sealed record ClusterDeclareQueueReplicationRequest(
    string Queue,
    bool Durable,
    bool ExactlyOnce,
    string? DeadLetterQueue,
    int? DefaultTtlMs,
    string SourceNodeId,
    long ReplicatedAtUnixMs);

public sealed record ClusterDeclareExchangeReplicationRequest(
    string Exchange,
    string Type,
    bool Durable,
    string SourceNodeId,
    long ReplicatedAtUnixMs);

public sealed record ClusterBindQueueReplicationRequest(
    string Exchange,
    string Queue,
    string RoutingKey,
    string SourceNodeId,
    long ReplicatedAtUnixMs);

public sealed record ClusterUnbindQueueReplicationRequest(
    string Exchange,
    string Queue,
    string RoutingKey,
    string SourceNodeId,
    long ReplicatedAtUnixMs);

public sealed record ClusterPublishReplicationRequest(
    string Queue,
    Guid MessageId,
    string BodyBase64,
    long EnqueuedAt,
    long? ExpiresAt,
    bool Persistent,
    bool Redelivered,
    int DeliveryCount,
    string SourceNodeId,
    long ReplicatedAtUnixMs);

public sealed record ClusterAckReplicationRequest(
    string Queue,
    Guid MessageId,
    string SourceNodeId,
    long ReplicatedAtUnixMs);

public sealed record ClusterPurgeReplicationRequest(
    string Queue,
    string SourceNodeId,
    long ReplicatedAtUnixMs);

public sealed record ClusterDeleteQueueReplicationRequest(
    string Queue,
    string SourceNodeId,
    long ReplicatedAtUnixMs);

public sealed record ClusterStatus(
    bool Enabled,
    string NodeId,
    string NodeAddress,
    bool IsLeader,
    string? LeaderNodeId,
    bool HasWriteQuorum,
    string Consistency,
    int ActiveNodes,
    int ExpectedNodes,
    IReadOnlyCollection<ClusterNodeInfo> Nodes);

public class ClusterCoordinator
{
    public const string ClusterApiKeyHeaderName = "X-Cluster-Key";

    private sealed class ClusterNodeState
    {
        public required string NodeId { get; init; }
        public required string NodeAddress { get; set; }
        public long LastSeenUnixMs;
    }

    private readonly MelonMQConfiguration _config;
    private readonly ILogger<ClusterCoordinator> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MelonMetrics _metrics;
    private readonly ConcurrentDictionary<string, ClusterNodeState> _nodesById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _expectedNodeAddresses = new(StringComparer.OrdinalIgnoreCase);

    private string? _leaderNodeId;

    public ClusterCoordinator(
        MelonMQConfiguration config,
        ILogger<ClusterCoordinator> logger,
        IHttpClientFactory httpClientFactory,
        MelonMetrics metrics)
    {
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _metrics = metrics;

        if (!Enabled)
        {
            return;
        }

        RegisterOrUpdateNode(_config.Cluster.NodeId, _config.Cluster.NodeAddress);

        foreach (var seed in _config.Cluster.SeedNodes)
        {
            var normalized = NormalizeAddress(seed);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _expectedNodeAddresses[normalized] = 0;
            }
        }

        _expectedNodeAddresses[NormalizeAddress(_config.Cluster.NodeAddress)] = 0;
        RecomputeLeadership();
    }

    public bool Enabled => _config.Cluster.Enabled;

    public string NodeId => _config.Cluster.NodeId;

    public string NodeAddress => NormalizeAddress(_config.Cluster.NodeAddress);

    public string? LeaderNodeId => _leaderNodeId;

    public bool IsLeader =>
        Enabled &&
        string.Equals(_leaderNodeId, NodeId, StringComparison.Ordinal) &&
        (HasWriteQuorum || !_config.Cluster.RequireQuorumForWrites || IsSingleNodeCluster);

    public bool HasWriteQuorum
    {
        get
        {
            if (!Enabled || !_config.Cluster.RequireQuorumForWrites)
            {
                return true;
            }

            return ActiveNodeCount >= ComputeMajority(ExpectedClusterSize);
        }
    }

    public bool IsSingleNodeCluster => ExpectedClusterSize <= 1;

    public int ActiveNodeCount => GetActiveNodesSnapshot().Count;

    public int ExpectedClusterSize => Math.Max(1, _expectedNodeAddresses.Count);

    public bool CanAcceptWrites(out string? reason)
    {
        if (!Enabled)
        {
            reason = null;
            return true;
        }

        if (_config.Cluster.RequireQuorumForWrites && !HasWriteQuorum)
        {
            reason = $"Write quorum unavailable. Active nodes: {ActiveNodeCount}, expected: {ExpectedClusterSize}.";
            return false;
        }

        if (!IsLeader)
        {
            reason = $"Node '{NodeId}' is follower. Current leader: '{LeaderNodeId ?? "unknown"}'.";
            return false;
        }

        reason = null;
        return true;
    }

    public ClusterStatus GetStatus()
    {
        var nodes = _nodesById.Values
            .Select(s => new ClusterNodeInfo(s.NodeId, s.NodeAddress, s.LastSeenUnixMs))
            .OrderBy(s => s.NodeId, StringComparer.Ordinal)
            .ToList();

        return new ClusterStatus(
            Enabled,
            NodeId,
            NodeAddress,
            IsLeader,
            LeaderNodeId,
            HasWriteQuorum,
            _config.Cluster.Consistency,
            ActiveNodeCount,
            ExpectedClusterSize,
            nodes);
    }

    public IReadOnlyCollection<ClusterNodeInfo> GetKnownNodes()
    {
        return _nodesById.Values
            .Select(s => new ClusterNodeInfo(s.NodeId, s.NodeAddress, s.LastSeenUnixMs))
            .ToList();
    }

    public void RegisterOrUpdateNode(string nodeId, string nodeAddress)
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(nodeAddress))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var normalizedAddress = NormalizeAddress(nodeAddress);

        _nodesById.AddOrUpdate(
            nodeId,
            _ => new ClusterNodeState
            {
                NodeId = nodeId,
                NodeAddress = normalizedAddress,
                LastSeenUnixMs = now
            },
            (_, current) =>
            {
                current.NodeAddress = normalizedAddress;
                current.LastSeenUnixMs = now;
                return current;
            });

        _expectedNodeAddresses[normalizedAddress] = 0;
        RecomputeLeadership();
    }

    public void RemoveNode(string nodeId)
    {
        if (!Enabled || string.Equals(nodeId, NodeId, StringComparison.Ordinal))
        {
            return;
        }

        _nodesById.TryRemove(nodeId, out _);
        RecomputeLeadership();
    }

    public bool ValidateClusterRequest(HttpContext context)
    {
        if (!Enabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_config.Cluster.SharedKey))
        {
            return true;
        }

        var provided = context.Request.Headers[ClusterApiKeyHeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(_config.Cluster.SharedKey);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    public async Task BroadcastJoinAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        var request = new ClusterJoinRequest(NodeId, NodeAddress);
        await BroadcastMembershipEventAsync("/cluster/join", request, cancellationToken);
    }

    public async Task BroadcastLeaveAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        var request = new ClusterLeaveRequest(NodeId);
        await BroadcastMembershipEventAsync("/cluster/leave", request, cancellationToken);
    }

    public async Task ProbeClusterAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in _config.Cluster.SeedNodes)
        {
            targets.Add(NormalizeAddress(seed));
        }

        foreach (var node in _nodesById.Values)
        {
            targets.Add(node.NodeAddress);
        }

        targets.Remove(NodeAddress);

        foreach (var target in targets)
        {
            await PingNodeAsync(target, cancellationToken);
        }

        PruneStaleNodes();
        RecomputeLeadership();
    }

    public async Task<bool> ReplicateDeclareQueueAsync(
        string queue,
        bool durable,
        bool exactlyOnce,
        string? deadLetterQueue,
        int? defaultTtlMs,
        CancellationToken cancellationToken = default)
    {
        var payload = new ClusterDeclareQueueReplicationRequest(
            queue,
            durable,
            exactlyOnce,
            deadLetterQueue,
            defaultTtlMs,
            NodeId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return await ReplicateAsync("declare", "/cluster/replicate/declare", payload, cancellationToken);
    }

    public async Task<bool> ReplicateDeclareExchangeAsync(
        string exchange,
        string type,
        bool durable,
        CancellationToken cancellationToken = default)
    {
        var payload = new ClusterDeclareExchangeReplicationRequest(
            exchange,
            type,
            durable,
            NodeId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return await ReplicateAsync("declare_exchange", "/cluster/replicate/exchange/declare", payload, cancellationToken);
    }

    public async Task<bool> ReplicateBindQueueAsync(
        string exchange,
        string queue,
        string routingKey,
        CancellationToken cancellationToken = default)
    {
        var payload = new ClusterBindQueueReplicationRequest(
            exchange,
            queue,
            routingKey,
            NodeId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return await ReplicateAsync("bind_queue", "/cluster/replicate/exchange/bind", payload, cancellationToken);
    }

    public async Task<bool> ReplicateUnbindQueueAsync(
        string exchange,
        string queue,
        string routingKey,
        CancellationToken cancellationToken = default)
    {
        var payload = new ClusterUnbindQueueReplicationRequest(
            exchange,
            queue,
            routingKey,
            NodeId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return await ReplicateAsync("unbind_queue", "/cluster/replicate/exchange/unbind", payload, cancellationToken);
    }

    public async Task<bool> ReplicatePublishAsync(
        string queue,
        QueueMessage message,
        CancellationToken cancellationToken = default)
    {
        var payload = new ClusterPublishReplicationRequest(
            queue,
            message.MessageId,
            Convert.ToBase64String(message.Body.Span),
            message.EnqueuedAt,
            message.ExpiresAt,
            message.Persistent,
            message.Redelivered,
            message.DeliveryCount,
            NodeId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return await ReplicateAsync("publish", "/cluster/replicate/publish", payload, cancellationToken);
    }

    public async Task<bool> ReplicateAckAsync(string queue, Guid messageId, CancellationToken cancellationToken = default)
    {
        var payload = new ClusterAckReplicationRequest(
            queue,
            messageId,
            NodeId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return await ReplicateAsync("ack", "/cluster/replicate/ack", payload, cancellationToken);
    }

    public async Task<bool> ReplicatePurgeAsync(string queue, CancellationToken cancellationToken = default)
    {
        var payload = new ClusterPurgeReplicationRequest(
            queue,
            NodeId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return await ReplicateAsync("purge", "/cluster/replicate/purge", payload, cancellationToken);
    }

    public async Task<bool> ReplicateDeleteQueueAsync(string queue, CancellationToken cancellationToken = default)
    {
        var payload = new ClusterDeleteQueueReplicationRequest(
            queue,
            NodeId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return await ReplicateAsync("delete", "/cluster/replicate/delete", payload, cancellationToken);
    }

    private async Task<bool> ReplicateAsync<T>(
        string operation,
        string path,
        T payload,
        CancellationToken cancellationToken)
    {
        if (!Enabled || !_config.Cluster.EnableReplication)
        {
            return true;
        }

        var targets = GetActiveNodesSnapshot()
            .Where(n => !string.Equals(n.NodeId, NodeId, StringComparison.Ordinal))
            .ToList();

        if (targets.Count == 0)
        {
            return true;
        }

        var tasks = targets
            .Select(node => SendReplicationAsync(operation, node.NodeAddress, path, payload, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        if (!string.Equals(_config.Cluster.Consistency, "quorum", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var successCount = results.Count(r => r);
        var totalVotes = Math.Max(ExpectedClusterSize, targets.Count + 1);
        var required = ComputeMajority(totalVotes);

        return (1 + successCount) >= required;
    }

    private async Task<bool> SendReplicationAsync<T>(
        string operation,
        string targetAddress,
        string path,
        T payload,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = _httpClientFactory.CreateClient("cluster");
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(targetAddress, path));
            request.Content = JsonContent.Create(payload);

            if (!string.IsNullOrWhiteSpace(_config.Cluster.SharedKey))
            {
                request.Headers.TryAddWithoutValidation(ClusterApiKeyHeaderName, _config.Cluster.SharedKey);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            var success = response.IsSuccessStatusCode;
            sw.Stop();
            _metrics.RecordClusterReplication(operation, targetAddress, success, sw.Elapsed);

            if (!success)
            {
                _logger.LogWarning(
                    "Cluster replication '{Operation}' to {Target} failed with status {StatusCode}",
                    operation,
                    targetAddress,
                    (int)response.StatusCode);
            }

            return success;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _metrics.RecordClusterReplication(operation, targetAddress, success: false, sw.Elapsed);
            _logger.LogWarning(ex, "Cluster replication '{Operation}' to {Target} failed", operation, targetAddress);
            return false;
        }
    }

    private async Task BroadcastMembershipEventAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in _config.Cluster.SeedNodes)
        {
            targets.Add(NormalizeAddress(seed));
        }

        foreach (var node in _nodesById.Values)
        {
            targets.Add(node.NodeAddress);
        }

        targets.Remove(NodeAddress);

        foreach (var target in targets)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("cluster");
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(target, path));
                request.Content = JsonContent.Create(payload);
                if (!string.IsNullOrWhiteSpace(_config.Cluster.SharedKey))
                {
                    request.Headers.TryAddWithoutValidation(ClusterApiKeyHeaderName, _config.Cluster.SharedKey);
                }

                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Cluster membership event {Path} to {Target} returned status {StatusCode}",
                        path,
                        target,
                        (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send cluster membership event {Path} to {Target}", path, target);
            }
        }
    }

    private async Task PingNodeAsync(string targetAddress, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("cluster");
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(targetAddress, "/cluster/ping"));
            request.Content = JsonContent.Create(new ClusterPingRequest(NodeId, NodeAddress));
            if (!string.IsNullOrWhiteSpace(_config.Cluster.SharedKey))
            {
                request.Headers.TryAddWithoutValidation(ClusterApiKeyHeaderName, _config.Cluster.SharedKey);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var pingResponse = await response.Content.ReadFromJsonAsync<ClusterPingResponse>(cancellationToken: cancellationToken);
            if (pingResponse == null)
            {
                return;
            }

            RegisterOrUpdateNode(pingResponse.NodeId, pingResponse.NodeAddress);
            foreach (var node in pingResponse.KnownNodes)
            {
                RegisterOrUpdateNode(node.NodeId, node.NodeAddress);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cluster ping to {TargetAddress} failed", targetAddress);
        }
    }

    private void PruneStaleNodes()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleThresholdMs = Math.Max(2000, _config.Cluster.NodeTimeoutSeconds * 1000L);

        foreach (var node in _nodesById.Values)
        {
            if (string.Equals(node.NodeId, NodeId, StringComparison.Ordinal))
            {
                continue;
            }

            if (now - node.LastSeenUnixMs > staleThresholdMs)
            {
                _nodesById.TryRemove(node.NodeId, out _);
            }
        }
    }

    private IReadOnlyCollection<ClusterNodeInfo> GetActiveNodesSnapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var staleThresholdMs = Math.Max(2000, _config.Cluster.NodeTimeoutSeconds * 1000L);

        return _nodesById.Values
            .Where(n => now - n.LastSeenUnixMs <= staleThresholdMs)
            .Select(n => new ClusterNodeInfo(n.NodeId, n.NodeAddress, n.LastSeenUnixMs))
            .ToList();
    }

    private void RecomputeLeadership()
    {
        if (!Enabled)
        {
            _leaderNodeId = null;
            return;
        }

        var activeNodes = GetActiveNodesSnapshot()
            .OrderBy(n => n.NodeId, StringComparer.Ordinal)
            .ToList();

        _leaderNodeId = activeNodes.Count > 0 ? activeNodes[0].NodeId : NodeId;
        _metrics.RecordClusterLeadership(IsLeader, HasWriteQuorum, activeNodes.Count);
    }

    private static int ComputeMajority(int totalNodes)
    {
        return Math.Max(1, (totalNodes / 2) + 1);
    }

    private static string NormalizeAddress(string address)
    {
        return address.Trim().TrimEnd('/');
    }

    private static string BuildUrl(string baseAddress, string path)
    {
        return $"{NormalizeAddress(baseAddress)}{path}";
    }
}
