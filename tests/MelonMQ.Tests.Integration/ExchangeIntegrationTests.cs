using FluentAssertions;
using MelonMQ.Client;
using System.Text;
using Xunit;

namespace MelonMQ.Tests.Integration;

public class ExchangeIntegrationTests : IAsyncLifetime
{
    private readonly TestBrokerHost _host;

    public ExchangeIntegrationTests()
    {
        _host = new TestBrokerHost(Path.Combine(Path.GetTempPath(), $"melon-test-exchange-{Guid.NewGuid():N}"));
    }

    public Task InitializeAsync() => _host.StartAsync();
    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<MelonConnection> Connect() =>
        MelonConnection.ConnectAsync($"melon://127.0.0.1:{_host.TcpPort}");

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(ReadOnlyMemory<byte> b) => Encoding.UTF8.GetString(b.Span);

    private static async Task<string?> TryReceiveOne(
        MelonChannel ch, string queue, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await foreach (var msg in ch.ConsumeAsync(queue, prefetch: 10, cancellationToken: cts.Token))
            {
                await ch.AckAsync(msg.DeliveryTag, cts.Token);
                return Str(msg.Body);
            }
        }
        catch (OperationCanceledException)
        {
            // timeout — no message arrived within the window
        }
        return null;
    }

    // ── fanout ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fanout_DeliversToBothBoundQueues()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareExchangeAsync("test.fanout", "fanout");
        await ch.DeclareQueueAsync("fanout.q1");
        await ch.DeclareQueueAsync("fanout.q2");
        await ch.BindQueueAsync("test.fanout", "fanout.q1", routingKey: "");
        await ch.BindQueueAsync("test.fanout", "fanout.q2", routingKey: "");

        await ch.PublishToExchangeAsync("test.fanout", "", Bytes("hello-fanout"));

        var r1 = await TryReceiveOne(ch, "fanout.q1");
        var r2 = await TryReceiveOne(ch, "fanout.q2");

        r1.Should().Be("hello-fanout");
        r2.Should().Be("hello-fanout");
    }

    [Fact]
    public async Task Fanout_DoesNotDeliverToUnboundQueue()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareExchangeAsync("test.fanout2", "fanout");
        await ch.DeclareQueueAsync("fanout2.q1");
        await ch.DeclareQueueAsync("fanout2.q2");
        await ch.BindQueueAsync("test.fanout2", "fanout2.q1", routingKey: "");
        // fanout2.q2 NOT bound

        await ch.PublishToExchangeAsync("test.fanout2", "", Bytes("ping"));

        var r1 = await TryReceiveOne(ch, "fanout2.q1");
        var r2 = await TryReceiveOne(ch, "fanout2.q2", timeoutMs: 500);

        r1.Should().Be("ping");
        r2.Should().BeNull("queue is not bound to the exchange");
    }

    // ── direct ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Direct_RoutesOnlyToMatchingKey()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareExchangeAsync("test.direct", "direct");
        await ch.DeclareQueueAsync("direct.info");
        await ch.DeclareQueueAsync("direct.error");
        await ch.BindQueueAsync("test.direct", "direct.info",  "info");
        await ch.BindQueueAsync("test.direct", "direct.error", "error");

        await ch.PublishToExchangeAsync("test.direct", "info",  Bytes("info-msg"));
        await ch.PublishToExchangeAsync("test.direct", "error", Bytes("error-msg"));

        var ri = await TryReceiveOne(ch, "direct.info");
        var re = await TryReceiveOne(ch, "direct.error");

        ri.Should().Be("info-msg");
        re.Should().Be("error-msg");
    }

    [Fact]
    public async Task Direct_NoDeliveryForUnmatchedKey()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareExchangeAsync("test.direct2", "direct");
        await ch.DeclareQueueAsync("direct2.logs");
        await ch.BindQueueAsync("test.direct2", "direct2.logs", "info");

        await ch.PublishToExchangeAsync("test.direct2", "debug", Bytes("debug-msg"));

        var r = await TryReceiveOne(ch, "direct2.logs", timeoutMs: 500);
        r.Should().BeNull("routing key 'debug' has no binding");
    }

    // ── topic ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Topic_StarWildcard_MatchesSingleWord()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareExchangeAsync("test.topic", "topic");
        await ch.DeclareQueueAsync("topic.orders");
        await ch.BindQueueAsync("test.topic", "topic.orders", "orders.*");

        await ch.PublishToExchangeAsync("test.topic", "orders.created", Bytes("order1"));
        await ch.PublishToExchangeAsync("test.topic", "orders.cancelled", Bytes("order2"));
        await ch.PublishToExchangeAsync("test.topic", "payments.done", Bytes("pay1")); // should NOT match

        var r1 = await TryReceiveOne(ch, "topic.orders");
        var r2 = await TryReceiveOne(ch, "topic.orders");
        var r3 = await TryReceiveOne(ch, "topic.orders", timeoutMs: 500);

        r1.Should().Be("order1");
        r2.Should().Be("order2");
        r3.Should().BeNull("payments.done does not match orders.*");
    }

    [Fact]
    public async Task Topic_HashWildcard_MatchesZeroOrMoreWords()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareExchangeAsync("test.topic2", "topic");
        await ch.DeclareQueueAsync("topic2.all-orders");
        await ch.BindQueueAsync("test.topic2", "topic2.all-orders", "orders.#");

        await ch.PublishToExchangeAsync("test.topic2", "orders.created",        Bytes("a"));
        await ch.PublishToExchangeAsync("test.topic2", "orders.paid.stripe",    Bytes("b"));
        await ch.PublishToExchangeAsync("test.topic2", "notifications.email",   Bytes("c")); // No match

        var r1 = await TryReceiveOne(ch, "topic2.all-orders");
        var r2 = await TryReceiveOne(ch, "topic2.all-orders");
        var r3 = await TryReceiveOne(ch, "topic2.all-orders", timeoutMs: 500);

        r1.Should().Be("a");
        r2.Should().Be("b");
        r3.Should().BeNull();
    }

    [Fact]
    public async Task Topic_HashAtEnd_MatchesZeroWords()
    {
        // "logs.#" should also match just "logs" (zero trailing words)
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareExchangeAsync("test.topic3", "topic");
        await ch.DeclareQueueAsync("topic3.logs");
        await ch.BindQueueAsync("test.topic3", "topic3.logs", "logs.#");

        await ch.PublishToExchangeAsync("test.topic3", "logs",           Bytes("bare"));
        await ch.PublishToExchangeAsync("test.topic3", "logs.api",       Bytes("api"));
        await ch.PublishToExchangeAsync("test.topic3", "logs.api.error", Bytes("nested"));

        var results = new List<string>();
        using var cts = new CancellationTokenSource(2000);
        await foreach (var msg in ch.ConsumeAsync("topic3.logs", prefetch: 10, cancellationToken: cts.Token))
        {
            results.Add(Str(msg.Body));
            await ch.AckAsync(msg.DeliveryTag);
            if (results.Count == 3) break;
        }

        results.Should().BeEquivalentTo(["bare", "api", "nested"]);
    }

    // ── unbind ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unbind_StopsDelivery()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareExchangeAsync("test.unbind", "direct");
        await ch.DeclareQueueAsync("unbind.q");
        await ch.BindQueueAsync("test.unbind", "unbind.q", "key");

        await ch.PublishToExchangeAsync("test.unbind", "key", Bytes("before-unbind"));
        (await TryReceiveOne(ch, "unbind.q")).Should().Be("before-unbind");

        await ch.UnbindQueueAsync("test.unbind", "unbind.q", "key");

        await ch.PublishToExchangeAsync("test.unbind", "key", Bytes("after-unbind"));
        (await TryReceiveOne(ch, "unbind.q", timeoutMs: 500)).Should().BeNull();
    }

    [Fact]
    public async Task DurableExchangeTopology_ShouldSurviveBrokerRestart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));

        await using (var host = new TestBrokerHost(dataDir))
        {
            await host.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var ch = await conn.CreateChannelAsync();

            await ch.DeclareExchangeAsync("durable.events", "topic", durable: true);
            await ch.DeclareQueueAsync("durable.events.orders");
            await ch.BindQueueAsync("durable.events", "durable.events.orders", "orders.*");
        }

        await using (var restarted = new TestBrokerHost(dataDir))
        {
            await restarted.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{restarted.TcpPort}");
            await using var ch = await conn.CreateChannelAsync();

            await ch.DeclareQueueAsync("durable.events.orders");
            await ch.PublishToExchangeAsync("durable.events", "orders.created", Bytes("after-restart"));

            var received = await TryReceiveOne(ch, "durable.events.orders");
            received.Should().Be("after-restart", "durable exchange topology should be loaded from persisted metadata after restart");
        }
    }
}
