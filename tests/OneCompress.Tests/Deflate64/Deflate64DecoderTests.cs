using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Deflate64;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Deflate64 vectors (testdata/deflate64/generate_vectors.py): hand-built streams with the pinned parameters and streams written by 7-Zip.</summary>
public class Deflate64DecoderTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "testdata", "deflate64");

    private static readonly Lazy<List<string[]>> s_rows = new(() =>
        File.ReadAllLines(Path.Combine(Dir, "manifest.tsv")).Where(static l => !l.StartsWith('#')).Select(static l => l.Split('\t')).ToList());

    public static TheoryData<string> OkNames() => Names("ok");

    public static TheoryData<string> BadNames() => Names("bad");

    private static TheoryData<string> Names(string kind)
    {
        var data = new TheoryData<string>();
        foreach (string[] row in s_rows.Value.Where(r => r[1] == kind))
        {
            data.Add(row[0]);
        }

        return data;
    }

    private static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Dir, "vectors", name + ".d64"));

    private static string[] Row(string name) => s_rows.Value.Single(r => r[0] == name);

    /// <summary>Runs the decoder with the given input and output piece sizes; returns everything written.</summary>
    internal static byte[] Decode(ReadOnlySpan<byte> source, int inPiece, int outPiece, Deflate64DecompressionOptions? options = null)
    {
        var decoder = new Deflate64Decoder(options);
        var result = new MemoryStream();
        byte[] buffer = new byte[outPiece];
        int pos = 0;
        while (true)
        {
            ReadOnlySpan<byte> piece = source.Slice(pos, Math.Min(inPiece, source.Length - pos));
            OperationStatus status = decoder.Decompress(piece, buffer, out int consumed, out int written);
            result.Write(buffer, 0, written);
            pos += consumed;
            if (status == OperationStatus.Done)
            {
                Assert.Equal(source.Length, pos);
                Assert.True(decoder.IsFinished);
                return result.ToArray();
            }

            if (status == OperationStatus.NeedMoreData)
            {
                Assert.Equal(piece.Length, consumed);
                if (pos == source.Length)
                {
                    decoder.ThrowIfIncomplete();
                    throw new InvalidOperationException("unreachable");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(OkNames))]
    public void VectorsDecodeToTheirHash(string name)
    {
        string[] row = Row(name);
        byte[] compressed = Read(name);
        byte[] output = Deflate64.Deflate64.Decompress(compressed);
        Assert.Equal(int.Parse(row[2], CultureInfo.InvariantCulture), output.Length);
        Assert.Equal(row[3], Convert.ToHexStringLower(SHA256.HashData(output)));
    }

    [Theory]
    [MemberData(nameof(OkNames))]
    public void VectorsDecodeAnyWayTheInputAndOutputAreSplit(string name)
    {
        string[] row = Row(name);
        byte[] compressed = Read(name);
        foreach ((int inPiece, int outPiece) in new[] { (1, 4096), (3, 1), (7, 13), (100000, 5), (1, 70000) })
        {
            if (inPiece == 3 && outPiece == 1 && int.Parse(row[2], CultureInfo.InvariantCulture) > 70000)
            {
                continue; // a byte at a time over 100,000 bytes is slow without testing anything more
            }

            byte[] output = Decode(compressed, inPiece, outPiece);
            Assert.Equal(row[3], Convert.ToHexStringLower(SHA256.HashData(output)));
        }
    }

    [Theory]
    [MemberData(nameof(BadNames))]
    public void HostileVectorsAreFormatErrors(string name)
    {
        byte[] compressed = Read(name);
        var e = Assert.Throws<CompressionFormatException>(() => Deflate64.Deflate64.Decompress(compressed));
        Assert.Equal("deflate64", e.Format);
        Assert.False(e.IsTruncated);
        Assert.True(e.Offset >= 0 && e.Offset <= compressed.Length);
    }

    [Fact]
    public void TruncatedVectorIsMarkedTruncated()
    {
        byte[] compressed = Read("truncated-far-distances");
        var e = Assert.Throws<CompressionFormatException>(() => Deflate64.Deflate64.Decompress(compressed));
        Assert.True(e.IsTruncated);
    }

    [Fact]
    public void EmptyInputIsTruncated()
    {
        Assert.True(Assert.Throws<CompressionFormatException>(() => Deflate64.Deflate64.Decompress(ReadOnlySpan<byte>.Empty)).IsTruncated);
    }

    [Fact]
    public void BytesAfterTheEndAreRefusedUnlessAllowed()
    {
        byte[] compressed = Read("empty-final-block").Concat(new byte[] { 1, 2, 3 }).ToArray();
        Assert.Throws<CompressionFormatException>(() => Deflate64.Deflate64.Decompress(compressed));
        Assert.Empty(Deflate64.Deflate64.Decompress(compressed, new Deflate64DecompressionOptions { AllowTrailingData = true }));
    }

    [Fact]
    public void OutputLimitIsATypedErrorAndNothingPastItIsWritten()
    {
        byte[] compressed = Read("long-lengths");
        var options = new Deflate64DecompressionOptions { MaxOutputSize = 50000 };
        var e = Assert.Throws<CompressionLimitException>(() => Deflate64.Deflate64.Decompress(compressed, options));
        Assert.Equal(nameof(Deflate64DecompressionOptions.MaxOutputSize), e.LimitName);
        var e2 = Assert.Throws<CompressionLimitException>(() => Decode(compressed, 100000, 1000, options));
        Assert.Equal(50000, e2.Limit);
    }

    [Fact]
    public void TryDecompressReportsASmallDestination()
    {
        byte[] compressed = Read("far-distances");
        Assert.False(Deflate64.Deflate64.TryDecompress(compressed, new byte[100], out int written));
        Assert.Equal(0, written);
        byte[] big = new byte[65625];
        Assert.True(Deflate64.Deflate64.TryDecompress(compressed, big, out written));
        Assert.Equal(65625, written);
    }

    [Fact]
    public void EveryTruncationAndBitFlipOfASmallVectorEndsInATypedError()
    {
        byte[] compressed = Read("dynamic-hlit288-hdist32");
        for (int cut = 0; cut < compressed.Length; cut++)
        {
            Assert.Throws<CompressionFormatException>(() => Deflate64.Deflate64.Decompress(compressed.AsSpan(0, cut)));
        }

        byte[] copy = compressed.ToArray();
        for (int bit = 0; bit < copy.Length * 8; bit++)
        {
            copy[bit >> 3] ^= (byte)(1 << (bit & 7));
            try
            {
                Deflate64.Deflate64.Decompress(copy, new Deflate64DecompressionOptions { MaxOutputSize = 1 << 22, AllowTrailingData = true });
            }
            catch (CompressionException)
            {
            }

            copy[bit >> 3] ^= (byte)(1 << (bit & 7));
        }
    }

    // Long runs (length symbol 285 with its 16 extra bits), repeats at distances up to 64 KiB and text-like literals, so the decoder's
    // fast loop, its hand-over to the per-symbol path and matches that reach into the window are all exercised.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)]
    [InlineData(9, 7)]
    public void LargeRoundTripDecodesAnyWayTheInputAndOutputAreSplit(int level, int seed)
    {
        var rng = new Random(seed);
        var data = new List<byte>();
        byte[] words = new byte[4096];
        rng.NextBytes(words);
        while (data.Count < 700_000)
        {
            switch (rng.Next(5))
            {
                case 0:
                    data.AddRange(Enumerable.Repeat((byte)rng.Next(256), rng.Next(1, 70000)));
                    break;
                case 1 when data.Count > 70000:
                    int distance = rng.Next(1, 65537);
                    int length = rng.Next(3, 70000);
                    for (int i = 0; i < length; i++)
                    {
                        data.Add(data[data.Count - distance]);
                    }

                    break;
                case 2:
                    for (int i = rng.Next(1, 3000); i > 0; i--)
                    {
                        data.Add((byte)(words[rng.Next(64)] & 0x1F | 0x40));
                    }

                    break;
                default:
                    for (int i = rng.Next(1, 200); i > 0; i--)
                    {
                        data.Add((byte)rng.Next(256));
                    }

                    break;
            }
        }

        byte[] original = data.ToArray();
        byte[] compressed = RecursiveAI.OneCompress.Deflate64.Deflate64.Compress(original, new Deflate64Options { Level = level });
        Assert.Equal(original, RecursiveAI.OneCompress.Deflate64.Deflate64.Decompress(compressed));
        foreach ((int inPiece, int outPiece) in new[] { (1 << 20, 1 << 20), (1000, 100000), (1 << 20, 1000), (4093, 65537), (17, 4099) })
        {
            Assert.Equal(original, Decode(compressed, inPiece, outPiece));
        }
    }
}
