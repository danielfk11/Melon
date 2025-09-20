using MelonMQ.Broker.Core;
using MelonMQ.Broker.Http;

var builder = WebApplication.CreateBuilder(args);

// Configure services
builder.Services.AddLogging();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyHeader()
               .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<QueueManager>(provider =>
{
    var config = provider.GetService<IConfiguration>();
    var dataDir = config?.GetValue<string>("MelonMQ:DataDirectory") ?? "data";
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    return new QueueManager(dataDir, loggerFactory);
});

builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<TcpServer>(provider =>
{
    var config = provider.GetService<IConfiguration>();
    var port = config?.GetValue<int>("MelonMQ:TcpPort") ?? 5672;
    var queueManager = provider.GetRequiredService<QueueManager>();
    var connectionManager = provider.GetRequiredService<ConnectionManager>();
    var logger = provider.GetRequiredService<ILogger<TcpServer>>();
    return new TcpServer(queueManager, connectionManager, port, logger);
});

builder.Services.AddHostedService<MelonMQService>();

var app = builder.Build();

// Configure middleware
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// Configure HTTP endpoints
app.Urls.Add($"http://localhost:{builder.Configuration.GetValue<int>("MelonMQ:HttpPort", 8080)}");

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/admin", () => Results.Redirect("/index.html"));
app.MapGet("/management", () => Results.Redirect("/index.html"));

app.MapGet("/health", () => new { status = "healthy", timestamp = DateTimeOffset.UtcNow });

app.MapGet("/stats", (QueueManager queueManager, ConnectionManager connectionManager) =>
{
    var queues = queueManager.GetAllQueues().Select(q => new
    {
        name = q.Name,
        durable = q.IsDurable,
        pendingMessages = q.PendingCount,
        inFlightMessages = q.InFlightCount
    });

    return new
    {
        queues = queues,
        connections = connectionManager.ConnectionCount,
        timestamp = DateTimeOffset.UtcNow
    };
});

app.MapPost("/queues/declare", async (QueueDeclareRequest request, QueueManager queueManager) =>
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

app.MapPost("/queues/{queueName}/purge", (string queueName, QueueManager queueManager) =>
{
    var queue = queueManager.GetQueue(queueName);
    if (queue == null)
    {
        return Results.NotFound(new { error = $"Queue '{queueName}' not found" });
    }

    try
    {
        _ = Task.Run(() => queue.PurgeAsync());
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