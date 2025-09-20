namespace MelonMQ.Common;

/// <summary>
/// Custom exceptions for MelonMQ
/// </summary>
public class MelonMqException : Exception
{
    public ErrorCode ErrorCode { get; }
    
    public MelonMqException(ErrorCode errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
    
    public MelonMqException(ErrorCode errorCode, string message, Exception innerException) 
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

public class ConnectionException : MelonMqException
{
    public ConnectionException(string message) : base(ErrorCode.ConnectionForced, message) { }
    public ConnectionException(string message, Exception innerException) : base(ErrorCode.ConnectionForced, message, innerException) { }
}

public class AuthenticationException : MelonMqException
{
    public AuthenticationException(string message) : base(ErrorCode.InvalidCredentials, message) { }
}

public class AuthorizationException : MelonMqException
{
    public AuthorizationException(string message) : base(ErrorCode.AccessRefused, message) { }
}

public class ChannelException : MelonMqException
{
    public ChannelException(ErrorCode errorCode, string message) : base(errorCode, message) { }
}

public class ResourceNotFoundException : MelonMqException
{
    public ResourceNotFoundException(string resourceType, string resourceName) 
        : base(ErrorCode.NotFound, $"{resourceType} '{resourceName}' not found") { }
}

public class ResourceExistsException : MelonMqException
{
    public ResourceExistsException(string resourceType, string resourceName) 
        : base(ErrorCode.ResourceExists, $"{resourceType} '{resourceName}' already exists") { }
}

public class MessageTooLargeException : MelonMqException
{
    public MessageTooLargeException(int messageSize, int maxSize) 
        : base(ErrorCode.MessageTooLarge, $"Message size {messageSize} exceeds maximum {maxSize}") { }
}

public class NoRouteException : MelonMqException
{
    public NoRouteException(string exchange, string routingKey) 
        : base(ErrorCode.NoRoute, $"No route for exchange '{exchange}' with routing key '{routingKey}'") { }
}

public class QueueFullException : MelonMqException
{
    public QueueFullException(string queueName) 
        : base(ErrorCode.ResourceError, $"Queue '{queueName}' is full") { }
}

public class PersistenceException : MelonMqException
{
    public PersistenceException(string message) : base(ErrorCode.InternalError, message) { }
    public PersistenceException(string message, Exception innerException) : base(ErrorCode.InternalError, message, innerException) { }
}