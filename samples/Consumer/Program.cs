using MelonMQ.Client;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.WriteLine("MelonMQ .NET Consumer — Classic (optional Groups/Exchange/Stream)");
Console.WriteLine("===============================================================");
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

var enableExchange = Environment.GetEnvironmentVariable("MELON_SAMPLE_EXCHANGE") == "1";
var enableGroups = Environment.GetEnvironmentVariable("MELON_SAMPLE_GROUPS") == "1";
var enableStream = Environment.GetEnvironmentVariable("MELON_SAMPLE_STREAM") == "1";
var dedupeMode = (Environment.GetEnvironmentVariable("MELON_SAMPLE_DEDUPE_MODE") ?? "sqlite")
    .Trim()
    .ToLowerInvariant();
var dedupeEnabled = dedupeMode != "off";
var dedupeTtlMinutes = int.TryParse(Environment.GetEnvironmentVariable("MELON_SAMPLE_DEDUPE_TTL_MIN"), out var ttl)
    ? Math.Clamp(ttl, 1, 1440)
    : 60;
var dedupeMaxEntries = int.TryParse(Environment.GetEnvironmentVariable("MELON_SAMPLE_DEDUPE_MAX"), out var maxEntries)
    ? Math.Clamp(maxEntries, 1_000, 5_000_000)
    : 200_000;
var dedupeDbPath = Environment.GetEnvironmentVariable("MELON_SAMPLE_DEDUPE_DB") ?? "melonmq_inbox.db";
var dedupeTtlMs = dedupeTtlMinutes * 60_000L;
var dedupeStore = CreateIdempotencyStore(dedupeMode, dedupeDbPath, dedupeTtlMs, dedupeMaxEntries);
if (dedupeEnabled && dedupeStore == null)
{
    Console.WriteLine($"Unknown idempotency mode '{dedupeMode}', falling back to sqlite.");
    dedupeMode = "sqlite";
    dedupeStore = CreateIdempotencyStore(dedupeMode, dedupeDbPath, dedupeTtlMs, dedupeMaxEntries);
}

if (dedupeEnabled && dedupeStore != null)
{
    await dedupeStore.InitializeAsync(CancellationToken.None);
}

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
Console.WriteLine($"Exchange consume: {(enableExchange ? "on" : "off")}");
Console.WriteLine($"Group consume   : {(enableGroups ? "on" : "off")}");
Console.WriteLine($"Stream consume  : {(enableStream ? "on" : "off")}\n");
if (!dedupeEnabled)
{
    Console.WriteLine("Idempotency     : off");
}
else if (dedupeMode == "sqlite")
{
    Console.WriteLine($"Idempotency     : sqlite (db {dedupeDbPath}, TTL {dedupeTtlMinutes}m)");
}
else
{
    Console.WriteLine($"Idempotency     : memory (TTL {dedupeTtlMinutes}m)");
}
Console.WriteLine();

// ── stats ─────────────────────────────────────────────────────────────────────
int totalConsumed = 0;
int totalAcked    = 0;
int totalErrors   = 0;
var perQueue = classicQueues
    .Concat(GetOptionalQueues(enableExchange, enableStream))
    .ToDictionary(q => q, _ => 0);

if (dedupeEnabled && dedupeStore != null)
{
    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await dedupeStore.CleanupAsync(now, cts.Token);
        }
    });
}

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
if (enableExchange) Console.WriteLine("Also consuming: topic-exchange queues");
if (enableGroups) Console.WriteLine("Also consuming: consumer group demo");
if (enableStream) Console.WriteLine("Also consuming: stream replay");
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
                if (!await ShouldProcessAsync(queueName, group, message.MessageId, cts.Token))
                {
                    await ch.AckAsync(message.DeliveryTag, cts.Token);
                    return;
                }

                var body  = Encoding.UTF8.GetString(message.Body.Span);
                var count = Interlocked.Increment(ref totalConsumed);
                var label = group != null ? $"{queueName}[{group}]" : queueName;
                lock (perQueue) { if (perQueue.ContainsKey(queueName)) perQueue[queueName]++; }

                Console.WriteLine($"  📥 [{count,5}] {label,-40} {body[..Math.Min(body.Length, 80)]}");

                // simulated async work (200-600 ms)
                await Task.Delay(Random.Shared.Next(200, 601), cts.Token);
                await MarkProcessedAsync(queueName, group, message.MessageId, cts.Token);
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

static IEnumerable<string> GetOptionalQueues(bool enableExchange, bool enableStream)
{
    if (enableExchange)
    {
        yield return "events.orders";
        yield return "events.users";
        yield return "events.payments";
        yield return "events.notifications";
        yield return "events.all";
    }

    if (enableStream)
    {
        yield return "audit.events[stream]";
    }
}

async Task<bool> ShouldProcessAsync(string queueName, string? group, Guid messageId, CancellationToken cancellationToken)
{
    if (!dedupeEnabled || dedupeStore == null)
        return true;

    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var key = BuildDedupeKey(queueName, group, messageId);
    return await dedupeStore.TryAcquireAsync(key, queueName, group, messageId, now, cancellationToken);
}

async Task MarkProcessedAsync(string queueName, string? group, Guid messageId, CancellationToken cancellationToken)
{
    if (!dedupeEnabled || dedupeStore == null)
        return;

    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var key = BuildDedupeKey(queueName, group, messageId);
    await dedupeStore.MarkProcessedAsync(key, now, cancellationToken);
}

static string BuildDedupeKey(string queueName, string? group, Guid messageId)
{
    var groupLabel = string.IsNullOrWhiteSpace(group) ? "default" : group;
    return $"{queueName}::{groupLabel}::{messageId}";
}

static IIdempotencyStore? CreateIdempotencyStore(
    string mode,
    string dbPath,
    long ttlMs,
    int maxEntries)
{
    return mode switch
    {
        "off" => null,
        "memory" => new MemoryIdempotencyStore(ttlMs, maxEntries),
        "sqlite" => new SqliteIdempotencyStore(dbPath, ttlMs, maxEntries),
        _ => null
    };
}

// ── 1. Classic queues (orders.created excluded — covered by group demo below) ─
var classicQueuesFiltered = enableGroups
    ? classicQueues.Where(q => q != "orders.created").ToArray()
    : classicQueues.ToArray();
var classicTasks = classicQueuesFiltered.Select(q => ConsumeQueue(classicChannel, q)).ToArray();

// ── 2. Exchange queues — own connection so backlog doesn't starve classic queues
var exchangeQueues = new[] { "events.orders", "events.users", "events.payments", "events.notifications", "events.all" };
var exchangeTasks  = enableExchange
    ? exchangeQueues.Select(q => ConsumeQueue(exchangeChannel, q)).ToArray()
    : Array.Empty<Task>();

// ── 3. Consumer group demo ────────────────────────────────────────────────────
// Two groups consuming from "orders.created" on the SAME connection:
//   "processors" — load-balanced work group
//   "auditors"   — independent group that sees all the same messages
// The broker now keys ActiveConsumers by (queue, group), so both coexist.
var groupTasks = enableGroups
    ? new[]
    {
        ConsumeQueue(groupChannel, "orders.created", group: "processors"),
        ConsumeQueue(groupChannel, "orders.created", group: "auditors"),
    }
    : Array.Empty<Task>();

// ── 4. Stream queue replay ────────────────────────────────────────────────────
var streamTask = enableStream
    ? Task.Run(async () =>
    {
        long lastCommitted = -1;
        int streamCount = 0;
        var streamGroup = "stream-reader";
        try
        {
            await foreach (var msg in streamChannel.ConsumeStreamAsync("audit.events", startOffset: 0, group: streamGroup, cancellationToken: cts.Token))
            {
                streamCount++;
                var currentIndex = streamCount;
                if (!await ShouldProcessAsync("audit.events", streamGroup, msg.MessageId, cts.Token))
                {
                    if (msg.Offset.HasValue)
                        lastCommitted = msg.Offset.Value;

                    if (currentIndex % 50 == 0 && lastCommitted >= 0)
                        await streamChannel.StreamAckAsync("audit.events", lastCommitted, group: streamGroup, cts.Token);
                    continue;
                }
                var body = Encoding.UTF8.GetString(msg.Body.Span);
                if (currentIndex <= 20 || currentIndex % 500 == 0)
                    Console.WriteLine($"  📼 [stream {currentIndex,6}] offset={msg.Offset}  {body[..Math.Min(body.Length, 60)]}");
                lock (perQueue) { perQueue["audit.events[stream]"]++; }

                await MarkProcessedAsync("audit.events", streamGroup, msg.MessageId, cts.Token);
                if (msg.Offset.HasValue)
                    lastCommitted = msg.Offset.Value;

                if (currentIndex % 50 == 0 && lastCommitted >= 0)
                    await streamChannel.StreamAckAsync("audit.events", lastCommitted, group: streamGroup, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"  ✗ Stream error: {ex.Message}"); }
    })
    : Task.CompletedTask;

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

interface IIdempotencyStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<bool> TryAcquireAsync(string key, string queueName, string? group, Guid messageId, long nowMs, CancellationToken cancellationToken);
    Task MarkProcessedAsync(string key, long nowMs, CancellationToken cancellationToken);
    Task CleanupAsync(long nowMs, CancellationToken cancellationToken);
}

sealed class MemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, long> _entries = new();
    private readonly long _ttlMs;
    private readonly int _maxEntries;

    public MemoryIdempotencyStore(long ttlMs, int maxEntries)
    {
        _ttlMs = ttlMs;
        _maxEntries = maxEntries;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> TryAcquireAsync(string key, string queueName, string? group, Guid messageId, long nowMs, CancellationToken cancellationToken)
    {
        var expiresAt = nowMs + _ttlMs;
        while (true)
        {
            if (!_entries.TryGetValue(key, out var currentExpiresAt) || currentExpiresAt <= nowMs)
            {
                if (_entries.TryAdd(key, expiresAt))
                    return Task.FromResult(true);

                if (currentExpiresAt <= nowMs && _entries.TryUpdate(key, expiresAt, currentExpiresAt))
                    return Task.FromResult(true);

                continue;
            }

            return Task.FromResult(false);
        }
    }

    public Task MarkProcessedAsync(string key, long nowMs, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(key, out var current))
            _entries.TryUpdate(key, Math.Max(current, nowMs + _ttlMs), current);
        return Task.CompletedTask;
    }

    public Task CleanupAsync(long nowMs, CancellationToken cancellationToken)
    {
        foreach (var kvp in _entries)
        {
            if (kvp.Value <= nowMs)
                _entries.TryRemove(kvp.Key, out _);
        }

        var overage = _entries.Count - _maxEntries;
        if (overage > 0)
        {
            foreach (var kvp in _entries.OrderBy(k => k.Value).Take(overage))
                _entries.TryRemove(kvp.Key, out _);
        }

        return Task.CompletedTask;
    }
}

sealed class SqliteIdempotencyStore : IIdempotencyStore
{
    private readonly string _connectionString;
    private readonly long _ttlMs;
    private readonly int _maxEntries;

    public SqliteIdempotencyStore(string dbPath, long ttlMs, int maxEntries)
    {
        _ttlMs = ttlMs;
        _maxEntries = maxEntries;

        var fullPath = Path.GetFullPath(dbPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS inbox (
  dedupe_key TEXT PRIMARY KEY,
  queue_name TEXT NOT NULL,
  group_name TEXT NOT NULL,
  message_id TEXT NOT NULL,
  received_at INTEGER NOT NULL,
  processed_at INTEGER NULL
);
CREATE INDEX IF NOT EXISTS idx_inbox_received_at ON inbox(received_at);
";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryAcquireAsync(string key, string queueName, string? group, Guid messageId, long nowMs, CancellationToken cancellationToken)
    {
        if (await TryInsertAsync(key, queueName, group, messageId, nowMs, cancellationToken))
            return true;

        var cutoff = nowMs - _ttlMs;
        var existingReceivedAt = await GetReceivedAtAsync(key, cancellationToken);
        if (existingReceivedAt.HasValue && existingReceivedAt.Value <= cutoff)
        {
            await DeleteKeyAsync(key, cancellationToken);
            return await TryInsertAsync(key, queueName, group, messageId, nowMs, cancellationToken);
        }

        return false;
    }

    public async Task MarkProcessedAsync(string key, long nowMs, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE inbox SET processed_at = $processedAt WHERE dedupe_key = $key;";
        cmd.Parameters.AddWithValue("$processedAt", nowMs);
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CleanupAsync(long nowMs, CancellationToken cancellationToken)
    {
        var cutoff = nowMs - _ttlMs;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var deleteOld = connection.CreateCommand();
        deleteOld.CommandText = "DELETE FROM inbox WHERE received_at <= $cutoff;";
        deleteOld.Parameters.AddWithValue("$cutoff", cutoff);
        await deleteOld.ExecuteNonQueryAsync(cancellationToken);

        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(1) FROM inbox;";
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        var overage = count - _maxEntries;
        if (overage <= 0)
            return;

        await using var trimCmd = connection.CreateCommand();
        trimCmd.CommandText = @"
DELETE FROM inbox
WHERE dedupe_key IN (
  SELECT dedupe_key FROM inbox
  ORDER BY received_at ASC
  LIMIT $limit
);";
        trimCmd.Parameters.AddWithValue("$limit", overage);
        await trimCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> TryInsertAsync(
        string key,
        string queueName,
        string? group,
        Guid messageId,
        long nowMs,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO inbox (dedupe_key, queue_name, group_name, message_id, received_at)
VALUES ($key, $queue, $group, $messageId, $receivedAt);";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$queue", queueName);
        cmd.Parameters.AddWithValue("$group", group ?? "default");
        cmd.Parameters.AddWithValue("$messageId", messageId.ToString());
        cmd.Parameters.AddWithValue("$receivedAt", nowMs);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    private async Task<long?> GetReceivedAtAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT received_at FROM inbox WHERE dedupe_key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result == null || result is DBNull ? null : Convert.ToInt64(result);
    }

    private async Task DeleteKeyAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM inbox WHERE dedupe_key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
