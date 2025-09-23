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
        Console.WriteLine("=== MelonMQ Simple Performance Test ===");
        Console.WriteLine("NOTE: Make sure broker is running: dotnet run --project src/MelonMQ.Broker");
        Console.WriteLine();
        
        try
        {
            using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
            using var channel = await connection.CreateChannelAsync();
            
            await channel.DeclareQueueAsync("perf-test");
            
            const int messageCount = 1000;
            var message = Encoding.UTF8.GetBytes("Simple performance test message");
            
            // Publish test
            Console.WriteLine($"Publishing {messageCount} messages...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            for (int i = 0; i < messageCount; i++)
            {
                await channel.PublishAsync("perf-test", message);
            }
            
            sw.Stop();
            var publishRate = messageCount / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"✅ Published {messageCount} messages in {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"✅ Publish rate: {publishRate:F0} msg/sec");
            Console.WriteLine();
            
            // Consume test
            Console.WriteLine($"Consuming {messageCount} messages...");
            sw.Restart();
            int consumedCount = 0;
            
            await foreach (var msg in channel.ConsumeAsync("perf-test"))
            {
                await channel.AckAsync(msg.DeliveryTag);
                consumedCount++;
                
                if (consumedCount >= messageCount)
                    break;
            }
            
            sw.Stop();
            var consumeRate = consumedCount / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"✅ Consumed {consumedCount} messages in {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"✅ Consume rate: {consumeRate:F0} msg/sec");
            Console.WriteLine();
            Console.WriteLine("=== Performance Test Complete ===");
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Performance test failed: {ex.Message}");
            Console.WriteLine("Make sure MelonMQ broker is running on localhost:5672");
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