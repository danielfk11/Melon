using MelonMQ.Broker.Core;

namespace MelonMQ.Broker.Http;

public class MelonMQService : BackgroundService
{
    private readonly TcpServer _tcpServer;
    private readonly ILogger<MelonMQService> _logger;

    public MelonMQService(TcpServer tcpServer, ILogger<MelonMQService> logger)
    {
        _tcpServer = tcpServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting MelonMQ TCP server...");
            await _tcpServer.StartAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MelonMQ service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in MelonMQ service");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping MelonMQ TCP server...");
        await _tcpServer.StopAsync(TimeSpan.FromSeconds(10));
        await base.StopAsync(cancellationToken);
    }
}