using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MelonMQ.Client.Protocol;
using MelonMQ.Protocol;

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

    public async IAsyncEnumerable<IncomingMessage> ConsumeAsync(string queue, int prefetch = 100, string? group = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The broker returns a logical delivery key that includes the group name
        // (e.g. "orders.created::grp:workers") so multiple groups on the same
        // queue/connection get separate delivery channels.
        var consumerKey = !string.IsNullOrEmpty(group)
            ? $"{queue}::grp:{group}"
            : queue;

        // Ensure delivery channel exists before subscription so early deliveries are not dropped.
        var deliveryChannel = _connection.GetOrCreateDeliveryChannel(consumerKey);

        // Set prefetch
        await SetPrefetchAsync(prefetch, cancellationToken);

        // Subscribe to queue
        var subscribePayload = new { queue = queue, group = group };
        var response = await _connection.SendRequestAsync(MessageType.ConsumeSubscribe, subscribePayload, cancellationToken);
        
        if (response.Type == MessageType.Error)
        {
            var errorMessage = response.Payload?.GetProperty("message").GetString() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to subscribe to queue: {errorMessage}");
        }

        // Read from delivery channel
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

        if (response.Payload?.TryGetProperty("success", out var successElement) == true && !successElement.GetBoolean())
        {
            throw new InvalidOperationException("ACK was rejected by the broker.");
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

        if (response.Payload?.TryGetProperty("success", out var successElement) == true && !successElement.GetBoolean())
        {
            throw new InvalidOperationException("NACK was rejected by the broker.");
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

    // ── Exchanges ─────────────────────────────────────────────────────────────

    /// <summary>Declares an exchange. Type must be "direct", "fanout", or "topic".</summary>
    public async Task DeclareExchangeAsync(string exchange, string type = "direct", bool durable = false, CancellationToken cancellationToken = default)
    {
        var payload = new { exchange, type, durable };
        var response = await _connection.SendRequestAsync(MessageType.DeclareExchange, payload, cancellationToken);
        if (response.Type == MessageType.Error)
            throw new InvalidOperationException(response.Payload?.GetProperty("message").GetString() ?? "Failed to declare exchange");
    }

    /// <summary>Binds a queue to an exchange with the given routing key.</summary>
    public async Task BindQueueAsync(string exchange, string queue, string routingKey, CancellationToken cancellationToken = default)
    {
        var payload = new { exchange, queue, routingKey };
        var response = await _connection.SendRequestAsync(MessageType.BindQueue, payload, cancellationToken);
        if (response.Type == MessageType.Error)
            throw new InvalidOperationException(response.Payload?.GetProperty("message").GetString() ?? "Failed to bind queue");
    }

    /// <summary>Removes a binding between a queue and an exchange.</summary>
    public async Task UnbindQueueAsync(string exchange, string queue, string routingKey, CancellationToken cancellationToken = default)
    {
        var payload = new { exchange, queue, routingKey };
        var response = await _connection.SendRequestAsync(MessageType.UnbindQueue, payload, cancellationToken);
        if (response.Type == MessageType.Error)
            throw new InvalidOperationException(response.Payload?.GetProperty("message").GetString() ?? "Failed to unbind queue");
    }

    /// <summary>
    /// Publishes a message to an exchange. The broker routes it to bound queues
    /// according to the exchange type and routing key.
    /// </summary>
    public async Task PublishToExchangeAsync(
        string exchange,
        string routingKey,
        ReadOnlyMemory<byte> body,
        bool persistent = false,
        int? ttlMs = null,
        Guid? messageId = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            exchange,
            routingKey,
            bodyBase64 = Convert.ToBase64String(body.Span),
            persistent,
            ttlMs,
            messageId = messageId ?? Guid.NewGuid()
        };
        var response = await _connection.SendRequestAsync(MessageType.Publish, payload, cancellationToken);
        if (response.Type == MessageType.Error)
            throw new InvalidOperationException(response.Payload?.GetProperty("message").GetString() ?? "Failed to publish to exchange");
    }

    // ── Stream queues ─────────────────────────────────────────────────────────

    /// <summary>Declares a stream queue (messages are retained after ACK).</summary>
    public async Task DeclareStreamQueueAsync(
        string name,
        bool durable = true,
        int maxMessages = 100_000,
        long? maxAgeMs = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            queue = name,
            durable,
            mode = "stream",
            streamMaxLengthMessages = maxMessages,
            streamMaxAgeMs = maxAgeMs
        };
        var response = await _connection.SendRequestAsync(MessageType.DeclareQueue, payload, cancellationToken);
        if (response.Type == MessageType.Error)
            throw new InvalidOperationException(response.Payload?.GetProperty("message").GetString() ?? "Failed to declare stream queue");
    }

    /// <summary>
    /// Subscribes to a stream queue, yielding messages starting from the specified offset.
    /// <paramref name="startOffset"/>: -1 = latest (default), 0 = beginning, N = from offset N.
    /// <paramref name="group"/>: share offset with other consumers in the same group.
    /// </summary>
    public async IAsyncEnumerable<IncomingMessage> ConsumeStreamAsync(
        string queue,
        long startOffset = -1,
        string? group = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var deliveryChannel = _connection.GetOrCreateDeliveryChannel(queue);

        var subscribePayload = new { queue, group, offset = startOffset };
        var response = await _connection.SendRequestAsync(MessageType.ConsumeSubscribe, subscribePayload, cancellationToken);
        if (response.Type == MessageType.Error)
            throw new InvalidOperationException(response.Payload?.GetProperty("message").GetString() ?? "Failed to subscribe to stream");

        await foreach (var message in deliveryChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Commits the stream offset for this consumer (or consumer group).
    /// Should be called after successfully processing a stream message.
    /// </summary>
    public async Task StreamAckAsync(string queue, long offset, string? group = null, CancellationToken cancellationToken = default)
    {
        var payload = new { queue, offset, group };
        var response = await _connection.SendRequestAsync(MessageType.StreamAck, payload, cancellationToken);
        if (response.Type == MessageType.Error)
            throw new InvalidOperationException(response.Payload?.GetProperty("message").GetString() ?? "Stream ACK failed");
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