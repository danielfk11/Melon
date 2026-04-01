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
    public ObservabilityConfiguration Observability { get; set; } = new();
    public ClusterConfiguration Cluster { get; set; } = new();
    public QueueGarbageCollectionConfiguration QueueGC { get; set; } = new();
}

public class ObservabilityConfiguration
{
    public string ServiceName { get; set; } = "MelonMQ.Broker";
    public string ServiceVersion { get; set; } = "1.0.0";
    public PrometheusConfiguration Prometheus { get; set; } = new();
    public OtlpConfiguration Otlp { get; set; } = new();
}

public class PrometheusConfiguration
{
    public bool Enabled { get; set; } = true;
    public string EndpointPath { get; set; } = "/metrics";
    public bool RequireAdminApiKey { get; set; } = false;
}

public class OtlpConfiguration
{
    public bool Enabled { get; set; } = false;
    public string Endpoint { get; set; } = string.Empty;
    public string Protocol { get; set; } = "http/protobuf";
    public string Headers { get; set; } = string.Empty;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableTraces { get; set; } = true;
    public int MetricsExportIntervalMs { get; set; } = 5000;
    public int TimeoutMs { get; set; } = 10000;
}

public class ClusterConfiguration
{
    public bool Enabled { get; set; } = false;
    public string NodeId { get; set; } = Environment.MachineName;
    public string NodeAddress { get; set; } = string.Empty;
    public string[] SeedNodes { get; set; } = Array.Empty<string>();
    public string SharedKey { get; set; } = string.Empty;
    public int DiscoveryIntervalSeconds { get; set; } = 5;
    public int NodeTimeoutSeconds { get; set; } = 15;
    public bool EnableReplication { get; set; } = true;
    public bool RequireQuorumForWrites { get; set; } = true;
    public string Consistency { get; set; } = "leader";
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

            if (config.Cluster.Enabled && string.IsNullOrWhiteSpace(config.Cluster.SharedKey))
            {
                throw new InvalidOperationException(
                    "Production clustering requires MelonMQ:Cluster:SharedKey to secure node-to-node traffic.");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Observability.Prometheus.EndpointPath) &&
            !config.Observability.Prometheus.EndpointPath.StartsWith('/'))
        {
            throw new ArgumentException(
                "Prometheus endpoint path must start with '/'.",
                nameof(config.Observability.Prometheus.EndpointPath));
        }

        if (config.Observability.Otlp.Enabled)
        {
            if (string.IsNullOrWhiteSpace(config.Observability.Otlp.Endpoint))
            {
                throw new InvalidOperationException(
                    "OTLP exporter is enabled but MelonMQ:Observability:Otlp:Endpoint is empty.");
            }

            if (!Uri.TryCreate(config.Observability.Otlp.Endpoint, UriKind.Absolute, out var otlpUri) ||
                (otlpUri.Scheme != Uri.UriSchemeHttp && otlpUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "OTLP endpoint must be a valid absolute HTTP/HTTPS URL.");
            }

            if (config.Observability.Otlp.MetricsExportIntervalMs < 500)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(config.Observability.Otlp.MetricsExportIntervalMs),
                    "OTLP export interval must be at least 500ms.");
            }

            if (config.Observability.Otlp.TimeoutMs < 1000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(config.Observability.Otlp.TimeoutMs),
                    "OTLP exporter timeout must be at least 1000ms.");
            }
        }

        if (config.Cluster.Enabled)
        {
            if (string.IsNullOrWhiteSpace(config.Cluster.NodeId))
            {
                throw new InvalidOperationException("Cluster is enabled but MelonMQ:Cluster:NodeId is empty.");
            }

            if (string.IsNullOrWhiteSpace(config.Cluster.NodeAddress))
            {
                throw new InvalidOperationException("Cluster is enabled but MelonMQ:Cluster:NodeAddress is empty.");
            }

            if (!Uri.TryCreate(config.Cluster.NodeAddress, UriKind.Absolute, out var nodeUri) ||
                (nodeUri.Scheme != Uri.UriSchemeHttp && nodeUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "Cluster node address must be a valid absolute HTTP/HTTPS URL.");
            }

            foreach (var seedNode in config.Cluster.SeedNodes)
            {
                if (!Uri.TryCreate(seedNode, UriKind.Absolute, out var seedUri) ||
                    (seedUri.Scheme != Uri.UriSchemeHttp && seedUri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException(
                        $"Invalid cluster seed node '{seedNode}'. Expected absolute HTTP/HTTPS URL.");
                }
            }

            if (config.Cluster.DiscoveryIntervalSeconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(config.Cluster.DiscoveryIntervalSeconds),
                    "Cluster discovery interval must be at least 1 second.");
            }

            if (config.Cluster.NodeTimeoutSeconds < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(config.Cluster.NodeTimeoutSeconds),
                    "Cluster node timeout must be at least 2 seconds.");
            }

            if (config.Cluster.NodeTimeoutSeconds <= config.Cluster.DiscoveryIntervalSeconds)
            {
                throw new InvalidOperationException(
                    "Cluster node timeout must be greater than discovery interval.");
            }

            var consistency = config.Cluster.Consistency.Trim();
            if (!string.Equals(consistency, "leader", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(consistency, "quorum", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Cluster consistency must be either 'leader' or 'quorum'.");
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