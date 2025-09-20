namespace MelonMQ.Common;

/// <summary>
/// Magic bytes for MelonMQ protocol
/// </summary>
public static class ProtocolConstants
{
    public const ushort Magic = 0x4D4D; // "MM" for MelonMQ
    public const byte Version = 1;
    public const int FrameHeaderSize = 16; // Magic(2) + Version(1) + Type(1) + Flags(1) + Length(4) + CorrId(8)
    public const int MaxFrameSize = 32 * 1024 * 1024; // 32MB
    public const int DefaultHeartbeatIntervalMs = 30000; // 30 seconds
    public const int DefaultAckTimeoutMs = 60000; // 60 seconds
    public const int DefaultPrefetch = 100;
    public const int MaxPriority = 9;
    public const string DefaultVHost = "/";
    public const string DefaultExchange = "";
}

/// <summary>
/// Frame types for the MelonMQ protocol
/// </summary>
public enum FrameType : byte
{
    // Connection
    Auth = 1,
    AuthOk = 2,
    AuthFailed = 3,
    
    // AMQP-like operations
    DeclareExchange = 10,
    DeclareQueue = 11,
    Bind = 12,
    Unbind = 13,
    Delete = 14,
    Purge = 15,
    
    // Message operations
    Publish = 20,
    Consume = 21,
    Deliver = 22,
    Ack = 23,
    Nack = 24,
    Reject = 25,
    Confirm = 26,
    
    // Flow control
    SetPrefetch = 30,
    
    // Connection management
    Heartbeat = 40,
    Close = 41,
    
    // Transaction support
    TxBegin = 50,
    TxCommit = 51,
    TxRollback = 52,
    
    // Statistics and management
    Stats = 60,
    
    // Error responses
    Error = 255
}

/// <summary>
/// Frame flags
/// </summary>
[Flags]
public enum FrameFlags : byte
{
    None = 0,
    Request = 1,        // Bit 0: 0=response, 1=request
    Durable = 2,        // Bit 1: message/queue persistence
    Mandatory = 4,      // Bit 2: routing must succeed
    Immediate = 8,      // Bit 3: immediate delivery required
    Compressed = 16     // Bit 4: payload is compressed
}

/// <summary>
/// Exchange types
/// </summary>
public enum ExchangeType
{
    Direct,
    Fanout,
    Topic,
    Headers
}

/// <summary>
/// Message delivery modes
/// </summary>
public enum DeliveryMode : byte
{
    NonPersistent = 1,
    Persistent = 2
}

/// <summary>
/// Queue types for special behavior
/// </summary>
public enum QueueType
{
    Classic,
    Quorum,
    Stream
}

/// <summary>
/// Authentication methods
/// </summary>
public enum AuthMethod
{
    Plain,
    ScramSha256
}

/// <summary>
/// Error codes for protocol errors
/// </summary>
public enum ErrorCode : ushort
{
    // General errors
    InternalError = 500,
    NotImplemented = 501,
    SyntaxError = 502,
    CommandInvalid = 503,
    ChannelError = 504,
    UnexpectedFrame = 505,
    ResourceError = 506,
    NotAllowed = 530,
    NotFound = 404,
    ResourceLocked = 405,
    PreconditionFailed = 406,
    
    // Authentication/Authorization
    AccessRefused = 403,
    InvalidCredentials = 401,
    
    // Connection errors
    ConnectionForced = 320,
    InvalidPath = 402,
    FrameTooLarge = 501,
    
    // Resource errors
    ResourceExists = 409,
    QueueNotFound = 404,
    ExchangeNotFound = 404,
    ConsumerCancelled = 313,
    
    // Message errors
    NoRoute = 312,
    MessageTooLarge = 413,
    TTLExpired = 408
}