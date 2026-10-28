using RecursiveAI.OneCompress.Filters;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>The branch and delta filters are exact inverses of themselves in the other direction, and any split of the data gives the same bytes.</summary>
public class FilterTests
{
    public static TheoryData<BranchArchitecture> Architectures() => new(Enum.GetValues<BranchArchitecture>());

    // Random bytes with a few instruction patterns of every kind written in, so every branch of every filter is reached.
    private static byte[] Sample(int seed, int size)
    {
        var rng = new Random(seed);
        byte[] data = new byte[size];
        rng.NextBytes(data);
        for (int i = 0; i + 16 <= size; i += 4 + (rng.Next(6) * 2))
        {
            switch (rng.Next(12))
            {
                case 0: data[i] = 0xE8; data[i + 4] = (byte)(rng.Next(2) * 0xFF); break;
                case 1: data[i + 3] = 0xEB; break;
                case 2: data[i] = 0x48; data[i + 3] = (byte)((data[i + 3] & ~3) | 1); break;
                case 3: data[i] = 0x40; data[i + 1] &= 0x3F; break;
                case 4: data[i + 1] = (byte)((data[i + 1] & 7) | 0xF0); data[i + 3] = (byte)((data[i + 3] & 7) | 0xF8); break;
                case 5: data[i + 3] = (byte)(0x94 | (data[i + 3] & 3)); break;
                case 6: data[i + 3] = (byte)((data[i + 3] & 0x60) | 0x90); data[i + 2] &= 0x03; break;
                case 7: data[i] = 0xEF; data[i + 1] &= 0xF0; break;
                case 8: BitConverter.TryWriteBytes(data.AsSpan(i), 0x17u | (uint)(rng.Next(1, 31) << 7) | ((uint)rng.Next() << 12)); break;
                case 9: BitConverter.TryWriteBytes(data.AsSpan(i), 0x3117u | ((uint)rng.Next() << 14)); break;
                case 10: data[i] = (byte)(rng.Next(16) * 2 | 1); break;
                default: break;
            }
        }

        return data;
    }

    private static byte[] Chunked(BranchArchitecture arch, uint start, bool encode, byte[] input, Random rng)
    {
        var filter = new BranchFilter(arch, start, encode);
        byte[] buffer = new byte[input.Length + 64];
        int have = 0;
        int fed = 0;
        int done = 0;
        while (fed < input.Length)
        {
            int n = Math.Min(rng.Next(1, 200), input.Length - fed);
            input.AsSpan(fed, n).CopyTo(buffer.AsSpan(have));
            fed += n;
            have += n;
            int c = filter.Convert(buffer.AsSpan(done, have - done));
            done += c;
        }

        return buffer.AsSpan(0, input.Length).ToArray();
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void EncodeThenDecodeGivesTheInput(BranchArchitecture arch)
    {
        foreach (uint start in new uint[] { 0, 16, 4096 })
        {
            byte[] input = Sample((int)arch * 7 + 1, 50000);
            byte[] data = input.ToArray();
            new BranchFilter(arch, start, encode: true).Convert(data);
            Assert.NotEqual(input, data);
            new BranchFilter(arch, start, encode: false).Convert(data);
            Assert.Equal(input, data);
        }
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void AnySplitGivesTheBytesOfOneCall(BranchArchitecture arch)
    {
        foreach (bool encode in new[] { true, false })
        {
            byte[] input = Sample((int)arch * 13 + 5, 30000);
            byte[] whole = input.ToArray();
            new BranchFilter(arch, 32, encode).Convert(whole);
            for (int seed = 0; seed < 5; seed++)
            {
                Assert.Equal(whole, Chunked(arch, 32, encode, input, new Random(seed)));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void ResetStartsOver(BranchArchitecture arch)
    {
        byte[] input = Sample(99, 5000);
        var filter = new BranchFilter(arch, 0, encode: true);
        byte[] a = input.ToArray();
        filter.Convert(a);
        filter.Reset();
        byte[] b = input.ToArray();
        filter.Convert(b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void StartOffsetMustBeAlignedAndShortInputsAreLeftAlone()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BranchFilter(BranchArchitecture.Arm, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BranchFilter(BranchArchitecture.Ia64, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BranchFilter(BranchArchitecture.RiscV, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BranchFilter((BranchArchitecture)99));
        Assert.Equal(1, BranchFilter.GetAlignment(BranchArchitecture.X86));
        Assert.Equal(16, BranchFilter.GetAlignment(BranchArchitecture.Ia64));
        foreach (BranchArchitecture arch in Enum.GetValues<BranchArchitecture>())
        {
            byte[] tiny = [0xE8, 1, 2];
            Assert.Equal(0, new BranchFilter(arch).Convert(tiny));
            Assert.Equal(0, new BranchFilter(arch).Convert([]));
            Assert.Equal(new byte[] { 0xE8, 1, 2 }, tiny);
        }
    }

    [Fact]
    public void KnownX86CallIsMadeAbsolute()
    {
        // CALL +0x10 at offset 0: the target 5 + 0x10 = 0x15 appears in the encoded bytes.
        byte[] code = [0xE8, 0x10, 0, 0, 0, 0x90, 0x90, 0x90, 0x90, 0x90];
        new BranchFilter(BranchArchitecture.X86, 0, encode: true).Convert(code);
        Assert.Equal(new byte[] { 0xE8, 0x15, 0, 0, 0 }, code[..5]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(256)]
    public void DeltaRoundTripsAndAnySplitIsTheSame(int distance)
    {
        byte[] input = Sample(distance, 10000);
        byte[] data = input.ToArray();
        new DeltaFilter(distance, encode: true).Convert(data);
        byte[] encoded = data.ToArray();
        var decoder = new DeltaFilter(distance);
        var rng = new Random(distance);
        for (int at = 0; at < data.Length;)
        {
            int n = Math.Min(rng.Next(1, 300), data.Length - at);
            decoder.Convert(data.AsSpan(at, n));
            at += n;
        }

        Assert.Equal(input, data);
        var encoder = new DeltaFilter(distance, encode: true);
        byte[] again = input.ToArray();
        encoder.Convert(again.AsSpan(0, 4000));
        encoder.Convert(again.AsSpan(4000));
        Assert.Equal(encoded, again);
    }

    [Fact]
    public void DeltaDistanceMustBeOneTo256AndMatchesTheSpecExample()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaFilter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeltaFilter(257));
        byte[] data = [10, 20, 30, 41];
        new DeltaFilter(1, encode: true).Convert(data);
        Assert.Equal(new byte[] { 10, 10, 10, 11 }, data);
        new DeltaFilter(1).Convert(data);
        Assert.Equal(new byte[] { 10, 20, 30, 41 }, data);
    }
}
