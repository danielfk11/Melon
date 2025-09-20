using System.CommandLine;
using System.Text.Json;
using MelonMQ.Client;
using MelonMQ.Common;

namespace MelonMQ.Cli;

/// <summary>
/// Main CLI program
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("MelonMQ CLI - Command line tool for MelonMQ message broker");
        
        // Global options
        var brokerOption = new Option<string>(
            aliases: ["--broker", "-b"],
            description: "Broker connection string",
            getDefaultValue: () => "melon://guest:guest@localhost:5672");
        
        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose output");
        
        rootCommand.AddGlobalOption(brokerOption);
        rootCommand.AddGlobalOption(verboseOption);
        
        // Add commands
        rootCommand.AddCommand(CreateDeclareCommand());
        rootCommand.AddCommand(CreateBindCommand());
        rootCommand.AddCommand(CreatePublishCommand());
        rootCommand.AddCommand(CreateConsumeCommand());
        rootCommand.AddCommand(CreateStatsCommand());
        rootCommand.AddCommand(CreatePurgeCommand());
        rootCommand.AddCommand(CreateDeleteCommand());
        
        return await rootCommand.InvokeAsync(args);
    }
    
    private static Command CreateDeclareCommand()
    {
        var declareCommand = new Command("declare", "Declare exchanges and queues");
        
        // Declare exchange
        var declareExchangeCommand = new Command("exchange", "Declare an exchange");
        declareExchangeCommand.AddOption(new Option<string>("--name", "Exchange name") { IsRequired = true });
        declareExchangeCommand.AddOption(new Option<string>("--type", "Exchange type (direct, fanout, topic, headers)") { IsRequired = true });
        declareExchangeCommand.AddOption(new Option<bool>("--durable", "Make the exchange durable"));
        declareExchangeCommand.AddOption(new Option<string?>("--arguments", "Exchange arguments as JSON"));
        
        declareExchangeCommand.SetHandler(async (string broker, bool verbose, string name, string type, bool durable, string? arguments) =>
        {
            try
            {
                using var connection = await MelonConnection.ConnectAsync(broker);
                using var channel = await connection.CreateChannelAsync();
                
                var exchangeType = Enum.Parse<ExchangeType>(type, true);
                var args = string.IsNullOrEmpty(arguments) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(arguments);
                
                await channel.DeclareExchangeAsync(name, exchangeType, durable, args);
                
                Console.WriteLine($"Exchange '{name}' declared successfully");
                if (verbose)
                {
                    Console.WriteLine($"  Type: {exchangeType}");
                    Console.WriteLine($"  Durable: {durable}");
                    if (args?.Count > 0)
                        Console.WriteLine($"  Arguments: {arguments}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error declaring exchange: {ex.Message}");
                Environment.Exit(1);
            }
        }, 
        brokerOption, verboseOption, 
        declareExchangeCommand.Options.OfType<Option<string>>().First(o => o.Name == "name"),
        declareExchangeCommand.Options.OfType<Option<string>>().First(o => o.Name == "type"),
        declareExchangeCommand.Options.OfType<Option<bool>>().First(o => o.Name == "durable"),
        declareExchangeCommand.Options.OfType<Option<string?>>().First(o => o.Name == "arguments"));
        
        // Declare queue
        var declareQueueCommand = new Command("queue", "Declare a queue");
        declareQueueCommand.AddOption(new Option<string>("--name", "Queue name") { IsRequired = true });
        declareQueueCommand.AddOption(new Option<bool>("--durable", "Make the queue durable"));
        declareQueueCommand.AddOption(new Option<bool>("--exclusive", "Make the queue exclusive"));
        declareQueueCommand.AddOption(new Option<bool>("--auto-delete", "Auto-delete the queue when unused"));
        declareQueueCommand.AddOption(new Option<string?>("--arguments", "Queue arguments as JSON"));
        
        declareQueueCommand.SetHandler(async (string broker, bool verbose, string name, bool durable, bool exclusive, bool autoDelete, string? arguments) =>
        {
            try
            {
                using var connection = await MelonConnection.ConnectAsync(broker);
                using var channel = await connection.CreateChannelAsync();
                
                var args = string.IsNullOrEmpty(arguments) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(arguments);
                
                await channel.DeclareQueueAsync(name, durable, exclusive, autoDelete, args);
                
                Console.WriteLine($"Queue '{name}' declared successfully");
                if (verbose)
                {
                    Console.WriteLine($"  Durable: {durable}");
                    Console.WriteLine($"  Exclusive: {exclusive}");
                    Console.WriteLine($"  Auto-delete: {autoDelete}");
                    if (args?.Count > 0)
                        Console.WriteLine($"  Arguments: {arguments}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error declaring queue: {ex.Message}");
                Environment.Exit(1);
            }
        },
        brokerOption, verboseOption,
        declareQueueCommand.Options.OfType<Option<string>>().First(o => o.Name == "name"),
        declareQueueCommand.Options.OfType<Option<bool>>().First(o => o.Name == "durable"),
        declareQueueCommand.Options.OfType<Option<bool>>().First(o => o.Name == "exclusive"),
        declareQueueCommand.Options.OfType<Option<bool>>().First(o => o.Name == "auto-delete"),
        declareQueueCommand.Options.OfType<Option<string?>>().First(o => o.Name == "arguments"));
        
        declareCommand.AddCommand(declareExchangeCommand);
        declareCommand.AddCommand(declareQueueCommand);
        
        return declareCommand;
    }
    
    private static Command CreateBindCommand()
    {
        var bindCommand = new Command("bind", "Bind a queue to an exchange");
        bindCommand.AddOption(new Option<string>("--queue", "Queue name") { IsRequired = true });
        bindCommand.AddOption(new Option<string>("--exchange", "Exchange name") { IsRequired = true });
        bindCommand.AddOption(new Option<string>("--routing-key", "Routing key") { IsRequired = true });
        bindCommand.AddOption(new Option<string?>("--arguments", "Binding arguments as JSON"));
        
        bindCommand.SetHandler(async (string broker, bool verbose, string queue, string exchange, string routingKey, string? arguments) =>
        {
            try
            {
                using var connection = await MelonConnection.ConnectAsync(broker);
                using var channel = await connection.CreateChannelAsync();
                
                var args = string.IsNullOrEmpty(arguments) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(arguments);
                
                await channel.BindQueueAsync(queue, exchange, routingKey, args);
                
                Console.WriteLine($"Queue '{queue}' bound to exchange '{exchange}' with routing key '{routingKey}'");
                if (verbose && args?.Count > 0)
                {
                    Console.WriteLine($"  Arguments: {arguments}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error binding queue: {ex.Message}");
                Environment.Exit(1);
            }
        },
        brokerOption, verboseOption,
        bindCommand.Options.OfType<Option<string>>().First(o => o.Name == "queue"),
        bindCommand.Options.OfType<Option<string>>().First(o => o.Name == "exchange"),
        bindCommand.Options.OfType<Option<string>>().First(o => o.Name == "routing-key"),
        bindCommand.Options.OfType<Option<string?>>().First(o => o.Name == "arguments"));
        
        return bindCommand;
    }
    
    private static Command CreatePublishCommand()
    {
        var publishCommand = new Command("publish", "Publish a message");
        publishCommand.AddOption(new Option<string>("--exchange", "Exchange name") { IsRequired = true });
        publishCommand.AddOption(new Option<string>("--routing-key", "Routing key") { IsRequired = true });
        publishCommand.AddOption(new Option<string>("--message", "Message body") { IsRequired = true });
        publishCommand.AddOption(new Option<bool>("--persistent", "Make message persistent"));
        publishCommand.AddOption(new Option<byte>("--priority", "Message priority (0-9)"));
        publishCommand.AddOption(new Option<string?>("--headers", "Message headers as JSON"));
        publishCommand.AddOption(new Option<int>("--count", "Number of messages to publish") { IsRequired = false });
        publishCommand.AddOption(new Option<int>("--rate", "Publishing rate (messages/second)") { IsRequired = false });
        
        publishCommand.SetHandler(async (string broker, bool verbose, string exchange, string routingKey, string message, bool persistent, byte priority, string? headers, int count, int rate) =>
        {
            try
            {
                using var connection = await MelonConnection.ConnectAsync(broker);
                using var channel = await connection.CreateChannelAsync();
                
                var headerDict = string.IsNullOrEmpty(headers) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(headers);
                var messageCount = count > 0 ? count : 1;
                var delayMs = rate > 0 ? 1000 / rate : 0;
                
                var startTime = DateTime.UtcNow;
                
                for (int i = 0; i < messageCount; i++)
                {
                    var properties = new MessageProperties
                    {
                        Priority = priority,
                        DeliveryMode = persistent ? DeliveryMode.Persistent : DeliveryMode.NonPersistent
                    };
                    
                    if (headerDict != null)
                    {
                        foreach (var (key, value) in headerDict)
                        {
                            properties.Headers[key] = value;
                        }
                    }
                    
                    var body = BinaryData.FromString(message);
                    await channel.PublishAsync(exchange, routingKey, body, properties, persistent, priority);
                    
                    if (verbose)
                    {
                        Console.WriteLine($"Published message {i + 1}/{messageCount} - ID: {properties.MessageId}");
                    }
                    
                    if (delayMs > 0 && i < messageCount - 1)
                    {
                        await Task.Delay(delayMs);
                    }
                }
                
                var elapsed = DateTime.UtcNow - startTime;
                var messagesPerSecond = messageCount / elapsed.TotalSeconds;
                
                Console.WriteLine($"Published {messageCount} messages in {elapsed.TotalMilliseconds:F0}ms ({messagesPerSecond:F1} msg/s)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error publishing message: {ex.Message}");
                Environment.Exit(1);
            }
        },
        brokerOption, verboseOption,
        publishCommand.Options.OfType<Option<string>>().First(o => o.Name == "exchange"),
        publishCommand.Options.OfType<Option<string>>().First(o => o.Name == "routing-key"),
        publishCommand.Options.OfType<Option<string>>().First(o => o.Name == "message"),
        publishCommand.Options.OfType<Option<bool>>().First(o => o.Name == "persistent"),
        publishCommand.Options.OfType<Option<byte>>().First(o => o.Name == "priority"),
        publishCommand.Options.OfType<Option<string?>>().First(o => o.Name == "headers"),
        publishCommand.Options.OfType<Option<int>>().First(o => o.Name == "count"),
        publishCommand.Options.OfType<Option<int>>().First(o => o.Name == "rate"));
        
        return publishCommand;
    }
    
    private static Command CreateConsumeCommand()
    {
        var consumeCommand = new Command("consume", "Consume messages from a queue");
        consumeCommand.AddOption(new Option<string>("--queue", "Queue name") { IsRequired = true });
        consumeCommand.AddOption(new Option<string?>("--consumer-tag", "Consumer tag"));
        consumeCommand.AddOption(new Option<int>("--prefetch", "Prefetch count") { IsRequired = false });
        consumeCommand.AddOption(new Option<bool>("--no-ack", "Disable acknowledgments"));
        consumeCommand.AddOption(new Option<int>("--count", "Maximum number of messages to consume"));
        consumeCommand.AddOption(new Option<int>("--timeout", "Timeout in seconds"));
        
        consumeCommand.SetHandler(async (string broker, bool verbose, string queue, string? consumerTag, int prefetch, bool noAck, int count, int timeout) =>
        {
            try
            {
                using var connection = await MelonConnection.ConnectAsync(broker);
                using var channel = await connection.CreateChannelAsync();
                
                if (prefetch > 0)
                {
                    await channel.SetPrefetchAsync(prefetch);
                }
                
                var cancellationTokenSource = new CancellationTokenSource();
                if (timeout > 0)
                {
                    cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(timeout));
                }
                
                Console.WriteLine($"Consuming from queue '{queue}'... (Press Ctrl+C to stop)");
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancellationTokenSource.Cancel();
                };
                
                int messageCount = 0;
                var startTime = DateTime.UtcNow;
                
                await foreach (var delivery in channel.ConsumeAsync(queue, consumerTag, prefetch > 0 ? prefetch : 100, noAck, cancellationTokenSource.Token))
                {
                    messageCount++;
                    
                    Console.WriteLine($"Received message #{messageCount}:");
                    Console.WriteLine($"  Message ID: {delivery.Message.Properties.MessageId}");
                    Console.WriteLine($"  Exchange: {delivery.Message.Exchange}");
                    Console.WriteLine($"  Routing Key: {delivery.Message.RoutingKey}");
                    Console.WriteLine($"  Priority: {delivery.Message.Properties.Priority}");
                    Console.WriteLine($"  Timestamp: {delivery.Message.Properties.Timestamp}");
                    Console.WriteLine($"  Body: {System.Text.Encoding.UTF8.GetString(delivery.Message.Body.Span)}");
                    
                    if (verbose)
                    {
                        Console.WriteLine($"  Consumer Tag: {delivery.ConsumerTag}");
                        Console.WriteLine($"  Delivery Tag: {delivery.DeliveryTag}");
                        if (delivery.Message.Properties.Headers.Count > 0)
                        {
                            Console.WriteLine("  Headers:");
                            foreach (var (key, value) in delivery.Message.Properties.Headers)
                            {
                                Console.WriteLine($"    {key}: {value}");
                            }
                        }
                    }
                    
                    if (!noAck)
                    {
                        await delivery.AckAsync(cancellationTokenSource.Token);
                        if (verbose)
                            Console.WriteLine("  Acknowledged");
                    }
                    
                    Console.WriteLine();
                    
                    if (count > 0 && messageCount >= count)
                        break;
                }
                
                var elapsed = DateTime.UtcNow - startTime;
                var messagesPerSecond = messageCount / elapsed.TotalSeconds;
                
                Console.WriteLine($"Consumed {messageCount} messages in {elapsed.TotalSeconds:F1}s ({messagesPerSecond:F1} msg/s)");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Consumption cancelled");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error consuming messages: {ex.Message}");
                Environment.Exit(1);
            }
        },
        brokerOption, verboseOption,
        consumeCommand.Options.OfType<Option<string>>().First(o => o.Name == "queue"),
        consumeCommand.Options.OfType<Option<string?>>().First(o => o.Name == "consumer-tag"),
        consumeCommand.Options.OfType<Option<int>>().First(o => o.Name == "prefetch"),
        consumeCommand.Options.OfType<Option<bool>>().First(o => o.Name == "no-ack"),
        consumeCommand.Options.OfType<Option<int>>().First(o => o.Name == "count"),
        consumeCommand.Options.OfType<Option<int>>().First(o => o.Name == "timeout"));
        
        return consumeCommand;
    }
    
    private static Command CreateStatsCommand()
    {
        var statsCommand = new Command("stats", "Get broker statistics");
        statsCommand.AddOption(new Option<bool>("--json", "Output as JSON"));
        
        statsCommand.SetHandler(async (string broker, bool verbose, bool json) =>
        {
            try
            {
                // For now, use HTTP API to get stats
                var httpClient = new HttpClient();
                var brokerUri = new Uri(broker);
                var adminUrl = $"http://{brokerUri.Host}:8080/api/admin/stats";
                
                var response = await httpClient.GetStringAsync(adminUrl);
                
                if (json)
                {
                    Console.WriteLine(response);
                }
                else
                {
                    var stats = JsonSerializer.Deserialize<BrokerStats>(response);
                    if (stats != null)
                    {
                        Console.WriteLine("Broker Statistics:");
                        Console.WriteLine($"  Uptime: {stats.Uptime}");
                        Console.WriteLine($"  Total Connections: {stats.TotalConnections}");
                        Console.WriteLine($"  Active Connections: {stats.ActiveConnections}");
                        Console.WriteLine($"  Active Channels: {stats.ActiveChannels}");
                        Console.WriteLine($"  Total Queues: {stats.TotalQueues}");
                        Console.WriteLine($"  Total Exchanges: {stats.TotalExchanges}");
                        Console.WriteLine($"  Messages in Flight: {stats.MessagesInFlight}");
                        Console.WriteLine($"  Memory Used: {stats.MemoryUsed / 1024 / 1024:F1} MB");
                        
                        if (stats.QueueStats.Count > 0)
                        {
                            Console.WriteLine("\nQueue Statistics:");
                            foreach (var (queueName, queueStats) in stats.QueueStats)
                            {
                                Console.WriteLine($"  {queueName}:");
                                Console.WriteLine($"    Messages: {queueStats.MessageCount}");
                                Console.WriteLine($"    Consumers: {queueStats.ConsumerCount}");
                                Console.WriteLine($"    Publish Rate: {queueStats.PublishRate:F1} msg/s");
                                Console.WriteLine($"    Delivery Rate: {queueStats.DeliveryRate:F1} msg/s");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error getting stats: {ex.Message}");
                Environment.Exit(1);
            }
        },
        brokerOption, verboseOption,
        statsCommand.Options.OfType<Option<bool>>().First(o => o.Name == "json"));
        
        return statsCommand;
    }
    
    private static Command CreatePurgeCommand()
    {
        var purgeCommand = new Command("purge", "Purge all messages from a queue");
        purgeCommand.AddOption(new Option<string>("--queue", "Queue name") { IsRequired = true });
        
        purgeCommand.SetHandler(async (string broker, bool verbose, string queue) =>
        {
            try
            {
                using var connection = await MelonConnection.ConnectAsync(broker);
                using var channel = await connection.CreateChannelAsync();
                
                await channel.PurgeQueueAsync(queue);
                
                Console.WriteLine($"Queue '{queue}' purged successfully");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error purging queue: {ex.Message}");
                Environment.Exit(1);
            }
        },
        brokerOption, verboseOption,
        purgeCommand.Options.OfType<Option<string>>().First(o => o.Name == "queue"));
        
        return purgeCommand;
    }
    
    private static Command CreateDeleteCommand()
    {
        var deleteCommand = new Command("delete", "Delete exchanges and queues");
        
        // Delete exchange
        var deleteExchangeCommand = new Command("exchange", "Delete an exchange");
        deleteExchangeCommand.AddOption(new Option<string>("--name", "Exchange name") { IsRequired = true });
        deleteExchangeCommand.AddOption(new Option<bool>("--if-unused", "Only delete if unused"));
        
        deleteExchangeCommand.SetHandler(async (string broker, bool verbose, string name, bool ifUnused) =>
        {
            try
            {
                using var connection = await MelonConnection.ConnectAsync(broker);
                using var channel = await connection.CreateChannelAsync();
                
                await channel.DeleteExchangeAsync(name, ifUnused);
                
                Console.WriteLine($"Exchange '{name}' deleted successfully");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error deleting exchange: {ex.Message}");
                Environment.Exit(1);
            }
        },
        brokerOption, verboseOption,
        deleteExchangeCommand.Options.OfType<Option<string>>().First(o => o.Name == "name"),
        deleteExchangeCommand.Options.OfType<Option<bool>>().First(o => o.Name == "if-unused"));
        
        // Delete queue
        var deleteQueueCommand = new Command("queue", "Delete a queue");
        deleteQueueCommand.AddOption(new Option<string>("--name", "Queue name") { IsRequired = true });
        deleteQueueCommand.AddOption(new Option<bool>("--if-unused", "Only delete if unused"));
        deleteQueueCommand.AddOption(new Option<bool>("--if-empty", "Only delete if empty"));
        
        deleteQueueCommand.SetHandler(async (string broker, bool verbose, string name, bool ifUnused, bool ifEmpty) =>
        {
            try
            {
                using var connection = await MelonConnection.ConnectAsync(broker);
                using var channel = await connection.CreateChannelAsync();
                
                await channel.DeleteQueueAsync(name, ifUnused, ifEmpty);
                
                Console.WriteLine($"Queue '{name}' deleted successfully");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error deleting queue: {ex.Message}");
                Environment.Exit(1);
            }
        },
        brokerOption, verboseOption,
        deleteQueueCommand.Options.OfType<Option<string>>().First(o => o.Name == "name"),
        deleteQueueCommand.Options.OfType<Option<bool>>().First(o => o.Name == "if-unused"),
        deleteQueueCommand.Options.OfType<Option<bool>>().First(o => o.Name == "if-empty"));
        
        deleteCommand.AddCommand(deleteExchangeCommand);
        deleteCommand.AddCommand(deleteQueueCommand);
        
        return deleteCommand;
    }
}