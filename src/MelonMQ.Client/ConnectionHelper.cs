using System.Net.Sockets;

namespace MelonMQ.Client;

public class ConnectionRetryPolicy
{
    public int MaxRetryAttempts { get; set; } = 5;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public double BackoffMultiplier { get; set; } = 2.0;
    public bool EnableRetry { get; set; } = true;
}

public static class ConnectionHelper
{
    public static async Task<TcpClient> ConnectWithRetryAsync(
        string host, 
        int port, 
        ConnectionRetryPolicy? retryPolicy = null,
        CancellationToken cancellationToken = default)
    {
        retryPolicy ??= new ConnectionRetryPolicy();
        
        if (!retryPolicy.EnableRetry)
        {
            var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            return client;
        }

        var attempt = 0;
        var delay = retryPolicy.InitialDelay;
        
        while (true)
        {
            attempt++;
            
            try
            {
                var client = new TcpClient();
                // Set connection timeout
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                
                await client.ConnectAsync(host, port, cancellationToken);
                return client;
            }
            catch (Exception ex) when (IsRetriableException(ex) && attempt < retryPolicy.MaxRetryAttempts)
            {
                // Wait before retry
                await Task.Delay(delay, cancellationToken);
                
                // Exponential backoff
                delay = TimeSpan.FromMilliseconds(Math.Min(
                    delay.TotalMilliseconds * retryPolicy.BackoffMultiplier,
                    retryPolicy.MaxDelay.TotalMilliseconds));
            }
        }
    }
    
    private static bool IsRetriableException(Exception ex)
    {
        return ex is SocketException or TimeoutException or IOException;
    }
}

public class MelonConnectionOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public ConnectionRetryPolicy RetryPolicy { get; set; } = new();
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public bool AutoReconnect { get; set; } = true;
    public int MaxChannels { get; set; } = 100;
}