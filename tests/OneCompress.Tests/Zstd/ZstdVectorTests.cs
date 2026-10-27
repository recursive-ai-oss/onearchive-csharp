using System.Buffers;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

public class ZstdVectorTests
{
    public static TheoryData<string> Names() => ZstdTestData.Names();

    [Theory]
    [MemberData(nameof(Names))]
    public void OneShotDecodeMatchesReference(string name)
    {
        ZstdVector v = ZstdTestData.Vector(name);
        byte[] output = Zstd.Zstd.Decompress(v.Compressed, v.Options());
        v.AssertMatches(output);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DecodeIntoExactBufferMatchesReference(string name)
    {
        ZstdVector v = ZstdTestData.Vector(name);
        byte[] output = new byte[v.Size];
        int written = Zstd.Zstd.Decompress(v.Compressed, output, v.Options());
        Assert.Equal(v.Size, written);
        v.AssertMatches(output);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamDecodeMatchesReference(string name)
    {
        ZstdVector v = ZstdTestData.Vector(name);
        using var stream = new ZstdStream(new MemoryStream(v.Compressed), v.Options());
        var result = new MemoryStream();
        stream.CopyTo(result);
        v.AssertMatches(result.ToArray());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task StreamAsyncDecodeMatchesReference(string name)
    {
        ZstdVector v = ZstdTestData.Vector(name);
        await using var stream = new ZstdStream(new OneByteStream(v.Compressed), v.Options());
        var result = new MemoryStream();
        await stream.CopyToAsync(result, 1000, TestContext.Current.CancellationToken);
        v.AssertMatches(result.ToArray());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 100000)]
    [InlineData(7, 3)]
    [InlineData(100000, 1)]
    [InlineData(333, 4097)]
    public void ChunkedDecodeMatchesReference(int inputChunk, int outputChunk)
    {
        foreach (ZstdVector v in ZstdTestData.Vectors.Where(v => v.Size <= 70000 || inputChunk > 1))
        {
            byte[] compressed = v.Compressed;
            if (inputChunk == 1 && outputChunk == 1 && compressed.Length > 3000)
            {
                continue;
            }

            byte[] output = ChunkedDecode(compressed, v.Options(), inputChunk, outputChunk);
            v.AssertMatches(output);
        }
    }

    [Fact]
    public void EverySplitPointOfSmallFramesWorks()
    {
        foreach (string name in new[] { "empty-l3", "onebyte", "text300-l3", "dict4-l3-p2100", "dict16-l19-p2101", "concat-skippable", "dict16-noid" })
        {
            ZstdVector v = ZstdTestData.Vector(name);
            byte[] compressed = v.Compressed;
            ZstdDecompressionOptions options = v.Options();
            for (int split = 0; split <= compressed.Length; split++)
            {
                var decoder = new ZstdDecoder(options);
                var output = new List<byte>();
                byte[] buffer = new byte[64];
                int pos = 0;
                foreach (var (start, end) in new[] { (0, split), (split, compressed.Length) })
                {
                    ReadOnlySpan<byte> input = compressed.AsSpan(start, end - start);
                    while (true)
                    {
                        OperationStatus s = decoder.Decompress(input, buffer, out int c, out int w);
                        output.AddRange(buffer.AsSpan(0, w).ToArray());
                        input = input[c..];
                        pos += c;
                        if (s == OperationStatus.DestinationTooSmall || (s == OperationStatus.Done && !input.IsEmpty))
                        {
                            continue;
                        }

                        break;
                    }
                }

                Assert.True(decoder.IsAtFrameBoundary, $"{name} split {split}");
                decoder.ThrowIfIncomplete();
                v.AssertMatches(output.ToArray());
            }
        }
    }

    [Fact]
    public void RandomSplitsOfLargeFramesWork()
    {
        var rng = new Random(12345);
        foreach (string name in new[] { "text300k-l3", "text300k-l19", "bin200k-l12", "runs1m-l3", "random64k-l3", "text300k-nosize-pipe", "text300k-long27", "patchfrom-l3" })
        {
            ZstdVector v = ZstdTestData.Vector(name);
            byte[] compressed = v.Compressed;
            for (int trial = 0; trial < 6; trial++)
            {
                var decoder = new ZstdDecoder(v.Options());
                var output = new MemoryStream();
                byte[] buffer = new byte[70000];
                int pos = 0;
                while (pos < compressed.Length || !decoder.IsAtFrameBoundary)
                {
                    int take = Math.Min(compressed.Length - pos, 1 + rng.Next(trial == 0 ? 50 : 40000));
                    int give = 1 + rng.Next(trial % 2 == 0 ? 700 : 70000);
                    OperationStatus s = decoder.Decompress(compressed.AsSpan(pos, take), buffer.AsSpan(0, give), out int c, out int w);
                    pos += c;
                    output.Write(buffer, 0, w);
                    if (pos >= compressed.Length && s == OperationStatus.NeedMoreData)
                    {
                        break;
                    }
                }

                decoder.ThrowIfIncomplete();
                v.AssertMatches(output.ToArray());
            }
        }
    }

    [Fact]
    public void SkippableFramesAreExposed()
    {
        ZstdVector v = ZstdTestData.Vector("concat-skippable");
        var seen = new List<(int Variant, int Length)>();
        var options = new ZstdDecompressionOptions
        {
            SkippableFrameHandler = (variant, data) => seen.Add((variant, data.Length)),
        };
        v.AssertMatches(Zstd.Zstd.Decompress(v.Compressed, options));
        Assert.Equal([(0, 5), (15, 0), (3, 100)], seen);

        // Without a handler they are skipped.
        v.AssertMatches(Zstd.Zstd.Decompress(v.Compressed));
    }

    [Fact]
    public void MissingDictionaryIsReported()
    {
        ZstdVector v = ZstdTestData.Vector("dict16-l3-p2100");
        var e = Assert.Throws<CompressionDictionaryException>(() => Zstd.Zstd.Decompress(v.Compressed));
        Assert.NotEqual(0u, e.DictionaryId);
    }

    [Fact]
    public void DictionariesAreChosenByIdFromTheSet()
    {
        var set = new List<ZstdDictionary>();
        foreach (string d in new[] { "dict4.zdict", "dict16.zdict", "dict112.zdict" })
        {
            set.Add(ZstdDictionary.Parse(File.ReadAllBytes(ZstdTestData.Path(d))));
        }

        var options = new ZstdDecompressionOptions { Dictionaries = set };
        foreach (string n in new[] { "dict4-l3-p2100", "dict16-l19-p2101", "dict112-l3-p2100" })
        {
            ZstdVector v = ZstdTestData.Vector(n);
            v.AssertMatches(Zstd.Zstd.Decompress(v.Compressed, options));
        }
    }

    [Fact]
    public void ChecksumMismatchIsDetected()
    {
        ZstdVector v = ZstdTestData.Vector("text300k-l3");
        byte[] bad = v.Compressed;
        bad[^1] ^= 1;
        var e = Assert.Throws<CompressionFormatException>(() => Zstd.Zstd.Decompress(bad));
        Assert.Contains("checksum", e.Expected, StringComparison.Ordinal);
        Assert.Equal(bad.Length - 4, e.Offset);

        // With verification off the same data decodes.
        v.AssertMatches(Zstd.Zstd.Decompress(bad, new ZstdDecompressionOptions { VerifyChecksum = false }));
    }

    internal static byte[] ChunkedDecode(byte[] compressed, ZstdDecompressionOptions options, int inputChunk, int outputChunk)
    {
        var decoder = new ZstdDecoder(options);
        var output = new MemoryStream();
        byte[] buffer = new byte[outputChunk];
        int pos = 0;
        while (true)
        {
            int take = Math.Min(inputChunk, compressed.Length - pos);
            OperationStatus s = decoder.Decompress(compressed.AsSpan(pos, take), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (pos >= compressed.Length && s != OperationStatus.DestinationTooSmall && !(s == OperationStatus.Done && pos < compressed.Length))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }

    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 777)], cancellationToken);
    }
}
