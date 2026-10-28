using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

public class ZstdHostileInputTests
{
    private static readonly string[] SingleFrameVectors =
    [
        "empty-l3", "onebyte", "text300-l1", "text300-l3", "dict4-l3-p2100", "dict16-l19-p2101", "dict112-l3-p2101",
        "dict16-noid", "rawdict-l3", "random64k-l3", "runs1m-l3", "text300k-nocheck",
    ];

    // Four Huffman streams of fewer than eight bytes each: the joint loop cannot run, and handing its registers back to the
    // readers used to read eight bytes from the start of each short stream.
    [Fact]
    public void FourShortHuffmanStreamsDecode()
    {
        var table = new HuffmanDecodingTable();
        table.BuildFromWeights([1]); // two symbols with one-bit codes
        byte[] source = [1, 0, 1, 0, 1, 0, 0x03, 0x02, 0x03, 0x02];
        byte[] output = new byte[4];
        table.DecodeFourStreams(source, output);
        Assert.Equal(new byte[] { 1, 0, 1, 0 }, output);

        // Streams of seven bytes (48 one-bits and the marker) are still too short for the joint loop.
        byte[] seven = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        byte[] longSource = [7, 0, 7, 0, 7, 0, .. seven, .. seven, .. seven, .. seven];
        byte[] longOutput = new byte[192];
        table.DecodeFourStreams(longSource, longOutput);
        Assert.All(longOutput, b => Assert.Equal(1, b));
    }

    // Which compressed sizes get every offset tried and which are sampled, to keep the run short.
    private static IEnumerable<int> Offsets(int length, int maxCount)
    {
        int step = Math.Max(1, length / maxCount);
        for (int i = 0; i < length; i += step)
        {
            yield return i;
        }

        yield return length - 1;
    }

    [Fact]
    public void EveryProperPrefixOfAFrameIsRejected()
    {
        foreach (string name in SingleFrameVectors)
        {
            ZstdVector v = ZstdTestData.Vector(name);
            byte[] compressed = v.Compressed;
            ZstdDecompressionOptions options = v.Options();
            foreach (int cut in Offsets(compressed.Length, 700))
            {
                var e = Assert.ThrowsAny<CompressionException>(() => Zstd.Zstd.Decompress(compressed.AsSpan(0, cut), options));
                if (e is CompressionFormatException f)
                {
                    Assert.Equal("zstd", f.Format);
                    Assert.InRange(f.Offset, 0, compressed.Length);
                }
            }
        }
    }

    [Fact]
    public void TruncatedInputIsReportedAsTruncatedWithItsOffset()
    {
        ZstdVector v = ZstdTestData.Vector("text300k-l3");
        byte[] compressed = v.Compressed;
        foreach (int cut in new[] { 0, 3, 4, 8, 100, 5000, compressed.Length - 5, compressed.Length - 1 })
        {
            // Through the stream, which has to detect the end of its inner stream.
            using var stream = new ZstdStream(new MemoryStream(compressed, 0, cut));
            var e = Assert.Throws<CompressionFormatException>(() => stream.CopyTo(Stream.Null));
            Assert.True(e.IsTruncated, e.Message);
            Assert.Equal(cut, e.Offset);
        }
    }

    [Fact]
    public void TruncatedStreamingDecodeNeverSucceeds()
    {
        foreach (string name in SingleFrameVectors.Where(n => ZstdTestData.Vector(n).Size < 400_000))
        {
            ZstdVector v = ZstdTestData.Vector(name);
            byte[] compressed = v.Compressed;
            ZstdDecompressionOptions options = v.Options();
            foreach (int cut in Offsets(compressed.Length, 120).Where(c => c > 0))
            {
                var decoder = new ZstdDecoder(options);
                byte[] buffer = new byte[1 << 16];
                int pos = 0;
                Assert.ThrowsAny<CompressionException>(() =>
                {
                    while (true)
                    {
                        OperationStatus s = decoder.Decompress(compressed.AsSpan(pos, Math.Min(cut - pos, 977)), buffer, out int c, out _);
                        pos += c;
                        if (pos >= cut && s != OperationStatus.DestinationTooSmall)
                        {
                            decoder.ThrowIfIncomplete();
                            break;
                        }
                    }
                });
            }
        }
    }

    [Fact]
    public void BitFlipsEndInTypedErrorsOrIdenticalOutput()
    {
        foreach (string name in SingleFrameVectors)
        {
            ZstdVector v = ZstdTestData.Vector(name);
            byte[] compressed = v.Compressed;
            ZstdDecompressionOptions options = v.Options();
            bool hasChecksum = (compressed[4] & 4) != 0;
            byte[] copy = compressed.ToArray();
            foreach (int index in Offsets(compressed.Length, compressed.Length <= 400 ? 400 : 150))
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    copy[index] ^= (byte)(1 << bit);
                    try
                    {
                        byte[] output = Zstd.Zstd.Decompress(copy, options);
                        if (hasChecksum)
                        {
                            // A flip that still decodes (an unused header bit, say) must not change the content.
                            v.AssertMatches(output);
                        }
                    }
                    catch (CompressionException)
                    {
                    }

                    copy[index] ^= (byte)(1 << bit);
                }
            }
        }
    }

    [Fact]
    public void WindowAboveTheLimitIsRefusedBeforeAllocating()
    {
        ZstdVector v = ZstdTestData.Vector("text300k-ultra22-pipe");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CompressionLimitException>(() => Zstd.Zstd.Decompress(v.Compressed));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("MaxWindowSize", e.LimitName);
        Assert.Equal(128L << 20, e.Requested);
        Assert.Equal(ZstdDecompressionOptions.DefaultMaxWindowSize, e.Limit);
        Assert.True(allocated < 1 << 20, $"allocated {allocated}");

        // Raising the limit makes it decode, and the decoder still does not reserve the window.
        before = GC.GetAllocatedBytesForCurrentThread();
        v.AssertMatches(Zstd.Zstd.Decompress(v.Compressed, v.Options()));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 << 20);
    }

    [Fact]
    public void DeclaredSizeAboveTheOutputLimitIsRefusedBeforeAllocating()
    {
        ZstdVector v = ZstdTestData.Vector("runs1m-l3");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CompressionLimitException>(() => Zstd.Zstd.Decompress(v.Compressed, new ZstdDecompressionOptions { MaxOutputSize = 1000 }));
        Assert.Equal("MaxOutputSize", e.LimitName);
        Assert.Equal(1000, e.Limit);
        Assert.Equal(1 << 20, e.Requested);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 16);

        // A limit equal to the size is fine.
        v.AssertMatches(Zstd.Zstd.Decompress(v.Compressed, new ZstdDecompressionOptions { MaxOutputSize = 1 << 20 }));
    }

    [Fact]
    public void UnknownSizeFrameStopsAtTheOutputLimit()
    {
        ZstdVector v = ZstdTestData.Vector("text300k-nosize-pipe");
        var e = Assert.Throws<CompressionLimitException>(() => Zstd.Zstd.Decompress(v.Compressed, new ZstdDecompressionOptions { MaxOutputSize = 100_000 }));
        Assert.Equal("MaxOutputSize", e.LimitName);

        using var stream = new ZstdStream(new MemoryStream(v.Compressed), new ZstdDecompressionOptions { MaxOutputSize = 100_000 });
        Assert.Throws<CompressionLimitException>(() => stream.CopyTo(Stream.Null));
    }

    [Fact]
    public void LimitsApplyAcrossFrames()
    {
        byte[] one = ZstdTestData.Vector("text300-l3").Compressed;
        byte[] three = [.. one, .. one, .. one];
        Assert.Equal(900, Zstd.Zstd.Decompress(three).Length);
        Assert.Throws<CompressionLimitException>(() => Zstd.Zstd.Decompress(three, new ZstdDecompressionOptions { MaxOutputSize = 899 }));
    }

    [Fact]
    public void ManyRleBlocksAreStoppedByTheOutputLimit()
    {
        // A frame of 200 RLE blocks of 128 KiB: 4 bytes of input per 128 KiB of output.
        var frame = new List<byte>();
        frame.AddRange([0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x58]); // magic, no size, window descriptor: 128 KiB
        for (int i = 0; i < 200; i++)
        {
            uint header = ((uint)131072 << 3) | (1 << 1) | (i == 199 ? 1u : 0u);
            frame.AddRange([(byte)header, (byte)(header >> 8), (byte)(header >> 16), 0x41]);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CompressionLimitException>(() => Zstd.Zstd.Decompress(frame.ToArray(), new ZstdDecompressionOptions { MaxOutputSize = 1 << 20 }));
        Assert.Equal("MaxOutputSize", e.LimitName);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 << 20);

        // And it decodes when allowed.
        byte[] all = Zstd.Zstd.Decompress(frame.ToArray(), new ZstdDecompressionOptions { MaxOutputSize = 200L * 131072 });
        Assert.Equal(200 * 131072, all.Length);
        Assert.All(all, b => Assert.Equal(0x41, b));
    }

    [Theory]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00, 0xFF }, "MaxWindowSize")]                                  // window 3.75 TB
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0xE0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, "MaxWindowSize")] // single segment of 2^63
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0xE0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "MaxWindowSize")] // single segment of 2^64-1
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x80, 0x58, 0xFF, 0xFF, 0xFF, 0xFF }, "MaxOutputSize")]          // 4 GiB declared in 128 KiB window
    public void HostileHeadersHitLimitsBeforeAnythingIsAllocated(byte[] header, string limit)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CompressionLimitException>(() => Zstd.Zstd.Decompress(header));
        Assert.Equal(limit, e.LimitName);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 16);
    }

    [Fact]
    public void ReservedAndMalformedHeadersAreRejected()
    {
        void Rejects(byte[] data, string expectedFragment)
        {
            var e = Assert.Throws<CompressionFormatException>(() => Zstd.Zstd.Decompress(data));
            Assert.Contains(expectedFragment, e.Expected, StringComparison.Ordinal);
            Assert.Equal("zstd", e.Format);
        }

        Rejects([1, 2, 3, 4, 5, 6, 7, 8], "magic");
        Rejects([0x28, 0xB5, 0x2F, 0xFD, 0x08, 0x00], "reserved bit");
        // Block type 3 (reserved): last block, type 3, size 0.
        Rejects([0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x00, 0x07, 0x00, 0x00], "reserved");
        // Single-segment frame of 1 byte whose raw block claims 5 bytes.
        Rejects([0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x01, 0x29, 0x00, 0x00, 1, 2, 3, 4, 5], "block");
        // Declares 3 bytes but the block holds 1.
        Rejects([0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x03, 0x09, 0x00, 0x00, 1], "declared content size");
        // Treeless literals with no earlier table; and repeat-mode tables with none to repeat.
        Rejects([0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x10, 0x25, 0x00, 0x00, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00], "Huffman");
    }

    [Fact]
    public void ADecoderThatFailedMustBeResetBeforeReuse()
    {
        var decoder = new ZstdDecoder();
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] output = new byte[10];
        Assert.Throws<CompressionFormatException>(() => decoder.Decompress(garbage, output, out _, out _));
        Assert.Throws<InvalidOperationException>(() => decoder.Decompress(garbage, output, out _, out _));
        decoder.Reset();
        byte[] good = ZstdTestData.Vector("onebyte").Compressed;
        Assert.Equal(OperationStatus.Done, decoder.Decompress(good, output, out int c, out int w));
        Assert.Equal(good.Length, c);
        Assert.Equal(1, w);
    }

    [Fact]
    public void SkippableFramePayloadAboveTheLimitIsRefused()
    {
        byte[] frame = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0x184D2A50);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 0xFFFFFFF0);
        var options = new ZstdDecompressionOptions { SkippableFrameHandler = (_, _) => { } };
        var e = Assert.Throws<CompressionLimitException>(() => Zstd.Zstd.Decompress(frame, options));
        Assert.Equal("MaxSkippableFrameSize", e.LimitName);
    }

    [Fact]
    public void BadDictionariesAreRejected()
    {
        byte[] good = File.ReadAllBytes(ZstdTestData.Path("dict4.zdict"));
        var accepted = new List<int>();
        for (int cut = 4; cut < 121; cut++)
        {
            try
            {
                ZstdDictionary.Parse(good.AsSpan(0, cut));
                accepted.Add(cut);
            }
            catch (CompressionException)
            {
            }
        }

        Assert.Empty(accepted);

        Assert.Throws<ArgumentException>(() => ZstdDictionary.FromContent(new byte[7]));
        ZstdDictionary parsed = ZstdDictionary.Parse(good);
        Assert.True(parsed.HasEntropyTables);
        Assert.NotEqual(0u, parsed.Id);
        Assert.True(parsed.Content.Length > 1000);
    }
}
