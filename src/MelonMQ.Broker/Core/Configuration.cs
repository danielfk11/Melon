using System.Net;

namespace MelonMQ.Broker.Core;

public class MelonMQConfiguration
{
    public int TcpPort { get; set; } = 5672;
    public string TcpBindAddress { get; set; } = "127.0.0.1";
    public int HttpPort { get; set; } = 9090;
    public string DataDirectory { get; set; } = "data";
    public int BatchFlushMs { get; set; } = 10;
    public int CompactionThresholdMB { get; set; } = 100;
    public int ChannelCapacity { get; set; } = 10000;
    public bool EnableAuth { get; set; } = false;
    public int ConnectionTimeout { get; set; } = 30000;
    public int HeartbeatInterval { get; set; } = 10000;
    public int MaxConnections { get; set; } = 1000;
    public int MaxMessageSize { get; set; } = 1048576; // 1MB
    public TcpTlsConfiguration TcpTls { get; set; } = new();
    public SecurityConfiguration Security { get; set; } = new();
    public QueueGarbageCollectionConfiguration QueueGC { get; set; } = new();
}

public class TcpTlsConfiguration
{
    public bool Enabled { get; set; } = false;
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
    public bool ClientCertificateRequired { get; set; } = false;
    public bool CheckCertificateRevocation { get; set; } = false;
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
    public bool OnlyNonDurable { get; set; } = true;

    /// <summary>
    /// Maximum number of queues allowed. New declarations are rejected after this limit.
    /// 0 = unlimited.
    /// </summary>
    public int MaxQueues { get; set; } = 1000;
}

public class SecurityConfiguration
{
    public string JwtSecret { get; set; } = string.Empty;
    public int JwtExpirationMinutes { get; set; } = 60;
    public bool RequireAuth { get; set; } = false;
    public bool RequireHashedPasswords { get; set; } = true;
    public bool RequireAdminApiKey { get; set; } = false;
    public bool ProtectReadEndpoints { get; set; } = true;
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
    
    /// <summary>
    /// API key required for HTTP write/admin endpoints (declare, publish, purge, delete, gc).
    /// If empty, those endpoints are unprotected.
    /// </summary>
    public string AdminApiKey { get; set; } = string.Empty;
    
    /// <summary>
    /// Credential store for TCP auth. Key = username, Value = password.
    /// Loaded from configuration section "MelonMQ:Security:Users".
    /// </summary>
    public Dictionary<string, string> Users { get; set; } = new();
    
    public bool IsConfigured => !string.IsNullOrEmpty(JwtSecret) || Users.Count > 0;
    public bool HasAdminApiKey => !string.IsNullOrEmpty(AdminApiKey);
}

public static class ConfigurationExtensions
{
    public static void ValidateConfiguration(this MelonMQConfiguration config, bool isProduction = false)
    {
        if (config.Security.RequireAuth && !config.Security.IsConfigured)
        {
            throw new InvalidOperationException(
                "Authentication is required but security is not configured. " +
                "Please configure MelonMQ:Security:Users (recommended) or MelonMQ:Security:JwtSecret.");
        }

        if (config.Security.RequireAuth && config.Security.Users.Count == 0)
        {
            throw new InvalidOperationException(
                "Authentication is required but no users are configured. " +
                "Please set MelonMQ:Security:Users with at least one username/password pair.");
        }

        if (config.Security.RequireHashedPasswords && config.Security.Users.Any(kvp => !PasswordHasher.IsHashedCredential(kvp.Value)))
        {
            throw new InvalidOperationException(
                "One or more configured user credentials are not hashed. " +
                "Use format 'pbkdf2-sha256$iterations$saltBase64$hashBase64'.");
        }

        if (config.Security.RequireAdminApiKey && !config.Security.HasAdminApiKey)
        {
            throw new InvalidOperationException(
                "Admin API key protection is enabled, but MelonMQ:Security:AdminApiKey is empty.");
        }

        if (config.TcpTls.Enabled)
        {
            if (string.IsNullOrWhiteSpace(config.TcpTls.CertificatePath))
            {
                throw new InvalidOperationException("TCP TLS is enabled but certificate path is not configured.");
            }

            if (!File.Exists(config.TcpTls.CertificatePath))
            {
                throw new FileNotFoundException(
                    $"TCP TLS certificate not found at '{config.TcpTls.CertificatePath}'.",
                    config.TcpTls.CertificatePath);
            }
        }

        if (isProduction)
        {
            if (!config.Security.RequireAuth)
            {
                throw new InvalidOperationException("Production requires MelonMQ:Security:RequireAuth=true.");
            }

            if (!config.TcpTls.Enabled)
            {
                throw new InvalidOperationException("Production requires MelonMQ:TcpTls:Enabled=true.");
            }

            if (!config.Security.RequireAdminApiKey)
            {
                throw new InvalidOperationException("Production requires MelonMQ:Security:RequireAdminApiKey=true.");
            }

            if (config.Security.AllowedOrigins.Length == 0)
            {
                throw new InvalidOperationException(
                    "Production requires explicit MelonMQ:Security:AllowedOrigins configuration.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.TcpBindAddress))
        {
            throw new ArgumentException("TCP bind address cannot be empty.", nameof(config.TcpBindAddress));
        }

        if (!IPAddress.TryParse(config.TcpBindAddress, out _) &&
            !string.Equals(config.TcpBindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "TCP bind address must be a valid IP address or 'localhost'.",
                nameof(config.TcpBindAddress));
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

        _ = MelonMQ.Protocol.MessageSizePolicy.ComputeMaxFrameSizeBytes(config.MaxMessageSize);
        
        if (config.MaxConnections < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(config.MaxConnections), 
                "Maximum connections must be at least 1.");
        }

        if (config.QueueGC.IntervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(config.QueueGC.IntervalSeconds),
                "Queue GC interval must be at least 1 second.");
        }

        if (config.QueueGC.InactiveThresholdSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(config.QueueGC.InactiveThresholdSeconds),
                "Queue GC inactive threshold must be at least 1 second.");
        }

        if (config.QueueGC.MaxQueues < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config.QueueGC.MaxQueues),
                "Maximum queues cannot be negative.");
        }
    }
}