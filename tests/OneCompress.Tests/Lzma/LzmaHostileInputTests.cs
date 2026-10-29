using System.Buffers;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;
using LzipApi = RecursiveAI.OneCompress.Lzip.Lzip;
using LzmaApi = RecursiveAI.OneCompress.Lzma.Lzma;
using XzApi = RecursiveAI.OneCompress.Xz.Xz;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Truncated, damaged and hand-built hostile input for the LZMA family: typed errors only, limits respected.</summary>
public class LzmaHostileInputTests
{
    private static readonly XzDecompressionOptions s_xzSmall = new() { MaxOutputSize = 1 << 22 };

    private static IEnumerable<int> Offsets(int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (i < 200 || i >= length - 60 || i % 37 == 0)
            {
                yield return i;
            }
        }
    }

    private static void ExpectOnlyLibraryErrors(Action decode)
    {
        try
        {
            decode();
        }
        catch (CompressionException)
        {
        }
    }

    [Theory]
    [InlineData("text20k-p6.xz")]
    [InlineData("mixed-blocks7000-sha256.xz")]
    [InlineData("code-delta-x86.xz")]
    public void XzTruncatedAtEveryOffsetIsATruncationError(string name)
    {
        byte[] full = LzmaTestData.Vector(name).Compressed;
        foreach (int cut in Offsets(full.Length))
        {
            var e = Assert.Throws<CompressionFormatException>(() => XzApi.Decompress(full.AsSpan(0, cut), s_xzSmall));
            Assert.Equal("xz", e.Format);
            Assert.True(e.Offset >= 0);
        }
    }

    [Theory]
    [InlineData("text20k.lzma")]
    [InlineData("structured60k-lc0lp4pb0.lzma")]
    public void LzmaTruncatedAtEveryOffsetIsATruncationError(string name)
    {
        byte[] full = LzmaTestData.Vector(name).Compressed;
        foreach (int cut in Offsets(full.Length))
        {
            var e = Assert.Throws<CompressionFormatException>(() => LzmaApi.Decompress(full.AsSpan(0, cut)));
            Assert.Equal("lzma", e.Format);
        }
    }

    [Theory]
    [InlineData("members3.lz")]
    [InlineData("zeros300k-l6.lz")]
    public void LzipTruncatedAtEveryOffsetIsATruncationError(string name)
    {
        byte[] full = LzmaTestData.Vector(name).Compressed;
        HashSet<int> boundaries = LzipBoundaries(full);
        foreach (int cut in Offsets(full.Length))
        {
            if (boundaries.Contains(cut))
            {
                continue;
            }

            var e = Assert.Throws<CompressionFormatException>(() => LzipApi.Decompress(full.AsSpan(0, cut)));
            Assert.Equal("lzip", e.Format);
        }
    }

    // A prefix that ends exactly after a member is a complete file of fewer members.
    private static HashSet<int> LzipBoundaries(byte[] file)
    {
        var boundaries = new HashSet<int>();
        var decoder = new LzipDecoder();
        byte[] buffer = new byte[1 << 16];
        int pos = 0;
        while (pos < file.Length)
        {
            OperationStatus status = decoder.Decompress(file.AsSpan(pos), buffer, out int c, out _);
            pos += c;
            if (status == OperationStatus.Done)
            {
                boundaries.Add(pos);
            }
        }

        return boundaries;
    }

    [Theory]
    [InlineData("text20k-p6.xz", 'x')]
    [InlineData("code-arm-delta-sparc.xz", 'x')]
    [InlineData("text20k.lzma", 'l')]
    [InlineData("members3.lz", 'z')]
    public void BitFlipsEndInAResultOrATypedError(string name, char kind)
    {
        byte[] data = LzmaTestData.Vector(name).Compressed.ToArray();
        int flips = 0;
        foreach (int at in Offsets(data.Length))
        {
            for (int bit = 0; bit < 8; bit += at < 40 ? 1 : 5)
            {
                data[at] ^= (byte)(1 << bit);
                ExpectOnlyLibraryErrors(() => Decode(kind, data));
                data[at] ^= (byte)(1 << bit);
                flips++;
            }
        }

        Assert.True(flips > 100);
    }

    private static void Decode(char kind, byte[] data)
    {
        switch (kind)
        {
            case 'x':
                XzApi.Decompress(data, s_xzSmall);
                break;
            case 'l':
                LzmaApi.Decompress(data, new LzmaDecompressionOptions { MaxOutputSize = 1 << 22 });
                break;
            default:
                LzipApi.Decompress(data, new LzipDecompressionOptions { MaxOutputSize = 1 << 22 });
                break;
        }
    }

    // ---- hand-built xz ----

    // A limit of long.MaxValue once made the one-call buffer grow one byte at a time (the limit plus one wrapped around).
    [Fact]
    public void UnlimitedOutputGrowsTheBufferGeometrically()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LzmaVector xz = LzmaTestData.Vector("zeros300k-p6.xz");
        LzmaVector lzma = LzmaTestData.Vector("zeros300k.lzma");
        LzmaVector lzip = LzmaTestData.Vector("zeros300k-l6.lz");
        xz.AssertMatches(XzApi.Decompress(xz.Compressed, new XzDecompressionOptions { MaxOutputSize = long.MaxValue }));
        lzma.AssertMatches(LzmaApi.Decompress(lzma.Compressed, new LzmaDecompressionOptions { MaxOutputSize = long.MaxValue }));
        lzip.AssertMatches(LzipApi.Decompress(lzip.Compressed, new LzipDecompressionOptions { MaxOutputSize = long.MaxValue }));
        Assert.True(sw.ElapsedMilliseconds < 2000, "decoding took " + sw.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void BuilderMakesAValidStreamForEveryCheck()
    {
        foreach (int check in new[] { 0, 1, 4, 10 })
        {
            var b = new XzBuilder { Check = check };
            Assert.Equal(b.Data, XzApi.Decompress(b.Build()));
        }
    }

    [Fact]
    public void ReservedChecksAreRejectedUnlessAllowed()
    {
        var b = new XzBuilder { Check = 2 };
        var e = Assert.Throws<CompressionFormatException>(() => XzApi.Decompress(b.Build()));
        Assert.Contains("check", e.Expected, StringComparison.Ordinal);
        Assert.Equal(b.Data, XzApi.Decompress(b.Build(), new XzDecompressionOptions { AllowUnsupportedChecks = true }));
    }

    public static TheoryData<string> BadXz() => new()
    {
        "header-crc", "stream-flags-reserved", "block-flags-reserved", "block-header-crc", "unknown-filter", "lzma2-not-last", "no-lzma2", "lzma2-prop-41",
        "lzma2-prop-reserved-bit", "bcj-start-misaligned", "bcj-prop-size-2", "delta-prop-size-0", "block-padding", "check-mismatch", "declared-compressed-wrong",
        "declared-uncompressed-wrong", "declared-compressed-zero", "index-count", "index-unpadded", "index-uncompressed", "index-padding", "index-crc",
        "footer-crc", "backward-size", "footer-flags", "footer-magic", "stream-padding-odd", "last-filter-bcj", "five-filters-flag",
    };

    [Theory]
    [MemberData(nameof(BadXz))]
    public void HandBuiltDamageIsATypedFormatError(string what)
    {
        var b = new XzBuilder();
        switch (what)
        {
            case "header-crc": b.BreakHeaderCrc = true; break;
            case "stream-flags-reserved": break;
            case "block-flags-reserved": b.BlockFlagsExtra = 0x04; break;
            case "block-header-crc": b.BreakHeaderCrc = true; break;
            case "unknown-filter": b.FilterFlags = [0x30, 0x00, 0x21, 0x01, 0x16]; b.FilterCount = 2; break;
            case "lzma2-not-last": b.FilterFlags = [0x21, 0x01, 0x16, 0x03, 0x01, 0x00]; b.FilterCount = 2; break;
            case "no-lzma2": b.FilterFlags = [0x03, 0x01, 0x00]; break;
            case "lzma2-prop-41": b.FilterFlags = [0x21, 0x01, 41]; break;
            case "lzma2-prop-reserved-bit": b.FilterFlags = [0x21, 0x01, 0x40 | 16]; break;
            case "bcj-start-misaligned": b.FilterFlags = [0x07, 0x04, 0x02, 0, 0, 0, 0x21, 0x01, 0x16]; b.FilterCount = 2; break;
            case "bcj-prop-size-2": b.FilterFlags = [0x04, 0x02, 0x00, 0x00, 0x21, 0x01, 0x16]; b.FilterCount = 2; break;
            case "delta-prop-size-0": b.FilterFlags = [0x03, 0x00, 0x21, 0x01, 0x16]; b.FilterCount = 2; break;
            case "block-padding": b.PadByte = 1; b.Data = "hello, xzz"u8.ToArray(); break;
            case "check-mismatch": b.CheckOverride = [1, 2, 3, 4]; break;
            case "declared-compressed-wrong": b.DeclaredCompressed = 3; break;
            case "declared-uncompressed-wrong": b.DeclaredUncompressed = 3; break;
            case "declared-compressed-zero": b.DeclaredCompressed = 0; break;
            case "index-count": b.RecordCountOverride = 2; break;
            case "index-unpadded": b.RecordUnpaddedOverride = 77; break;
            case "index-uncompressed": b.RecordUncompressedOverride = 77; break;
            case "index-padding": b.IndexPadByte = 1; b.Data = new byte[200]; break;
            case "index-crc": b.BreakIndexCrc = true; break;
            case "footer-crc": b.BreakFooterCrc = true; break;
            case "backward-size": b.BackwardSizeDelta = 1; break;
            case "footer-flags": b.FooterFlagsOverride = 4; break;
            case "footer-magic": b.FooterMagic = "YY"u8.ToArray(); break;
            case "stream-padding-odd": b.Streams = 2; b.StreamPadding = [0, 0, 0]; break;
            case "last-filter-bcj": b.FilterFlags = [0x04, 0x00]; break;
            default: b.FilterCount = 4; break;
        }

        byte[] bytes = b.Build();
        if (what == "stream-flags-reserved")
        {
            bytes[7] |= 0x10;
            BinaryPrimitives_WriteCrc(bytes);
        }

        var e = Assert.Throws<CompressionFormatException>(() => XzApi.Decompress(bytes));
        Assert.Equal("xz", e.Format);
        Assert.False(string.IsNullOrEmpty(e.Expected));
    }

    private static void BinaryPrimitives_WriteCrc(byte[] bytes)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), RecursiveAI.OneCompress.Checksums.Crc32.Compute(bytes.AsSpan(6, 2)));
    }

    [Fact]
    public void GoodHandBuiltVariantsDecode()
    {
        var b = new XzBuilder { Streams = 3, StreamPadding = new byte[8], DeclaredCompressed = 13, DeclaredUncompressed = 9 };
        Assert.Equal(b.Data.Concat(b.Data).Concat(b.Data), XzApi.Decompress(b.Build()));
        var c = new XzBuilder { FilterFlags = [0x07, 0x04, 0x04, 0, 0, 0, 0x21, 0x01, 0x16], FilterCount = 2 };
        Assert.Equal(c.Data.Length, XzApi.Decompress(c.Build()).Length);
    }

    [Fact]
    public void TrailingBytesAfterAStreamAreAnErrorUnlessAllowed()
    {
        byte[] bytes = [.. new XzBuilder().Build(), 1, 2, 3, 4];
        Assert.Throws<CompressionFormatException>(() => XzApi.Decompress(bytes));
        Assert.Equal(new XzBuilder().Data, XzApi.Decompress(bytes, new XzDecompressionOptions { AllowTrailingData = true }));
        byte[] padded = [.. new XzBuilder().Build(), 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(new XzBuilder().Data, XzApi.Decompress(padded));
        byte[] oddPadded = [.. new XzBuilder().Build(), 0, 0, 0];
        Assert.Throws<CompressionFormatException>(() => XzApi.Decompress(oddPadded));
    }

    [Fact]
    public void ChecksCanBeSkippedButTheStructureIsStillChecked()
    {
        var b = new XzBuilder { CheckOverride = [9, 9, 9, 9] };
        Assert.Equal(b.Data, XzApi.Decompress(b.Build(), new XzDecompressionOptions { VerifyChecks = false }));
        var c = new XzBuilder { RecordCountOverride = 3 };
        Assert.Throws<CompressionFormatException>(() => XzApi.Decompress(c.Build(), new XzDecompressionOptions { VerifyChecks = false }));
    }

    // ---- limits ----

    [Fact]
    public void XzDictionaryLimitIsRaisedBeforeAnythingIsAllocated()
    {
        var b = new XzBuilder { FilterFlags = [0x21, 0x01, 30] };
        var e = Assert.Throws<CompressionLimitException>(() => XzApi.Decompress(b.Build()));
        Assert.Equal("MaxDictionarySize", e.LimitName);
        Assert.Equal(128L << 20, e.Requested);
        Assert.Equal(b.Data, XzApi.Decompress(b.Build(), new XzDecompressionOptions { MaxDictionarySize = 1 << 28 }));
        var huge = new XzBuilder { FilterFlags = [0x21, 0x01, 40] };
        Assert.Throws<CompressionLimitException>(() => XzApi.Decompress(huge.Build(), new XzDecompressionOptions { MaxDictionarySize = uint.MaxValue }));
    }

    [Fact]
    public void LzmaAndLzipDictionaryLimits()
    {
        byte[] lzma = LzmaTestData.Vector("text20k.lzma").Compressed.ToArray();
        lzma[1] = lzma[2] = lzma[3] = lzma[4] = 0xFF;
        var e = Assert.Throws<CompressionLimitException>(() => LzmaApi.Decompress(lzma));
        Assert.Equal("MaxDictionarySize", e.LimitName);

        byte[] lzip = LzmaTestData.Vector("text150k-l6.lz").Compressed.ToArray();
        lzip[5] = 0x1D;
        e = Assert.Throws<CompressionLimitException>(() => LzipApi.Decompress(lzip));
        Assert.Equal("MaxDictionarySize", e.LimitName);
        lzip[5] = 0x1E;
        Assert.Throws<CompressionFormatException>(() => LzipApi.Decompress(lzip));
    }

    [Fact]
    public void DeclaredLzmaSizeOverTheLimitIsRejectedBeforeDecoding()
    {
        byte[] lzma = LzmaTestData.Vector("text20k.lzma").Compressed.ToArray();
        BitConverter.TryWriteBytes(lzma.AsSpan(5), 1L << 40);
        var e = Assert.Throws<CompressionLimitException>(() => LzmaApi.Decompress(lzma));
        Assert.Equal("MaxOutputSize", e.LimitName);
    }

    [Theory]
    [InlineData("zeros300k-p6.xz")]
    [InlineData("zeros300k.lzma")]
    [InlineData("zeros300k-l6.lz")]
    public void OutputLimitIsExactAndNeverOvershot(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        foreach (long limit in new long[] { 0, 1, 1000, v.Size - 1 })
        {
            Assert.Throws<CompressionLimitException>(() => Run(name, v.Compressed, limit));
        }

        Assert.Equal(v.Size, Run(name, v.Compressed, v.Size).Length);
        Assert.Equal(v.Size, Run(name, v.Compressed, v.Size + 1).Length);
    }

    private static byte[] Run(string name, byte[] data, long limit) => Path.GetExtension(name) switch
    {
        ".xz" => XzApi.Decompress(data, new XzDecompressionOptions { MaxOutputSize = limit }),
        ".lzma" => LzmaApi.Decompress(data, new LzmaDecompressionOptions { MaxOutputSize = limit }),
        _ => LzipApi.Decompress(data, new LzipDecompressionOptions { MaxOutputSize = limit }),
    };

    [Fact]
    public void SpanDecoderStopsAtTheLimitWithoutWritingPastIt()
    {
        LzmaVector v = LzmaTestData.Vector("zeros300k-p6.xz");
        var decoder = new XzDecoder(new XzDecompressionOptions { MaxOutputSize = 1000 });
        byte[] buffer = new byte[5000];
        Array.Fill(buffer, (byte)0xAA);
        Assert.Throws<CompressionLimitException>(() => decoder.Decompress(v.Compressed, buffer, out _, out _));
        Assert.Equal(0xAA, buffer[1000]);
        Assert.Throws<InvalidOperationException>(() => decoder.Decompress(v.Compressed, buffer, out _, out _));
        decoder.Reset();
        Assert.Throws<CompressionLimitException>(() => decoder.Decompress(v.Compressed, buffer, out _, out _));
    }

    [Fact]
    public void FormatErrorsNameFormatOffsetAndExpectation()
    {
        byte[] bad = LzmaTestData.Vector("text20k-p6.xz").Compressed.ToArray();
        bad[0] = 0;
        var e = Assert.Throws<CompressionFormatException>(() => XzApi.Decompress(bad));
        Assert.Equal("xz", e.Format);
        Assert.Equal(0, e.Offset);
        Assert.Contains("magic", e.Expected, StringComparison.Ordinal);
        Assert.StartsWith("xz: invalid data at byte offset 0: expected ", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyInputIsATruncationError()
    {
        Assert.True(Assert.Throws<CompressionFormatException>(() => XzApi.Decompress([])).IsTruncated);
        Assert.True(Assert.Throws<CompressionFormatException>(() => LzmaApi.Decompress([])).IsTruncated);
        Assert.True(Assert.Throws<CompressionFormatException>(() => LzipApi.Decompress([])).IsTruncated);
    }

    [Fact]
    public async Task CancellationStopsAnAsyncRead()
    {
        LzmaVector v = LzmaTestData.Vector("text150k-p6.xz");
        await using var stream = new XzStream(new MemoryStream(v.Compressed));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await stream.ReadAsync(new byte[100], cts.Token));
    }

    [Fact]
    public void SteadyStateDecodingAllocatesNothing()
    {
        foreach ((string name, Func<byte[], int> decodeOnce) in SteadyStateCases())
        {
            byte[] data = LzmaTestData.Vector(name).Compressed;

            // The runtime's tiered compilation allocates a little while it warms up, so wait for three clean calls in
            // a row; an allocation made by every call never gets there.
            var allocations = new List<long>();
            int clean = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clean < 3 && clock.ElapsedMilliseconds < 20_000)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                decodeOnce(data);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                allocations.Add(allocated);
                clean = allocated < 512 ? clean + 1 : 0;
            }

            Assert.True(clean >= 3, $"{name} kept allocating: {string.Join(",", allocations.TakeLast(10))} bytes per call");
        }
    }

    private static IEnumerable<(string, Func<byte[], int>)> SteadyStateCases()
    {
        var xz = new XzDecoder();
        var lz = new LzipDecoder();
        var alone = new LzmaDecoder();
        byte[] buffer = new byte[1 << 18];
        yield return ("text20k-p6.xz", d => { xz.Reset(); return xz.Decompress(d, buffer, out _, out int w) == OperationStatus.Done ? w : -1; });
        yield return ("text20k.lzma", d => { alone.Reset(); return alone.Decompress(d, buffer, out _, out int w) == OperationStatus.Done ? w : -1; });
        yield return ("text150k-l6.lz", d => { lz.Reset(); return lz.Decompress(d, buffer, out _, out int w) == OperationStatus.Done ? w : -1; });
    }
}
