using Microsoft.Extensions.Logging;
using MelonMQ.Broker.Core;
using System.Net;
using System.Net.Sockets;

namespace MelonMQ.Tests.Integration;

internal sealed class TestBrokerHost : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _serverTask;

    public string DataDirectory { get; }
    public int TcpPort { get; }
    public MelonMQConfiguration Configuration { get; }
    public QueueManager QueueManager { get; }
    public ConnectionManager ConnectionManager { get; }
    public TcpServer TcpServer { get; }

    public TestBrokerHost(string dataDirectory, Action<MelonMQConfiguration>? configure = null)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(DataDirectory);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        TcpPort = GetFreePort();

        Configuration = new MelonMQConfiguration
        {
            TcpBindAddress = "127.0.0.1",
            TcpPort = TcpPort,
            HttpPort = GetFreePort(),
            DataDirectory = DataDirectory,
            MaxMessageSize = 1024 * 1024,
            Security = new SecurityConfiguration
            {
                RequireAuth = false,
                RequireAdminApiKey = false,
                ProtectReadEndpoints = false,
                RequireHashedPasswords = false,
                AllowedOrigins = Array.Empty<string>(),
                Users = new Dictionary<string, string>()
            }
        };

        configure?.Invoke(Configuration);
        Configuration.ValidateConfiguration(isProduction: false);

        QueueManager = new QueueManager(
            Configuration.DataDirectory,
            _loggerFactory,
            Configuration.MaxConnections,
            Configuration.ChannelCapacity,
            Configuration.CompactionThresholdMB,
            Configuration.BatchFlushMs,
            Configuration.QueueGC.MaxQueues);

        ConnectionManager = new ConnectionManager(
            _loggerFactory.CreateLogger<ConnectionManager>(),
            Configuration);

        TcpServer = new TcpServer(
            QueueManager,
            ConnectionManager,
            Configuration,
            _loggerFactory.CreateLogger<TcpServer>(),
            new MelonMetrics(),
            exchangeManager: new MelonMQ.Broker.Core.ExchangeManager(
                _loggerFactory.CreateLogger<MelonMQ.Broker.Core.ExchangeManager>(),
                Configuration.DataDirectory));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _serverTask = Task.Run(() => TcpServer.StartAsync(_lifetimeCts.Token), _lifetimeCts.Token);
        await WaitForPortOpenAsync("127.0.0.1", TcpPort, TimeSpan.FromSeconds(5), cancellationToken);
    }

    public async Task StopAsync()
    {
        _lifetimeCts.Cancel();
        await TcpServer.StopAsync(TimeSpan.FromSeconds(2));

        if (_serverTask != null)
        {
            await Task.WhenAny(_serverTask, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        QueueManager.Dispose();
        _lifetimeCts.Dispose();
        _loggerFactory.Dispose();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(250));
                await client.ConnectAsync(host, port, cts.Token);
                return;
            }
            catch
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new TimeoutException($"Timed out waiting for broker on {host}:{port}");
    }
}