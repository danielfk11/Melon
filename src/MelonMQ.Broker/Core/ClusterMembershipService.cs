namespace MelonMQ.Broker.Core;

public class ClusterMembershipService : BackgroundService
{
    private readonly ClusterCoordinator _clusterCoordinator;
    private readonly MelonMQConfiguration _configuration;
    private readonly ILogger<ClusterMembershipService> _logger;

    public ClusterMembershipService(
        ClusterCoordinator clusterCoordinator,
        MelonMQConfiguration configuration,
        ILogger<ClusterMembershipService> logger)
    {
        _clusterCoordinator = clusterCoordinator;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_clusterCoordinator.Enabled)
        {
            return;
        }

        _logger.LogInformation(
            "Cluster membership service started for node {NodeId} at {NodeAddress}",
            _clusterCoordinator.NodeId,
            _clusterCoordinator.NodeAddress);

        try
        {
            await _clusterCoordinator.BroadcastJoinAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await _clusterCoordinator.ProbeClusterAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_configuration.Cluster.DiscoveryIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cluster membership loop crashed");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_clusterCoordinator.Enabled)
        {
            await _clusterCoordinator.BroadcastLeaveAsync(cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }
}
