using System.Buffers;
using RecursiveAI.OneCompress.Lz4;
using static RecursiveAI.OneCompress.Tests.Lz4Builder;

namespace RecursiveAI.OneCompress.Tests;

public class Lz4HostileInputTests
{
    private static readonly byte[] s_text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 30)));

    // Any outcome but a typed error or a correct decode is a bug.
    private static byte[]? TryDecode(byte[] data, Lz4DecompressionOptions? options = null)
    {
        try
        {
            return Lz4.Lz4.Decompress(data, options);
        }
        catch (CompressionException)
        {
            return null;
        }
    }

    [Fact]
    public void EveryPrefixOfAFrameFailsAsTruncated()
    {
        foreach (Lz4Vector v in Lz4TestData.Vectors)
        {
            byte[] frame = v.Compressed;
            if (frame.Length > 2500 || v.Name.Contains("concat", StringComparison.Ordinal) || v.Name.Contains("legacy", StringComparison.Ordinal))
            {
                continue;
            }

            for (int length = 0; length < frame.Length; length++)
            {
                var e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(frame.AsSpan(0, length), v.Options()));
                Assert.True(e.IsTruncated, $"{v.Name} cut at {length}: {e.Message}");
                Assert.True(e.Offset <= length, $"{v.Name} cut at {length}: offset {e.Offset}");
            }
        }
    }

    [Fact]
    public void EveryPrefixOfALargeFrameFailsOrIsAFrameBoundary()
    {
        foreach (Lz4Vector v in Lz4TestData.Vectors.Where(v => v.Name is "text100k-B4-BD-BX" or "random70k-B4-BX-BD" or "text100k-legacy" or "concat-skippable"))
        {
            byte[] frame = v.Compressed;
            int stride = Math.Max(1, frame.Length / 3000);
            for (int length = 0; length < frame.Length; length += stride)
            {
                byte[]? output = TryDecode(frame[..length], v.Options());
                if (output is not null)
                {
                    // Only a legacy frame or a concatenation can end cleanly early; what it produced is a prefix of the real output.
                    Assert.True(v.Name.Contains("legacy", StringComparison.Ordinal) || v.Name.Contains("concat", StringComparison.Ordinal), $"{v.Name} cut at {length}");
                }
            }
        }
    }

    [Fact]
    public void EveryPrefixOfTheStreamingDecoderEndsInATruncationError()
    {
        Lz4Vector v = Lz4TestData.Vector("text100k-B4-BD-BX");
        byte[] frame = v.Compressed;
        for (int length = 1; length < frame.Length; length += 37)
        {
            var decoder = new Lz4Decoder();
            byte[] buffer = new byte[100_000];
            decoder.Decompress(frame.AsSpan(0, length), buffer, out _, out _);
            var e = Assert.Throws<CompressionFormatException>(() => decoder.ThrowIfIncomplete());
            Assert.True(e.IsTruncated);
        }
    }

    [Fact]
    public void BitFlipsEndInSuccessOrATypedErrorAndChecksumsCatchTheRest()
    {
        int checkedFlips = 0;
        foreach (Lz4Vector v in Lz4TestData.Vectors.Where(v => !v.Name.Contains("5m", StringComparison.Ordinal)))
        {
            byte[] frame = v.Compressed;
            int stride = frame.Length <= 700 ? 1 : Math.Max(1, frame.Length / 600);
            bool hasContentChecksum = (frame.Length > 5) && (frame[4] & 0x04) != 0 && (frame[0] == 0x04);
            for (int i = 0; i < frame.Length; i += stride)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    byte[] copy = (byte[])frame.Clone();
                    copy[i] ^= (byte)(1 << bit);
                    byte[]? output = TryDecode(copy, v.Options());
                    checkedFlips++;
                    if (output is not null && hasContentChecksum && !v.Name.Contains("concat", StringComparison.Ordinal))
                    {
                        // With a content checksum a flip may only go unnoticed when it changed nothing that matters
                        // (it cannot change the output). Skippable-frame payloads are the exception, hence concat is excluded.
                        Assert.True(output.AsSpan().SequenceEqual(Lz4Splits.Decode(frame, v.Options(), 1 << 20, 1 << 20)), $"{v.Name} flip {i}:{bit}");
                    }
                }
            }
        }

        Assert.True(checkedFlips > 10_000);
    }

    [Fact]
    public void HugeDeclaredContentSizeIsRefusedBeforeAllocating()
    {
        // FLG: version 1, independent, content size present.
        byte[] frame = [.. Header(0x60 | 0x08, 0x40, contentSize: 1L << 40), .. DataBlock([0x30, 1, 2, 3], uncompressed: false), .. EndMark];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(frame));
        Assert.Equal("MaxOutputSize", e.LimitName);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 16);
        e = Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(frame, new byte[10]));
        Assert.Equal("MaxOutputSize", e.LimitName);
        var decoder = new Lz4Decoder();
        e = Assert.Throws<CompressionLimitException>(() => decoder.Decompress(frame, new byte[10], out _, out _));
        Assert.Equal(1L << 30, e.Limit);
        Assert.Equal(1L << 40, e.Requested);

        // Content sizes too large for the maximum array are refused the same way even when the limit is raised.
        var raised = new Lz4DecompressionOptions { MaxOutputSize = long.MaxValue };
        Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(frame, raised));
    }

    [Fact]
    public void OutputLimitAppliesAcrossFramesAndWithoutDeclaredSizes()
    {
        Lz4Vector v = Lz4TestData.Vector("text100k-pipe"); // 100 KiB, no content size
        var options = new Lz4DecompressionOptions { MaxOutputSize = 50_000 };
        var e = Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(v.Compressed, options));
        Assert.Equal("MaxOutputSize", e.LimitName);

        // Two frames that each fit but not together.
        Lz4Vector small = Lz4TestData.Vector("text300-1");
        byte[] twice = [.. small.Compressed, .. small.Compressed];
        Assert.Equal(600, Lz4.Lz4.Decompress(twice, new Lz4DecompressionOptions { MaxOutputSize = 600 }).Length);
        Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(twice, new Lz4DecompressionOptions { MaxOutputSize = 599 }));
        Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(twice, new byte[1000], new Lz4DecompressionOptions { MaxOutputSize = 599 }));

        // The streaming decoder stops at the limit too, without having produced more than it.
        var decoder = new Lz4Decoder(options);
        byte[] buffer = new byte[1000];
        byte[] data = v.Compressed;
        int pos = 0;
        long total = 0;
        void Pump()
        {
            while (true)
            {
                decoder.Decompress(data.AsSpan(pos, Math.Min(500, data.Length - pos)), buffer, out int c, out int w);
                pos += c;
                total += w;
            }
        }

        Assert.Throws<CompressionLimitException>(Pump);
        Assert.True(total <= 50_000);
    }

    [Fact]
    public void BlockMaximumLimitAppliesToTheDeclaredSize()
    {
        Lz4Vector v = Lz4TestData.Vector("runs5m-B7"); // blocks up to 4 MiB declared
        var options = new Lz4DecompressionOptions { MaxBlockSize = 256 * 1024 };
        var e = Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(v.Compressed, options));
        Assert.Equal("MaxBlockSize", e.LimitName);
        Assert.Equal(4 << 20, e.Requested);

        // Legacy frames need 8 MiB blocks.
        Lz4Vector legacy = Lz4TestData.Vector("text100k-legacy");
        e = Assert.Throws<CompressionLimitException>(() => Lz4.Lz4.Decompress(legacy.Compressed, options));
        Assert.Equal(8 << 20, e.Requested);
    }

    [Fact]
    public void BlockThatDecodesPastTheFrameBlockMaximumIsRejected()
    {
        // Block maximum 64 KiB (BD 0x40); the block's match runs to 100,000 bytes.
        var block = new List<byte> { 0x1F, (byte)'x', 1, 0 };
        int extra = 100_000 - 19;
        block.AddRange(Enumerable.Repeat((byte)255, extra / 255));
        block.Add((byte)(extra % 255));
        block.AddRange([0x50, 1, 2, 3, 4, 5]);
        byte[] frame = [.. Header(0x60, 0x40), .. DataBlock([.. block], uncompressed: false), .. EndMark];
        var e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(frame));
        Assert.Contains("65536", e.Expected, StringComparison.Ordinal);
        Assert.True(e.Offset >= 12); // inside the block, which starts at byte 12
    }

    [Fact]
    public void BlockSizeAboveTheBlockMaximumIsRejected()
    {
        byte[] frame = [.. Header(0x60, 0x40), .. DataBlock(new byte[65537], uncompressed: true), .. EndMark];
        var e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(frame));
        Assert.Equal(7, e.Offset);
        Assert.Contains("65536", e.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentSizeMustMatchWhatTheBlocksProduce()
    {
        byte[] data = s_text[..200];
        foreach (long declared in new long[] { 199, 201, 0, 1000 })
        {
            byte[] frame = [.. Header(0x60 | 0x08, 0x40, contentSize: declared), .. DataBlock(data, uncompressed: true), .. EndMark];
            var e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(frame));
            Assert.False(e.IsTruncated, declared.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // The same through the streaming decoder.
            Assert.Throws<CompressionFormatException>(() => Lz4Splits.Decode(frame, Lz4DecompressionOptions.Default, 3, 50));
        }

        byte[] right = [.. Header(0x60 | 0x08, 0x40, contentSize: 200), .. DataBlock(data, uncompressed: true), .. EndMark];
        Assert.Equal(data, Lz4.Lz4.Decompress(right));
    }

    [Fact]
    public void ChecksumsAreVerifiedUnlessSwitchedOff()
    {
        byte[] data = s_text[..300];
        // Independent blocks, block checksums and a content checksum, one uncompressed block and one compressed.
        byte[] compressedBlock = Block(new Sequence(data[..20], 0, 0));
        byte[] frame = [.. Header(0x60 | 0x10 | 0x04, 0x40), .. DataBlock(data, uncompressed: true, checksum: true), .. DataBlock(compressedBlock, uncompressed: false, checksum: true), .. EndMark, .. ContentChecksum([.. data, .. data[..20]])];
        Assert.Equal([.. data, .. data[..20]], Lz4.Lz4.Decompress(frame));

        int rawChecksumAt = 7 + 4 + 300;
        int compressedChecksumAt = rawChecksumAt + 4 + 4 + compressedBlock.Length;
        int contentChecksumAt = frame.Length - 4;
        var off = new Lz4DecompressionOptions { VerifyChecksum = false };
        foreach ((int at, string what) in new[] { (rawChecksumAt, "block checksum"), (compressedChecksumAt, "block checksum"), (contentChecksumAt, "content checksum") })
        {
            byte[] bad = (byte[])frame.Clone();
            bad[at] ^= 0x40;
            var e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(bad));
            Assert.Contains(what, e.Expected, StringComparison.Ordinal);
            Assert.Equal(at, e.Offset);
            Assert.Throws<CompressionFormatException>(() => Lz4Splits.Decode(bad, Lz4DecompressionOptions.Default, 5, 33));
            Assert.Equal([.. data, .. data[..20]], Lz4.Lz4.Decompress(bad, off));
        }

        // A damaged compressed block fails its checksum before any of it is decoded.
        byte[] damaged = (byte[])frame.Clone();
        damaged[7 + 4 + 300 + 4 + 4 + 5] ^= 1;
        var e2 = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(damaged));
        Assert.Contains("block checksum", e2.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderFieldsAreValidated()
    {
        byte[] good = Header(0x60, 0x40);
        Assert.Equal([.. good, .. EndMark], [.. good, .. EndMark]);

        void Expect(byte[] frame, long offset, string text)
        {
            var e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(frame));
            Assert.Equal(offset, e.Offset);
            Assert.Contains(text, e.Expected, StringComparison.Ordinal);
        }

        Expect([.. Header(0x20, 0x40), .. EndMark], 4, "version 1");
        Expect([.. Header(0xE0, 0x40), .. EndMark], 4, "version 1");
        Expect([.. Header(0x62, 0x40), .. EndMark], 4, "reserved");
        Expect([.. Header(0x60, 0xC0), .. EndMark], 5, "reserved");
        Expect([.. Header(0x60, 0x41), .. EndMark], 5, "reserved");
        Expect([.. Header(0x60, 0x30), .. EndMark], 5, "block maximum size");
        Expect([.. Header(0x60, 0x00), .. EndMark], 5, "block maximum size");
        byte[] badChecksum = (byte[])good.Clone();
        badChecksum[^1] ^= 1;
        Expect([.. badChecksum, .. EndMark], 6, "header checksum");
        Expect("not lz4 data at all"u8.ToArray(), 0, "magic number");
        Expect([0x04, 0x22, 0x4D, 0x17, 0, 0, 0, 0], 0, "magic number");
        byte[] hugeSize = Header(0x68, 0x40, contentSize: -1);
        Expect([.. hugeSize, .. EndMark], 6, "below 2^63");
    }

    [Fact]
    public void CompressedBlockOfSizeAboveTheMaximumAndOtherFramingMistakesAreRejected()
    {
        // A zero-length compressed block is the EndMark, so nothing after it is read as part of this frame.
        byte[] frame = [.. Header(0x60, 0x40), .. EndMark, 0x41, 0x42];
        Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(frame));

        // An empty uncompressed block (0x80000000) is valid and does not end the frame.
        byte[] withEmpty = [.. Header(0x60, 0x40), .. DataBlock([], uncompressed: true), .. DataBlock([1, 2, 3], uncompressed: true), .. EndMark];
        Assert.Equal([1, 2, 3], Lz4.Lz4.Decompress(withEmpty));

        // Trailing bytes that are not a frame.
        byte[] trailing = [.. withEmpty, 1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(trailing));
    }

    [Fact]
    public void LinkedBlockCannotReachBeforeTheFrameStart()
    {
        // Two blocks; the second block refers 10 bytes back, which exists only if blocks are linked.
        byte[] first = Block(new Sequence(s_text[..40], 0, 0));
        byte[] second = Block(new Sequence(s_text[..2], 10, 20), new Sequence(s_text[..12], 0, 0));
        byte[] linked = [.. Header(0x40, 0x40), .. DataBlock(first, false), .. DataBlock(second, false), .. EndMark];
        byte[] independent = [.. Header(0x60, 0x40), .. DataBlock(first, false), .. DataBlock(second, false), .. EndMark];
        byte[] expected = [.. Expand([], new Sequence(s_text[..40], 0, 0)), .. Expand(s_text[..40], new Sequence(s_text[..2], 10, 20), new Sequence(s_text[..12], 0, 0))];
        Assert.Equal(expected, Lz4.Lz4.Decompress(linked));
        Assert.Equal(expected, Lz4Splits.Decode(linked, Lz4DecompressionOptions.Default, 7, 9));
        Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(independent));
        Assert.Throws<CompressionFormatException>(() => Lz4Splits.Decode(independent, Lz4DecompressionOptions.Default, 7, 9));
        Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(independent, new byte[1000]));

        // A new frame starts with no history, even after a linked frame.
        byte[] second2 = [.. linked, .. Header(0x40, 0x40), .. DataBlock(second, false), .. EndMark];
        Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(second2));
    }

    [Fact]
    public void LegacyFramesRejectBadSizes()
    {
        byte[] magic = [0x02, 0x21, 0x4C, 0x18];
        byte[] tooBig = [.. magic, 0xFF, 0xFF, 0xFF, 0x0F, 1, 2, 3];
        var e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress(tooBig));
        Assert.Equal(4, e.Offset);
        // Cut inside the size field.
        e = Assert.Throws<CompressionFormatException>(() => Lz4.Lz4.Decompress([.. magic, 1, 0]));
        Assert.True(e.IsTruncated);
        // Just the magic number is a legacy frame with no blocks.
        Assert.Empty(Lz4.Lz4.Decompress(magic));
        // The kernel's end marker is accepted, and a frame may follow it.
        Lz4Vector v = Lz4TestData.Vector("text300-1");
        Assert.Equal(300, Lz4.Lz4.Decompress([.. magic, 0, 0, 0, 0, .. v.Compressed]).Length);
    }

    [Fact]
    public void DecoderRefusesInputAfterAnErrorUntilReset()
    {
        var decoder = new Lz4Decoder();
        byte[] buffer = new byte[100];
        Assert.Throws<CompressionFormatException>(() => decoder.Decompress("not lz4 at all.."u8, buffer, out _, out _));
        Assert.Throws<InvalidOperationException>(() => decoder.Decompress("x"u8, buffer, out _, out _));
        decoder.Reset();
        Lz4Vector v = Lz4TestData.Vector("text300-1");
        Assert.Equal(OperationStatus.Done, decoder.Decompress(v.Compressed, new byte[300], out int c, out int w));
        Assert.Equal(v.Compressed.Length, c);
        Assert.Equal(300, w);
    }

    [Fact]
    public void ConsumedAndWrittenAreSetWhenTheCallThrows()
    {
        byte[] frame = [.. Header(0x60, 0x40), .. DataBlock(s_text[..100], uncompressed: true), 0xFF, 0xFF, 0xFF, 0xFF];
        var decoder = new Lz4Decoder();
        byte[] buffer = new byte[1000];
        int consumed = -1;
        int written = -1;
        try
        {
            _ = decoder.Decompress(frame, buffer, out consumed, out written);
        }
        catch (CompressionFormatException)
        {
        }

        Assert.Equal(frame.Length, consumed);
        Assert.Equal(100, written);
    }
}
