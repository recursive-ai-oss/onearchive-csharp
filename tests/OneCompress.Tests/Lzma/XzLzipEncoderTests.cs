using System.Buffers;
using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Round trips of the xz and lzip encoders through this library's decoders, and the property that the output does not depend on how the input is cut.</summary>
public class XzLzipEncoderTests
{
    private static byte[] Mixed(int seed) =>
        [.. LzmaEncoderTests.Text(60_000, seed), .. LzmaEncoderTests.Structured(80_000, seed + 1), .. LzmaEncoderTests.Random(5_000, seed + 2), .. new byte[20_000]];

    public static TheoryData<XzCheck> Checks() => new(XzCheck.None, XzCheck.Crc32, XzCheck.Crc64, XzCheck.Sha256);

    [Theory]
    [MemberData(nameof(Checks))]
    public void XzRoundTripsWithEveryCheck(XzCheck check)
    {
        foreach (int size in new[] { 0, 1, 100, 170_000 })
        {
            byte[] input = Mixed(1)[..Math.Min(size, 165_000)];
            byte[] packed = Xz.Xz.Compress(input, new XzCompressionOptions { Check = check, Lzma = new LzmaOptions { Preset = 3 } });
            Assert.Equal(input, Xz.Xz.Decompress(packed));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(50_000)]
    [InlineData(70_000)]
    public void XzBlocksRoundTrip(long blockSize)
    {
        byte[] input = Mixed(2);
        byte[] packed = Xz.Xz.Compress(input, new XzCompressionOptions { BlockSize = blockSize, Lzma = new LzmaOptions { Preset = 2 } });
        Assert.Equal(input, Xz.Xz.Decompress(packed));
    }

    [Fact]
    public void XzFilterChainsRoundTrip()
    {
        byte[] input = Mixed(3);
        foreach (BranchArchitecture arch in Enum.GetValues<BranchArchitecture>())
        {
            var options = new XzCompressionOptions { Filters = [XzFilter.Branch(arch)], Lzma = new LzmaOptions { Preset = 1 } };
            Assert.Equal(input, Xz.Xz.Decompress(Xz.Xz.Compress(input, options)));
        }

        var chain = new XzCompressionOptions { Filters = [XzFilter.Branch(BranchArchitecture.X86, 16), XzFilter.Delta(4), XzFilter.Delta(1)], BlockSize = 40_000, Lzma = new LzmaOptions { Preset = 1 } };
        Assert.Equal(input, Xz.Xz.Decompress(Xz.Xz.Compress(input, chain)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 13)]
    [InlineData(4096, 100)]
    [InlineData(1 << 20, 1 << 20)]
    public void XzStreamingMatchesOneCall(int inputPiece, int outputPiece)
    {
        byte[] input = Mixed(4);
        var options = new XzCompressionOptions
        {
            Check = XzCheck.Sha256,
            BlockSize = 70_000,
            Filters = [XzFilter.Branch(BranchArchitecture.Arm64), XzFilter.Delta(2)],
            Lzma = new LzmaOptions { Preset = 2, DictionarySize = 1 << 16 },
        };
        byte[] expected = Xz.Xz.Compress(input, options);
        var encoder = new XzEncoder(options);
        Assert.Equal(expected, Drive(encoder.Compress, input, inputPiece, outputPiece));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public void LzipRoundTripsAtEveryLevel(int level)
    {
        foreach (int size in new[] { 0, 1, 5, 100_000 })
        {
            byte[] input = Mixed(5)[..size];
            byte[] packed = Lzip.Lzip.Compress(input, new LzipOptions { Level = level });
            Assert.Equal(input, Lzip.Lzip.Decompress(packed));
        }
    }

    [Fact]
    public void LzipMembersRoundTripAndStream()
    {
        byte[] input = Mixed(6);
        var options = new LzipOptions { Level = 3, MemberSize = 30_000, DictionarySize = 1 << 15 };
        byte[] expected = Lzip.Lzip.Compress(input, options);
        Assert.Equal(input, Lzip.Lzip.Decompress(expected));
        var encoder = new LzipEncoder(options);
        Assert.Equal(expected, Drive(encoder.Compress, input, 999, 77));
    }

    [Fact]
    public void LzipDictionaryByteCoversTheRequestedSize()
    {
        foreach (uint size in new uint[] { 4096, 4097, 5000, 65536, 1_000_000, 3 << 19, (1u << 29) - 1, 1u << 29 })
        {
            byte b = LzipEncoder.EncodeDictionarySize(size);
            uint actual = (1u << (b & 31)) - (uint)((b >> 5) * ((1u << (b & 31)) >> 4));
            Assert.True(actual >= size, $"{size}: {actual}");
        }
    }

    [Fact]
    public async Task EncoderStreamsMatchOneCallAndSupportAsync()
    {
        byte[] input = Mixed(7);
        var xzOptions = new XzCompressionOptions { Lzma = new LzmaOptions { Preset = 2 } };
        var sink = new MemoryStream();
        using (var stream = new XzEncoderStream(sink, xzOptions, leaveOpen: true))
        {
            for (int i = 0; i < input.Length; i += 1234)
            {
                stream.Write(input, i, Math.Min(1234, input.Length - i));
            }
        }

        Assert.Equal(Xz.Xz.Compress(input, xzOptions), sink.ToArray());

        var sink2 = new MemoryStream();
        await using (var stream = new LzipEncoderStream(sink2, new LzipOptions { Level = 2 }, leaveOpen: true))
        {
            await stream.WriteAsync(input.AsMemory(), TestContext.Current.CancellationToken);
        }

        Assert.Equal(input, Lzip.Lzip.Decompress(sink2.ToArray()));

        var sink3 = new MemoryStream();
        using (var stream = new LzmaEncoderStream(sink3, new LzmaOptions { Preset = 2 }, leaveOpen: true))
        {
            stream.Write(input);
        }

        Assert.Equal(input, Lzma.Lzma.Decompress(sink3.ToArray()));
    }

    [Fact]
    public void InvalidOptionsThrowArgumentExceptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Lzma.Lzma.Compress([], new LzmaOptions { Preset = 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lzma.Lzma.Compress([], new LzmaOptions { Lc = 9 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lzma.Lzma.Compress([], new LzmaOptions { DictionarySize = 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lzma.Lzma2.Compress([], out _, new LzmaOptions { Lc = 4, Lp = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lzip.Lzip.Compress([], new LzipOptions { Level = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Xz.Xz.Compress([], new XzCompressionOptions { Check = (XzCheck)7 }));
        Assert.Throws<ArgumentException>(() => Xz.Xz.Compress([], new XzCompressionOptions { Filters = [XzFilter.Delta(1), XzFilter.Delta(2), XzFilter.Delta(3), XzFilter.Delta(4)] }));
    }

    private delegate OperationStatus CompressStep(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinal);

    private static byte[] Drive(CompressStep step, byte[] input, int inputPiece, int outputPiece)
    {
        var result = new MemoryStream();
        byte[] buffer = new byte[outputPiece];
        int pos = 0;
        while (true)
        {
            int take = Math.Min(inputPiece, input.Length - pos);
            OperationStatus s = step(input.AsSpan(pos, take), buffer, out int c, out int w, pos + take == input.Length);
            pos += c;
            result.Write(buffer, 0, w);
            if (s == OperationStatus.Done)
            {
                return result.ToArray();
            }
        }
    }
}
