using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using MelonMQ.Client;
using System.Text;

namespace MelonMQ.Tests.Performance;

public class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--simple")
        {
            await RunSimplePerformanceTest();
        }
        else
        {
            Console.WriteLine("Running BenchmarkDotNet...");
            Console.WriteLine("Make sure MelonMQ broker is running: dotnet run --project src/MelonMQ.Broker");
            BenchmarkRunner.Run<MelonMQBenchmarks>();
        }
    }

    public static async Task RunSimplePerformanceTest()
    {
        var brokerUri = Environment.GetEnvironmentVariable("MELONMQ_BENCHMARK_URI") ?? "melon://localhost:5672";
        var queueName = Environment.GetEnvironmentVariable("MELONMQ_BENCHMARK_QUEUE") ?? "perf-test";
        var messageCount = int.TryParse(Environment.GetEnvironmentVariable("MELONMQ_BENCHMARK_MESSAGES"), out var configuredCount)
            ? configuredCount
            : 1000;
        var payloadBytes = int.TryParse(Environment.GetEnvironmentVariable("MELONMQ_BENCHMARK_PAYLOAD_BYTES"), out var configuredPayloadBytes)
            ? configuredPayloadBytes
            : 32;
        payloadBytes = Math.Max(1, payloadBytes);
        messageCount = Math.Max(1, messageCount);

        Console.WriteLine("=== MelonMQ Simple Performance Test ===");
        Console.WriteLine($"broker_uri={brokerUri}");
        Console.WriteLine($"queue={queueName}");
        Console.WriteLine($"messages={messageCount}");
        Console.WriteLine($"payload_bytes={payloadBytes}");
        Console.WriteLine();
        
        try
        {
            using var connection = await MelonConnection.ConnectAsync(brokerUri);
            using var channel = await connection.CreateChannelAsync();
            
            await channel.DeclareQueueAsync(queueName);
            
            var message = Encoding.UTF8.GetBytes(new string('x', payloadBytes));
            
            // Publish test
            Console.WriteLine($"Publishing {messageCount} messages...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            for (int i = 0; i < messageCount; i++)
            {
                await channel.PublishAsync(queueName, message);
            }
            
            sw.Stop();
            var publishRate = messageCount / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"published_messages={messageCount}");
            Console.WriteLine($"publish_elapsed_ms={sw.ElapsedMilliseconds}");
            Console.WriteLine($"publish_rate_msg_per_sec={publishRate:F0}");
            Console.WriteLine();
            
            // Consume test
            Console.WriteLine($"Consuming {messageCount} messages...");
            sw.Restart();
            int consumedCount = 0;
            
            await foreach (var msg in channel.ConsumeAsync(queueName))
            {
                await channel.AckAsync(msg.DeliveryTag);
                consumedCount++;
                
                if (consumedCount >= messageCount)
                    break;
            }
            
            sw.Stop();
            var consumeRate = consumedCount / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"consumed_messages={consumedCount}");
            Console.WriteLine($"consume_elapsed_ms={sw.ElapsedMilliseconds}");
            Console.WriteLine($"consume_rate_msg_per_sec={consumeRate:F0}");
            Console.WriteLine();
            Console.WriteLine("=== Performance Test Complete ===");
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"benchmark_error={ex.Message}");
            Environment.ExitCode = 1;
        }
    }
}

[MemoryDiagnoser]
[SimpleJob]
public class MelonMQBenchmarks
{
    private MelonConnection? _connection;
    private MelonChannel? _channel;
    private readonly byte[] _testMessage = Encoding.UTF8.GetBytes("Benchmark test message");

    [GlobalSetup]
    public async Task Setup()
    {
        _connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
        _channel = await _connection.CreateChannelAsync();
        await _channel.DeclareQueueAsync("benchmark-queue");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }

    [Benchmark]
    public async Task PublishMessage()
    {
        await _channel!.PublishAsync("benchmark-queue", _testMessage);
    }
}
