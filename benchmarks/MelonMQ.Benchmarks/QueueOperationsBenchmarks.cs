using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MelonMQ.Broker.Core;
using MelonMQ.Common;

namespace MelonMQ.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class QueueOperationsBenchmarks
{
    private Queue _queue;
    private Consumer _consumer;
    private QueuedMessage[] _messages;

    [GlobalSetup]
    public void Setup()
    {
        _queue = new Queue("benchmark-queue", durable: false, exclusive: false, autoDelete: false);
        _consumer = new Consumer
        {
            ConsumerTag = "benchmark-consumer",
            Prefetch = 1000,
            NoAck = false,
            Exclusive = false
        };

        // Pre-create messages for benchmarking
        _messages = new QueuedMessage[1000];
        for (int i = 0; i < _messages.Length; i++)
        {
            _messages[i] = new QueuedMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                Exchange = "benchmark-exchange",
                RoutingKey = $"benchmark.key.{i}",
                Body = BinaryData.FromString($"Benchmark message {i}"),
                Properties = new MessageProperties
                {
                    Priority = (byte)(i % 10),
                    MessageId = Guid.NewGuid().ToString()
                },
                Priority = (byte)(i % 10),
                Timestamp = DateTime.UtcNow,
                DeliveryTag = 0
            };
        }
    }

    [Benchmark]
    public async Task EnqueueSingleMessage()
    {
        await _queue.EnqueueAsync(_messages[0]);
        await _queue.PurgeAsync(); // Clean up for next iteration
    }

    [Benchmark]
    public async Task EnqueueMultipleMessages()
    {
        for (int i = 0; i < 100; i++)
        {
            await _queue.EnqueueAsync(_messages[i]);
        }
        await _queue.PurgeAsync(); // Clean up for next iteration
    }

    [Benchmark]
    public async Task EnqueueWithPriorities()
    {
        // Enqueue messages with different priorities
        for (int i = 0; i < 100; i++)
        {
            var message = _messages[i];
            message.Priority = (byte)(i % 10); // Vary priorities
            await _queue.EnqueueAsync(message);
        }
        await _queue.PurgeAsync(); // Clean up for next iteration
    }

    [Benchmark]
    public async Task DequeueMessages()
    {
        // Setup: Enqueue messages first
        for (int i = 0; i < 100; i++)
        {
            await _queue.EnqueueAsync(_messages[i]);
        }

        await _queue.AddConsumerAsync(_consumer);

        // Benchmark: Dequeue all messages
        for (int i = 0; i < 100; i++)
        {
            await _queue.TryDequeueAsync(_consumer.ConsumerTag, 100);
        }

        await _queue.RemoveConsumerAsync(_consumer.ConsumerTag);
    }

    [Benchmark]
    public async Task EnqueueDequeueRoundTrip()
    {
        await _queue.AddConsumerAsync(_consumer);

        for (int i = 0; i < 100; i++)
        {
            await _queue.EnqueueAsync(_messages[i]);
            var message = await _queue.TryDequeueAsync(_consumer.ConsumerTag, 100);
            if (message != null)
            {
                await _queue.AckMessageAsync(message.DeliveryTag);
            }
        }

        await _queue.RemoveConsumerAsync(_consumer.ConsumerTag);
    }

    [Benchmark]
    public async Task AcknowledgeMessages()
    {
        // Setup: Enqueue and dequeue messages
        for (int i = 0; i < 100; i++)
        {
            await _queue.EnqueueAsync(_messages[i]);
        }

        await _queue.AddConsumerAsync(_consumer);
        var deliveryTags = new List<ulong>();

        for (int i = 0; i < 100; i++)
        {
            var message = await _queue.TryDequeueAsync(_consumer.ConsumerTag, 100);
            if (message != null)
            {
                deliveryTags.Add(message.DeliveryTag);
            }
        }

        // Benchmark: Acknowledge all messages
        foreach (var deliveryTag in deliveryTags)
        {
            await _queue.AckMessageAsync(deliveryTag);
        }

        await _queue.RemoveConsumerAsync(_consumer.ConsumerTag);
    }

    [Benchmark]
    public async Task PurgeQueue()
    {
        // Setup: Add many messages
        for (int i = 0; i < 1000; i++)
        {
            await _queue.EnqueueAsync(_messages[i]);
        }

        // Benchmark: Purge all messages
        await _queue.PurgeAsync();
    }

    [Benchmark]
    public async Task AddRemoveConsumer()
    {
        for (int i = 0; i < 100; i++)
        {
            var consumer = new Consumer
            {
                ConsumerTag = $"consumer-{i}",
                Prefetch = 100,
                NoAck = false,
                Exclusive = false
            };

            await _queue.AddConsumerAsync(consumer);
            await _queue.RemoveConsumerAsync(consumer.ConsumerTag);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _queue.PurgeAsync();
        _queue.Dispose();
    }
}