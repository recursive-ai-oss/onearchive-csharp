using RecursiveAI.OneCompress.Lzx;

namespace RecursiveAI.OneCompress.Tests.Lzx;

public class LzxHostileInputTests
{
    private static readonly byte[] s_data = LzxDecoderTests.Sample(90_000, 21, withCalls: true);

    private static byte[] Stream(LzxTestEncoder.Settings settings) => LzxDecoderTests.Concat(LzxTestEncoder.Encode(s_data, settings));

    private static void DecodeOrTypedError(byte[] compressed, int windowBits = 16, int length = 90_000)
    {
        try
        {
            RecursiveAI.OneCompress.Lzx.Lzx.Decompress(compressed, windowBits, length);
        }
        catch (CompressionFormatException)
        {
        }
    }

    [Fact]
    public void TruncationAtEveryOffsetIsAFormatError()
    {
        byte[] stream = Stream(new LzxTestEncoder.Settings { BlockSize = 20_000, Kinds = [LzxTestEncoder.Kind.Verbatim, LzxTestEncoder.Kind.Aligned, LzxTestEncoder.Kind.Uncompressed] });
        for (int n = 0; n < stream.Length; n += n < 300 ? 1 : 61)
        {
            Assert.Throws<CompressionFormatException>(() => RecursiveAI.OneCompress.Lzx.Lzx.Decompress(stream.AsSpan(0, n), 16, 90_000));
        }
    }

    [Fact]
    public void DamagedStreamsEndInTypedErrorsOrOutput()
    {
        byte[] stream = Stream(new LzxTestEncoder.Settings { BlockSize = 30_000, TranslationSize = 50_000 });
        var rng = new Random(11);
        for (int i = 0; i < 4000; i++)
        {
            byte[] copy = (byte[])stream.Clone();
            int flips = 1 + rng.Next(3);
            for (int k = 0; k < flips; k++)
            {
                int at = i < 300 ? i : rng.Next(copy.Length);
                copy[at] ^= (byte)(1 << rng.Next(8));
            }

            DecodeOrTypedError(copy);
        }
    }

    [Fact]
    public void BlockTypeZeroIsRejectedWithTheOffset()
    {
        // Translation bit 0, block type 000: the first 16-bit word is zero.
        var ex = Assert.Throws<CompressionFormatException>(() => RecursiveAI.OneCompress.Lzx.Lzx.Decompress(new byte[64], 16, 100));
        Assert.Equal("lzx", ex.Format);
        Assert.Contains("block type", ex.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedOffsetFromAnUncompressedBlockIsCheckedAgainstTheOutput()
    {
        // Noise, then a run: the second block opens with a literal and a match at the first repeated offset.
        byte[] data = new byte[50_000];
        new Random(1).NextBytes(data.AsSpan(0, 40_000));
        byte[] stream = LzxDecoderTests.Concat(LzxTestEncoder.Encode(data, new LzxTestEncoder.Settings { BlockSize = 40_000, Kinds = [LzxTestEncoder.Kind.Uncompressed, LzxTestEncoder.Kind.Verbatim] }));
        Assert.Equal(data, RecursiveAI.OneCompress.Lzx.Lzx.Decompress(stream, 16, data.Length));

        // After the 4-byte block header the raw block carries R0, R1 and R2.
        BitConverter.TryWriteBytes(stream.AsSpan(4), 0x7FFFFFFFu);
        BitConverter.TryWriteBytes(stream.AsSpan(8), 0xFFFFFFFFu);
        var ex = Assert.Throws<CompressionFormatException>(() => RecursiveAI.OneCompress.Lzx.Lzx.Decompress(stream, 16, data.Length));
        Assert.Contains("offset", ex.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void OffsetsBeyondTheDecodedBytesAreRejected()
    {
        // A window of 2^21 lets a slot name an offset larger than the 40,000 bytes decoded so far when the stream is damaged;
        // here a stream for a 2^21 window is decoded as if the window were 2^15, which changes the tree sizes and so the codes.
        byte[] stream = Stream(new LzxTestEncoder.Settings { WindowBits = 21 });
        DecodeOrTypedError(stream, windowBits: 15);
    }

    [Fact]
    public void ArgumentsAndStateAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LzxDecoder(14));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LzxDecoder(22));
        var d = new LzxDecoder(16);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.DecodeFrame(new byte[10], [], out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.DecodeFrame(new byte[10], new byte[32769], out _));
        Assert.Throws<CompressionFormatException>(() => d.DecodeFrame(new byte[64], new byte[100], out _));
        Assert.Throws<InvalidOperationException>(() => d.DecodeFrame(new byte[64], new byte[100], out _));
        d.Reset();

        // A short frame ends the stream.
        byte[] stream = LzxDecoderTests.Concat(LzxTestEncoder.Encode(s_data[..100], new LzxTestEncoder.Settings()));
        d.DecodeFrame(stream, new byte[100], out _);
        Assert.Throws<InvalidOperationException>(() => d.DecodeFrame(stream, new byte[100], out _));
    }

    [Fact]
    public void TheOutputLimitIsCheckedBeforeDecoding()
    {
        byte[] stream = Stream(new LzxTestEncoder.Settings());
        var options = new LzxDecompressionOptions { MaxOutputSize = 50_000 };
        var ex = Assert.Throws<CompressionLimitException>(() => RecursiveAI.OneCompress.Lzx.Lzx.Decompress(stream, 16, 90_000, options));
        Assert.Equal(nameof(LzxDecompressionOptions.MaxOutputSize), ex.LimitName);
        var d = new LzxDecoder(16, options);
        d.DecodeFrame(stream, new byte[32768], out _);
        Assert.Throws<CompressionLimitException>(() => d.DecodeFrame(stream, new byte[32768], out _));
    }

    [Fact]
    public void PositionSlotTablesFollowTheSpecification()
    {
        // [MS-PATCH] 2.6.2: spot checks of the published table.
        int[] bases = [0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 393216, 524288, 655360, 786432, 917504, 1048576];
        int[] bits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 17, 17, 17, 17, 17];
        for (int s = 0; s < bases.Length; s++)
        {
            Assert.Equal(bases[s], LzxPositionSlots.GetBasePosition(s));
            Assert.Equal(bits[s], LzxPositionSlots.GetFooterBits(s));
        }

        Assert.Equal([30, 32, 34, 36, 38, 42, 50], Enumerable.Range(15, 7).Select(LzxPositionSlots.GetSlotCount));
    }

    [Fact]
    public void ADocumentedExampleDecodes()
    {
        // [MS-PATCH] 3: "abc" as an uncompressed block, after the 2-byte chunk-size prefix LZX DELTA adds and a cabinet does not have.
        byte[] stream = [0x00, 0x30, 0x30, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x61, 0x62, 0x63, 0x00];
        Assert.Equal("abc"u8.ToArray(), RecursiveAI.OneCompress.Lzx.Lzx.Decompress(stream, 15, 3));
    }
}
