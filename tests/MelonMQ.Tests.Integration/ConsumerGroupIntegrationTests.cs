using FluentAssertions;
using MelonMQ.Client;
using System.Text;
using System.Collections.Concurrent;
using Xunit;

namespace MelonMQ.Tests.Integration;

public class ConsumerGroupIntegrationTests : IAsyncLifetime
{
    private readonly TestBrokerHost _host;

    public ConsumerGroupIntegrationTests()
    {
        _host = new TestBrokerHost(Path.Combine(Path.GetTempPath(), $"melon-test-groups-{Guid.NewGuid():N}"));
    }

    public Task InitializeAsync() => _host.StartAsync();
    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private Task<MelonConnection> Connect() =>
        MelonConnection.ConnectAsync($"melon://127.0.0.1:{_host.TcpPort}");

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(ReadOnlyMemory<byte> b) => Encoding.UTF8.GetString(b.Span);

    // ── load balance within same group ────────────────────────────────────────

    [Fact]
    public async Task SameGroup_LoadBalances_MessagesAcrossConsumers()
    {
        // Two consumers in the same group receive disjoint subsets of messages.
        using var connPub = await Connect();
        using var conn1   = await Connect();
        using var conn2   = await Connect();
        using var chPub   = await connPub.CreateChannelAsync();
        using var ch1     = await conn1.CreateChannelAsync();
        using var ch2     = await conn2.CreateChannelAsync();

        await chPub.DeclareQueueAsync("group.tasks");

        const int totalMessages = 20;

        var received1 = new ConcurrentBag<string>();
        var received2 = new ConcurrentBag<string>();
        using var cts = new CancellationTokenSource(10_000);

        // Consumers must subscribe BEFORE publishing so fan-out wires up
        var t1 = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in ch1.ConsumeAsync("group.tasks", prefetch: 10, group: "workers", cancellationToken: cts.Token))
                {
                    received1.Add(Str(msg.Body));
                    await ch1.AckAsync(msg.DeliveryTag);
                    if (received1.Count + received2.Count >= totalMessages) cts.Cancel();
                }
            }
            catch (OperationCanceledException) { }
        });

        var t2 = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in ch2.ConsumeAsync("group.tasks", prefetch: 10, group: "workers", cancellationToken: cts.Token))
                {
                    received2.Add(Str(msg.Body));
                    await ch2.AckAsync(msg.DeliveryTag);
                    if (received1.Count + received2.Count >= totalMessages) cts.Cancel();
                }
            }
            catch (OperationCanceledException) { }
        });

        // Give both consumers time to subscribe (fan-out wires up on first subscribe)
        await Task.Delay(400);

        // Publish after both consumers are subscribed
        for (int i = 0; i < totalMessages; i++)
            await chPub.PublishAsync("group.tasks", Bytes($"task-{i}"));

        await Task.WhenAll(t1, t2);

        var all = received1.Concat(received2).ToList();
        all.Should().HaveCount(totalMessages, "every message must be received exactly once");
        all.Should().OnlyHaveUniqueItems("no duplicates within a group");
        received1.Should().NotBeEmpty("consumer 1 should receive some messages");
        received2.Should().NotBeEmpty("consumer 2 should receive some messages");
    }

    // ── different groups receive all messages independently ───────────────────

    [Fact]
    public async Task DifferentGroups_EachReceiveAllMessages()
    {
        // Publish 5 messages; group-A and group-B each get all 5.
        using var connA = await Connect();
        using var connB = await Connect();
        var connPub = await Connect();
        using var chPub = await connPub.CreateChannelAsync();
        using var chA = await connA.CreateChannelAsync();
        using var chB = await connB.CreateChannelAsync();

        await chPub.DeclareQueueAsync("group.broadcast");

        const int n = 5;

        var gotA = new List<string>();
        var gotB = new List<string>();

        using var cts = new CancellationTokenSource(6000);

        // Start consumers first
        var tA = Task.Run(async () =>
        {
            await foreach (var msg in chA.ConsumeAsync("group.broadcast", prefetch: 10, group: "groupA", cancellationToken: cts.Token))
            {
                gotA.Add(Str(msg.Body));
                await chA.AckAsync(msg.DeliveryTag);
                if (gotA.Count >= n) break;
            }
        });

        var tB = Task.Run(async () =>
        {
            await foreach (var msg in chB.ConsumeAsync("group.broadcast", prefetch: 10, group: "groupB", cancellationToken: cts.Token))
            {
                gotB.Add(Str(msg.Body));
                await chB.AckAsync(msg.DeliveryTag);
                if (gotB.Count >= n) break;
            }
        });

        // Give consumers a moment to subscribe, then publish
        await Task.Delay(200);
        for (int i = 0; i < n; i++)
            await chPub.PublishAsync("group.broadcast", Bytes($"msg-{i}"));

        await Task.WhenAll(tA, tB);

        gotA.Should().HaveCount(n, "group-A should receive all messages");
        gotB.Should().HaveCount(n, "group-B should receive all messages");
        gotA.Should().BeEquivalentTo(gotB, "same set of messages for both groups");
    }

    // ── solo consumer (no group) ─────────────────────────────────────────────

    [Fact]
    public async Task NoGroup_BehavesAsNormalQueue()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareQueueAsync("nogroup.queue");
        await ch.PublishAsync("nogroup.queue", Bytes("hello"));

        using var cts = new CancellationTokenSource(3000);
        string? received = null;
        await foreach (var msg in ch.ConsumeAsync("nogroup.queue", cancellationToken: cts.Token))
        {
            received = Str(msg.Body);
            await ch.AckAsync(msg.DeliveryTag);
            break;
        }

        received.Should().Be("hello");
    }
}
