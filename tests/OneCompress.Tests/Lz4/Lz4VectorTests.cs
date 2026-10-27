using System.Buffers;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.OneCompress.Tests;

public class Lz4VectorTests
{
    public static TheoryData<string> Names() => Lz4TestData.Names();

    [Theory]
    [MemberData(nameof(Names))]
    public void OneShotDecodeMatchesReference(string name)
    {
        Lz4Vector v = Lz4TestData.Vector(name);
        v.AssertMatches(Lz4.Lz4.Decompress(v.Compressed, v.Options()));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodeIntoExactBufferMatchesReference(string name)
    {
        Lz4Vector v = Lz4TestData.Vector(name);
        byte[] output = new byte[v.Size];
        Assert.Equal(v.Size, Lz4.Lz4.Decompress(v.Compressed, output, v.Options()));
        v.AssertMatches(output);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodeIntoTooSmallBufferReportsIt(string name)
    {
        Lz4Vector v = Lz4TestData.Vector(name);
        if (v.Size == 0)
        {
            return;
        }

        Assert.False(Lz4.Lz4.TryDecompress(v.Compressed, new byte[v.Size - 1], out _, v.Options()));
        var e = Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(v.Compressed, new byte[v.Size - 1], v.Options()));
        Assert.Equal("destination.Length", e.LimitName);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DeclaredSizeIsReported(string name)
    {
        Lz4Vector v = Lz4TestData.Vector(name);
        if (Lz4Decoder.TryGetDecompressedSize(v.Compressed, out long size))
        {
            Assert.Equal(v.Size, size);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamDecodeMatchesReference(string name)
    {
        Lz4Vector v = Lz4TestData.Vector(name);
        using var stream = new Lz4Stream(new MemoryStream(v.Compressed), v.Options());
        var result = new MemoryStream();
        stream.CopyTo(result);
        v.AssertMatches(result.ToArray());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task StreamAsyncDecodeMatchesReference(string name)
    {
        Lz4Vector v = Lz4TestData.Vector(name);
        await using var stream = new Lz4Stream(new OneByteStream(v.Compressed), v.Options());
        var result = new MemoryStream();
        await stream.CopyToAsync(result, 1000, TestContext.Current.CancellationToken);
        v.AssertMatches(result.ToArray());
    }

    [Fact]
    public async Task AsyncReadHonoursCancellation()
    {
        Lz4Vector v = Lz4TestData.Vector("text100k-1");
        await using var stream = new Lz4Stream(new MemoryStream(v.Compressed), v.Options());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => Assert.Equal(0, await stream.ReadAsync(new byte[100], cts.Token)));
    }

    [Fact]
    public void StreamOfNothingOrOfJunkFailsWithATypedError()
    {
        using var empty = new Lz4Stream(new MemoryStream());
        var e = Assert.Throws<CompressionFormatException>(() => empty.ReadByte());
        Assert.True(e.IsTruncated);
        using var junk = new Lz4Stream(new MemoryStream("not lz4 data at all"u8.ToArray()));
        Assert.Throws<CompressionFormatException>(() => junk.ReadByte());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 100000)]
    [InlineData(7, 3)]
    [InlineData(100000, 1)]
    [InlineData(333, 4097)]
    [InlineData(65536, 65536)]
    public void ChunkedDecodeMatchesReference(int inputChunk, int outputChunk)
    {
        foreach (Lz4Vector v in Lz4TestData.Vectors.Where(v => v.Size <= 70000 || inputChunk > 1))
        {
            byte[] output = Lz4Splits.Decode(v.Compressed, v.Options(), inputChunk, outputChunk);
            v.AssertMatches(output);
        }
    }

    [Fact]
    public void EveryInputSplitPointOnSmallFramesGivesTheSameOutput()
    {
        foreach (Lz4Vector v in Lz4TestData.Vectors)
        {
            byte[] compressed = v.Compressed;
            if (compressed.Length > 1500)
            {
                continue;
            }

            foreach (int outputChunk in new[] { 1, 13, 4096 })
            {
                for (int split = 0; split <= compressed.Length; split++)
                {
                    byte[] output = Lz4Splits.Decode(compressed, v.Options(), split, outputChunk, twoPieces: true);
                    v.AssertMatches(output);
                }
            }
        }
    }

    [Fact]
    public void RandomSplitsOnLargeFramesGiveTheSameOutput()
    {
        var random = new Random(12345);
        foreach (Lz4Vector v in Lz4TestData.Vectors.Where(v => v.Name.Contains("100k", StringComparison.Ordinal) || v.Name.Contains("concat", StringComparison.Ordinal) || v.Name.Contains("70k", StringComparison.Ordinal)))
        {
            byte[] compressed = v.Compressed;
            for (int round = 0; round < 6; round++)
            {
                byte[] output = Lz4Splits.Decode(compressed, v.Options(), 1 + random.Next(9000), 1 + random.Next(200000), randomSizes: random);
                v.AssertMatches(output);
            }
        }
    }

    [Fact]
    public void SkippableFramesAreReportedToTheHandler()
    {
        Lz4Vector v = Lz4TestData.Vector("concat-skippable");
        var seen = new List<(int Variant, int Length)>();
        var options = new Lz4DecompressionOptions { SkippableFrameHandler = (variant, data) => seen.Add((variant, data.Length)) };
        v.AssertMatches(Lz4.Lz4.Decompress(v.Compressed, options));
        Assert.Equal([(0, 5), (15, 0), (3, 100)], seen);

        // Split every which way, the handler still sees each payload once and whole.
        seen.Clear();
        v.AssertMatches(Lz4Splits.Decode(v.Compressed, options, 1, 5));
        Assert.Equal([(0, 5), (15, 0), (3, 100)], seen);

        var small = new Lz4DecompressionOptions { SkippableFrameHandler = (_, _) => { }, MaxSkippableFrameSize = 50 };
        var limit = Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(v.Compressed, small));
        Assert.Equal("MaxSkippableFrameSize", limit.LimitName);
    }

    [Fact]
    public void FrameThatNamesADictionaryIdFindsItByIdOrFallsBack()
    {
        // text300 compressed with the dictionary from the manifest, but with the dictionary ID flag set by hand.
        Lz4Vector v = Lz4TestData.Vector("payload2100-dict8k");
        byte[] dictionaryBytes = File.ReadAllBytes(Lz4TestData.Path("dict8k.bin"));
        byte[] frame = Lz4FrameBuilder.WithDictionaryId(v.Compressed, 0xCAFEF00D);

        var byId = new Lz4DecompressionOptions { Dictionaries = [new Lz4Dictionary(dictionaryBytes, 0xCAFEF00D)] };
        v.AssertMatches(Lz4.Lz4.Decompress(frame, byId));

        var fallback = new Lz4DecompressionOptions { Dictionary = new Lz4Dictionary(dictionaryBytes) };
        v.AssertMatches(Lz4.Lz4.Decompress(frame, fallback));

        var wrongId = new Lz4DecompressionOptions { Dictionaries = [new Lz4Dictionary(dictionaryBytes, 7)] };
        var e = Assert.Throws<CompressionDictionaryException>(() => Lz4.Lz4.Decompress(frame, wrongId));
        Assert.Equal(0xCAFEF00Du, e.DictionaryId);
        Assert.Throws<CompressionDictionaryException>(() => Lz4.Lz4.Decompress(frame));
    }

    [Fact]
    public void EmptyInputIsATruncatedFrame()
    {
        var e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress([]));
        Assert.True(e.IsTruncated);
        Assert.Equal(0, e.Offset);
    }

    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}

/// <summary>Drives <see cref="Lz4Decoder"/> with the input and the output cut into pieces.</summary>
internal static class Lz4Splits
{
    public static byte[] Decode(byte[] compressed, Lz4DecompressionOptions options, int inputChunk, int outputChunk, bool twoPieces = false, Random? randomSizes = null)
    {
        var decoder = new Lz4Decoder(options);
        var output = new MemoryStream();
        byte[] buffer = new byte[outputChunk];
        int pos = 0;
        int available = twoPieces ? Math.Min(inputChunk, compressed.Length) : Math.Min(inputChunk, compressed.Length);
        for (int guard = 0; guard < 50_000_000; guard++)
        {
            int outLimit = randomSizes is null ? buffer.Length : Math.Min(buffer.Length, 1 + randomSizes.Next(buffer.Length));
            OperationStatus status = decoder.Decompress(compressed.AsSpan(pos, available - pos), buffer.AsSpan(0, outLimit), out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.NeedMoreData || (status == OperationStatus.Done && pos == available))
            {
                if (available == compressed.Length)
                {
                    break;
                }

                available = twoPieces ? compressed.Length : Math.Min(compressed.Length, available + (randomSizes is null ? inputChunk : 1 + randomSizes.Next(inputChunk)));
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }
}

/// <summary>Builds frames by hand for cases the lz4 tool will not write.</summary>
internal static class Lz4FrameBuilder
{
    /// <summary>Rewrites a frame header (no content size, no dictionary ID) to name a dictionary ID, fixing the header checksum.</summary>
    public static byte[] WithDictionaryId(byte[] frame, uint id)
    {
        int flg = frame[4];
        Assert.Equal(0, flg & 0x09); // neither a content size nor an ID yet
        byte[] descriptor = [(byte)(flg | 1), frame[5], (byte)id, (byte)(id >> 8), (byte)(id >> 16), (byte)(id >> 24)];
        byte hc = (byte)(RecursiveAI.OneCompress.Checksums.XxHash32.Compute(descriptor) >> 8);
        return [.. frame[..4], .. descriptor, hc, .. frame[7..]];
    }
}
