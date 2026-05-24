using FluentAssertions;
using MelonMQ.Client;
using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace MelonMQ.Tests.Integration;

public class StreamQueueIntegrationTests : IAsyncLifetime
{
    private readonly TestBrokerHost _host;

    public StreamQueueIntegrationTests()
    {
        _host = new TestBrokerHost(Path.Combine(Path.GetTempPath(), $"melon-test-stream-{Guid.NewGuid():N}"));
    }

    public Task InitializeAsync() => _host.StartAsync();
    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private Task<MelonConnection> Connect() =>
        MelonConnection.ConnectAsync($"melon://127.0.0.1:{_host.TcpPort}");

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Str(ReadOnlyMemory<byte> b) => Encoding.UTF8.GetString(b.Span);

    // ── helper: collect N stream messages ─────────────────────────────────────
    private static async Task<List<IncomingMessage>> CollectN(
        MelonChannel ch, string queue, int n,
        long startOffset = 0, string? group = null, int timeoutMs = 5000)
    {
        var results = new List<IncomingMessage>();
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var msg in ch.ConsumeStreamAsync(queue, startOffset, group, cts.Token))
        {
            results.Add(msg);
            if (results.Count >= n) break;
        }
        return results;
    }

    // ── basic appended / replay ────────────────────────────────────────────────

    [Fact]
    public async Task Stream_RetainsMessagesAfterConsumption()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareStreamQueueAsync("stream.events");

        // Publish 3 messages
        await ch.PublishAsync("stream.events", Bytes("event-0"));
        await ch.PublishAsync("stream.events", Bytes("event-1"));
        await ch.PublishAsync("stream.events", Bytes("event-2"));

        // First consumer reads from beginning
        var first = await CollectN(ch, "stream.events", 3, startOffset: 0);
        first.Select(m => Str(m.Body))
             .Should().Equal("event-0", "event-1", "event-2");

        // Second consumer also reads from beginning — messages must still be there
        using var conn2 = await Connect();
        using var ch2 = await conn2.CreateChannelAsync();
        var second = await CollectN(ch2, "stream.events", 3, startOffset: 0);
        second.Select(m => Str(m.Body))
              .Should().Equal(["event-0", "event-1", "event-2"],
                because: "stream messages are retained and can be replayed");
    }

    [Fact]
    public async Task Stream_OffsetZero_ReadsFromBeginning()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareStreamQueueAsync("stream.offset0");

        await ch.PublishAsync("stream.offset0", Bytes("first"));
        await ch.PublishAsync("stream.offset0", Bytes("second"));

        var msgs = await CollectN(ch, "stream.offset0", 2, startOffset: 0);
        msgs[0].Offset.Should().Be(0);
        msgs[1].Offset.Should().Be(1);
        Str(msgs[0].Body).Should().Be("first");
        Str(msgs[1].Body).Should().Be("second");
    }

    [Fact]
    public async Task Stream_OffsetMinus1_ReceivesOnlyNewMessages()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareStreamQueueAsync("stream.latest");

        // Publish 2 "old" messages before subscribing
        await ch.PublishAsync("stream.latest", Bytes("old-0"));
        await ch.PublishAsync("stream.latest", Bytes("old-1"));

        // Subscribe at latest (-1) — these old messages should NOT be received
        using var cts = new CancellationTokenSource(5000);
        var received = new List<string>();

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var msg in ch.ConsumeStreamAsync("stream.latest", startOffset: -1, cancellationToken: cts.Token))
            {
                received.Add(Str(msg.Body));
                if (received.Count >= 2) break;
            }
        });

        // Wait briefly to ensure subscription is established, then publish new ones
        await Task.Delay(300);
        await ch.PublishAsync("stream.latest", Bytes("new-0"));
        await ch.PublishAsync("stream.latest", Bytes("new-1"));

        await consumeTask;

        received.Should().Equal(["new-0", "new-1"], because: "offset=-1 skips historical messages");
    }

    [Fact]
    public async Task Stream_SpecificOffset_ReadsFromThatPoint()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareStreamQueueAsync("stream.seek");

        for (int i = 0; i < 5; i++)
            await ch.PublishAsync("stream.seek", Bytes($"msg-{i}"));

        // Start from offset 2
        var msgs = await CollectN(ch, "stream.seek", 3, startOffset: 2);

        msgs.Should().HaveCount(3);
        Str(msgs[0].Body).Should().Be("msg-2");
        Str(msgs[1].Body).Should().Be("msg-3");
        Str(msgs[2].Body).Should().Be("msg-4");
    }

    // ── StreamAck persists offset ─────────────────────────────────────────────

    [Fact]
    public async Task StreamAck_CommitsOffset_ResumesContinuesFromCommit()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareStreamQueueAsync("stream.ackresume");

        for (int i = 0; i < 6; i++)
            await ch.PublishAsync("stream.ackresume", Bytes($"item-{i}"));

        // Read first 3 and commit after item-2 (offset=2)
        var batch1 = await CollectN(ch, "stream.ackresume", 3, startOffset: 0, group: "mygroup");
        await ch.StreamAckAsync("stream.ackresume", offset: batch1[^1].Offset!.Value, group: "mygroup");

        // New connection with same group — should resume from offset 3
        using var conn2 = await Connect();
        using var ch2 = await conn2.CreateChannelAsync();
        var batch2 = await CollectN(ch2, "stream.ackresume", 3, group: "mygroup");

        Str(batch2[0].Body).Should().Be("item-3", "group resumes from committed offset");
        Str(batch2[1].Body).Should().Be("item-4");
        Str(batch2[2].Body).Should().Be("item-5");
    }

    [Fact]
    public async Task StreamAck_CommitsOffset_PersistsAcrossBrokerRestart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        const string queueName = "stream.ackrestart";
        const string groupName = "restart-group";

        await using (var host = new TestBrokerHost(dataDir))
        {
            await host.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var ch = await conn.CreateChannelAsync();

            await ch.DeclareStreamQueueAsync(queueName, durable: true);

            for (int i = 0; i < 6; i++)
                await ch.PublishAsync(queueName, Bytes($"item-{i}"), persistent: true);

            var batch1 = await CollectN(ch, queueName, 3, startOffset: 0, group: groupName);
            await ch.StreamAckAsync(queueName, batch1[^1].Offset!.Value, group: groupName);
        }

        await using (var restarted = new TestBrokerHost(dataDir))
        {
            await restarted.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{restarted.TcpPort}");
            await using var ch = await conn.CreateChannelAsync();

            await ch.DeclareStreamQueueAsync(queueName, durable: true);

            var batch2 = await CollectN(ch, queueName, 3, group: groupName);

            Str(batch2[0].Body).Should().Be("item-3", "group should resume from persisted committed offset after restart");
            Str(batch2[1].Body).Should().Be("item-4");
            Str(batch2[2].Body).Should().Be("item-5");
        }
    }

    // ── independent groups get all messages ──────────────────────────────────

    [Fact]
    public async Task Stream_TwoGroups_BothGetAllMessages()
    {
        using var connA = await Connect();
        using var connB = await Connect();
        using var connPub = await Connect();
        using var chA = await connA.CreateChannelAsync();
        using var chB = await connB.CreateChannelAsync();
        using var chPub = await connPub.CreateChannelAsync();

        await chPub.DeclareStreamQueueAsync("stream.multigroup");

        for (int i = 0; i < 4; i++)
            await chPub.PublishAsync("stream.multigroup", Bytes($"evt-{i}"));

        var msgsA = await CollectN(chA, "stream.multigroup", 4, startOffset: 0, group: "groupA");
        var msgsB = await CollectN(chB, "stream.multigroup", 4, startOffset: 0, group: "groupB");

        msgsA.Select(m => Str(m.Body)).Should().Equal("evt-0", "evt-1", "evt-2", "evt-3");
        msgsB.Select(m => Str(m.Body)).Should().Equal(["evt-0", "evt-1", "evt-2", "evt-3"],
            because: "independent groups each receive all messages");
    }

    // ── live streaming (messages published AFTER subscribe) ───────────────────

    [Fact]
    public async Task Stream_ReceivesMessagesPublishedAfterSubscribe()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();

        await ch.DeclareStreamQueueAsync("stream.live");

        using var cts = new CancellationTokenSource(6000);
        var received = new List<string>();

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var msg in ch.ConsumeStreamAsync("stream.live", startOffset: -1, cancellationToken: cts.Token))
            {
                received.Add(Str(msg.Body));
                if (received.Count >= 3) break;
            }
        });

        await Task.Delay(300); // let subscription establish
        await ch.PublishAsync("stream.live", Bytes("live-a"));
        await ch.PublishAsync("stream.live", Bytes("live-b"));
        await ch.PublishAsync("stream.live", Bytes("live-c"));

        await consumeTask;

        received.Should().Equal("live-a", "live-b", "live-c");
    }

    [Fact]
    public async Task Stream_PartitionKey_DistributesConsistently()
    {
        using var conn = await Connect();
        using var ch = await conn.CreateChannelAsync();
        const string queue = "stream.partitioned";

        await ch.DeclareStreamQueueAsync(queue, partitionCount: 4);

        var keys = Enumerable.Range(0, 8).Select(i => $"tenant-{i}").ToArray();
        foreach (var key in keys)
            await ch.PublishAsync(queue, Bytes($"{key}:evt"), partitionKey: key);

        var msgs = await CollectN(ch, queue, keys.Length, startOffset: 0);
        var partitionByKey = new Dictionary<string, int>();
        foreach (var msg in msgs)
        {
            var body = Str(msg.Body);
            var key = body.Split(':')[0];
            msg.Partition.Should().NotBeNull();
            if (!partitionByKey.TryAdd(key, msg.Partition!.Value))
            {
                msg.Partition!.Value.Should().Be(partitionByKey[key], "same partition key must always map to the same partition");
            }
        }

        partitionByKey.Values.Distinct().Count().Should().BeGreaterThan(1, "different keys should spread across partitions");
    }

    [Fact]
    public async Task Stream_GroupRebalance_ResumesOffsets_AndUpdatesLagMetrics()
    {
        using var connPub = await Connect();
        using var conn1 = await Connect();
        using var conn2 = await Connect();
        using var chPub = await connPub.CreateChannelAsync();
        using var ch1 = await conn1.CreateChannelAsync();
        using var ch2 = await conn2.CreateChannelAsync();

        const string queue = "stream.rebalance";
        const string group = "workers";
        await chPub.DeclareStreamQueueAsync(queue, partitionCount: 4);

        for (int i = 0; i < 8; i++)
            await chPub.PublishAsync(queue, Bytes($"warm-{i}"), partition: i % 4);

        var seenOffsets = new ConcurrentDictionary<long, string>();
        var newMessagePartitionsByConsumer = new ConcurrentDictionary<string, ConcurrentBag<int>>();
        var totalNewMessages = 16;
        var consumedNewMessages = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        Task Consume(MelonChannel channel, string consumerName) => Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in channel.ConsumeStreamAsync(queue, startOffset: 0, group: group, cancellationToken: cts.Token))
                {
                    msg.Offset.Should().NotBeNull();
                    seenOffsets.TryAdd(msg.Offset!.Value, consumerName).Should().BeTrue("group consumption should not duplicate offsets when every message is acked");

                    await channel.StreamAckAsync(
                        queue,
                        msg.Offset!.Value,
                        group: group,
                        partition: msg.Partition,
                        partitionOffset: msg.PartitionOffset,
                        cancellationToken: cts.Token);

                    if (Str(msg.Body).StartsWith("new-", StringComparison.Ordinal))
                    {
                        newMessagePartitionsByConsumer
                            .GetOrAdd(consumerName, _ => new ConcurrentBag<int>())
                            .Add(msg.Partition!.Value);

                        if (Interlocked.Increment(ref consumedNewMessages) >= totalNewMessages)
                            cts.Cancel();
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        var consumer1Task = Consume(ch1, "c1");
        await Task.Delay(500);
        var consumer2Task = Consume(ch2, "c2");
        await Task.Delay(500);

        for (int i = 0; i < totalNewMessages; i++)
            await chPub.PublishAsync(queue, Bytes($"new-{i}"), partition: i % 4);

        await Task.WhenAll(consumer1Task, consumer2Task);

        newMessagePartitionsByConsumer.ContainsKey("c1").Should().BeTrue();
        newMessagePartitionsByConsumer.ContainsKey("c2").Should().BeTrue("rebalance should assign at least one partition to the second consumer");

        var allPostRebalancePartitions = newMessagePartitionsByConsumer
            .SelectMany(kvp => kvp.Value.Select(p => (Consumer: kvp.Key, Partition: p)))
            .ToList();
        allPostRebalancePartitions.Should().NotBeEmpty();
        allPostRebalancePartitions.Select(x => x.Partition).Distinct().Should().OnlyContain(p => p >= 0 && p < 4);

        await Task.Delay(300);
        var lagByGroupPartition = _host.Metrics.GetStreamGroupPartitionLagSnapshot()
            .Where(kvp => kvp.Key.StartsWith($"{queue}|{group}|", StringComparison.Ordinal))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        lagByGroupPartition.Should().HaveCount(4);
        lagByGroupPartition.Values.Should().OnlyContain(v => v == 0);
    }
}
