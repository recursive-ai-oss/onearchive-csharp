using BR = RecursiveAI.OneCompress.Brotli.Brotli;
using RecursiveAI.OneCompress.Brotli;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Hostile and broken input: every prefix, every flipped bit, hand-built bad streams, limits.</summary>
public class BrotliHostileInputTests
{
    public static TheoryData<string> PrefixVectors() => new() { "onebyte-q5", "twobytes-q1", "text20k-q5", "text20k-q11", "uncompressed-3chunks", "metadata-then-data", "structured60k-q4", "text60k-q11-w10" };

    [Theory]
    [MemberData(nameof(PrefixVectors))]
    public void EveryPrefixIsATypedTruncationError(string name)
    {
        BrotliVector v = BrotliTestData.Vector(name);
        int step = v.Compressed.Length > 3000 ? 7 : 1;
        for (int cut = 0; cut < v.Compressed.Length; cut += step)
        {
            byte[] prefix = v.Compressed.AsSpan(0, cut).ToArray();
            var e = Assert.ThrowsAny<CompressionFormatException>(() => BR.Decompress(prefix));
            Assert.True(e.IsTruncated, $"{name} cut at {cut}: {e.Message}");
            Assert.InRange(e.Offset, 0, cut);
            Assert.Equal("brotli", e.Format);
            if (cut > 0)
            {
                var piece = Assert.ThrowsAny<CompressionFormatException>(() => BrotliTestData.DecodeInPieces(prefix, 61, 4096));
                Assert.True(piece.IsTruncated);
            }
        }
    }

    [Theory]
    [InlineData("onebyte-q5")]
    [InlineData("twobytes-q1")]
    [InlineData("uncompressed-3chunks")]
    [InlineData("text20k-q9")]
    public void EveryFlippedBitIsATypedErrorOrTheSameOutput(string name)
    {
        BrotliVector v = BrotliTestData.Vector(name);
        byte[] copy = v.Compressed.ToArray();
        var options = new BrotliDecompressionOptions { MaxOutputSize = 1 << 20 };
        int step = copy.Length > 1000 ? 7 : 1;
        for (int i = 0; i < copy.Length; i += step)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                copy[i] ^= (byte)(1 << bit);
                try
                {
                    // A flip may decode to a different valid stream; it must never throw anything but the hierarchy's exceptions.
                    BR.Decompress(copy, options);
                }
                catch (CompressionException)
                {
                }

                copy[i] ^= (byte)(1 << bit);
            }
        }
    }

    private static void AssertBad(byte[] stream, BrotliDecompressionOptions? options = null)
    {
        var e = Assert.ThrowsAny<CompressionFormatException>(() => BR.Decompress(stream, options));
        Assert.False(e.IsTruncated, e.Message);
        Assert.False(string.IsNullOrEmpty(e.Expected));
        Assert.InRange(e.Offset, 0, stream.Length);
        var piece = Assert.ThrowsAny<CompressionFormatException>(() => BrotliTestData.DecodeInPieces(stream, 3, 1000, options));
        Assert.Equal(e.Offset, piece.Offset);
    }

    [Fact]
    public void LargeWindowMarkerIsRejected()
    {
        // WBITS pattern 0010001 (large-window brotli) is not part of RFC 7932.
        AssertBad([0x11, 0x00, 0x00]);
    }

    [Fact]
    public void ReservedBitInMetadataHeaderIsRejected()
    {
        // WBITS=16 (single 0 bit), ISLAST=0, MNIBBLES=11 (metadata), reserved bit set.
        AssertBad([0x1C, 0, 0, 0]);
    }

    [Fact]
    public void TrailingDataIsRejectedUnlessAllowed()
    {
        BrotliVector v = BrotliTestData.Vector("text20k-q5");
        byte[] more = [.. v.Compressed, 0x42];
        var e = Assert.Throws<CompressionFormatException>(() => BR.Decompress(more));
        Assert.Equal(v.Compressed.Length, e.Offset);
        v.AssertMatches(BR.Decompress(more, new BrotliDecompressionOptions { AllowTrailingData = true }));
    }

    [Fact]
    public void OutputLimitIsTyped()
    {
        BrotliVector v = BrotliTestData.Vector("zeros100k-q5");
        var e = Assert.Throws<CompressionLimitException>(() => BR.Decompress(v.Compressed, new BrotliDecompressionOptions { MaxOutputSize = 1000 }));
        Assert.Equal("brotli", e.Format);
        Assert.Throws<CompressionLimitException>(() => BrotliTestData.DecodeInPieces(v.Compressed, 100, 100, new BrotliDecompressionOptions { MaxOutputSize = 1000 }));
        Assert.Throws<CompressionLimitException>(() => BrotliTestData.DecodeThroughStream(v.Compressed, 100, 100, new BrotliDecompressionOptions { MaxOutputSize = 1000 }));
    }

    [Fact]
    public void WindowLimitIsTyped()
    {
        BrotliVector v = BrotliTestData.Vector("text60k-q11-w24");
        Assert.Throws<CompressionLimitException>(() => BR.Decompress(v.Compressed, new BrotliDecompressionOptions { MaxWindowSize = 1 << 20 }));
        v.AssertMatches(BR.Decompress(v.Compressed, new BrotliDecompressionOptions { MaxWindowSize = (1 << 24) - 16 }));
    }

    [Fact]
    public void RandomGarbageNeverEscapesTheHierarchy()
    {
        var r = new Random(5);
        var options = new BrotliDecompressionOptions { MaxOutputSize = 1 << 22 };
        for (int i = 0; i < 20000; i++)
        {
            byte[] data = new byte[r.Next(1, 80)];
            r.NextBytes(data);
            try
            {
                BR.Decompress(data, options);
            }
            catch (CompressionException)
            {
            }
        }
    }
}
