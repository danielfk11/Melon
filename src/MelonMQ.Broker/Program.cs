using MelonMQ.Broker.Core;
using MelonMQ.Broker.Http;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Bind and validate configuration
var melonConfig = new MelonMQConfiguration();
builder.Configuration.GetSection("MelonMQ").Bind(melonConfig);
melonConfig.ValidateConfiguration();
builder.Services.AddSingleton(melonConfig);

// Configure services
builder.Services.AddLogging();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (melonConfig.Security.AllowedOrigins.Length > 0)
        {
            policy.WithOrigins(melonConfig.Security.AllowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

builder.Services.AddSingleton<QueueManager>(provider =>
{
    var config = provider.GetRequiredService<MelonMQConfiguration>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    return new QueueManager(config.DataDirectory, loggerFactory, config.MaxConnections, config.ChannelCapacity, config.CompactionThresholdMB);
});

builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<TcpServer>(provider =>
{
    var config = provider.GetRequiredService<MelonMQConfiguration>();
    var queueManager = provider.GetRequiredService<QueueManager>();
    var connectionManager = provider.GetRequiredService<ConnectionManager>();
    var logger = provider.GetRequiredService<ILogger<TcpServer>>();
    return new TcpServer(queueManager, connectionManager, config, logger);
});

builder.Services.AddHostedService<MelonMQService>();

var app = builder.Build();

// Configure middleware
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// Configure HTTP endpoints
app.Urls.Add($"http://localhost:{melonConfig.HttpPort}");

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/admin", () => Results.Redirect("/index.html"));
app.MapGet("/management", () => Results.Redirect("/index.html"));

app.MapGet("/health", (TcpServer tcpServer, ConnectionManager connectionManager) =>
{
    var isListening = tcpServer.IsListening;
    var status = isListening ? "healthy" : "degraded";
    
    return Results.Ok(new
    {
        status,
        tcpServer = isListening ? "listening" : "not listening",
        connections = connectionManager.ConnectionCount,
        timestamp = DateTimeOffset.UtcNow
    });
});

app.MapGet("/stats", (QueueManager queueManager, ConnectionManager connectionManager) =>
{
    var queues = queueManager.GetAllQueues().Select(q => new
    {
        name = q.Name,
        durable = q.IsDurable,
        pendingMessages = q.PendingCount,
        inFlightMessages = q.InFlightCount
    });

    var metrics = MelonMQ.Broker.Core.MelonMetrics.Instance.GetAllMetrics();

    return new
    {
        queues = queues,
        connections = connectionManager.ConnectionCount,
        metrics = metrics,
        uptime = DateTime.UtcNow.Subtract(Process.GetCurrentProcess().StartTime.ToUniversalTime()),
        timestamp = DateTimeOffset.UtcNow
    };
});

app.MapPost("/queues/declare", (QueueDeclareRequest request, QueueManager queueManager) =>
{
    try
    {
        var queue = queueManager.DeclareQueue(
            request.Name, 
            request.Durable, 
            request.DeadLetterQueue, 
            request.DefaultTtlMs);

        return Results.Ok(new { success = true, queue = request.Name });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/queues/{queueName}/purge", async (string queueName, QueueManager queueManager) =>
{
    var queue = queueManager.GetQueue(queueName);
    if (queue == null)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }

    try
    {
        await queue.PurgeAsync();
        return Results.Ok(new { success = true, queue = queueName });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("MelonMQ Broker starting...");

app.Run();

public record QueueDeclareRequest(
    string Name, 
    bool Durable = false, 
    string? DeadLetterQueue = null, 
    int? DefaultTtlMs = null);