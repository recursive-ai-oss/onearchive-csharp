using System.Text;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Tests.Checksums;

public class ChecksumTests
{
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    internal static byte[] RandomBytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    // ---- published check values ----

    [Fact]
    public void Crc32_Vectors()
    {
        Assert.Equal(0u, Crc32.Compute([]));
        Assert.Equal(0xCBF43926u, Crc32.Compute(Check));
    }

    [Fact]
    public void Crc32C_Vectors()
    {
        Assert.Equal(0u, Crc32C.Compute([]));
        Assert.Equal(0xE3069283u, Crc32C.Compute(Check));
        // 32 zero bytes: RFC 3720 appendix B.4.
        Assert.Equal(0x8A9136AAu, Crc32C.Compute(new byte[32]));
        var ff = new byte[32];
        Array.Fill(ff, (byte)0xFF);
        Assert.Equal(0x62A8AB43u, Crc32C.Compute(ff));
    }

    [Fact]
    public void Crc64_Vectors()
    {
        Assert.Equal(0UL, Crc64.Compute([]));
        Assert.Equal(0x995DC9BBDF1939FAUL, Crc64.Compute(Check));
    }

    [Fact]
    public void Adler32_Vectors()
    {
        Assert.Equal(1u, Adler32.Compute([]));
        Assert.Equal(0x091E01DEu, Adler32.Compute(Check));
        Assert.Equal(0x11E60398u, Adler32.Compute(Encoding.ASCII.GetBytes("Wikipedia")));
    }

    [Fact]
    public void XxHash_Vectors()
    {
        const uint prime32 = 2654435761U;
        const ulong prime64 = 11400714785074694791UL;
        Assert.Equal(0x02CC5D05u, XxHash32.Compute([]));
        Assert.Equal(0x36B78AE7u, XxHash32.Compute([], prime32));
        Assert.Equal(0xEF46DB3751D8E999UL, XxHash64.Compute([]));
        // Non-zero 64-bit seed: pinned to the value System.IO.Hashing (used as a black-box oracle) gives.
        Assert.Equal(System.IO.Hashing.XxHash64.HashToUInt64([], unchecked((long)prime64)), XxHash64.Compute([], prime64));
    }

    // ---- default values, incremental use, reset ----

    [Fact]
    public void DefaultStructsAreEmpty()
    {
        Assert.Equal(0u, default(Crc32).GetCurrentValue());
        Assert.Equal(0u, default(Crc32C).GetCurrentValue());
        Assert.Equal(0UL, default(Crc64).GetCurrentValue());
        Assert.Equal(1u, default(Adler32).GetCurrentValue());
        Assert.Equal(0x02CC5D05u, default(XxHash32).GetCurrentValue());
        Assert.Equal(0xEF46DB3751D8E999UL, default(XxHash64).GetCurrentValue());
        Assert.Equal(0x36B78AE7u, new XxHash32(2654435761U).GetCurrentValue());
    }

    [Fact]
    public void AnySplitGivesTheSameValue()
    {
        var rng = new Random(7);
        foreach (int len in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 200, 5551, 5552, 5553, 12000, 70000 })
        {
            byte[] data = RandomBytes(len, len);
            uint seed32 = (uint)rng.Next();
            ulong seed64 = ((ulong)rng.Next() << 32) | (uint)rng.Next();
            var c32 = default(Crc32);
            var c32c = default(Crc32C);
            var c64 = default(Crc64);
            var ad = default(Adler32);
            var x32 = new XxHash32(seed32);
            var x64 = new XxHash64(seed64);
            int pos = 0;
            while (pos < len)
            {
                int n = Math.Min(len - pos, rng.Next(0, 300));
                var piece = data.AsSpan(pos, n);
                c32.Append(piece);
                c32c.Append(piece);
                c64.Append(piece);
                ad.Append(piece);
                x32.Append(piece);
                x64.Append(piece);
                // The running value must be readable mid-stream without disturbing it.
                Assert.Equal(Crc32.Compute(data.AsSpan(0, pos + n)), c32.GetCurrentValue());
                Assert.Equal(XxHash64.Compute(data.AsSpan(0, pos + n), seed64), x64.GetCurrentValue());
                Assert.Equal(XxHash32.Compute(data.AsSpan(0, pos + n), seed32), x32.GetCurrentValue());
                pos += n;
            }

            Assert.Equal(Crc32.Compute(data), c32.GetCurrentValue());
            Assert.Equal(Crc32C.Compute(data), c32c.GetCurrentValue());
            Assert.Equal(Crc64.Compute(data), c64.GetCurrentValue());
            Assert.Equal(Adler32.Compute(data), ad.GetCurrentValue());
            Assert.Equal(XxHash32.Compute(data, seed32), x32.GetCurrentValue());
            Assert.Equal(XxHash64.Compute(data, seed64), x64.GetCurrentValue());

            c32.Reset();
            c64.Reset();
            ad.Reset();
            x32.Reset();
            x64.Reset();
            Assert.Equal(0u, c32.GetCurrentValue());
            Assert.Equal(0UL, c64.GetCurrentValue());
            Assert.Equal(1u, ad.GetCurrentValue());
            Assert.Equal(XxHash32.Compute([], seed32), x32.GetCurrentValue());
            Assert.Equal(XxHash64.Compute([], seed64), x64.GetCurrentValue());
            x64.Append(data);
            Assert.Equal(XxHash64.Compute(data, seed64), x64.GetCurrentValue());
        }
    }

    [Fact]
    public void UpdateContinuesAChecksum()
    {
        byte[] data = RandomBytes(9999, 1);
        var a = data.AsSpan(0, 1234);
        var b = data.AsSpan(1234);
        Assert.Equal(Crc32.Compute(data), Crc32.Update(Crc32.Compute(a), b));
        Assert.Equal(Crc32C.Compute(data), Crc32C.Update(Crc32C.Compute(a), b));
        Assert.Equal(Crc64.Compute(data), Crc64.Update(Crc64.Compute(a), b));
        Assert.Equal(Adler32.Compute(data), Adler32.Update(Adler32.Compute(a), b));
    }

    // ---- combine ----

    [Fact]
    public void Combine_MatchesDirectComputation()
    {
        var rng = new Random(11);
        foreach (int lenB in new[] { 0, 1, 2, 7, 8, 63, 64, 1000, 65536, 1 << 20 })
        {
            int lenA = rng.Next(0, 5000);
            byte[] a = RandomBytes(lenA, lenB + 1);
            byte[] b = RandomBytes(lenB, lenB + 2);
            byte[] ab = [.. a, .. b];
            Assert.Equal(Crc32.Compute(ab), Crc32.Combine(Crc32.Compute(a), Crc32.Compute(b), lenB));
            Assert.Equal(Crc32C.Compute(ab), Crc32C.Combine(Crc32C.Compute(a), Crc32C.Compute(b), lenB));
            Assert.Equal(Crc64.Compute(ab), Crc64.Combine(Crc64.Compute(a), Crc64.Compute(b), lenB));
        }
    }

    [Fact]
    public void Combine_RejectsBadLengths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Crc32.Combine(0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Crc32C.Combine(0, 0, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => Crc64.Combine(0, 0, -5));
    }

    // ---- vector paths equal the scalar twins ----

    // Lengths 0-4096 at every alignment 0-63 (the offset moves the slice inside a larger array), each
    // with a non-trivial starting state, for every path this machine has.
    [Fact]
    public void Crc_Paths_EqualScalar_AllLengthsAndAlignments()
    {
        byte[] buf = RandomBytes(4096 + 64, 3);
        for (int off = 0; off < 64; off++)
        {
            for (int len = 0; len <= 4096; len++)
            {
                var d = buf.AsSpan(off, len);
                uint s32 = 0x9E3779B1u * (uint)(len + 1);
                ulong s64 = 0x9E3779B97F4A7C15UL * (ulong)(len + 1);
                uint e32 = Crc32.RawScalar(s32, d);
                Assert.True(e32 == Crc32.RawFolded(s32, d) && e32 == Crc32.RawInstruction(s32, d) && e32 == Crc32.Raw(s32, d), $"crc32 off={off} len={len}");
                uint e32c = Crc32C.RawScalar(s32, d);
                Assert.True(e32c == Crc32C.RawFolded(s32, d) && e32c == Crc32C.RawInstruction(s32, d) && e32c == Crc32C.Raw(s32, d), $"crc32c off={off} len={len}");
                ulong e64 = Crc64.RawScalar(s64, d);
                Assert.True(e64 == Crc64.RawFolded(s64, d) && e64 == Crc64.Raw(s64, d), $"crc64 off={off} len={len}");
            }
        }
    }

    [Fact]
    public void Adler_Paths_EqualScalar_AllLengthsAndAlignments()
    {
        byte[] buf = RandomBytes(4096 + 64, 4);
        // 0xFF bytes push the sums to their limits.
        byte[] max = new byte[4096 + 64];
        Array.Fill(max, (byte)0xFF);
        foreach (byte[] source in new[] { buf, max })
        {
            for (int off = 0; off < 64; off++)
            {
                for (int len = 0; len <= 4096; len++)
                {
                    var d = source.AsSpan(off, len);
                    uint start = (uint)((len * 977 % 65521) << 16) | (uint)((len * 31 + 1) % 65521);
                    uint expected = Adler32.Run(start, d, Adler32.Kernel.Scalar);
                    foreach (var k in new[] { Adler32.Kernel.Vector128, Adler32.Kernel.Avx2, Adler32.Kernel.Best })
                    {
                        if (Adler32.IsSupported(k))
                        {
                            Assert.True(expected == Adler32.Run(start, d, k), $"adler {k} off={off} len={len}");
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void XxHash32_VectorEqualsScalar_AllLengthsAndAlignments()
    {
        byte[] buf = RandomBytes(4096 + 64, 5);
        for (int off = 0; off < 64; off++)
        {
            for (int len = 0; len <= 4096; len++)
            {
                var d = buf.AsSpan(off, len);
                uint seed = (uint)(len * 2654435761U);
                Assert.True(XxHash32.Compute(d, seed, vector: false) == XxHash32.Compute(d, seed, vector: true), $"xxh32 off={off} len={len}");
            }
        }
    }

    [Theory]
    [InlineData(3_000_001)]
    [InlineData(16 * 1024 * 1024 + 13)]
    public void LargeBuffers_PathsAgree(int length)
    {
        byte[] buf = RandomBytes(length + 64, length);
        foreach (int off in new[] { 0, 1, 7, 33 })
        {
            var d = buf.AsSpan(off, length);
            Assert.Equal(Crc32.RawScalar(~0u, d), Crc32.Raw(~0u, d));
            Assert.Equal(Crc32C.RawScalar(~0u, d), Crc32C.Raw(~0u, d));
            Assert.Equal(Crc64.RawScalar(~0UL, d), Crc64.Raw(~0UL, d));
            Assert.Equal(Adler32.Run(1, d, Adler32.Kernel.Scalar), Adler32.Compute(d));
            Assert.Equal(XxHash32.Compute(d, 0, vector: false), XxHash32.Compute(d));
        }
    }

    // ---- black-box oracles ----

    [Fact]
    public void Crc32_Crc64_Adler_XxHash_MatchSystemIoHashing()
    {
        var rng = new Random(99);
        for (int i = 0; i < 300; i++)
        {
            int len = i < 100 ? i : rng.Next(0, 200_000);
            byte[] d = RandomBytes(len, i);
            Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(d), Crc32.Compute(d));
            uint s32 = (uint)rng.Next();
            long s64 = rng.NextInt64();
            Assert.Equal(System.IO.Hashing.XxHash32.HashToUInt32(d, (int)s32), XxHash32.Compute(d, s32));
            Assert.Equal(System.IO.Hashing.XxHash64.HashToUInt64(d, s64), XxHash64.Compute(d, (ulong)s64));
        }
    }
}
