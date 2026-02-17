namespace MelonMQ.Broker.Core;

public class MelonMQConfiguration
{
    public int TcpPort { get; set; } = 5672;
    public int HttpPort { get; set; } = 8080;
    public string DataDirectory { get; set; } = "data";
    public int BatchFlushMs { get; set; } = 10;
    public int CompactionThresholdMB { get; set; } = 100;
    public int ChannelCapacity { get; set; } = 10000;
    public bool EnableAuth { get; set; } = false;
    public int ConnectionTimeout { get; set; } = 30000;
    public int HeartbeatInterval { get; set; } = 10000;
    public int MaxConnections { get; set; } = 1000;
    public int MaxMessageSize { get; set; } = 1048576; // 1MB
    public SecurityConfiguration Security { get; set; } = new();
    public QueueGarbageCollectionConfiguration QueueGC { get; set; } = new();
}

public class QueueGarbageCollectionConfiguration
{
    /// <summary>
    /// Enable automatic cleanup of inactive empty queues.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often (in seconds) the GC runs to check for inactive queues.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Time in seconds a queue must be empty and idle before being deleted.
    /// </summary>
    public int InactiveThresholdSeconds { get; set; } = 300; // 5 minutes

    /// <summary>
    /// If true, only non-durable queues are eligible for auto-delete.
    /// If false, all empty inactive queues (including durable) are deleted.
    /// </summary>
    public bool OnlyNonDurable { get; set; } = false;

    /// <summary>
    /// Maximum number of queues allowed. New declarations are rejected after this limit.
    /// 0 = unlimited.
    /// </summary>
    public int MaxQueues { get; set; } = 0;
}

public class SecurityConfiguration
{
    public string JwtSecret { get; set; } = string.Empty;
    public int JwtExpirationMinutes { get; set; } = 60;
    public bool RequireAuth { get; set; } = false;
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
    
    public bool IsConfigured => !string.IsNullOrEmpty(JwtSecret);
}

public static class ConfigurationExtensions
{
    public static void ValidateConfiguration(this MelonMQConfiguration config)
    {
        if (config.Security.RequireAuth && !config.Security.IsConfigured)
        {
            throw new InvalidOperationException(
                "Authentication is required but JWT secret is not configured. " +
                "Please set MelonMQ:Security:JwtSecret in configuration or environment variable.");
        }
        
        if (config.TcpPort < 1 || config.TcpPort > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(config.TcpPort), 
                "TCP port must be between 1 and 65535.");
        }
        
        if (config.HttpPort < 1 || config.HttpPort > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(config.HttpPort), 
                "HTTP port must be between 1 and 65535.");
        }
        
        if (config.MaxMessageSize < 1024) // Minimum 1KB
        {
            throw new ArgumentOutOfRangeException(nameof(config.MaxMessageSize), 
                "Maximum message size must be at least 1024 bytes.");
        }
        
        if (config.MaxConnections < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(config.MaxConnections), 
                "Maximum connections must be at least 1.");
        }
    }
}