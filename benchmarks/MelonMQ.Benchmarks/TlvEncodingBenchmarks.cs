using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MelonMQ.Common;

namespace MelonMQ.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class TlvEncodingBenchmarks
{
    private List<byte> _buffer;
    private Dictionary<string, object> _smallDict;
    private Dictionary<string, object> _mediumDict;
    private Dictionary<string, object> _largeDict;
    private byte[] _serializedSmallDict;
    private byte[] _serializedMediumDict;
    private byte[] _serializedLargeDict;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new List<byte>(4096);

        // Small dictionary (typical message headers)
        _smallDict = new Dictionary<string, object>
        {
            ["messageId"] = Guid.NewGuid().ToString(),
            ["priority"] = 5,
            ["persistent"] = true
        };

        // Medium dictionary
        _mediumDict = new Dictionary<string, object>();
        for (int i = 0; i < 20; i++)
        {
            _mediumDict[$"key{i}"] = $"value{i}";
            _mediumDict[$"num{i}"] = i;
            _mediumDict[$"flag{i}"] = i % 2 == 0;
        }

        // Large dictionary
        _largeDict = new Dictionary<string, object>();
        for (int i = 0; i < 100; i++)
        {
            _largeDict[$"property{i}"] = $"This is a longer value for property {i} with some additional text";
            _largeDict[$"counter{i}"] = i * 1000;
            _largeDict[$"enabled{i}"] = i % 3 == 0;
        }

        // Pre-serialize for read benchmarks
        _buffer.Clear();
        TlvEncoding.WriteDictionary(_buffer, _smallDict);
        _serializedSmallDict = _buffer.ToArray();

        _buffer.Clear();
        TlvEncoding.WriteDictionary(_buffer, _mediumDict);
        _serializedMediumDict = _buffer.ToArray();

        _buffer.Clear();
        TlvEncoding.WriteDictionary(_buffer, _largeDict);
        _serializedLargeDict = _buffer.ToArray();
    }

    [Benchmark]
    public void WriteString()
    {
        _buffer.Clear();
        TlvEncoding.WriteString(_buffer, "This is a test string for benchmarking purposes");
    }

    [Benchmark]
    public void WriteInt32()
    {
        _buffer.Clear();
        TlvEncoding.WriteInt32(_buffer, 12345);
    }

    [Benchmark]
    public void WriteBoolean()
    {
        _buffer.Clear();
        TlvEncoding.WriteBoolean(_buffer, true);
    }

    [Benchmark]
    public void WriteByteArray()
    {
        _buffer.Clear();
        var data = new byte[256];
        Array.Fill(data, (byte)0xAB);
        TlvEncoding.WriteByteArray(_buffer, data);
    }

    [Benchmark]
    public void WriteSmallDictionary()
    {
        _buffer.Clear();
        TlvEncoding.WriteDictionary(_buffer, _smallDict);
    }

    [Benchmark]
    public void WriteMediumDictionary()
    {
        _buffer.Clear();
        TlvEncoding.WriteDictionary(_buffer, _mediumDict);
    }

    [Benchmark]
    public void WriteLargeDictionary()
    {
        _buffer.Clear();
        TlvEncoding.WriteDictionary(_buffer, _largeDict);
    }

    [Benchmark]
    public string ReadString()
    {
        _buffer.Clear();
        TlvEncoding.WriteString(_buffer, "This is a test string for benchmarking purposes");
        var (result, _) = TlvEncoding.ReadString(_buffer.ToArray().AsSpan());
        return result;
    }

    [Benchmark]
    public int ReadInt32()
    {
        _buffer.Clear();
        TlvEncoding.WriteInt32(_buffer, 12345);
        var (result, _) = TlvEncoding.ReadInt32(_buffer.ToArray().AsSpan());
        return result;
    }

    [Benchmark]
    public bool ReadBoolean()
    {
        _buffer.Clear();
        TlvEncoding.WriteBoolean(_buffer, true);
        var (result, _) = TlvEncoding.ReadBoolean(_buffer.ToArray().AsSpan());
        return result;
    }

    [Benchmark]
    public Dictionary<string, object> ReadSmallDictionary()
    {
        var (result, _) = TlvEncoding.ReadDictionary(_serializedSmallDict.AsSpan());
        return result;
    }

    [Benchmark]
    public Dictionary<string, object> ReadMediumDictionary()
    {
        var (result, _) = TlvEncoding.ReadDictionary(_serializedMediumDict.AsSpan());
        return result;
    }

    [Benchmark]
    public Dictionary<string, object> ReadLargeDictionary()
    {
        var (result, _) = TlvEncoding.ReadDictionary(_serializedLargeDict.AsSpan());
        return result;
    }

    [Benchmark]
    public Dictionary<string, object> WriteAndReadSmallDictionary()
    {
        _buffer.Clear();
        TlvEncoding.WriteDictionary(_buffer, _smallDict);
        var (result, _) = TlvEncoding.ReadDictionary(_buffer.ToArray().AsSpan());
        return result;
    }

    [Benchmark]
    public Dictionary<string, object> WriteAndReadMediumDictionary()
    {
        _buffer.Clear();
        TlvEncoding.WriteDictionary(_buffer, _mediumDict);
        var (result, _) = TlvEncoding.ReadDictionary(_buffer.ToArray().AsSpan());
        return result;
    }
}