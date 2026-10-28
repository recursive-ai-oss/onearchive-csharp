using System.Buffers;
using System.IO.Compression;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>The decoder against streams written by the BCL's codecs (run as a black box) at several levels, and behaviour at its edges.</summary>
public class DeflateDecoderTests
{
    public static TheoryData<string, DeflateFormat, CompressionLevel> BclCases()
    {
        var data = new TheoryData<string, DeflateFormat, CompressionLevel>();
        foreach ((string name, _) in DeflateTestData.Inputs())
        {
            foreach (DeflateFormat format in new[] { DeflateFormat.Raw, DeflateFormat.ZLib, DeflateFormat.GZip })
            {
                foreach (CompressionLevel level in new[] { CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.SmallestSize, CompressionLevel.NoCompression })
                {
                    data.Add(name, format, level);
                }
            }
        }

        return data;
    }

    private static byte[] Input(string name) => DeflateTestData.Inputs().Single(i => i.Name == name).Data;

    [Theory]
    [MemberData(nameof(BclCases))]
    public void DecodesBclOutput(string name, DeflateFormat format, CompressionLevel level)
    {
        byte[] data = Input(name);
        byte[] compressed = DeflateTestData.BclCompress(format, data, level);
        if (data.Length == 0)
        {
            return; // the BCL writes nothing at all for empty input; the zlib-written vectors cover empty streams
        }

        byte[] expected = data;
        Assert.Equal(expected, Decode(format, compressed));
        if (data.Length <= 200_000)
        {
            Assert.Equal(expected, DeflateVectorTests.Pieces(format, compressed, DeflateDecompressionOptions.Default, 1000, 777));
        }
    }

    private static byte[] Decode(DeflateFormat format, byte[] compressed, DeflateDecompressionOptions? options = null) => format switch
    {
        DeflateFormat.Raw => Deflate.Deflate.Decompress(compressed, options),
        DeflateFormat.ZLib => ZLib.Decompress(compressed, options),
        _ => GZip.Decompress(compressed, options),
    };

    [Fact]
    public void ErrorsNameTheFormatAndOffset()
    {
        byte[] data = DeflateTestData.Text(1, 5000);
        foreach (DeflateFormat format in new[] { DeflateFormat.Raw, DeflateFormat.ZLib, DeflateFormat.GZip })
        {
            byte[] compressed = DeflateTestData.BclCompress(format, data, CompressionLevel.Optimal);
            string name = format switch { DeflateFormat.Raw => "deflate", DeflateFormat.ZLib => "zlib", _ => "gzip" };

            // Truncation: the offset is where the input ended.
            var truncated = Assert.Throws<CompressionFormatException>(() => Decode(format, compressed[..^3]));
            Assert.True(truncated.IsTruncated);
            Assert.Equal(name, truncated.Format);
            Assert.Equal(compressed.Length - 3, truncated.Offset);
            Assert.StartsWith(name + ": invalid data at byte offset ", truncated.Message, StringComparison.Ordinal);

            Assert.Throws<CompressionFormatException>(() => Decode(format, []));
        }
    }

    [Fact]
    public void ChecksumMismatchIsAnError()
    {
        byte[] data = DeflateTestData.Text(2, 3000);
        byte[] zlib = DeflateTestData.BclCompress(DeflateFormat.ZLib, data, CompressionLevel.Optimal);
        zlib[^1] ^= 1;
        var e = Assert.Throws<CompressionFormatException>(() => ZLib.Decompress(zlib));
        Assert.Contains("Adler-32", e.Expected, StringComparison.Ordinal);
        Assert.Equal(data, ZLib.Decompress(zlib, new DeflateDecompressionOptions { VerifyChecksum = false }));

        byte[] gzip = DeflateTestData.BclCompress(DeflateFormat.GZip, data, CompressionLevel.Optimal);
        gzip[^5] ^= 1; // the CRC
        e = Assert.Throws<CompressionFormatException>(() => GZip.Decompress(gzip));
        Assert.Contains("CRC-32", e.Expected, StringComparison.Ordinal);
        Assert.Equal(data, GZip.Decompress(gzip, new DeflateDecompressionOptions { VerifyChecksum = false }));
        gzip[^5] ^= 1;
        gzip[^1] ^= 0x10; // ISIZE
        e = Assert.Throws<CompressionFormatException>(() => GZip.Decompress(gzip));
        Assert.Contains("ISIZE", e.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void ZLibHeaderIsValidated()
    {
        Assert.Throws<CompressionFormatException>(() => ZLib.Decompress([0x78, 0x9D, 3, 0, 0, 0, 0, 1])); // FCHECK wrong
        Assert.Throws<CompressionFormatException>(() => ZLib.Decompress([0x79, 0x9C, 3, 0, 0, 0, 0, 1])); // CM 9
        Assert.Throws<CompressionFormatException>(() => ZLib.Decompress([0x88, 0x1C, 3, 0, 0, 0, 0, 1])); // window 64 KiB
        Assert.Empty(ZLib.Decompress([0x78, 0x9C, 3, 0, 0, 0, 0, 1]));
    }

    [Fact]
    public void GZipHeaderFieldsAreExposed()
    {
        DeflateVector v = DeflateTestData.Vector("header-all-fields");
        var decoder = new DeflateDecoder(DeflateFormat.GZip);
        byte[] buffer = new byte[1000];
        Assert.Equal(OperationStatus.Done, decoder.Decompress(v.Compressed, buffer, out _, out int written));
        Assert.Equal(v.Size, written);
        GZipHeader h = decoder.CurrentGZipHeader;
        Assert.True(h.IsText);
        Assert.Equal("report.txt", h.GetName());
        Assert.Equal("made by a test", h.GetComment());
        Assert.Equal(1700000000u, h.ModificationTime);
        Assert.Equal(3, h.OperatingSystem);
        Assert.Equal(2, h.ExtraFlags);
        Assert.Equal(new byte[] { (byte)'A', (byte)'B', 4, 0, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'x', (byte)'y', 0, 1, 2 }, h.Extra.ToArray());

        // Byte by byte: same header.
        decoder = new DeflateDecoder(DeflateFormat.GZip);
        for (int i = 0; i < v.Compressed.Length; i++)
        {
            OperationStatus s = decoder.Decompress(v.Compressed.AsSpan(i, 1), buffer, out _, out _);
            Assert.Equal(i == v.Compressed.Length - 1 ? OperationStatus.Done : OperationStatus.NeedMoreData, s);
        }

        Assert.Equal("report.txt", decoder.CurrentGZipHeader.GetName());

        DeflateVector plain = DeflateTestData.Vector("header-extra-only");
        decoder = new DeflateDecoder(DeflateFormat.GZip);
        decoder.Decompress(plain.Compressed, buffer, out _, out _);
        Assert.False(decoder.CurrentGZipHeader.HasName);
        Assert.Null(decoder.CurrentGZipHeader.GetName());
        Assert.Equal(255, decoder.CurrentGZipHeader.OperatingSystem);
        Assert.Equal(0, decoder.CurrentGZipHeader.Extra.Length);
    }

    [Fact]
    public void GZipHeaderChecksAndLimits()
    {
        DeflateVector v = DeflateTestData.Vector("header-all-fields");
        byte[] bad = (byte[])v.Compressed.Clone();
        // The CRC-16 follows the comment's terminator.
        int crcAt = 10 + 2 + 13 + 11 + 15; // fixed part, XLEN, extra data, name with its zero, comment with its zero
        bad[crcAt] ^= 0x55;
        var e = Assert.Throws<CompressionFormatException>(() => GZip.Decompress(bad, v.Options()));
        Assert.Contains("CRC-16", e.Expected, StringComparison.Ordinal);
        Assert.Equal(v.Size, GZip.Decompress(bad, new DeflateDecompressionOptions { VerifyChecksum = false }).Length);

        var limit = Assert.Throws<CompressionLimitException>(() => GZip.Decompress(v.Compressed, new DeflateDecompressionOptions { MaxHeaderFieldSize = 12 }));
        Assert.Equal(nameof(DeflateDecompressionOptions.MaxHeaderFieldSize), limit.LimitName);

        byte[] magic = [0x1F, 0x8C, 8, 0, 0, 0, 0, 0, 0, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<CompressionFormatException>(() => GZip.Decompress(magic));
        byte[] method = [0x1F, 0x8B, 7, 0, 0, 0, 0, 0, 0, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<CompressionFormatException>(() => GZip.Decompress(method));
        byte[] reserved = [0x1F, 0x8B, 8, 0x20, 0, 0, 0, 0, 0, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<CompressionFormatException>(() => GZip.Decompress(reserved));
    }

    [Fact]
    public void PresetDictionaryIsMatchedByAdler()
    {
        DeflateVector v = DeflateTestData.Vector("text300-dict-zlib");
        v.AssertMatches(ZLib.Decompress(v.Compressed, v.Options()));

        var missing = Assert.Throws<CompressionDictionaryException>(() => ZLib.Decompress(v.Compressed));
        Assert.Equal("zlib", missing.Format);
        Assert.Equal(RecursiveAI.OneCompress.Checksums.Adler32.Compute(v.Dictionary!), missing.DictionaryId);

        byte[] other = (byte[])v.Dictionary!.Clone();
        other[0] ^= 1;
        Assert.Throws<CompressionDictionaryException>(() => ZLib.Decompress(v.Compressed, new DeflateDecompressionOptions { Dictionary = other }));

        // Raw streams cannot name a dictionary; the one given is used, and without it matches reach before the start.
        DeflateVector raw = DeflateTestData.Vector("text300-dict-raw");
        raw.AssertMatches(Deflate.Deflate.Decompress(raw.Compressed, raw.Options()));
        Assert.Throws<CompressionFormatException>(() => Deflate.Deflate.Decompress(raw.Compressed));
    }

    [Fact]
    public void OutputLimitIsEnforcedBeforeAllocating()
    {
        byte[] zeros = new byte[20_000_000];
        foreach (DeflateFormat format in new[] { DeflateFormat.Raw, DeflateFormat.ZLib, DeflateFormat.GZip })
        {
            byte[] compressed = DeflateTestData.BclCompress(format, zeros, CompressionLevel.Optimal);
            Assert.True(compressed.Length < 40_000);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var e = Assert.Throws<CompressionLimitException>(() => Decode(format, compressed, new DeflateDecompressionOptions { MaxOutputSize = 1 << 20 }));
            Assert.Equal(nameof(DeflateDecompressionOptions.MaxOutputSize), e.LimitName);
            Assert.Equal(1 << 20, e.Limit);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8_000_000, "allocation stayed small");

            // Exactly at the limit is fine; one under is not.
            Assert.Equal(zeros.Length, Decode(format, compressed, new DeflateDecompressionOptions { MaxOutputSize = zeros.Length }).Length);
            Assert.Throws<CompressionLimitException>(() => Decode(format, compressed, new DeflateDecompressionOptions { MaxOutputSize = zeros.Length - 1 }));
        }
    }

    [Fact]
    public void DecoderWritesNothingPastTheLimit()
    {
        byte[] compressed = DeflateTestData.BclCompress(DeflateFormat.Raw, new byte[100_000], CompressionLevel.Optimal);
        var decoder = new DeflateDecoder(DeflateFormat.Raw, new DeflateDecompressionOptions { MaxOutputSize = 1000 });
        byte[] buffer = new byte[5000];
        Array.Fill(buffer, (byte)0xAA);
        Assert.Throws<CompressionLimitException>(() => decoder.Decompress(compressed, buffer, out _, out _));
        Assert.All(buffer.AsSpan(1000).ToArray(), static b => Assert.Equal(0xAA, b));
        Assert.Throws<InvalidOperationException>(() => decoder.Decompress(compressed, buffer, out _, out _));
        decoder.Reset();
        Assert.Throws<CompressionLimitException>(() => decoder.Decompress(compressed, buffer, out _, out _));
    }

    [Fact]
    public void TrailingDataIsRefusedUnlessAllowed()
    {
        byte[] data = DeflateTestData.Text(3, 2000);
        foreach (DeflateFormat format in new[] { DeflateFormat.Raw, DeflateFormat.ZLib, DeflateFormat.GZip })
        {
            byte[] compressed = [.. DeflateTestData.BclCompress(format, data, CompressionLevel.Optimal), .. new byte[] { 9, 9, 9, 9, 9 }];
            Assert.Throws<CompressionFormatException>(() => Decode(format, compressed));
            Assert.Equal(data, Decode(format, compressed, new DeflateDecompressionOptions { AllowTrailingData = true }));
            using var stream = new DeflateCodecStream(new MemoryStream(compressed), format, new DeflateDecompressionOptions { AllowTrailingData = true });
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(data, ms.ToArray());
            using var strict = new DeflateCodecStream(new MemoryStream(compressed), format);
            Assert.Throws<CompressionFormatException>(() => strict.CopyTo(Stream.Null));
        }
    }

    [Fact]
    public void ConcatenatedGZipMembersJoin()
    {
        byte[] a = DeflateTestData.Text(4, 3000);
        byte[] b = DeflateTestData.Text(5, 70_000);
        byte[] joined = [.. DeflateTestData.BclCompress(DeflateFormat.GZip, a, CompressionLevel.Optimal), .. DeflateTestData.Vector("empty-gzip").Compressed, .. DeflateTestData.BclCompress(DeflateFormat.GZip, b, CompressionLevel.Fastest)];
        Assert.Equal([.. a, .. b], GZip.Decompress(joined));

        // A decoder returns Done after each member and starts the next on the next call.
        var decoder = new DeflateDecoder(DeflateFormat.GZip);
        byte[] buffer = new byte[100_000];
        int pos = 0;
        int members = 0;
        while (pos < joined.Length)
        {
            Assert.Equal(OperationStatus.Done, decoder.Decompress(joined.AsSpan(pos), buffer, out int c, out _));
            pos += c;
            members++;
        }

        Assert.Equal(3, members);
        Assert.True(decoder.IsAtFrameBoundary);
    }

    [Fact]
    public void DecoderDoesNotAllocateInSteadyState()
    {
        foreach (DeflateFormat format in new[] { DeflateFormat.Raw, DeflateFormat.ZLib, DeflateFormat.GZip })
        {
            byte[] compressed = DeflateTestData.BclCompress(format, Input("mixed300k"), CompressionLevel.Optimal);
            var decoder = new DeflateDecoder(format);
            byte[] buffer = new byte[Input("mixed300k").Length];
            byte[] small = new byte[1000];
            DecodeAll(decoder, compressed, buffer);
            DecodeAllSmall(decoder, compressed, small);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 3; i++)
            {
                DecodeAll(decoder, compressed, buffer);
                DecodeAllSmall(decoder, compressed, small);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    private static void DecodeAll(DeflateDecoder decoder, byte[] compressed, byte[] buffer)
    {
        // Plain checks: an assertion may itself allocate, and the test counts allocations.
        if (decoder.Decompress(compressed, buffer, out int c, out _) != OperationStatus.Done || c != compressed.Length)
        {
            throw new InvalidOperationException("decode did not finish");
        }
    }

    private static void DecodeAllSmall(DeflateDecoder decoder, byte[] compressed, byte[] small)
    {
        int pos = 0;
        OperationStatus status;
        do
        {
            status = decoder.Decompress(compressed.AsSpan(pos, Math.Min(compressed.Length - pos, 4096)), small, out int c, out _);
            pos += c;
        }
        while (status != OperationStatus.Done);
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeflateDecoder((DeflateFormat)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeflateDecoder(DeflateFormat.Raw, new DeflateDecompressionOptions { MaxOutputSize = -1 }));
        Assert.Throws<ArgumentNullException>(() => new DeflateCodecStream(null!, DeflateFormat.Raw));
        Assert.Throws<ArgumentException>(() => new DeflateCodecStream(new WriteOnlyStream(), DeflateFormat.Raw));
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }

    // Matches that reach back into earlier calls' output (the window), short distances, and literal pairs, with the output handed over
    // in pieces of every size from 1 byte up: each piece boundary lands somewhere else in the stream.
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(300)]
    [InlineData(4099)]
    [InlineData(70000)]
    public void OutputInPiecesGivesTheSameBytes(int piece)
    {
        foreach (string name in new[] { "text1k", "runs500k", "mixed300k" })
        {
            byte[] data = Input(name);
            if (piece == 1 && data.Length > 100_000)
            {
                data = data.AsSpan(0, 100_000).ToArray();
            }

            foreach (CompressionLevel level in new[] { CompressionLevel.Fastest, CompressionLevel.SmallestSize })
            {
                byte[] compressed = DeflateTestData.BclCompress(DeflateFormat.Raw, data, level);
                var decoder = new DeflateDecoder(DeflateFormat.Raw);
                byte[] output = new byte[data.Length];
                int consumed = 0;
                int written = 0;
                OperationStatus status;
                do
                {
                    int take = Math.Min(piece, output.Length - written);
                    status = decoder.Decompress(compressed.AsSpan(consumed), output.AsSpan(written, take), out int c, out int w);
                    consumed += c;
                    written += w;
                }
                while (status is OperationStatus.DestinationTooSmall or OperationStatus.NeedMoreData && (written < output.Length || status == OperationStatus.NeedMoreData && consumed < compressed.Length));

                Assert.Equal(data.Length, written);
                Assert.True(data.AsSpan().SequenceEqual(output), $"{name} {level} piece {piece}");
            }
        }
    }

    [Fact]
    public async Task StreamHonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await using var stream = new DeflateCodecStream(new MemoryStream(DeflateTestData.BclCompress(DeflateFormat.GZip, [1, 2, 3], CompressionLevel.Optimal)), DeflateFormat.GZip);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[1], cts.Token));
    }
}
