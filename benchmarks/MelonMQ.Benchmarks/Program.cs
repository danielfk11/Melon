using BenchmarkDotNet.Running;

namespace MelonMQ.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<FrameCodecBenchmarks>(args);
        BenchmarkRunner.Run<TlvEncodingBenchmarks>(args);
        BenchmarkRunner.Run<QueueOperationsBenchmarks>(args);
        BenchmarkRunner.Run<EndToEndBenchmarks>(args);
    }
}