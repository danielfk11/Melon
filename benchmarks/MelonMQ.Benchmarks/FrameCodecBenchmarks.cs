using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MelonMQ.Common;
using System.Buffers;

namespace MelonMQ.Benchmarks;

[Config(typeof(Config))]
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class FrameCodecBenchmarks
{
    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default.WithId("Default"));
        }
    }

    private ProtocolFrame _smallFrame;
    private ProtocolFrame _mediumFrame;
    private ProtocolFrame _largeFrame;
    private ArrayBufferWriter<byte> _bufferWriter;
    private byte[] _serializedSmallFrame;
    private byte[] _serializedMediumFrame;
    private byte[] _serializedLargeFrame;

    [GlobalSetup]
    public void Setup()
    {
        // Small frame (typical control message)
        _smallFrame = new ProtocolFrame(
            Type: FrameType.AuthRequest,
            Flags: 0x01,
            CorrelationId: 12345,
            Payload: "user:password"u8.ToArray()
        );

        // Medium frame (typical message)
        var mediumPayload = new byte[1024];
        Array.Fill(mediumPayload, (byte)'M');
        _mediumFrame = new ProtocolFrame(
            Type: FrameType.PublishMessage,
            Flags: 0x02,
            CorrelationId: 67890,
            Payload: mediumPayload
        );

        // Large frame (large message)
        var largePayload = new byte[64 * 1024]; // 64KB
        Array.Fill(largePayload, (byte)'L');
        _largeFrame = new ProtocolFrame(
            Type: FrameType.PublishMessage,
            Flags: 0x03,
            CorrelationId: 999999,
            Payload: largePayload
        );

        _bufferWriter = new ArrayBufferWriter<byte>();

        // Pre-serialize frames for read benchmarks
        FrameCodec.WriteFrame(_bufferWriter, _smallFrame);
        _serializedSmallFrame = _bufferWriter.WrittenSpan.ToArray();
        _bufferWriter.Clear();

        FrameCodec.WriteFrame(_bufferWriter, _mediumFrame);
        _serializedMediumFrame = _bufferWriter.WrittenSpan.ToArray();
        _bufferWriter.Clear();

        FrameCodec.WriteFrame(_bufferWriter, _largeFrame);
        _serializedLargeFrame = _bufferWriter.WrittenSpan.ToArray();
        _bufferWriter.Clear();
    }

    [Benchmark]
    public void WriteSmallFrame()
    {
        _bufferWriter.Clear();
        FrameCodec.WriteFrame(_bufferWriter, _smallFrame);
    }

    [Benchmark]
    public void WriteMediumFrame()
    {
        _bufferWriter.Clear();
        FrameCodec.WriteFrame(_bufferWriter, _mediumFrame);
    }

    [Benchmark]
    public void WriteLargeFrame()
    {
        _bufferWriter.Clear();
        FrameCodec.WriteFrame(_bufferWriter, _largeFrame);
    }

    [Benchmark]
    public bool ReadSmallFrame()
    {
        return FrameCodec.TryReadFrame(_serializedSmallFrame, out _, out _);
    }

    [Benchmark]
    public bool ReadMediumFrame()
    {
        return FrameCodec.TryReadFrame(_serializedMediumFrame, out _, out _);
    }

    [Benchmark]
    public bool ReadLargeFrame()
    {
        return FrameCodec.TryReadFrame(_serializedLargeFrame, out _, out _);
    }

    [Benchmark]
    public void WriteAndReadSmallFrame()
    {
        _bufferWriter.Clear();
        FrameCodec.WriteFrame(_bufferWriter, _smallFrame);
        FrameCodec.TryReadFrame(_bufferWriter.WrittenMemory, out _, out _);
    }

    [Benchmark]
    public void WriteAndReadMediumFrame()
    {
        _bufferWriter.Clear();
        FrameCodec.WriteFrame(_bufferWriter, _mediumFrame);
        FrameCodec.TryReadFrame(_bufferWriter.WrittenMemory, out _, out _);
    }

    [Benchmark]
    public void WriteAndReadLargeFrame()
    {
        _bufferWriter.Clear();
        FrameCodec.WriteFrame(_bufferWriter, _largeFrame);
        FrameCodec.TryReadFrame(_bufferWriter.WrittenMemory, out _, out _);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bufferWriter?.Dispose();
    }
}