using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MelonMQ.Broker;
using MelonMQ.Client;
using MelonMQ.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace MelonMQ.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class EndToEndBenchmarks
{
    private IHost _broker;
    private IMelonConnection _connection;
    private IMelonChannel _channel;
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"melonmq-benchmark-{Guid.NewGuid()}");
    private readonly int _brokerPort = GetAvailablePort();

    [GlobalSetup]
    public async Task Setup()
    {
        Directory.CreateDirectory(_tempDirectory);

        // Start broker
        var builder = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<BrokerService>();
                services.Configure<BrokerConfiguration>(config =>
                {
                    config.Host = "localhost";
                    config.Port = _brokerPort;
                    config.DataDirectory = _tempDirectory;
                    config.MaxConnections = 1000;
                    config.HeartbeatInterval = TimeSpan.FromSeconds(30);
                });
            })
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
            });

        _broker = builder.Build();
        await _broker.StartAsync();

        // Give broker time to start
        await Task.Delay(1000);

        // Create connection and channel
        _connection = await MelonConnection.ConnectAsync($"melon://guest:guest@localhost:{_brokerPort}");
        _channel = await _connection.CreateChannelAsync();

        // Setup test topology
        await _channel.DeclareExchangeAsync("benchmark-exchange", ExchangeType.Direct, durable: false);
        await _channel.DeclareQueueAsync("benchmark-queue", durable: false, exclusive: false, autoDelete: true);
        await _channel.BindQueueAsync("benchmark-queue", "benchmark-exchange", "benchmark.key");
    }

    [Benchmark]
    public async Task PublishSingleMessage()
    {
        var message = BinaryData.FromString("Benchmark message");
        var properties = new MessageProperties();
        
        await _channel.PublishAsync("benchmark-exchange", "benchmark.key", message, properties, persistent: false, priority: 0);
    }

    [Benchmark]
    public async Task PublishMultipleMessages()
    {
        var tasks = new List<Task>();
        
        for (int i = 0; i < 100; i++)
        {
            var message = BinaryData.FromString($"Benchmark message {i}");
            var properties = new MessageProperties();
            
            tasks.Add(_channel.PublishAsync("benchmark-exchange", "benchmark.key", message, properties, persistent: false, priority: 0));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task PublishWithDifferentPriorities()
    {
        var tasks = new List<Task>();
        
        for (int i = 0; i < 100; i++)
        {
            var message = BinaryData.FromString($"Priority message {i}");
            var properties = new MessageProperties { Priority = (byte)(i % 10) };
            var priority = (byte)(i % 10);
            
            tasks.Add(_channel.PublishAsync("benchmark-exchange", "benchmark.key", message, properties, persistent: false, priority: priority));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task PublishAndConsumeSingleMessage()
    {
        var message = BinaryData.FromString("Roundtrip message");
        var properties = new MessageProperties();
        
        // Publish
        await _channel.PublishAsync("benchmark-exchange", "benchmark.key", message, properties, persistent: false, priority: 0);
        
        // Consume
        await foreach (var delivery in _channel.ConsumeAsync("benchmark-queue", consumerTag: "benchmark-consumer", prefetch: 1, noAck: false, CancellationToken.None))
        {
            await delivery.AckAsync();
            break; // Only consume one message
        }
    }

    [Benchmark]
    public async Task PublishAndConsumeMultipleMessages()
    {
        const int messageCount = 100;
        
        // Publish messages
        var publishTasks = new List<Task>();
        for (int i = 0; i < messageCount; i++)
        {
            var msg = BinaryData.FromString($"Batch message {i}");
            var props = new MessageProperties();
            publishTasks.Add(_channel.PublishAsync("benchmark-exchange", "benchmark.key", msg, props, persistent: false, priority: 0));
        }
        await Task.WhenAll(publishTasks);

        // Consume messages
        int consumedCount = 0;
        await foreach (var delivery in _channel.ConsumeAsync("benchmark-queue", consumerTag: "batch-consumer", prefetch: 100, noAck: false, CancellationToken.None))
        {
            await delivery.AckAsync();
            consumedCount++;
            if (consumedCount >= messageCount) break;
        }
    }

    [Benchmark]
    public async Task ConcurrentPublishers()
    {
        const int publisherCount = 10;
        const int messagesPerPublisher = 10;
        
        var tasks = new List<Task>();
        
        for (int p = 0; p < publisherCount; p++)
        {
            var publisherId = p;
            tasks.Add(Task.Run(async () =>
            {
                for (int m = 0; m < messagesPerPublisher; m++)
                {
                    var message = BinaryData.FromString($"Concurrent message from publisher {publisherId}, message {m}");
                    var properties = new MessageProperties();
                    await _channel.PublishAsync("benchmark-exchange", "benchmark.key", message, properties, persistent: false, priority: 0);
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task HighThroughputPublish()
    {
        const int messageCount = 1000;
        var message = BinaryData.FromString("High throughput message");
        var properties = new MessageProperties();
        
        var tasks = new List<Task>();
        for (int i = 0; i < messageCount; i++)
        {
            tasks.Add(_channel.PublishAsync("benchmark-exchange", "benchmark.key", message, properties, persistent: false, priority: 0));
        }

        await Task.WhenAll(tasks);
    }

    [Benchmark]
    public async Task CreateAndDisposeChannel()
    {
        for (int i = 0; i < 10; i++)
        {
            using var tempChannel = await _connection.CreateChannelAsync();
            // Channel is automatically disposed
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        
        if (_broker != null)
        {
            await _broker.StopAsync();
            _broker.Dispose();
        }

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}