using System.Net;
using MelonMQ.Broker.Core;
using MelonMQ.Broker.Network;
using MelonMQ.Broker.Persistence;
using MelonMQ.Common;
using Microsoft.Extensions.Options;
using Serilog;

namespace MelonMQ.Broker;

/// <summary>
/// Configuration classes for the broker
/// </summary>
public class BrokerConfiguration
{
    public ServerConfig Server { get; set; } = new();
    public PersistenceConfig Persistence { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public QueueDefaultsConfig QueueDefaults { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public ObservabilityConfig Observability { get; set; } = new();
}

public class ServerConfig
{
    public int TcpPort { get; set; } = 5672;
    public string TcpHost { get; set; } = "0.0.0.0";
    public int AdminPort { get; set; } = 8080;
    public string AdminHost { get; set; } = "127.0.0.1";
    public bool TlsEnabled { get; set; } = false;
    public string TlsCertificateFile { get; set; } = "";
    public string TlsCertificatePassword { get; set; } = "";
    public int MaxConnections { get; set; } = 1000;
    public int MaxChannelsPerConnection { get; set; } = 1000;
    public int ConnectionTimeoutMs { get; set; } = 30000;
    public int HeartbeatIntervalMs { get; set; } = 30000;
}

public class PersistenceConfig
{
    public string DataDirectory { get; set; } = "./data";
    public int SegmentSize { get; set; } = 128 * 1024 * 1024; // 128MB
    public string SyncPolicy { get; set; } = "Batch";
    public int FlushIntervalMs { get; set; } = 1000;
    public int RetentionDays { get; set; } = 7;
    public int MaxDiskUsageGB { get; set; } = 50;
    public int CompactionIntervalHours { get; set; } = 6;
    public int CompactionThresholdMB { get; set; } = 1000;
    
    public SyncPolicy GetSyncPolicy() => Enum.Parse<SyncPolicy>(SyncPolicy, true);
}

public class SecurityConfig
{
    public string DefaultUsername { get; set; } = "admin";
    public string DefaultPassword { get; set; } = "admin123";
    public bool AuthRequired { get; set; } = true;
    public string AuthMethod { get; set; } = "ScramSha256";
    public int ScramIterations { get; set; } = 4096;
    public int ConnectionRateLimit { get; set; } = 100;
    public int ConnectionRateWindowMs { get; set; } = 60000;
    public int MaxFrameSize { get; set; } = 32 * 1024 * 1024; // 32MB
    public int MaxMessageSize { get; set; } = 16 * 1024 * 1024; // 16MB
}

public class QueueDefaultsConfig
{
    public int MessageTtlMs { get; set; } = 3600000; // 1 hour
    public int VisibilityTimeoutMs { get; set; } = 300000; // 5 minutes
    public int MaxDeliveryCount { get; set; } = 5;
    public int MaxLength { get; set; } = 100000;
    public int MaxPriority { get; set; } = 9;
}

public class PerformanceConfig
{
    public int IoThreads { get; set; } = 0; // 0 = auto
    public int WorkerThreads { get; set; } = 0; // 0 = auto
    public bool GcServerMode { get; set; } = true;
    public bool GcConcurrentMode { get; set; } = true;
    public int ReceiveBufferSize { get; set; } = 65536;
    public int SendBufferSize { get; set; } = 65536;
    public int PipeBufferSize { get; set; } = 65536;
}

public class ObservabilityConfig
{
    public bool MetricsEnabled { get; set; } = true;
    public int MetricsPort { get; set; } = 9090;
    public bool TracingEnabled { get; set; } = false;
    public string TracingEndpoint { get; set; } = "";
    public bool PrometheusEnabled { get; set; } = true;
    public string PrometheusPath { get; set; } = "/metrics";
}

/// <summary>
/// Main program entry point
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            // Configure Serilog early
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateBootstrapLogger();

            Log.Information("Starting MelonMQ Broker");

            var builder = WebApplication.CreateBuilder(args);
            
            // Configure Serilog from configuration
            builder.Host.UseSerilog((context, loggerConfig) =>
            {
                loggerConfig
                    .ReadFrom.Configuration(context.Configuration)
                    .WriteTo.Console()
                    .WriteTo.File(
                        path: "./logs/melonmq-.log",
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        fileSizeLimitBytes: 100 * 1024 * 1024);
            });

            // Configure services
            ConfigureServices(builder);

            var app = builder.Build();
            
            // Configure pipeline
            ConfigurePipeline(app);

            // Start the application
            await app.RunAsync();
            
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "MelonMQ Broker failed to start");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        // Bind configuration
        services.Configure<BrokerConfiguration>(configuration);
        
        // Core services
        services.AddSingleton<IQueueManager, QueueManager>();
        services.AddSingleton<IExchangeManager, ExchangeManager>();
        services.AddSingleton<IChannelManager, ChannelManager>();
        
        // Persistence
        services.AddSingleton<IWriteAheadLog>(provider =>
        {
            var config = provider.GetRequiredService<IOptions<BrokerConfiguration>>().Value;
            var logger = provider.GetRequiredService<ILogger<SegmentedWal>>();
            
            return new SegmentedWal(
                config.Persistence.DataDirectory,
                config.Persistence.SegmentSize,
                config.Persistence.GetSyncPolicy(),
                logger);
        });
        
        // Network services
        services.AddSingleton<IConnectionHandler, ProtocolHandler>();
        services.AddHostedService<TcpServer>(provider =>
        {
            var config = provider.GetRequiredService<IOptions<BrokerConfiguration>>().Value;
            var connectionHandler = provider.GetRequiredService<IConnectionHandler>();
            var logger = provider.GetRequiredService<ILogger<TcpServer>>();
            
            var endpoint = new IPEndPoint(IPAddress.Parse(config.Server.TcpHost), config.Server.TcpPort);
            return new TcpServer(endpoint, connectionHandler, logger);
        });

        // Admin API
        services.AddControllers();
        services.AddOpenApi();
        
        // Observability
        if (builder.Configuration.GetValue<bool>("Observability:MetricsEnabled"))
        {
            services.AddOpenTelemetry()
                .WithMetrics(builder =>
                {
                    builder
                        .AddAspNetCoreInstrumentation()
                        .AddPrometheusExporter();
                });
        }
        
        // Performance optimization
        ConfigurePerformance(builder.Configuration);
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        // Development tools
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        // Metrics endpoint
        if (app.Configuration.GetValue<bool>("Observability:PrometheusEnabled"))
        {
            app.MapPrometheusScrapingEndpoint();
        }

        // Admin API
        app.MapControllers();
        
        // Health check
        app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }));
    }

    private static void ConfigurePerformance(IConfiguration configuration)
    {
        var perfConfig = configuration.GetSection("Performance").Get<PerformanceConfig>() ?? new PerformanceConfig();
        
        // Configure GC
        if (perfConfig.GcServerMode)
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Batch;
        }
        
        // Configure thread pool
        if (perfConfig.WorkerThreads > 0 || perfConfig.IoThreads > 0)
        {
            ThreadPool.GetMinThreads(out var minWorker, out var minIo);
            ThreadPool.SetMinThreads(
                perfConfig.WorkerThreads > 0 ? perfConfig.WorkerThreads : minWorker,
                perfConfig.IoThreads > 0 ? perfConfig.IoThreads : minIo);
        }
    }
}

/// <summary>
/// Admin API controllers
/// </summary>
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly IQueueManager _queueManager;
    private readonly IExchangeManager _exchangeManager;
    private readonly IChannelManager _channelManager;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        IQueueManager queueManager,
        IExchangeManager exchangeManager,
        IChannelManager channelManager,
        ILogger<AdminController> logger)
    {
        _queueManager = queueManager;
        _exchangeManager = exchangeManager;
        _channelManager = channelManager;
        _logger = logger;
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var queues = await _queueManager.GetAllQueuesAsync();
        var exchanges = await _exchangeManager.GetAllExchangesAsync();
        var channels = await _channelManager.GetAllChannelsAsync();

        var stats = new BrokerStats
        {
            TotalQueues = queues.Length,
            TotalExchanges = exchanges.Length,
            ActiveChannels = channels.Length,
            ActiveConnections = channels.Select(c => c.Connection.Id).Distinct().Count(),
            Uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime,
            MemoryUsed = GC.GetTotalMemory(false)
        };

        foreach (var queue in queues)
        {
            var queueStats = await queue.GetStatsAsync();
            stats.QueueStats[queue.Name] = queueStats;
            stats.MessageCount += queueStats.MessageCount;
        }

        return Ok(stats);
    }

    [HttpPost("queues")]
    public async Task<IActionResult> CreateQueue([FromBody] QueueConfig config)
    {
        try
        {
            await _queueManager.CreateQueueAsync(config);
            return Ok(new { Message = $"Queue '{config.Name}' created successfully" });
        }
        catch (ResourceExistsException)
        {
            return Conflict(new { Error = $"Queue '{config.Name}' already exists" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating queue {QueueName}", config.Name);
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }

    [HttpDelete("queues/{queueName}")]
    public async Task<IActionResult> DeleteQueue(string queueName)
    {
        try
        {
            await _queueManager.DeleteQueueAsync(queueName);
            return Ok(new { Message = $"Queue '{queueName}' deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting queue {QueueName}", queueName);
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }

    [HttpPost("queues/{queueName}/purge")]
    public async Task<IActionResult> PurgeQueue(string queueName)
    {
        try
        {
            var queue = await _queueManager.GetQueueAsync(queueName);
            if (queue == null)
            {
                return NotFound(new { Error = $"Queue '{queueName}' not found" });
            }

            await queue.PurgeAsync();
            return Ok(new { Message = $"Queue '{queueName}' purged successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error purging queue {QueueName}", queueName);
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }

    [HttpGet("queues")]
    public async Task<IActionResult> GetQueues()
    {
        try
        {
            var queues = await _queueManager.GetAllQueuesAsync();
            var queueInfos = new List<object>();

            foreach (var queue in queues)
            {
                var stats = await queue.GetStatsAsync();
                queueInfos.Add(new
                {
                    Name = queue.Name,
                    Config = queue.Config,
                    Stats = stats
                });
            }

            return Ok(queueInfos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting queues");
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }

    [HttpGet("exchanges")]
    public async Task<IActionResult> GetExchanges()
    {
        try
        {
            var exchanges = await _exchangeManager.GetAllExchangesAsync();
            var exchangeInfos = exchanges.Select(e => new
            {
                Name = e.Name,
                Type = e.Type,
                Config = e.Config
            });

            return Ok(exchangeInfos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting exchanges");
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }

    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections()
    {
        try
        {
            var channels = await _channelManager.GetAllChannelsAsync();
            var connections = channels.GroupBy(c => c.Connection.Id)
                .Select(g => new
                {
                    ConnectionId = g.Key,
                    RemoteEndpoint = g.First().Connection.RemoteEndPoint?.ToString(),
                    ChannelCount = g.Count(),
                    Channels = g.Select(c => new
                    {
                        ChannelId = c.Id,
                        IsAuthenticated = c.IsAuthenticated,
                        Username = c.Username,
                        VHost = c.VHost
                    })
                });

            return Ok(connections);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connections");
            return StatusCode(500, new { Error = "Internal server error" });
        }
    }
}