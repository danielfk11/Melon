using MelonMQ.Client;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.WriteLine("MelonMQ .NET Consumer — Continuous");
Console.WriteLine("===================================");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nShutting down gracefully...");
};

// ── discover queues via HTTP ─────────────────────────────────────────────────
var httpPort = Environment.GetEnvironmentVariable("MELON_HTTP_PORT") ?? "9090";
var httpHost = Environment.GetEnvironmentVariable("MELON_HOST") ?? "localhost";

string[] queues;
try
{
    using var http = new HttpClient();
    var json = await http.GetStringAsync($"http://{httpHost}:{httpPort}/stats");
    var doc = JsonNode.Parse(json)!;
    queues = doc["queues"]!.AsArray()
        .Select(q => q!["name"]!.GetValue<string>())
        .ToArray();
}
catch (Exception ex)
{
    Console.WriteLine($"✗ Could not reach broker HTTP API: {ex.Message}");
    return;
}

if (queues.Length == 0)
{
    Console.WriteLine("No queues found. Run the producer first.");
    return;
}

Console.WriteLine($"Found {queues.Length} queues: {string.Join(", ", queues)}\n");

// ── stats ────────────────────────────────────────────────────────────────────
int totalConsumed = 0;
int totalAcked = 0;
int totalErrors = 0;
var perQueue = queues.ToDictionary(q => q, _ => 0);

// periodic stats printer
_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(15_000, cts.Token).ContinueWith(_ => { });
        if (cts.Token.IsCancellationRequested) break;
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] ── stats ── consumed={totalConsumed}  acked={totalAcked}  errors={totalErrors}");
        lock (perQueue)
        {
            foreach (var (q, n) in perQueue.OrderByDescending(k => k.Value).Take(5))
                if (n > 0) Console.WriteLine($"    {q,-40} {n}");
        }
        Console.WriteLine();
    }
});

// ── subscribe to all queues concurrently ─────────────────────────────────────
// Each queue has its own semaphore (MAX_CONCURRENT slots).
// Messages from the same queue are processed in parallel up to that limit —
// simulating real-world async work (DB calls, HTTP requests, etc.).
const int MaxConcurrentPerQueue = 8;

try
{
    using var connection = await MelonConnection.ConnectAsync(
        $"melon://{httpHost}:" + (Environment.GetEnvironmentVariable("MELON_PORT") ?? "5672"));
    using var channel = await connection.CreateChannelAsync();

    Console.WriteLine($"Subscribing to {queues.Length} queues (prefetch=50, concurrency={MaxConcurrentPerQueue}/queue)...\n");

    var consumerTasks = queues.Select(async q =>
    {
        // one semaphore per queue — limits how many messages are in-flight simultaneously
        var sem = new SemaphoreSlim(MaxConcurrentPerQueue, MaxConcurrentPerQueue);

        await foreach (var message in channel.ConsumeAsync(q, prefetch: 50, cts.Token))
        {
            // acquire a slot — if all slots busy, wait here (applies back-pressure)
            await sem.WaitAsync(cts.Token);

            // fire-and-forget: processing runs in parallel for this queue
            _ = Task.Run(async () =>
            {
                try
                {
                    var body = System.Text.Encoding.UTF8.GetString(message.Body.Span);
                    var count = Interlocked.Increment(ref totalConsumed);
                    lock (perQueue) perQueue[q]++;

                    Console.WriteLine($"  📥 [{count,5}] {q,-30} {body[..Math.Min(body.Length, 80)]}");

                    // ── simulated async work (replace with real DB/HTTP call) ──────
                    // await dbRepository.SaveAsync(body, cts.Token);
                    // await httpClient.PostAsync(...);
                    // ─────────────────────────────────────────────────────────────

                    await channel.AckAsync(message.DeliveryTag, cts.Token);
                    Interlocked.Increment(ref totalAcked);
                    Console.WriteLine($"         ✓ ack  tag={message.DeliveryTag}");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref totalErrors);
                    Console.WriteLine($"         ✗ ack failed: {ex.Message}");
                    try { await channel.NackAsync(message.DeliveryTag, requeue: true, cts.Token); } catch { }
                }
                finally
                {
                    sem.Release(); // free the slot for the next message
                }
            }, cts.Token);
        }
    }).ToArray();

    Console.WriteLine("Consuming... (Ctrl+C to stop)\n");
    await Task.WhenAll(consumerTasks);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Console.WriteLine($"✗ Error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

// ── final report ─────────────────────────────────────────────────────────────
Console.WriteLine($"\n{'=',-44}");
Console.WriteLine($"Total consumed : {totalConsumed}");
Console.WriteLine($"Total ACKed    : {totalAcked}");
Console.WriteLine($"Total errors   : {totalErrors}");
Console.WriteLine($"{'=',-44}");
Console.WriteLine("\nPer-queue breakdown:");
foreach (var (q, n) in perQueue.OrderBy(k => k.Key))
    Console.WriteLine($"  {q,-40} {n}");