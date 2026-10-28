using System.Buffers;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Deflate64;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Deflate64 encoder round trips through our decoder (7-Zip's acceptance is checked by the interop tests).</summary>
public class Deflate64EncoderTests
{
    public static TheoryData<int> Levels() => new(Enumerable.Range(0, 10));

    internal static byte[] Text(int length, int seed)
    {
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "the ", "of ", "and ", "\n"];
        var rng = new Random(seed);
        var bytes = new List<byte>(length + 16);
        while (bytes.Count < length)
        {
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(words[rng.Next(words.Length)] + " "));
        }

        return bytes.Take(length).ToArray();
    }

    /// <summary>Random blocks of 40 KiB repeated with gaps, so matches reach 40 to 65 KiB back; plus a long run.</summary>
    internal static byte[] FarMatches(int seed)
    {
        var rng = new Random(seed);
        byte[] a = new byte[50000];
        rng.NextBytes(a);
        byte[] b = new byte[15000];
        rng.NextBytes(b);
        var list = new List<byte>();
        list.AddRange(a);
        list.AddRange(b);
        list.AddRange(a); // distance 65000
        list.AddRange(new byte[70000]); // a run longer than 258 and than 64 KiB
        list.AddRange(a.Take(30000));
        list.AddRange(Enumerable.Repeat((byte)7, 300));
        return list.ToArray();
    }

    public static IEnumerable<object[]> Inputs()
    {
        var rng = new Random(5);
        byte[] random = new byte[100000];
        rng.NextBytes(random);
        yield return ["empty", Array.Empty<byte>()];
        yield return ["one", new byte[] { 42 }];
        yield return ["two", new byte[] { 1, 1 }];
        yield return ["three-same", new byte[] { 9, 9, 9 }];
        yield return ["text-5k", Text(5000, 1)];
        yield return ["text-300k", Text(300000, 2)];
        yield return ["random-100k", random];
        yield return ["zeros-1m", new byte[1 << 20]];
        yield return ["far", FarMatches(3)];
        yield return ["text-exact-chunk", Text((1 << 17) + 65536, 4)];
        yield return ["text-chunk-plus-one", Text((1 << 17) + 65536 + 1, 4)];
    }

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (object[] input in Inputs())
        {
            for (int level = 0; level <= 9; level++)
            {
                data.Add((string)input[0], level);
            }
        }

        return data;
    }

    private static byte[] Input(string name) => (byte[])Inputs().Single(i => (string)i[0] == name)[1];

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsThroughOurDecoder(string name, int level)
    {
        byte[] input = Input(name);
        byte[] packed = Deflate64.Deflate64.Compress(input, new Deflate64Options { Level = level });
        Assert.True(packed.Length <= Deflate64.Deflate64.GetMaxCompressedLength(input.Length));
        Assert.Equal(input, Deflate64.Deflate64.Decompress(packed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void OutputDoesNotDependOnHowCallsAreSplit(int level)
    {
        byte[] input = Text(400000, 11);
        byte[] whole = Deflate64.Deflate64.Compress(input, new Deflate64Options { Level = level });
        foreach ((int inPiece, int outPiece) in new[] { (1000, 77), (70001, 1 << 20), (1, 1 << 20) })
        {
            var encoder = new Deflate64Encoder(new Deflate64Options { Level = level });
            var result = new MemoryStream();
            byte[] buffer = new byte[outPiece];
            int pos = 0;
            int step = inPiece == 1 ? 3000 : inPiece; // a byte-at-a-time run over 400 KB is slow without testing more
            while (true)
            {
                var piece = input.AsSpan(pos, Math.Min(step, input.Length - pos));
                bool last = pos + piece.Length == input.Length;
                OperationStatus status = encoder.Compress(piece, buffer, out int c, out int w, last);
                result.Write(buffer, 0, w);
                pos += c;
                if (status == OperationStatus.Done)
                {
                    break;
                }

                if (status == OperationStatus.NeedMoreData)
                {
                    Assert.Equal(piece.Length, c);
                }
            }

            Assert.Equal(whole, result.ToArray());
        }
    }

    [Fact]
    public void SyncFlushMakesEverythingSoFarDecodable()
    {
        byte[] input = Text(100000, 9);
        var encoder = new Deflate64Encoder(new Deflate64Options { Level = 5 });
        byte[] buffer = new byte[200000];
        encoder.Compress(input.AsSpan(0, 60000), buffer, out int c, out int w1);
        Assert.Equal(60000, c);
        Assert.Equal(OperationStatus.Done, encoder.Flush(buffer.AsSpan(w1), out int w2));
        var decoder = new Deflate64Decoder();
        byte[] output = new byte[100000];
        decoder.Decompress(buffer.AsSpan(0, w1 + w2), output, out _, out int produced);
        Assert.Equal(60000, produced);
        Assert.Equal(input.AsSpan(0, 60000).ToArray(), output.AsSpan(0, 60000).ToArray());
        encoder.Compress(input.AsSpan(60000), buffer.AsSpan(w1 + w2), out _, out int w3, isFinalBlock: true);
        Assert.Equal(input, Deflate64.Deflate64.Decompress(buffer.AsSpan(0, w1 + w2 + w3)));
    }

    [Fact]
    public void ResetStartsAnIdenticalStream()
    {
        byte[] input = Text(200000, 12);
        var encoder = new Deflate64Encoder();
        byte[] a = new byte[300000];
        byte[] b = new byte[300000];
        encoder.Compress(input, a, out _, out int wa, true);
        encoder.Reset();
        encoder.Compress(input, b, out _, out int wb, true);
        Assert.Equal(a.AsSpan(0, wa).ToArray(), b.AsSpan(0, wb).ToArray());
    }

    [Fact]
    public void HigherLevelsDoNotCompressWorseOnText()
    {
        byte[] input = Text(300000, 21);
        int previous = int.MaxValue;
        foreach (int level in new[] { 1, 4, 9 })
        {
            int size = Deflate64.Deflate64.Compress(input, new Deflate64Options { Level = level }).Length;
            Assert.True(size <= previous, $"level {level}: {size} > {previous}");
            previous = size;
        }
    }

    [Fact]
    public void LongRunsUseTheLongLengthSymbol()
    {
        byte[] input = new byte[1 << 20];
        byte[] packed = Deflate64.Deflate64.Compress(input);
        Assert.True(packed.Length < 200, $"{packed.Length}");
        Assert.Equal(SHA256.HashData(input), SHA256.HashData(Deflate64.Deflate64.Decompress(packed)));
    }

    [Fact]
    public void TooSmallDestinationIsReportedAndResumable()
    {
        byte[] input = Text(50000, 8);
        var encoder = new Deflate64Encoder();
        var result = new MemoryStream();
        byte[] buffer = new byte[10];
        int pos = 0;
        while (true)
        {
            OperationStatus status = encoder.Compress(input.AsSpan(pos), buffer, out int c, out int w, true);
            result.Write(buffer, 0, w);
            pos += c;
            if (status == OperationStatus.Done)
            {
                break;
            }

            Assert.Equal(OperationStatus.DestinationTooSmall, status);
        }

        Assert.Equal(input, Deflate64.Deflate64.Decompress(result.ToArray()));
    }

    [Fact]
    public void LevelOutsideRangeIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Deflate64Encoder(new Deflate64Options { Level = 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Deflate64Encoder(new Deflate64Options { Level = -1 }));
    }
}

public class Deflate64StreamTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task WritesAndReadsBackSyncAndAsync(int level)
    {
        byte[] input = Deflate64EncoderTests.Text(250000, 31);
        var compressed = new MemoryStream();
        using (var writer = new Deflate64Stream(compressed, new Deflate64Options { Level = level }, leaveOpen: true))
        {
            writer.Write(input, 0, 1000);
            writer.Flush();
            writer.Write(input.AsSpan(1000, 100000));
            await writer.WriteAsync(input.AsMemory(101000), TestContext.Current.CancellationToken);
        }

        Assert.Equal(input, Deflate64.Deflate64.Decompress(compressed.ToArray()));
        compressed.Position = 0;
        var output = new MemoryStream();
        using (var reader = new Deflate64Stream(compressed, leaveOpen: true))
        {
            await reader.CopyToAsync(output, TestContext.Current.CancellationToken);
        }

        Assert.Equal(input, output.ToArray());
        compressed.Position = 0;
        var output2 = new MemoryStream();
        using (var reader = new Deflate64Stream(compressed))
        {
            reader.CopyTo(output2);
        }

        Assert.Equal(input, output2.ToArray());
    }

    [Fact]
    public void TruncatedAndTrailingInputAreTypedErrors()
    {
        byte[] packed = Deflate64.Deflate64.Compress(Deflate64EncoderTests.Text(20000, 3));
        using var truncated = new Deflate64Stream(new MemoryStream(packed, 0, packed.Length - 3));
        Assert.True(Assert.Throws<CompressionFormatException>(() => truncated.CopyTo(Stream.Null)).IsTruncated);
        using var empty = new Deflate64Stream(new MemoryStream());
        Assert.Throws<CompressionFormatException>(() => empty.CopyTo(Stream.Null));
        using var trailing = new Deflate64Stream(new MemoryStream(packed.Concat(new byte[] { 1 }).ToArray()));
        Assert.Throws<CompressionFormatException>(() => trailing.CopyTo(Stream.Null));
        using var allowed = new Deflate64Stream(new MemoryStream(packed.Concat(new byte[] { 1 }).ToArray()), new Deflate64DecompressionOptions { AllowTrailingData = true });
        allowed.CopyTo(Stream.Null);
    }

    [Fact]
    public void WrongDirectionIsNotSupported()
    {
        using var reader = new Deflate64Stream(new MemoryStream(Deflate64.Deflate64.Compress([1, 2, 3])));
        Assert.Throws<NotSupportedException>(() => reader.Write([1], 0, 1));
        using var writer = new Deflate64Stream(new MemoryStream(), (Deflate64Options?)null);
        Assert.Throws<NotSupportedException>(() => writer.Read(new byte[1], 0, 1));
    }
}
