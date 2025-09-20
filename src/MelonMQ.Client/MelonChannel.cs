using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MelonMQ.Client.Protocol;

namespace MelonMQ.Client;

public class MelonChannel : IDisposable, IAsyncDisposable
{
    private readonly MelonConnection _connection;
    private bool _disposed;

    internal MelonChannel(MelonConnection connection)
    {
        _connection = connection;
    }

    public async Task DeclareQueueAsync(string name, bool durable = false, string? dlq = null, int? defaultTtlMs = null, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            queue = name,
            durable = durable,
            deadLetterQueue = dlq,
            defaultTtlMs = defaultTtlMs
        };

        var response = await _connection.SendRequestAsync(MessageType.DeclareQueue, payload, cancellationToken);
        
        if (response.Type == MessageType.Error)
        {
            var errorMessage = response.Payload?.GetProperty("message").GetString() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to declare queue: {errorMessage}");
        }
    }

    public async Task PublishAsync(string queue, ReadOnlyMemory<byte> body, bool persistent = false, int? ttlMs = null, Guid? messageId = null, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            queue = queue,
            bodyBase64 = Convert.ToBase64String(body.Span),
            ttlMs = ttlMs,
            persistent = persistent,
            messageId = messageId ?? Guid.NewGuid()
        };

        var response = await _connection.SendRequestAsync(MessageType.Publish, payload, cancellationToken);
        
        if (response.Type == MessageType.Error)
        {
            var errorMessage = response.Payload?.GetProperty("message").GetString() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to publish message: {errorMessage}");
        }
    }

    public async IAsyncEnumerable<IncomingMessage> ConsumeAsync(string queue, int prefetch = 100, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Set prefetch
        await SetPrefetchAsync(prefetch, cancellationToken);

        // Subscribe to queue
        var subscribePayload = new { queue = queue };
        var response = await _connection.SendRequestAsync(MessageType.ConsumeSubscribe, subscribePayload, cancellationToken);
        
        if (response.Type == MessageType.Error)
        {
            var errorMessage = response.Payload?.GetProperty("message").GetString() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to subscribe to queue: {errorMessage}");
        }

        // Get delivery channel and read from it
        var deliveryChannel = _connection.GetOrCreateDeliveryChannel(queue);
        
        await foreach (var message in deliveryChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    public async Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken = default)
    {
        var payload = new { deliveryTag = deliveryTag };
        var response = await _connection.SendRequestAsync(MessageType.Ack, payload, cancellationToken);
        
        if (response.Type == MessageType.Error)
        {
            var errorMessage = response.Payload?.GetProperty("message").GetString() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to ack message: {errorMessage}");
        }
    }

    public async Task NackAsync(ulong deliveryTag, bool requeue = true, CancellationToken cancellationToken = default)
    {
        var payload = new { deliveryTag = deliveryTag, requeue = requeue };
        var response = await _connection.SendRequestAsync(MessageType.Nack, payload, cancellationToken);
        
        if (response.Type == MessageType.Error)
        {
            var errorMessage = response.Payload?.GetProperty("message").GetString() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to nack message: {errorMessage}");
        }
    }

    public async Task SetPrefetchAsync(int prefetch, CancellationToken cancellationToken = default)
    {
        var payload = new { prefetch = prefetch };
        var response = await _connection.SendRequestAsync(MessageType.SetPrefetch, payload, cancellationToken);
        
        if (response.Type == MessageType.Error)
        {
            var errorMessage = response.Payload?.GetProperty("message").GetString() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to set prefetch: {errorMessage}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}