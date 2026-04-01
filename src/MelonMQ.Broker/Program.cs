using MelonMQ.Broker.Core;
using MelonMQ.Broker.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Bind and validate configuration
var melonConfig = new MelonMQConfiguration();
builder.Configuration.GetSection("MelonMQ").Bind(melonConfig);
melonConfig.ValidateConfiguration(builder.Environment.IsProduction());
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
        else if (builder.Environment.IsDevelopment())
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
    return new QueueManager(config.DataDirectory, loggerFactory, config.MaxConnections, config.ChannelCapacity, config.CompactionThresholdMB, config.BatchFlushMs, config.QueueGC.MaxQueues);
});

builder.Services.AddSingleton<MelonMetrics>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<TcpServer>(provider =>
{
    var config = provider.GetRequiredService<MelonMQConfiguration>();
    var queueManager = provider.GetRequiredService<QueueManager>();
    var connectionManager = provider.GetRequiredService<ConnectionManager>();
    var logger = provider.GetRequiredService<ILogger<TcpServer>>();
    var metrics = provider.GetRequiredService<MelonMetrics>();
    return new TcpServer(queueManager, connectionManager, config, logger, metrics);
});

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<MelonMQService>();
    builder.Services.AddHostedService<QueueGarbageCollector>();
}

var app = builder.Build();

// Configure middleware
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

// Configure HTTP endpoints
app.Urls.Add($"http://localhost:{melonConfig.HttpPort}");

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/admin", () => Results.Redirect("/index.html"));
app.MapGet("/management", () => Results.Redirect("/index.html"));

app.MapGet("/health", (TcpServer tcpServer, ConnectionManager connectionManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context, readOnlyEndpoint: true))
        return Results.Unauthorized();

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

app.MapGet("/queues", (QueueManager queueManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context, readOnlyEndpoint: true))
        return Results.Unauthorized();

    var queues = queueManager.GetAllQueues().Select(q => new
    {
        name = q.Name,
        durable = q.IsDurable,
        pendingMessages = q.PendingCount,
        inFlightMessages = q.InFlightCount,
        idleTimeMs = q.IdleTimeMs
    });

    return Results.Ok(new { queues = queues });
});

app.MapGet("/stats", (HttpContext context, QueueManager queueManager, ConnectionManager connectionManager, MelonMetrics metrics) =>
{
    if (!ValidateAdminApiKey(context, readOnlyEndpoint: true))
        return Results.Unauthorized();

    var queues = queueManager.GetAllQueues().Select(q => new
    {
        name = q.Name,
        durable = q.IsDurable,
        pendingMessages = q.PendingCount,
        inFlightMessages = q.InFlightCount,
        idleTimeMs = q.IdleTimeMs
    });

    var allMetrics = metrics.GetAllMetrics();

    return Results.Ok(new
    {
        queues = queues,
        connections = connectionManager.ConnectionCount,
        totalQueues = queueManager.QueueCount,
        metrics = allMetrics,
        uptime = DateTime.UtcNow.Subtract(Process.GetCurrentProcess().StartTime.ToUniversalTime()),
        timestamp = DateTimeOffset.UtcNow
    });
});

app.MapPost("/queues/declare", (QueueDeclareRequest request, QueueManager queueManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    try
    {
        queueManager.DeclareQueue(
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

app.MapPost("/queues/{queueName}/purge", async (string queueName, QueueManager queueManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

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

app.MapPost("/queues/{queueName}/publish", async (string queueName, PublishRequest request, QueueManager queueManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    var queue = queueManager.GetQueue(queueName);
    if (queue == null)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }

    try
    {
        var body = System.Text.Encoding.UTF8.GetBytes(request.Message);
        if (body.Length > melonConfig.MaxMessageSize)
        {
            return Results.BadRequest(new
            {
                error = $"Message size ({body.Length} bytes) exceeds maximum allowed ({melonConfig.MaxMessageSize} bytes)"
            });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var effectiveTtlMs = request.TtlMs ?? queue.DefaultTtlMs;

        var message = new QueueMessage
        {
            MessageId = request.MessageId ?? Guid.NewGuid(),
            Body = new ReadOnlyMemory<byte>(body),
            EnqueuedAt = now,
            Persistent = request.Persistent,
            ExpiresAt = effectiveTtlMs.HasValue
                ? now + effectiveTtlMs.Value
                : null
        };

        var enqueued = await queue.EnqueueAsync(message);
        return Results.Ok(new { success = enqueued, messageId = message.MessageId });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/queues/{queueName}/consume", async (string queueName, QueueManager queueManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context, readOnlyEndpoint: true))
        return Results.Unauthorized();

    var queue = queueManager.GetQueue(queueName);
    if (queue == null)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }

    try
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5 second timeout
        var result = await queue.DequeueAsync("http-client", cts.Token);
        
        if (result == null)
        {
            return Results.Ok(new { message = (string?)null });
        }

        var (message, deliveryTag) = result.Value;
        
        // Auto-ack for HTTP consumers (stateless, no way to ack later)
        await queue.AckAsync(deliveryTag);

        var bodyString = System.Text.Encoding.UTF8.GetString(message.Body.Span);
        return Results.Ok(new 
        { 
            messageId = message.MessageId,
            message = bodyString,
            redelivered = message.Redelivered
        });
    }
    catch (OperationCanceledException)
    {
        return Results.Ok(new { message = (string?)null }); // No messages available
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("MelonMQ Broker starting...");

// Helper: validate admin API key for HTTP write/admin endpoints
bool ValidateAdminApiKey(HttpContext context, bool readOnlyEndpoint = false)
{
    var runtimeConfig = context.RequestServices.GetRequiredService<MelonMQConfiguration>();
    var security = runtimeConfig.Security;

    if (readOnlyEndpoint && !security.ProtectReadEndpoints)
    {
        return true;
    }

    if (!security.RequireAdminApiKey)
    {
        return true;
    }

    if (!security.HasAdminApiKey)
    {
        return false;
    }

    var apiKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
    if (string.IsNullOrEmpty(apiKey))
    {
        return false;
    }

    var providedBytes = Encoding.UTF8.GetBytes(apiKey);
    var expectedBytes = Encoding.UTF8.GetBytes(security.AdminApiKey);
    return providedBytes.Length == expectedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
}

// Queue deletion endpoint
app.MapDelete("/queues/{queueName}", (string queueName, QueueManager queueManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    var deleted = queueManager.DeleteQueue(queueName);
    if (!deleted)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }
    return Results.Ok(new { success = true, queue = queueName, message = $"Queue '{queueName}' deleted" });
});

// List inactive queues eligible for cleanup
app.MapGet("/queues/inactive", (QueueManager queueManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context, readOnlyEndpoint: true))
        return Results.Unauthorized();

    var threshold = melonConfig.QueueGC.InactiveThresholdSeconds;
    var inactiveQueues = queueManager.GetInactiveQueues(threshold)
        .Select(q => new
        {
            name = q.Name,
            durable = q.Durable,
            idleTimeSeconds = q.IdleTimeMs / 1000.0
        });

    return Results.Ok(new
    {
        thresholdSeconds = threshold,
        count = inactiveQueues.Count(),
        queues = inactiveQueues
    });
});

// Force GC run manually
app.MapPost("/queues/gc", (QueueManager queueManager, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    var removed = queueManager.CleanupInactiveQueues(
        melonConfig.QueueGC.InactiveThresholdSeconds,
        melonConfig.QueueGC.OnlyNonDurable);

    return Results.Ok(new
    {
        success = true,
        removedQueues = removed,
        remainingQueues = queueManager.QueueCount
    });
});

// GC configuration status
app.MapGet("/queues/gc/status", (QueueManager queueManager, MelonMetrics metrics, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context, readOnlyEndpoint: true))
        return Results.Unauthorized();

    return Results.Ok(new
    {
        enabled = melonConfig.QueueGC.Enabled,
        intervalSeconds = melonConfig.QueueGC.IntervalSeconds,
        inactiveThresholdSeconds = melonConfig.QueueGC.InactiveThresholdSeconds,
        onlyNonDurable = melonConfig.QueueGC.OnlyNonDurable,
        maxQueues = melonConfig.QueueGC.MaxQueues == 0 ? "unlimited" : (object)melonConfig.QueueGC.MaxQueues,
        totalQueues = queueManager.QueueCount,
        gcRuns = metrics.GetCounter("gc.runs"),
        totalQueuesRemoved = metrics.GetCounter("gc.queues_removed")
    });
});

app.Run();

public record QueueDeclareRequest(
    string Name, 
    bool Durable = false, 
    string? DeadLetterQueue = null, 
    int? DefaultTtlMs = null);

public record PublishRequest(
    string Message,
    bool Persistent = false,
    int? TtlMs = null,
    Guid? MessageId = null);

public partial class Program { }