using MelonMQ.Broker.Core;
using MelonMQ.Broker.Http;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Bind and validate configuration
var melonConfig = new MelonMQConfiguration();
builder.Configuration.GetSection("MelonMQ").Bind(melonConfig);
var strictSecurityEnvironment = builder.Environment.IsProduction() || builder.Environment.IsStaging();
melonConfig.ValidateConfiguration(strictSecurityEnvironment);
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

builder.Services.AddHttpClient("cluster", client =>
{
    client.Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, melonConfig.Cluster.NodeTimeoutSeconds * 1000));
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource =>
    {
        resource.AddService(
            serviceName: melonConfig.Observability.ServiceName,
            serviceVersion: melonConfig.Observability.ServiceVersion);
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(MelonMetrics.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();

        if (melonConfig.Observability.Prometheus.Enabled)
        {
            metrics.AddPrometheusExporter();
        }

        if (melonConfig.Observability.Otlp.Enabled && melonConfig.Observability.Otlp.EnableMetrics)
        {
            metrics.AddOtlpExporter((options, metricReaderOptions) =>
            {
                var protocol = ParseOtlpProtocol(melonConfig.Observability.Otlp.Protocol);
                options.Endpoint = BuildOtlpSignalEndpoint(
                    melonConfig.Observability.Otlp.Endpoint,
                    protocol,
                    "/v1/metrics");
                options.Protocol = protocol;
                options.Headers = melonConfig.Observability.Otlp.Headers;
                options.TimeoutMilliseconds = melonConfig.Observability.Otlp.TimeoutMs;

                metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                    melonConfig.Observability.Otlp.MetricsExportIntervalMs;
                metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds =
                    melonConfig.Observability.Otlp.TimeoutMs;
            });
        }
    })
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(MelonMetrics.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (melonConfig.Observability.Otlp.Enabled && melonConfig.Observability.Otlp.EnableTraces)
        {
            tracing.AddOtlpExporter(options =>
            {
                var protocol = ParseOtlpProtocol(melonConfig.Observability.Otlp.Protocol);
                options.Endpoint = BuildOtlpSignalEndpoint(
                    melonConfig.Observability.Otlp.Endpoint,
                    protocol,
                    "/v1/traces");
                options.Protocol = protocol;
                options.Headers = melonConfig.Observability.Otlp.Headers;
                options.TimeoutMilliseconds = melonConfig.Observability.Otlp.TimeoutMs;
            });
        }
    });

builder.Services.AddSingleton<MelonMetrics>();
builder.Services.AddSingleton<ClusterCoordinator>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<ExchangeManager>();
builder.Services.AddSingleton<TcpServer>(provider =>
{
    var config = provider.GetRequiredService<MelonMQConfiguration>();
    var queueManager = provider.GetRequiredService<QueueManager>();
    var connectionManager = provider.GetRequiredService<ConnectionManager>();
    var logger = provider.GetRequiredService<ILogger<TcpServer>>();
    var metrics = provider.GetRequiredService<MelonMetrics>();
    var clusterCoordinator = provider.GetRequiredService<ClusterCoordinator>();
    var exchangeManager = provider.GetRequiredService<ExchangeManager>();
    return new TcpServer(queueManager, connectionManager, config, logger, metrics, clusterCoordinator, exchangeManager);
});

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<MelonMQService>();
    builder.Services.AddHostedService<QueueGarbageCollector>();
    builder.Services.AddHostedService<ClusterMembershipService>();
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

    if (melonConfig.Observability.Prometheus.Enabled &&
        context.Request.Path.Equals(melonConfig.Observability.Prometheus.EndpointPath, StringComparison.OrdinalIgnoreCase) &&
        melonConfig.Observability.Prometheus.RequireAdminApiKey &&
        !ValidateAdminApiKey(context, readOnlyEndpoint: true))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

// Configure HTTP endpoints
app.Urls.Add($"http://localhost:{melonConfig.HttpPort}");

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/admin", () => Results.Redirect("/index.html"));
app.MapGet("/management", () => Results.Redirect("/index.html"));

if (melonConfig.Observability.Prometheus.Enabled)
{
    app.MapPrometheusScrapingEndpoint(melonConfig.Observability.Prometheus.EndpointPath);
}

app.MapGet("/cluster/status", (ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context, readOnlyEndpoint: true))
        return Results.Unauthorized();

    return Results.Ok(clusterCoordinator.GetStatus());
});

app.MapPost("/cluster/ping", (ClusterPingRequest request, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!clusterCoordinator.ValidateClusterRequest(context))
        return Results.Unauthorized();

    clusterCoordinator.RegisterOrUpdateNode(request.NodeId, request.NodeAddress);
    var response = new ClusterPingResponse(
        clusterCoordinator.NodeId,
        clusterCoordinator.NodeAddress,
        clusterCoordinator.GetKnownNodes().ToList());
    return Results.Ok(response);
});

app.MapPost("/cluster/join", (ClusterJoinRequest request, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!clusterCoordinator.ValidateClusterRequest(context))
        return Results.Unauthorized();

    clusterCoordinator.RegisterOrUpdateNode(request.NodeId, request.NodeAddress);
    return Results.Ok(new { success = true });
});

app.MapPost("/cluster/leave", (ClusterLeaveRequest request, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!clusterCoordinator.ValidateClusterRequest(context))
        return Results.Unauthorized();

    clusterCoordinator.RemoveNode(request.NodeId);
    return Results.Ok(new { success = true });
});

app.MapPost("/cluster/replicate/declare", (ClusterDeclareQueueReplicationRequest request, QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!clusterCoordinator.ValidateClusterRequest(context))
        return Results.Unauthorized();

    queueManager.DeclareQueue(request.Queue, request.Durable, request.DeadLetterQueue, request.DefaultTtlMs);
    return Results.Ok(new { success = true });
});

app.MapPost("/cluster/replicate/publish", async (ClusterPublishReplicationRequest request, QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!clusterCoordinator.ValidateClusterRequest(context))
        return Results.Unauthorized();

    var queue = queueManager.GetQueue(request.Queue) ?? queueManager.DeclareQueue(request.Queue, durable: false);

    var message = new QueueMessage
    {
        MessageId = request.MessageId,
        Body = Convert.FromBase64String(request.BodyBase64),
        EnqueuedAt = request.EnqueuedAt,
        ExpiresAt = request.ExpiresAt,
        Persistent = request.Persistent,
        Redelivered = request.Redelivered,
        DeliveryCount = request.DeliveryCount
    };

    await queue.EnqueueAsync(message);
    return Results.Ok(new { success = true });
});

app.MapPost("/cluster/replicate/ack", async (ClusterAckReplicationRequest request, QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!clusterCoordinator.ValidateClusterRequest(context))
        return Results.Unauthorized();

    var queue = queueManager.GetQueue(request.Queue);
    if (queue == null)
        return Results.NotFound(new { error = $"Queue '{request.Queue}' not found" });

    await queue.AckByMessageIdAsync(request.MessageId);
    return Results.Ok(new { success = true });
});

app.MapPost("/cluster/replicate/purge", async (ClusterPurgeReplicationRequest request, QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!clusterCoordinator.ValidateClusterRequest(context))
        return Results.Unauthorized();

    var queue = queueManager.GetQueue(request.Queue);
    if (queue == null)
        return Results.NotFound(new { error = $"Queue '{request.Queue}' not found" });

    await queue.PurgeAsync();
    return Results.Ok(new { success = true });
});

app.MapPost("/cluster/replicate/delete", (ClusterDeleteQueueReplicationRequest request, QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!clusterCoordinator.ValidateClusterRequest(context))
        return Results.Unauthorized();

    var deleted = queueManager.DeleteQueue(request.Queue);
    return Results.Ok(new { success = deleted });
});

app.MapGet("/health", (TcpServer tcpServer, ConnectionManager connectionManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
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
        cluster = clusterCoordinator.GetStatus(),
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

app.MapGet("/stats", (HttpContext context, QueueManager queueManager, ConnectionManager connectionManager, MelonMetrics metrics, ClusterCoordinator clusterCoordinator) =>
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
        cluster = clusterCoordinator.GetStatus(),
        uptime = DateTime.UtcNow.Subtract(Process.GetCurrentProcess().StartTime.ToUniversalTime()),
        timestamp = DateTimeOffset.UtcNow
    });
});

app.MapPost("/queues/declare", async (QueueDeclareRequest request, QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    if (!clusterCoordinator.CanAcceptWrites(out var clusterWriteError))
        return Results.Conflict(new { error = clusterWriteError });

    try
    {
        var alreadyExisted = queueManager.GetQueue(request.Name) != null;
        queueManager.DeclareQueue(
            request.Name, 
            request.Durable, 
            request.DeadLetterQueue, 
            request.DefaultTtlMs);

        if (!alreadyExisted && clusterCoordinator.Enabled)
        {
            var replicated = await clusterCoordinator.ReplicateDeclareQueueAsync(
                request.Name,
                request.Durable,
                request.DeadLetterQueue,
                request.DefaultTtlMs);

            if (!replicated)
            {
                queueManager.DeleteQueue(request.Name);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Results.Ok(new { success = true, queue = request.Name });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/queues/{queueName}/purge", async (string queueName, QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    if (!clusterCoordinator.CanAcceptWrites(out var clusterWriteError))
        return Results.Conflict(new { error = clusterWriteError });

    var queue = queueManager.GetQueue(queueName);
    if (queue == null)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }

    try
    {
        await queue.PurgeAsync();

        if (clusterCoordinator.Enabled)
        {
            var replicated = await clusterCoordinator.ReplicatePurgeAsync(queueName);
            if (!replicated)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Results.Ok(new { success = true, queue = queueName });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/queues/{queueName}/publish", async (string queueName, PublishRequest request, QueueManager queueManager, ClusterCoordinator clusterCoordinator, MelonMetrics metrics, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    if (!clusterCoordinator.CanAcceptWrites(out var clusterWriteError))
        return Results.Conflict(new { error = clusterWriteError });

    var queue = queueManager.GetQueue(queueName);
    if (queue == null)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }

    try
    {
        var sw = Stopwatch.StartNew();
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
        if (!enqueued)
        {
            return Results.Ok(new { success = false, messageId = message.MessageId });
        }

        if (clusterCoordinator.Enabled)
        {
            var replicated = await clusterCoordinator.ReplicatePublishAsync(queueName, message);
            if (!replicated)
            {
                await queue.AckByMessageIdAsync(message.MessageId);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        sw.Stop();
        metrics.RecordMessagePublished(
            queueName,
            sw.Elapsed,
            payloadSizeBytes: body.Length,
            source: "http",
            replicated: clusterCoordinator.Enabled);

        return Results.Ok(new { success = true, messageId = message.MessageId });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/queues/{queueName}/consume", async (string queueName, QueueManager queueManager, ClusterCoordinator clusterCoordinator, MelonMetrics metrics, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context, readOnlyEndpoint: true))
        return Results.Unauthorized();

    if (!clusterCoordinator.CanAcceptWrites(out var clusterWriteError))
        return Results.Conflict(new { error = clusterWriteError });

    var queue = queueManager.GetQueue(queueName);
    if (queue == null)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }

    try
    {
        var sw = Stopwatch.StartNew();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5 second timeout
        var result = await queue.DequeueAsync("http-client", cts.Token);
        
        if (result == null)
        {
            return Results.Ok(new { message = (string?)null });
        }

        var (message, deliveryTag) = result.Value;
        
        // Auto-ack for HTTP consumers (stateless, no way to ack later)
        var acked = await queue.AckAsync(deliveryTag);
        if (!acked)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (clusterCoordinator.Enabled)
        {
            var replicated = await clusterCoordinator.ReplicateAckAsync(queueName, message.MessageId);
            if (!replicated)
            {
                metrics.RecordError("consume", "cluster_replication_failed", queueName);
            }
        }

        sw.Stop();
        metrics.RecordMessageConsumed(queueName, sw.Elapsed, message.Redelivered, source: "http");

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

static OtlpExportProtocol ParseOtlpProtocol(string protocol)
{
    return string.Equals(protocol, "grpc", StringComparison.OrdinalIgnoreCase)
        ? OtlpExportProtocol.Grpc
        : OtlpExportProtocol.HttpProtobuf;
}

static Uri BuildOtlpSignalEndpoint(string configuredEndpoint, OtlpExportProtocol protocol, string defaultHttpPath)
{
    var endpoint = new Uri(configuredEndpoint, UriKind.Absolute);
    if (protocol != OtlpExportProtocol.HttpProtobuf)
    {
        return endpoint;
    }

    var absolutePath = endpoint.AbsolutePath;
    if (string.IsNullOrEmpty(absolutePath) || string.Equals(absolutePath, "/", StringComparison.Ordinal))
    {
        return new Uri(endpoint, defaultHttpPath);
    }

    return endpoint;
}

// Queue deletion endpoint
app.MapDelete("/queues/{queueName}", async (string queueName, QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    if (!clusterCoordinator.CanAcceptWrites(out var clusterWriteError))
        return Results.Conflict(new { error = clusterWriteError });

    var deleted = queueManager.DeleteQueue(queueName);
    if (!deleted)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }

    if (clusterCoordinator.Enabled)
    {
        var replicated = await clusterCoordinator.ReplicateDeleteQueueAsync(queueName);
        if (!replicated)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
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
app.MapPost("/queues/gc", (QueueManager queueManager, ClusterCoordinator clusterCoordinator, HttpContext context) =>
{
    if (!ValidateAdminApiKey(context))
        return Results.Unauthorized();

    if (clusterCoordinator.Enabled)
    {
        return Results.BadRequest(new
        {
            error = "Manual queue GC is disabled in cluster mode. Use coordinated queue lifecycle operations."
        });
    }

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