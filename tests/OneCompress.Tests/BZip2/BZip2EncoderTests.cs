using RecursiveAI.OneCompress.BZip2;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>The bzip2 encoder: its parts, round trips through our decoder, chunking, streams. Reference-tool interop is in <see cref="BZip2EncoderInteropTests"/>.</summary>
public class BZip2EncoderTests
{
    internal static byte[] Text(int length, int seed)
    {
        var rng = new Random(seed);
        string[] words = ["the", "of", "and", "compression", "block", "sorting", "transform", "huffman", "table", "stream", "a", "to", "in", "is", "that", "it", "was", "for", "on", "are"];
        var sb = new System.Text.StringBuilder();
        while (sb.Length < length)
        {
            sb.Append(words[rng.Next(words.Length)]).Append(rng.Next(12) == 0 ? ".\n" : " ");
        }

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString())[..length];
    }

    internal static byte[] Random(int length, int seed)
    {
        byte[] b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    internal static byte[] NoRuns(int length, int seed)
    {
        var rng = new Random(seed);
        byte[] b = new byte[length];
        int prev = -1;
        for (int i = 0; i < length; i++)
        {
            int v;
            do
            {
                v = rng.Next(256);
            }
            while (v == prev);
            b[i] = (byte)v;
            prev = v;
        }

        return b;
    }

    public static IEnumerable<object[]> SmallCases()
    {
        var cases = new List<(string, byte[])>
        {
            ("empty", []),
            ("one", [42]),
            ("two", [1, 1]),
            ("four-equal", [7, 7, 7, 7]),
            ("text-20k", Text(20_000, 1)),
            ("random-50k", Random(50_000, 2)),
            ("alphabet", Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray()),
            ("periodic", Enumerable.Range(0, 5000).Select(static i => (byte)("abc"[i % 3])).ToArray()),
        };
        foreach (int run in new[] { 3, 4, 5, 8, 254, 255, 256, 258, 259, 260, 263, 510, 511, 1000, 100_000 })
        {
            cases.Add(($"run-{run}", [.. "xy"u8, .. Enumerable.Repeat((byte)'a', run), .. "z"u8]));
        }

        foreach ((string name, byte[] data) in cases)
        {
            for (int level = 1; level <= 9; level += level < 3 ? 1 : 3)
            {
                yield return [name, level, data];
            }
        }
    }

    [Theory]
    [MemberData(nameof(SmallCases))]
    public void RoundTrips(string name, int level, byte[] data)
    {
        Assert.NotNull(name);
        byte[] packed = BZ.Compress(data, new BZip2Options { Level = level });
        Assert.Equal((byte)'B', packed[0]);
        Assert.Equal((byte)('0' + level), packed[3]);
        Assert.Equal(data, BZ.Decompress(packed));
    }

    [Fact]
    public void EmptyInputIsFourteenBytes()
    {
        byte[] packed = BZ.Compress([]);
        Assert.Equal(14, packed.Length);
        Assert.Equal("BZh9"u8.ToArray(), packed[..4]);
        Assert.Equal([0x17, 0x72, 0x45, 0x38, 0x50, 0x90, 0, 0, 0, 0], packed[4..]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public void BlockBoundaries(int level)
    {
        int capacity = (level * 100_000) - 19;
        foreach (int length in new[] { capacity - 1, capacity, capacity + 1, (2 * capacity) + 1 })
        {
            byte[] data = NoRuns(length, length);
            byte[] packed = BZ.Compress(data, new BZip2Options { Level = level });
            Assert.Equal(data, BZ.Decompress(packed));
            int blocks = CountBlocks(packed);
            Assert.Equal((length + capacity - 1) / capacity, blocks);
        }
    }

    // Counts block magics at any bit offset: good enough for random-looking data where a false hit is a 2^-48 event.
    private static int CountBlocks(byte[] packed)
    {
        int count = 0;
        ulong window = 0;
        for (int i = 0; i < packed.Length; i++)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                window = ((window << 1) | (uint)((packed[i] >> bit) & 1)) & 0xFFFFFFFFFFFF;
                if (window == 0x314159265359 && i >= 6)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void LongRunsSplitAcrossBlocks()
    {
        // A run that straddles the end of a level-1 block must be cut into pieces and still decode.
        for (int head = 99_900; head <= 99_990; head += 3)
        {
            byte[] data = [.. NoRuns(head, head), .. Enumerable.Repeat((byte)'z', 2000), .. "tail"u8];
            byte[] packed = BZ.Compress(data, new BZip2Options { Level = 1 });
            Assert.Equal(data, BZ.Decompress(packed));
        }
    }

    [Fact]
    public void OutputDoesNotDependOnHowInputIsCut()
    {
        byte[] data = [.. Text(150_000, 5), .. Enumerable.Repeat((byte)0, 3000), .. Random(5000, 6), .. Text(80_000, 7)];
        byte[] whole = BZ.Compress(data, new BZip2Options { Level = 1 });
        foreach (int piece in new[] { 1, 7, 4096, 99_981, 100_000 })
        {
            foreach (int room in new[] { 1, 13, 70_000 })
            {
                var encoder = new BZip2Encoder(new BZip2Options { Level = 1 });
                var output = new List<byte>();
                byte[] dest = new byte[room];
                int at = 0;
                bool done = false;
                while (!done)
                {
                    int take = Math.Min(piece, data.Length - at);
                    bool final = at + take == data.Length;
                    System.Buffers.OperationStatus status = encoder.Compress(data.AsSpan(at, take), dest, out int c, out int w, final);
                    output.AddRange(dest.AsSpan(0, w).ToArray());
                    at += c;
                    done = status == System.Buffers.OperationStatus.Done;
                    if (piece == 1 && room == 1 && output.Count > 3_000_000)
                    {
                        Assert.Fail("runaway");
                    }
                }

                Assert.True(whole.AsSpan().SequenceEqual(output.ToArray()), $"piece {piece}, room {room}");
            }
        }
    }

    [Fact]
    public void TryCompressReportsSmallDestination()
    {
        byte[] data = Text(10_000, 3);
        Assert.False(BZ.TryCompress(data, new byte[10], out int n));
        Assert.Equal(0, n);
        byte[] big = new byte[20_000];
        Assert.True(BZ.TryCompress(data, big, out n));
        Assert.Equal(data, BZ.Decompress(big.AsSpan(0, n)));
    }

    [Fact]
    public void ConcatenatedStreamsDecode()
    {
        byte[] a = Text(30_000, 1);
        byte[] b = Random(1000, 2);
        byte[] joined = [.. BZ.Compress(a, new BZip2Options { Level = 2 }), .. BZ.Compress([]), .. BZ.Compress(b, new BZip2Options { Level = 7 })];
        Assert.Equal([.. a, .. b], BZ.Decompress(joined));
    }

    [Fact]
    public void ResetStartsANewStream()
    {
        var encoder = new BZip2Encoder(new BZip2Options { Level = 3 });
        byte[] dest = new byte[100_000];
        byte[] data = Text(5000, 9);
        encoder.Compress(data, dest, out _, out int w1, true);
        Assert.Throws<InvalidOperationException>(() => encoder.Compress(data, dest, out _, out _, true));
        encoder.Reset();
        encoder.Compress(data, dest.AsSpan(w1), out _, out int w2, true);
        Assert.Equal([.. data, .. data], BZ.Decompress(dest.AsSpan(0, w1 + w2)));
    }

    [Fact]
    public async Task StreamRoundTrips()
    {
        byte[] data = [.. Text(250_000, 11), .. Random(10_000, 12)];
        var ms = new MemoryStream();
        using (var z = new BZip2Stream(ms, new BZip2Options { Level = 1 }, leaveOpen: true))
        {
            Assert.True(z.CanWrite);
            Assert.False(z.CanRead);
            for (int at = 0; at < data.Length; at += 7001)
            {
                z.Write(data, at, Math.Min(7001, data.Length - at));
            }

            z.Flush();
            Assert.Throws<NotSupportedException>(() => z.Read(new byte[1], 0, 1));
        }

        Assert.Equal(BZ.Compress(data, new BZip2Options { Level = 1 }), ms.ToArray());

        var ms2 = new MemoryStream();
        await using (var z = new BZip2Stream(ms2, new BZip2Options { Level = 1 }, leaveOpen: true))
        {
            await z.WriteAsync(data.AsMemory(0, 100_000), TestContext.Current.CancellationToken);
            await z.FlushAsync(TestContext.Current.CancellationToken);
            await z.WriteAsync(data.AsMemory(100_000), TestContext.Current.CancellationToken);
        }

        Assert.Equal(ms.ToArray(), ms2.ToArray());

        ms.Position = 0;
        using var reader = new BZip2Stream(ms);
        var back = new MemoryStream();
        reader.CopyTo(back);
        Assert.Equal(data, back.ToArray());
    }

    [Fact]
    public void LevelIsValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BZip2Options { Level = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BZip2Options { Level = 10 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BZip2Options { MaxCodeLength = 21 });
    }

    // ---- tier 3 parts ----

    [Fact]
    public void SorterMatchesPlainRotationSort()
    {
        var sorter = new BZip2BlockSorter();
        var rng = new Random(77);
        for (int trial = 0; trial < 600; trial++)
        {
            int n = 1 + rng.Next(trial < 400 ? 40 : 600);
            int alphabet = 1 + rng.Next(trial % 4 == 0 ? 1 : 4);
            byte[] block = new byte[n];
            if (trial % 5 == 0)
            {
                int period = 1 + rng.Next(6);
                byte[] unit = new byte[period];
                for (int i = 0; i < period; i++)
                {
                    unit[i] = (byte)rng.Next(alphabet + 1);
                }

                for (int i = 0; i < n; i++)
                {
                    block[i] = unit[i % period];
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    block[i] = (byte)rng.Next(alphabet + 1);
                }
            }

            int[] rotations = Enumerable.Range(0, n).ToArray();
            Array.Sort(rotations, (x, y) =>
            {
                for (int k = 0; k < n; k++)
                {
                    int c = block[(x + k) % n].CompareTo(block[(y + k) % n]);
                    if (c != 0)
                    {
                        return c;
                    }
                }

                return x.CompareTo(y);
            });
            byte[] expected = rotations.Select(p => block[(p + n - 1) % n]).ToArray();
            byte[] last = new byte[n];
            int row = sorter.Sort(block, last);
            Assert.Equal(expected, last);
            Assert.Equal(Array.IndexOf(rotations, 0), row);
        }
    }

    [Fact]
    public void HuffmanBuilderLimitsLengthsAndFillsTheCodeSpace()
    {
        var builder = new BZip2HuffmanBuilder();
        // Fibonacci-like frequencies need a very deep tree.
        int[] freq = new int[40];
        long a = 1;
        long b = 1;
        for (int i = 0; i < freq.Length; i++)
        {
            freq[i] = (int)Math.Min(a, 1_000_000_000);
            (a, b) = (b, a + b);
        }

        foreach (int limit in new[] { 9, 12, 17, 20 })
        {
            byte[] lengths = new byte[freq.Length];
            builder.Build(freq, lengths, limit);
            Assert.True(lengths.Max() <= limit && lengths.Min() >= 1);
            double kraft = lengths.Sum(static l => Math.Pow(2, -l));
            Assert.True(Math.Abs(kraft - 1) < 1e-12, $"limit {limit}: Kraft sum {kraft}");
            new BZip2HuffmanTable().Build(lengths);
        }

        // Zero counts get a length too.
        byte[] l2 = new byte[258];
        builder.Build(new int[258], l2);
        Assert.All(l2, static l => Assert.InRange(l, (byte)1, (byte)17));
    }

    [Fact]
    public void RunLengthEncoderCodesRunsAsTheToolDoes()
    {
        var rle = new BZip2RunLengthEncoder(9);
        byte[] data = [.. "x"u8, .. Enumerable.Repeat((byte)'a', 259), .. "y"u8];
        Assert.Equal(data.Length, rle.Add(data));
        ReadOnlySpan<byte> block = rle.Finish(out uint crc);
        Assert.Equal(BZip2Crc.Compute(data), crc);
        // Facts F2: 259 copies are four, count 251, four, count 0.
        Assert.Equal([.. "x"u8, 97, 97, 97, 97, 251, 97, 97, 97, 97, 0, .. "y"u8], block.ToArray());
    }
}
