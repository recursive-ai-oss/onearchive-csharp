using BenchmarkDotNet.Attributes;
using RecursiveAI.OneCompress.Checksums;
using Hashing = System.IO.Hashing;

namespace RecursiveAI.Bench.Micro;

/// <summary>Ours against System.IO.Hashing, over buffers that fit L1, L2 and fall out of cache.</summary>
[BenchmarkCategory("Checksum")]
public class ChecksumBenchmarks
{
    private byte[] _data = [];

    [Params(4096, 65536, 16 * 1024 * 1024)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size];
        new Random(1).NextBytes(_data);
    }

    [Benchmark] public uint Crc32_Ours() => Crc32.Compute(_data);
    [Benchmark] public uint Crc32_SystemIoHashing() => Hashing.Crc32.HashToUInt32(_data);
    [Benchmark] public uint Crc32C_Ours() => Crc32C.Compute(_data);
    [Benchmark] public ulong Crc64_Ours() => Crc64.Compute(_data);
    [Benchmark] public ulong Crc64_SystemIoHashing() => Hashing.Crc64.HashToUInt64(_data);
    [Benchmark] public uint Adler32_Ours() => Adler32.Compute(_data);
    [Benchmark] public uint XxHash32_Ours() => XxHash32.Compute(_data);
    [Benchmark] public uint XxHash32_SystemIoHashing() => Hashing.XxHash32.HashToUInt32(_data);
    [Benchmark] public ulong XxHash64_Ours() => XxHash64.Compute(_data);
    [Benchmark] public ulong XxHash64_SystemIoHashing() => Hashing.XxHash64.HashToUInt64(_data);
}
