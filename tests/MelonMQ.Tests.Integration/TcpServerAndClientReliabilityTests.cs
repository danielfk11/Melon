using FluentAssertions;
using MelonMQ.Client;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace MelonMQ.Tests.Integration;

public class TcpServerAndClientReliabilityTests
{
    [Fact]
    public async Task ConcurrentConsumers_ShouldProcessAllMessagesExactlyOnce()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        await using var host = new TestBrokerHost(dataDir);
        await host.StartAsync();

        await using var connA = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
        await using var connB = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");

        await using var channelA = await connA.CreateChannelAsync();
        await using var channelB = await connB.CreateChannelAsync();

        const string queue = "concurrent-consumers";
        const int totalMessages = 40;

        await channelA.DeclareQueueAsync(queue, durable: false);
        for (var i = 0; i < totalMessages; i++)
        {
            await channelA.PublishAsync(queue, Encoding.UTF8.GetBytes($"msg-{i}"));
        }

        var seen = new ConcurrentDictionary<string, byte>();
        var duplicates = 0;
        var consumed = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        Task WorkerAsync(MelonChannel channel) => Task.Run(async () =>
        {
            try
            {
                await foreach (var message in channel.ConsumeAsync(queue, prefetch: 5, cancellationToken: cts.Token))
                {
                    var body = Encoding.UTF8.GetString(message.Body.Span);
                    if (!seen.TryAdd(body, 0))
                    {
                        Interlocked.Increment(ref duplicates);
                    }

                    await channel.AckAsync(message.DeliveryTag);

                    if (Interlocked.Increment(ref consumed) >= totalMessages)
                    {
                        cts.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when all target messages were consumed.
            }
        }, cts.Token);

        await Task.WhenAll(WorkerAsync(channelA), WorkerAsync(channelB));

        duplicates.Should().Be(0);
        seen.Count.Should().Be(totalMessages);
        consumed.Should().Be(totalMessages);
    }

    [Fact]
    public async Task AckFromDifferentConnection_ShouldBeRejected()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        await using var host = new TestBrokerHost(dataDir);
        await host.StartAsync();

        await using var connA = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
        await using var connB = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");

        await using var channelA = await connA.CreateChannelAsync();
        await using var channelB = await connB.CreateChannelAsync();

        const string queue = "ownership-queue";
        await channelA.DeclareQueueAsync(queue, durable: false);
        await channelA.PublishAsync(queue, Encoding.UTF8.GetBytes("payload"));

        var consumed = await ConsumeSingleAsync(channelA, queue, TimeSpan.FromSeconds(4));
        consumed.Should().NotBeNull();

        var wrongAck = async () => await channelB.AckAsync(consumed!.DeliveryTag);
        await wrongAck.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Delivery tag does not belong to this connection*");

        await channelA.AckAsync(consumed.DeliveryTag);
    }

    [Fact]
    public async Task UnackedMessage_ShouldBeRequeued_WhenConsumerDisconnects()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        await using var host = new TestBrokerHost(dataDir);
        await host.StartAsync();

        const string queue = "requeue-on-disconnect";

        await using var publisherConn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
        await using var publisherChannel = await publisherConn.CreateChannelAsync();
        await publisherChannel.DeclareQueueAsync(queue, durable: false);
        await publisherChannel.PublishAsync(queue, Encoding.UTF8.GetBytes("will-redeliver"));

        var consumerConn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
        await using var consumerChannel = await consumerConn.CreateChannelAsync();

        var firstDelivery = await ConsumeSingleAsync(consumerChannel, queue, TimeSpan.FromSeconds(4));
        firstDelivery.Should().NotBeNull();

        var firstMessageId = firstDelivery!.MessageId;
        await consumerConn.DisposeAsync(); // disconnect without ack

        await Task.Delay(250);

        await using var recoveryConn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
        await using var recoveryChannel = await recoveryConn.CreateChannelAsync();

        var redelivery = await ConsumeSingleAsync(recoveryChannel, queue, TimeSpan.FromSeconds(4));
        redelivery.Should().NotBeNull();
        redelivery!.MessageId.Should().Be(firstMessageId);
        redelivery.Redelivered.Should().BeTrue();
        await recoveryChannel.AckAsync(redelivery.DeliveryTag);
    }

    [Fact]
    public async Task AckedMessage_ShouldNotReappear_AfterBrokerRestart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        var queueName = "durable-ack-restart";

        await using (var host = new TestBrokerHost(dataDir))
        {
            await host.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            await channel.PublishAsync(queueName, Encoding.UTF8.GetBytes("first"), persistent: true);

            var message = await ConsumeSingleAsync(channel, queueName, TimeSpan.FromSeconds(4));
            message.Should().NotBeNull();
            await channel.AckAsync(message!.DeliveryTag);
        }

        await using (var restarted = new TestBrokerHost(dataDir))
        {
            await restarted.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{restarted.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            var replayed = await ConsumeSingleAsync(channel, queueName, TimeSpan.FromSeconds(1));
            replayed.Should().BeNull("acked messages must be tombstoned durably across restarts");
        }
    }

    [Fact]
    public async Task PurgedDurableQueue_ShouldStayEmpty_AfterBrokerRestart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        var queueName = "durable-purge-restart";

        await using (var host = new TestBrokerHost(dataDir))
        {
            await host.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);
            await channel.PublishAsync(queueName, Encoding.UTF8.GetBytes("to-be-purged"), persistent: true);

            var queue = host.QueueManager.GetQueue(queueName);
            queue.Should().NotBeNull();
            await queue!.PurgeAsync();
        }

        await using (var restarted = new TestBrokerHost(dataDir))
        {
            await restarted.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{restarted.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true);

            var replayed = await ConsumeSingleAsync(channel, queueName, TimeSpan.FromSeconds(1));
            replayed.Should().BeNull("purge should persist and prevent replay after restart");
        }
    }

    [Fact]
    public async Task ExactlyOnceQueue_ShouldSuppressDuplicatePublish_AfterRestart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        var queueName = "exactly-once-restart";
        var messageId = Guid.NewGuid();

        await using (var host = new TestBrokerHost(dataDir))
        {
            await host.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true, exactlyOnce: true);

            await channel.PublishAsync(queueName, Encoding.UTF8.GetBytes("first"), persistent: true, messageId: messageId);

            var message = await ConsumeSingleAsync(channel, queueName, TimeSpan.FromSeconds(4));
            message.Should().NotBeNull();
            await channel.AckAsync(message!.DeliveryTag);
        }

        await using (var restarted = new TestBrokerHost(dataDir))
        {
            await restarted.StartAsync();

            await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{restarted.TcpPort}");
            await using var channel = await conn.CreateChannelAsync();
            await channel.DeclareQueueAsync(queueName, durable: true, exactlyOnce: true);

            await channel.PublishAsync(queueName, Encoding.UTF8.GetBytes("duplicate"), persistent: true, messageId: messageId);

            var replayed = await ConsumeSingleAsync(channel, queueName, TimeSpan.FromSeconds(1));
            replayed.Should().BeNull("exactly-once queue should reject duplicate messageIds even after restart");
        }
    }

    [Fact]
    public async Task ConnectAsync_ShouldFailFast_WhenBrokerIsUnavailable()
    {
        var unusedPort = GetUnusedPort();

        var connect = async () =>
        {
            await MelonConnection.ConnectAsync(
                $"melon://127.0.0.1:{unusedPort}",
                new MelonConnectionOptions
                {
                    RetryPolicy = new ConnectionRetryPolicy
                    {
                        EnableRetry = false
                    }
                });
        };

        await connect.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task RequestAfterBrokerStop_ShouldFailWithConnectionError()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "melonmq-tests", Guid.NewGuid().ToString("N"));
        await using var host = new TestBrokerHost(dataDir);
        await host.StartAsync();

        await using var conn = await MelonConnection.ConnectAsync($"melon://127.0.0.1:{host.TcpPort}");
        await using var channel = await conn.CreateChannelAsync();

        await host.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var act = async () => await channel.DeclareQueueAsync("after-stop", cancellationToken: cts.Token);

        var ex = await Assert.ThrowsAnyAsync<Exception>(act);
        (ex is InvalidOperationException || ex is IOException || ex is OperationCanceledException || ex is SocketException)
            .Should().BeTrue();
    }

    private static async Task<IncomingMessage?> ConsumeSingleAsync(MelonChannel channel, string queue, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await using var enumerator = channel.ConsumeAsync(queue, prefetch: 1, cancellationToken: cts.Token).GetAsyncEnumerator();

        try
        {
            if (await enumerator.MoveNextAsync())
            {
                return enumerator.Current;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout with no messages available.
        }

        return null;
    }

    private static int GetUnusedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
