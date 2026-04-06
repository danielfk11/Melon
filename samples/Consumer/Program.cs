using MelonMQ.Client;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.WriteLine("MelonMQ .NET Consumer — Classic + Groups + Stream");
Console.WriteLine("==================================================");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nShutting down gracefully...");
};

// ── config ───────────────────────────────────────────────────────────────────
var httpPort = Environment.GetEnvironmentVariable("MELON_HTTP_PORT") ?? "9090";
var httpHost = Environment.GetEnvironmentVariable("MELON_HOST")      ?? "localhost";
var tcpPort  = Environment.GetEnvironmentVariable("MELON_PORT")      ?? "5672";

// ── discover classic queues via HTTP ─────────────────────────────────────────
string[] classicQueues;
try
{
    using var http = new HttpClient();
    var json = await http.GetStringAsync($"http://{httpHost}:{httpPort}/stats");
    var doc = JsonNode.Parse(json)!;
    classicQueues = doc["queues"]!.AsArray()
        .Select(q => q!["name"]!.GetValue<string>())
        .Where(n => !n.StartsWith("events.") && !n.StartsWith("_grp:") && n != "audit.events")
        .ToArray();
}
catch (Exception ex)
{
    Console.WriteLine($"✗ Could not reach broker HTTP API: {ex.Message}");
    return;
}

if (classicQueues.Length == 0)
{
    Console.WriteLine("No queues found. Run the producer first.");
    return;
}

Console.WriteLine($"Found {classicQueues.Length} classic queues\n");

// ── stats ─────────────────────────────────────────────────────────────────────
int totalConsumed = 0;
int totalAcked    = 0;
int totalErrors   = 0;
var perQueue = classicQueues
    .Concat(["events.orders", "events.users", "events.payments", "events.notifications", "events.all", "audit.events[stream]"])
    .ToDictionary(q => q, _ => 0);

_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(15_000, cts.Token).ContinueWith(_ => { });
        if (cts.Token.IsCancellationRequested) break;
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] ── stats ── consumed={totalConsumed}  acked={totalAcked}  errors={totalErrors}");
        lock (perQueue)
        {
            foreach (var (q, n) in perQueue.OrderByDescending(k => k.Value).Take(8))
                if (n > 0) Console.WriteLine($"    {q,-45} {n}");
        }
        Console.WriteLine();
    }
});

// ── shared channel factory ────────────────────────────────────────────────────
const int MaxConcurrentPerQueue = 8;

// ── connections (one per domain so prefetch budgets are independent) ──────────
using var classicConn    = await MelonConnection.ConnectAsync($"melon://{httpHost}:{tcpPort}");
using var classicChannel = await classicConn.CreateChannelAsync();
// Exchange queues — isolated so large backlogs from routing don't starve classic
using var exchangeConn    = await MelonConnection.ConnectAsync($"melon://{httpHost}:{tcpPort}");
using var exchangeChannel = await exchangeConn.CreateChannelAsync();
// Consumer groups — multiple groups on same queue now supported on one connection
using var groupConn    = await MelonConnection.ConnectAsync($"melon://{httpHost}:{tcpPort}");
using var groupChannel = await groupConn.CreateChannelAsync();
// Stream replay — own connection so offset-0 catch-up doesn't block anything
using var streamConn    = await MelonConnection.ConnectAsync($"melon://{httpHost}:{tcpPort}");
using var streamChannel = await streamConn.CreateChannelAsync();

Console.WriteLine($"Subscribing to {classicQueues.Length} classic queues  (prefetch=50, concurrency={MaxConcurrentPerQueue}/queue)");
Console.WriteLine("Also consuming: topic-exchange queues, consumer group demo, stream replay");
Console.WriteLine("Ctrl+C to stop.\n");

// ── helper: consume one queue with per-queue concurrency ─────────────────────
async Task ConsumeQueue(MelonChannel ch, string queueName, string? group = null)
{
    var sem = new SemaphoreSlim(MaxConcurrentPerQueue, MaxConcurrentPerQueue);
    await foreach (var message in ch.ConsumeAsync(queueName, prefetch: 50, group: group, cancellationToken: cts.Token))
    {
        await sem.WaitAsync(cts.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                var body  = Encoding.UTF8.GetString(message.Body.Span);
                var count = Interlocked.Increment(ref totalConsumed);
                var label = group != null ? $"{queueName}[{group}]" : queueName;
                lock (perQueue) { if (perQueue.ContainsKey(queueName)) perQueue[queueName]++; }

                Console.WriteLine($"  📥 [{count,5}] {label,-40} {body[..Math.Min(body.Length, 80)]}");

                // simulated async work (200-600 ms)
                await Task.Delay(Random.Shared.Next(200, 601), cts.Token);

                await ch.AckAsync(message.DeliveryTag, cts.Token);
                Interlocked.Increment(ref totalAcked);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Interlocked.Increment(ref totalErrors);
                Console.WriteLine($"         ✗ {ex.Message}");
                try { await ch.NackAsync(message.DeliveryTag, requeue: true, cts.Token); } catch { }
            }
            finally { sem.Release(); }
        }, cts.Token);
    }
}

// ── 1. Classic queues (orders.created excluded — covered by group demo below) ─
var classicQueuesFiltered = classicQueues.Where(q => q != "orders.created").ToArray();
var classicTasks = classicQueuesFiltered.Select(q => ConsumeQueue(classicChannel, q)).ToArray();

// ── 2. Exchange queues — own connection so backlog doesn't starve classic queues
var exchangeQueues = new[] { "events.orders", "events.users", "events.payments", "events.notifications", "events.all" };
var exchangeTasks  = exchangeQueues.Select(q => ConsumeQueue(exchangeChannel, q)).ToArray();

// ── 3. Consumer group demo ────────────────────────────────────────────────────
// Two groups consuming from "orders.created" on the SAME connection:
//   "processors" — load-balanced work group
//   "auditors"   — independent group that sees all the same messages
// The broker now keys ActiveConsumers by (queue, group), so both coexist.
var groupTasks = new[]
{
    ConsumeQueue(groupChannel, "orders.created", group: "processors"),
    ConsumeQueue(groupChannel, "orders.created", group: "auditors"),
};

// ── 4. Stream queue replay ────────────────────────────────────────────────────
var streamTask = Task.Run(async () =>
{
    long lastCommitted = -1;
    int streamCount = 0;
    try
    {
        await foreach (var msg in streamChannel.ConsumeStreamAsync("audit.events", startOffset: 0, group: "stream-reader", cancellationToken: cts.Token))
        {
            streamCount++;
            var body = Encoding.UTF8.GetString(msg.Body.Span);
            if (streamCount <= 20 || streamCount % 500 == 0)
                Console.WriteLine($"  📼 [stream {streamCount,6}] offset={msg.Offset}  {body[..Math.Min(body.Length, 60)]}");
            lock (perQueue) { perQueue["audit.events[stream]"]++; }

            if (msg.Offset.HasValue)
                lastCommitted = msg.Offset.Value;

            if (streamCount % 50 == 0 && lastCommitted >= 0)
                await streamChannel.StreamAckAsync("audit.events", lastCommitted, group: "stream-reader", cts.Token);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Console.WriteLine($"  ✗ Stream error: {ex.Message}"); }
});

Console.WriteLine("Consuming... (Ctrl+C to stop)\n");
await Task.WhenAll([.. classicTasks, .. exchangeTasks, .. groupTasks, streamTask]);

// ── final report ─────────────────────────────────────────────────────────────
Console.WriteLine($"\n{'=',-48}");
Console.WriteLine($"Total consumed : {totalConsumed}");
Console.WriteLine($"Total ACKed    : {totalAcked}");
Console.WriteLine($"Total errors   : {totalErrors}");
Console.WriteLine($"{'=',-48}");
Console.WriteLine("\nPer-queue breakdown:");
foreach (var (q, n) in perQueue.OrderBy(k => k.Key))
    Console.WriteLine($"  {q,-45} {n}");
