using MelonMQ.Client;

Console.WriteLine("MelonMQ Consumer Sample");
Console.WriteLine("=======================");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nShutting down gracefully...");
};

try
{
    using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
    using var channel = await connection.CreateChannelAsync();

    // Declare queue (idempotent)
    await channel.DeclareQueueAsync("test-queue", durable: true, dlq: "test-queue.dlq");
    Console.WriteLine("Queue 'test-queue' declared");

    Console.WriteLine("Waiting for messages... Press Ctrl+C to exit\n");

    var messageCount = 0;
    await foreach (var message in channel.ConsumeAsync("test-queue", prefetch: 50, cts.Token))
    {
        try
        {
            var body = System.Text.Encoding.UTF8.GetString(message.Body.Span);
            messageCount++;
            
            Console.WriteLine($"[{messageCount:D3}] Received: {body}");
            Console.WriteLine($"      Delivery Tag: {message.DeliveryTag}");
            Console.WriteLine($"      Message ID: {message.MessageId}");
            Console.WriteLine($"      Redelivered: {message.Redelivered}");

            // Simulate processing time
            await Task.Delay(500, cts.Token);

            // Acknowledge the message
            await channel.AckAsync(message.DeliveryTag);
            Console.WriteLine($"      ✓ Acknowledged\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing message: {ex.Message}");
            // NACK and requeue on error
            await channel.NackAsync(message.DeliveryTag, requeue: true);
        }

        if (cts.Token.IsCancellationRequested)
            break;
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Consumer stopped by user");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine("Consumer shut down.");