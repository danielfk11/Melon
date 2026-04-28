using MelonMQ.Client;
using System.Text;
using System.Text.Json;

Console.WriteLine("MelonMQ .NET Producer — Classic (optional Exchange/Stream)");
Console.WriteLine("=========================================================");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nShutting down...");
};

var enableExchange = Environment.GetEnvironmentVariable("MELON_SAMPLE_EXCHANGE") == "1";
var enableStream = Environment.GetEnvironmentVariable("MELON_SAMPLE_STREAM") == "1";

Console.WriteLine($"Exchange publish: {(enableExchange ? "on" : "off")}");
Console.WriteLine($"Stream publish  : {(enableStream ? "on" : "off")}");
Console.WriteLine();

// ── queue catalogue (classic) ────────────────────────────────────────────────
var queues = new (string Name, string? Dlq, int Ttl)[]
{
    new("orders.created",       "orders.created.dlq",       300_000),
    new("orders.paid",          "orders.paid.dlq",          300_000),
    new("orders.shipped",       "orders.shipped.dlq",       300_000),
    new("orders.delivered",     "orders.delivered.dlq",     300_000),
    new("orders.cancelled",     "orders.cancelled.dlq",     300_000),
    new("users.signup",         "users.signup.dlq",         300_000),
    new("users.login",          null,                       300_000),
    new("users.profile_update", "users.profile_update.dlq", 300_000),
    new("payments.processing",  "payments.dlq",             300_000),
    new("payments.completed",   "payments.dlq",             300_000),
    new("notifications.email",  "notifications.email.dlq",  300_000),
    new("notifications.sms",    "notifications.sms.dlq",    300_000),
    new("notifications.push",   null,                       300_000),
    new("inventory.reserved",   "inventory.dlq",            300_000),
    new("inventory.released",   "inventory.dlq",            300_000),
    new("logs.application",     null,                       300_000),
    new("logs.audit",           "logs.audit.dlq",           300_000),
    new("analytics.pageview",   null,                       300_000),
    new("analytics.click",      null,                       300_000),
    new("chat.messages",        "chat.messages.dlq",        300_000),
};

// ── exchange bindings (topic) ────────────────────────────────────────────────
// "events" topic exchange routes to category queues by routing key pattern
const string EventsExchange = "events";
var exchangeBindings = new (string Queue, string RoutingKey)[]
{
    ("events.orders",        "orders.#"),
    ("events.users",         "users.#"),
    ("events.payments",      "payments.#"),
    ("events.notifications", "notifications.#"),
    ("events.all",           "#"),           // receives everything
};

// ── stream queue ─────────────────────────────────────────────────────────────
const string AuditStream = "audit.events";

var rng = new Random();
int totalSent = 0;
int round = 0;

try
{
    using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
    using var channel = await connection.CreateChannelAsync();

    // ── declare classic queues ────────────────────────────────────────────────
    Console.WriteLine($"[setup] Declaring {queues.Length} classic queues...");
    foreach (var q in queues)
        await channel.DeclareQueueAsync(q.Name, durable: true, dlq: q.Dlq, defaultTtlMs: q.Ttl);
 
    // ── declare exchange + bound queues ───────────────────────────────────────
    if (enableExchange)
    {
        Console.WriteLine($"[setup] Declaring exchange '{EventsExchange}' (topic)...");
        await channel.DeclareExchangeAsync(EventsExchange, type: "topic", durable: true);
        foreach (var (queueName, _) in exchangeBindings)
            await channel.DeclareQueueAsync(queueName, durable: true);
        foreach (var (queueName, routingKey) in exchangeBindings)
            await channel.BindQueueAsync(EventsExchange, queueName, routingKey);
    }

    // ── declare stream queue ──────────────────────────────────────────────────
    if (enableStream)
    {
        Console.WriteLine($"[setup] Declaring stream queue '{AuditStream}'...");
        await channel.DeclareStreamQueueAsync(AuditStream, durable: true, maxMessages: 10_000);
    }

    Console.WriteLine($"  ✓ Setup complete\n");
    Console.WriteLine("Ctrl+C to stop.\n");

    // ── continuous publish loop ───────────────────────────────────────────────
    while (!cts.Token.IsCancellationRequested)
    {
        round++;
        var q = queues[rng.Next(queues.Length)];
        int count = rng.Next(200, 450);

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Round {round} → classic:{q.Name}  ({count} msgs)");

        for (int i = 0; i < count && !cts.Token.IsCancellationRequested; i++)
        {
            var payload = BuildPayload(q.Name, rng.Next(1000), rng);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

            // 1) Publish directly to classic queue
            await channel.PublishAsync(q.Name, body, persistent: true, ttlMs: q.Ttl, cancellationToken: cts.Token);

            if (enableExchange)
            {
                // 2) Also publish to topic exchange with the queue name as routing key
                await channel.PublishToExchangeAsync(EventsExchange, q.Name, body,
                    persistent: true, cancellationToken: cts.Token);
            }

            if (enableStream)
            {
                // 3) Append to audit stream
                await channel.PublishAsync(AuditStream, body, cancellationToken: cts.Token);
            }

            totalSent++;
            Console.WriteLine($"  ✓ [{totalSent,4}] {q.Name} → {JsonSerializer.Serialize(payload)}");
        }

        int delayMs = rng.Next(2_000, 5_001);
        Console.WriteLine($"  → next round in {delayMs / 1000}s  (total sent so far: {totalSent})\n");
        await Task.Delay(delayMs, cts.Token);
    }
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Console.WriteLine($"\n✗ Error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine($"\n✓ Producer stopped. Total messages sent: {totalSent}");

// ── payload builders ─────────────────────────────────────────────────────────
static object BuildPayload(string queue, int i, Random rng) => queue switch
{
    "orders.created"       => new { @event = "order_created",   orderId = 1000 + i, total = Math.Round(rng.NextDouble() * 500, 2) },
    "orders.paid"          => new { @event = "order_paid",       orderId = 1000 + i, method = new[]{"pix","credit","debit"}[i % 3] },
    "orders.shipped"       => new { @event = "order_shipped",    orderId = 1000 + i, carrier = new[]{"correios","fedex","dhl"}[i % 3] },
    "orders.delivered"     => new { @event = "order_delivered",  orderId = 1000 + i, deliveredAt = DateTime.UtcNow },
    "orders.cancelled"     => new { @event = "order_cancelled",  orderId = 1000 + i, reason = new[]{"user_request","fraud","timeout"}[i % 3] },
    "users.signup"         => new { @event = "user_signup",      userId = 5000 + i, email = $"user{i}@example.com" },
    "users.login"          => new { @event = "user_login",       userId = 5000 + i, ip = $"192.168.1.{i % 254 + 1}" },
    "users.profile_update" => new { @event = "profile_update",   userId = 5000 + i, field = new[]{"name","avatar","bio"}[i % 3] },
    "payments.processing"  => new { @event = "payment_proc",     txId = $"TX-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{i}", amount = Math.Round(rng.NextDouble() * 1000, 2) },
    "payments.completed"   => new { @event = "payment_done",     txId = $"TX-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{i}", status = "completed" },
    "notifications.email"  => new { to = $"user{i}@mail.com", subject = $"Welcome #{i}", template = "welcome" },
    "notifications.sms"    => new { phone = $"+5511999{i % 10000:D4}", text = $"Your code is {1000 + i % 9000}" },
    "notifications.push"   => new { deviceToken = $"tok-{i}", title = "New offer!", body = $"Discount {rng.Next(5, 51)}%" },
    "inventory.reserved"   => new { sku = $"SKU-{2000 + i % 500}", qty = rng.Next(1, 20), warehouse = new[]{"SP","RJ","MG"}[i % 3] },
    "inventory.released"   => new { sku = $"SKU-{2000 + i % 500}", qty = rng.Next(1, 10), reason = "order_cancelled" },
    "logs.application"     => new { level = new[]{"info","warn","error"}[i % 3], service = "api", msg = $"Log entry #{i}" },
    "logs.audit"           => new { action = new[]{"create","update","delete"}[i % 3], resource = "order", actor = $"admin-{i % 10}" },
    "analytics.pageview"   => new { page = $"/products/{i % 200}", sessionId = $"sess-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", durationMs = rng.Next(500, 10000) },
    "analytics.click"      => new { element = $"btn-buy-{i % 50}", page = "/checkout", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
    "chat.messages"        => new { room = $"room-{(i % 10) + 1}", from = $"user-{i % 100}", text = $"Hey! Message #{i}" },
    _                      => new { data = $"message-{i}" },
};
Console.WriteLine();

// var cts = new CancellationTokenSource();
// Console.CancelKeyPress += (_, e) =>
// {
//     e.Cancel = true;
//     cts.Cancel();
//     Console.WriteLine("\nShutting down...");
// };

// // ── queue catalogue ──────────────────────────────────────────────────────────
// var queues = new (string Name, string? Dlq, int Ttl)[]
// {
//     new("orders.created",       "orders.created.dlq",       300_000),
//     new("orders.paid",          "orders.paid.dlq",          300_000),
//     new("orders.shipped",       "orders.shipped.dlq",       300_000),
//     new("orders.delivered",     "orders.delivered.dlq",     300_000),
//     new("orders.cancelled",     "orders.cancelled.dlq",     300_000),
//     new("users.signup",         "users.signup.dlq",         300_000),
//     new("users.login",          null,                       300_000),
//     new("users.profile_update", "users.profile_update.dlq", 300_000),
//     new("payments.processing",  "payments.dlq",             300_000),
//     new("payments.completed",   "payments.dlq",             300_000),
//     new("notifications.email",  "notifications.email.dlq",  300_000),
//     new("notifications.sms",    "notifications.sms.dlq",    300_000),
//     new("notifications.push",   null,                       300_000),
//     new("inventory.reserved",   "inventory.dlq",            300_000),
//     new("inventory.released",   "inventory.dlq",            300_000),
//     new("logs.application",     null,                       300_000),
//     new("logs.audit",           "logs.audit.dlq",           300_000),
//     new("analytics.pageview",   null,                       300_000),
//     new("analytics.click",      null,                       300_000),
//     new("chat.messages",        "chat.messages.dlq",        300_000),
// };

// var rng = new Random();
// int totalSent = 0;
// int round = 0;

// try
// {
//     using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
//     using var channel = await connection.CreateChannelAsync();

//     // ── declare all queues once ───────────────────────────────────────────────
//     Console.WriteLine($"Declaring {queues.Length} queues...");
//     foreach (var q in queues)
//         await channel.DeclareQueueAsync(q.Name, durable: true, dlq: q.Dlq, defaultTtlMs: q.Ttl);
//     Console.WriteLine($"  ✓ {queues.Length} queues declared\n");
//     Console.WriteLine("Ctrl+C to stop.\n");

//     // ── continuous publish loop ───────────────────────────────────────────────
//     while (!cts.Token.IsCancellationRequested)
//     {
//         round++;

//         // pick a random queue and a random batch size (2..50)
//         var q = queues[rng.Next(queues.Length)];
//         int count = rng.Next(5000, 7000);

//         Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Round {round} → {q.Name}  ({count} msgs)");

//         for (int i = 0; i < count && !cts.Token.IsCancellationRequested; i++)
//         {
//             var payload = BuildPayload(q.Name, rng.Next(1000), rng);
//             var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
//             await channel.PublishAsync(q.Name, body, persistent: true, ttlMs: q.Ttl, cancellationToken: cts.Token);
//             totalSent++;
//             Console.WriteLine($"  ✓ [{totalSent,4}] {q.Name} → {JsonSerializer.Serialize(payload)}");
//         }

//         // wait 10–15 seconds before next round
//         int delayMs = rng.Next(10_000, 15_001);
//         Console.WriteLine($"  → next round in {delayMs / 1000}s  (total sent so far: {totalSent})\n");
//         await Task.Delay(delayMs, cts.Token);
//     }
// }
// catch (OperationCanceledException) { }
// catch (Exception ex)
// {
//     Console.WriteLine($"\n✗ Error: {ex.Message}");
//     Console.WriteLine(ex.StackTrace);
// }

// Console.WriteLine($"\n✓ Producer stopped. Total messages sent: {totalSent}");

// // ── payload builders ─────────────────────────────────────────────────────────
// static object BuildPayload(string queue, int i, Random rng) => queue switch
// {
//     "orders.created"       => new { @event = "order_created",   orderId = 1000 + i, total = Math.Round(rng.NextDouble() * 500, 2) },
//     "orders.paid"          => new { @event = "order_paid",       orderId = 1000 + i, method = new[]{"pix","credit","debit"}[i % 3] },
//     "orders.shipped"       => new { @event = "order_shipped",    orderId = 1000 + i, carrier = new[]{"correios","fedex","dhl"}[i % 3] },
//     "orders.delivered"     => new { @event = "order_delivered",  orderId = 1000 + i, deliveredAt = DateTime.UtcNow },
//     "orders.cancelled"     => new { @event = "order_cancelled",  orderId = 1000 + i, reason = new[]{"user_request","fraud","timeout"}[i % 3] },
//     "users.signup"         => new { @event = "user_signup",      userId = 5000 + i, email = $"user{i}@example.com" },
//     "users.login"          => new { @event = "user_login",       userId = 5000 + i, ip = $"192.168.1.{i % 254 + 1}" },
//     "users.profile_update" => new { @event = "profile_update",   userId = 5000 + i, field = new[]{"name","avatar","bio"}[i % 3] },
//     "payments.processing"  => new { @event = "payment_proc",     txId = $"TX-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{i}", amount = Math.Round(rng.NextDouble() * 1000, 2) },
//     "payments.completed"   => new { @event = "payment_done",     txId = $"TX-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{i}", status = "completed" },
//     "notifications.email"  => new { to = $"user{i}@mail.com", subject = $"Welcome #{i}", template = "welcome" },
//     "notifications.sms"    => new { phone = $"+5511999{i % 10000:D4}", text = $"Your code is {1000 + i % 9000}" },
//     "notifications.push"   => new { deviceToken = $"tok-{i}", title = "New offer!", body = $"Discount {rng.Next(5, 51)}%" },
//     "inventory.reserved"   => new { sku = $"SKU-{2000 + i % 500}", qty = rng.Next(1, 20), warehouse = new[]{"SP","RJ","MG"}[i % 3] },
//     "inventory.released"   => new { sku = $"SKU-{2000 + i % 500}", qty = rng.Next(1, 10), reason = "order_cancelled" },
//     "logs.application"     => new { level = new[]{"info","warn","error"}[i % 3], service = "api", msg = $"Log entry #{i}" },
//     "logs.audit"           => new { action = new[]{"create","update","delete"}[i % 3], resource = "order", actor = $"admin-{i % 10}" },
//     "analytics.pageview"   => new { page = $"/products/{i % 200}", sessionId = $"sess-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", durationMs = rng.Next(500, 10000) },
//     "analytics.click"      => new { element = $"btn-buy-{i % 50}", page = "/checkout", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
//     "chat.messages"        => new { room = $"room-{(i % 10) + 1}", from = $"user-{i % 100}", text = $"Hey! Message #{i}" },
//     _                      => new { data = $"message-{i}" },
// };