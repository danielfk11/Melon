using MelonMQ.Client;

Console.WriteLine("MelonMQ Producer Sample");
Console.WriteLine("======================");

try
{
    using var connection = await MelonConnection.ConnectAsync("melon://localhost:5672");
    using var channel = await connection.CreateChannelAsync();

    // Declare queue
    await channel.DeclareQueueAsync("test-queue", durable: true, dlq: "test-queue.dlq");
    Console.WriteLine("Queue 'test-queue' declared");

    // Publish messages
    for (int i = 1; i <= 10; i++)
    {
        var message = $"Hello MelonMQ! Message #{i} at {DateTime.Now:HH:mm:ss}";
        var body = System.Text.Encoding.UTF8.GetBytes(message);
        
        await channel.PublishAsync("test-queue", body, persistent: true, ttlMs: 60000);
        Console.WriteLine($"Published: {message}");
        
        await Task.Delay(1000); // 1 second between messages
    }

    Console.WriteLine("\nAll messages published successfully!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine("Press any key to exit...");
Console.ReadKey();