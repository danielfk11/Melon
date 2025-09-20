using System.Buffers;

namespace MelonMQ.Common;

/// <summary>
/// Thread-safe object pool for byte arrays
/// </summary>
public static class BufferPool
{
    private static readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
    
    public static byte[] Rent(int minimumLength) => _pool.Get(minimumLength);
    
    public static void Return(byte[] array, bool clearArray = false) => _pool.Return(array, clearArray);
}

/// <summary>
/// CRC32C implementation for data integrity
/// </summary>
public static class Crc32C
{
    private static readonly uint[] Table = GenerateTable();
    
    private static uint[] GenerateTable()
    {
        const uint polynomial = 0x82F63B78;
        var table = new uint[256];
        
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ polynomial : crc >> 1;
            }
            table[i] = crc;
        }
        
        return table;
    }
    
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        
        return crc ^ 0xFFFFFFFF;
    }
    
    public static bool Verify(ReadOnlySpan<byte> data, uint expectedCrc)
    {
        return Compute(data) == expectedCrc;
    }
}

/// <summary>
/// High-resolution timestamp utilities
/// </summary>
public static class TimestampHelper
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    
    public static long ToUnixTimestampMs(DateTime dateTime)
    {
        return (long)(dateTime.ToUniversalTime() - UnixEpoch).TotalMilliseconds;
    }
    
    public static DateTime FromUnixTimestampMs(long timestampMs)
    {
        return UnixEpoch.AddMilliseconds(timestampMs);
    }
    
    public static long UtcNowMs => ToUnixTimestampMs(DateTime.UtcNow);
}

/// <summary>
/// Topic routing pattern matcher for exchange routing
/// </summary>
public static class TopicMatcher
{
    /// <summary>
    /// Matches a routing key against a topic pattern
    /// * matches exactly one word
    /// # matches zero or more words
    /// </summary>
    public static bool IsMatch(string pattern, string routingKey)
    {
        if (string.IsNullOrEmpty(pattern) && string.IsNullOrEmpty(routingKey))
            return true;
            
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(routingKey))
            return false;
        
        var patternParts = pattern.Split('.');
        var routingParts = routingKey.Split('.');
        
        return IsMatchInternal(patternParts, 0, routingParts, 0);
    }
    
    private static bool IsMatchInternal(string[] pattern, int pIndex, string[] routing, int rIndex)
    {
        // End of both patterns
        if (pIndex >= pattern.Length && rIndex >= routing.Length)
            return true;
        
        // End of pattern but routing has more parts
        if (pIndex >= pattern.Length)
            return false;
        
        var currentPattern = pattern[pIndex];
        
        // Hash matches zero or more words
        if (currentPattern == "#")
        {
            // Last pattern part, hash matches everything remaining
            if (pIndex == pattern.Length - 1)
                return true;
            
            // Try matching rest of pattern with current routing position
            for (int i = rIndex; i <= routing.Length; i++)
            {
                if (IsMatchInternal(pattern, pIndex + 1, routing, i))
                    return true;
            }
            
            return false;
        }
        
        // End of routing but pattern has more parts (not hash)
        if (rIndex >= routing.Length)
            return false;
        
        var currentRouting = routing[rIndex];
        
        // Star matches exactly one word
        if (currentPattern == "*")
        {
            return IsMatchInternal(pattern, pIndex + 1, routing, rIndex + 1);
        }
        
        // Exact match required
        if (currentPattern == currentRouting)
        {
            return IsMatchInternal(pattern, pIndex + 1, routing, rIndex + 1);
        }
        
        return false;
    }
}

/// <summary>
/// Extension methods for common operations
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Converts a string to UTF-8 bytes
    /// </summary>
    public static byte[] ToUtf8Bytes(this string str)
    {
        return System.Text.Encoding.UTF8.GetBytes(str);
    }
    
    /// <summary>
    /// Converts UTF-8 bytes to string
    /// </summary>
    public static string FromUtf8Bytes(this ReadOnlySpan<byte> bytes)
    {
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
    
    /// <summary>
    /// Safely gets a value from a dictionary
    /// </summary>
    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue? defaultValue = default)
        where TKey : notnull
    {
        return dict.TryGetValue(key, out var value) ? value : defaultValue;
    }
    
    /// <summary>
    /// Checks if a DateTime is expired given a TTL
    /// </summary>
    public static bool IsExpired(this DateTime timestamp, TimeSpan? ttl)
    {
        return ttl.HasValue && DateTime.UtcNow - timestamp > ttl.Value;
    }
    
    /// <summary>
    /// Gets the remaining TTL for a timestamp
    /// </summary>
    public static TimeSpan? GetRemainingTtl(this DateTime timestamp, TimeSpan? ttl)
    {
        if (!ttl.HasValue) return null;
        
        var elapsed = DateTime.UtcNow - timestamp;
        var remaining = ttl.Value - elapsed;
        
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

/// <summary>
/// Thread-safe counter for generating unique IDs
/// </summary>
public class AtomicCounter
{
    private long _value;
    
    public AtomicCounter(long initialValue = 0)
    {
        _value = initialValue;
    }
    
    public long Next() => Interlocked.Increment(ref _value);
    
    public long Current => Interlocked.Read(ref _value);
    
    public void Reset(long value = 0) => Interlocked.Exchange(ref _value, value);
}

/// <summary>
/// Simple rate limiter for connection throttling
/// </summary>
public class RateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _timeWindow;
    private readonly Queue<DateTime> _requests = new();
    private readonly object _lock = new();
    
    public RateLimiter(int maxRequests, TimeSpan timeWindow)
    {
        _maxRequests = maxRequests;
        _timeWindow = timeWindow;
    }
    
    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var cutoff = now - _timeWindow;
            
            // Remove old requests
            while (_requests.Count > 0 && _requests.Peek() < cutoff)
            {
                _requests.Dequeue();
            }
            
            // Check if we can accept new request
            if (_requests.Count >= _maxRequests)
                return false;
            
            _requests.Enqueue(now);
            return true;
        }
    }
}