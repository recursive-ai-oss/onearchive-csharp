using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

public class ZstdSeekableTests
{
    private static byte[] Sample(int length, int seed = 1)
    {
        // Compressible but not trivial: words from a small vocabulary.
        var rng = new Random(seed);
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", " ", "\n"];
        var bytes = new byte[length];
        int p = 0;
        while (p < length)
        {
            string w = words[rng.Next(words.Length)];
            for (int i = 0; i < w.Length && p < length; i++)
            {
                bytes[p++] = (byte)w[i];
            }
        }

        return bytes;
    }

    [Theory]
    [InlineData(0, 1000, true)]
    [InlineData(1, 1000, true)]
    [InlineData(999, 1000, false)]
    [InlineData(1000, 1000, true)]
    [InlineData(1001, 1000, true)]
    [InlineData(250_000, 4096, true)]
    [InlineData(250_000, 4096, false)]
    [InlineData(100_000, 1 << 20, true)]
    public void RoundTripAndTable(int length, int frameSize, bool checksums)
    {
        byte[] data = Sample(length);
        byte[] file = ZstdSeekable.Compress(data, new ZstdSeekableOptions { FrameSize = frameSize, Checksums = checksums });
        ZstdSeekTable table = ZstdSeekable.ReadTable(file);
        Assert.Equal((length + frameSize - 1) / frameSize, table.Count);
        Assert.Equal(checksums, table.HasChecksums);
        Assert.Equal(length, table.DecompressedLength);
        Assert.Equal(file.Length, table.CompressedLength + table.EncodedLength);
        Assert.Equal(data, ZstdSeekable.Decompress(file));
        // It is also a plain Zstandard file.
        Assert.Equal(data, Zstd.Zstd.Decompress(file));
        for (int i = 0; i < table.Count; i++)
        {
            ZstdSeekFrame f = table.GetFrame(i);
            Assert.Equal((long)i * frameSize, f.DecompressedOffset);
            if (checksums)
            {
                Assert.Equal((uint)XxHash64.Compute(data.AsSpan((int)f.DecompressedOffset, (int)f.DecompressedSize)), f.Checksum);
            }
        }
    }

    [Fact]
    public void RandomRangesEqualInputSlices()
    {
        byte[] data = Sample(300_000, 7);
        byte[] file = ZstdSeekable.Compress(data, new ZstdSeekableOptions { FrameSize = 10_000 });
        using var stream = new ZstdSeekableStream(new MemoryStream(file));
        var rng = new Random(42);
        for (int i = 0; i < 500; i++)
        {
            long offset = rng.Next(-5, data.Length + 100);
            int length = rng.Next(0, 3) == 0 ? rng.Next(0, 100_000) : rng.Next(0, 300);
            if (offset < 0)
            {
                offset = 0;
            }

            int expected = (int)Math.Max(0, Math.Min(length, data.Length - offset));
            byte[] got = new byte[length];
            int n = stream.Reader.Read(offset, got);
            Assert.Equal(expected, n);
            Assert.True(got.AsSpan(0, n).SequenceEqual(data.AsSpan((int)Math.Min(offset, data.Length), n)));
            Assert.Equal(got.AsSpan(0, n).ToArray(), ZstdSeekable.DecompressRange(file, offset, length));
        }
    }

    [Fact]
    public void ReadCostIsTheFramesTouched()
    {
        byte[] data = Sample(100_000);
        byte[] file = ZstdSeekable.Compress(data, new ZstdSeekableOptions { FrameSize = 10_000 });
        using var reader = new ZstdSeekableReader(new MemoryStream(file));
        var buf = new byte[20_000];
        Assert.Equal(20_000, reader.Read(5_000, buf));
        Assert.Equal(3, reader.FramesDecoded);
        Assert.Equal(10_000, reader.Read(50_000, buf.AsSpan(0, 10_000)));
        Assert.Equal(4, reader.FramesDecoded);
        // The same frame again in small pieces is decoded once.
        for (int i = 0; i < 10; i++)
        {
            reader.Read(70_000 + (i * 100), buf.AsSpan(0, 100));
        }

        Assert.Equal(5, reader.FramesDecoded);
    }

    [Fact]
    public async Task StreamSeekAndAsync()
    {
        byte[] data = Sample(50_000, 3);
        byte[] file = ZstdSeekable.Compress(data, new ZstdSeekableOptions { FrameSize = 7000 });
        await using var s = new ZstdSeekableStream(new MemoryStream(file));
        Assert.True(s.CanSeek);
        Assert.False(s.CanWrite);
        Assert.Equal(data.Length, s.Length);
        s.Seek(-1234, SeekOrigin.End);
        var buf = new byte[5000];
        int n = await s.ReadAsync(buf, TestContext.Current.CancellationToken);
        Assert.Equal(1234, n);
        Assert.Equal(data.AsSpan(data.Length - 1234).ToArray(), buf.AsSpan(0, n).ToArray());
        s.Position = 0;
        var all = new MemoryStream();
        await s.CopyToAsync(all, TestContext.Current.CancellationToken);
        Assert.Equal(data, all.ToArray());
        Assert.Throws<NotSupportedException>(() => s.Write(buf, 0, 1));
    }

    [Fact]
    public void NonSeekableInputIsRejectedClearly()
    {
        byte[] file = ZstdSeekable.Compress(Sample(1000));
        using var ms = new NonSeekable(file);
        Assert.Throws<ArgumentException>(() => new ZstdSeekableStream(ms));
        // The documented fallback: sequential decoding skips the seek table.
        using var ms2 = new NonSeekable(file);
        using var zs = new ZstdStream(ms2, ZstdDecompressionOptions.Default);
        var output = new MemoryStream();
        zs.CopyTo(output);
        Assert.Equal(Sample(1000), output.ToArray());
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public void WriterBytesDoNotDependOnWriteChunking()
    {
        byte[] data = Sample(120_000, 5);
        var options = new ZstdSeekableOptions { FrameSize = 16_384, Compression = new ZstdCompressionOptions { Level = 5 } };
        byte[] expected = ZstdSeekable.Compress(data, options);
        foreach (int chunk in new[] { 1, 7, 4096, 16_384, 16_385, 50_000 })
        {
            var ms = new MemoryStream();
            using (var w = new ZstdSeekableWriter(ms, options, leaveOpen: true))
            {
                for (int p = 0; p < data.Length; p += chunk)
                {
                    w.Write(data.AsSpan(p, Math.Min(chunk, data.Length - p)));
                }
            }

            Assert.Equal(expected, ms.ToArray());
        }
    }

    [Fact]
    public async Task AsyncWriterAndReuse()
    {
        byte[] data = Sample(70_000, 9);
        var options = new ZstdSeekableOptions { FrameSize = 10_000 };
        byte[] expected = ZstdSeekable.Compress(data, options);
        var ms = new MemoryStream();
        var w = new ZstdSeekableWriter(ms, options, leaveOpen: true);
        await w.WriteAsync(data.AsMemory(0, 33_333), TestContext.Current.CancellationToken);
        await w.WriteAsync(data.AsMemory(33_333), TestContext.Current.CancellationToken);
        await w.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, ms.ToArray());
        Assert.Equal(7, w.FramesWritten);
        Assert.Throws<InvalidOperationException>(() => w.Write(data.AsSpan(0, 1)));
        var sink = new ArrayBufferWriter<byte>();
        w.Reset(sink);
        w.Write(data);
        w.Complete();
        Assert.Equal(expected, sink.WrittenSpan.ToArray());
        await w.DisposeAsync();
    }

    [Fact]
    public void EndFrameCutsEarly()
    {
        var sink = new ArrayBufferWriter<byte>();
        var w = new ZstdSeekableWriter(sink, new ZstdSeekableOptions { FrameSize = 1000 });
        w.Write(new byte[300]);
        w.EndFrame();
        w.EndFrame();
        w.Write(new byte[2500]);
        w.Complete();
        ZstdSeekTable t = ZstdSeekTable.Read(sink.WrittenSpan);
        Assert.Equal([300L, 1000, 1000, 500], Enumerable.Range(0, t.Count).Select(i => t.GetFrame(i).DecompressedSize).ToArray());
    }

    [Fact]
    public void LevelsDictionaryAndFrameChecksumWork()
    {
        byte[] data = Sample(80_000, 11);
        foreach (int level in new[] { -3, 1, 9, 15 })
        {
            var options = new ZstdSeekableOptions { FrameSize = 20_000, Compression = new ZstdCompressionOptions { Level = level, Checksum = true } };
            Assert.Equal(data, ZstdSeekable.Decompress(ZstdSeekable.Compress(data, options)));
        }

        var dict = ZstdCompressionDictionary.FromContent(data.AsSpan(0, 5000), 77);
        byte[] file = ZstdSeekable.Compress(data, new ZstdSeekableOptions { FrameSize = 20_000, Compression = new ZstdCompressionOptions { Dictionary = dict } });
        Assert.Throws<CompressionDictionaryException>(() => ZstdSeekable.Decompress(file));
        var read = new ZstdSeekableReaderOptions { Decompression = new ZstdDecompressionOptions { Dictionary = ZstdDictionary.FromContent(data.AsSpan(0, 5000), 77) } };
        Assert.Equal(data, ZstdSeekable.Decompress(file, read));
    }

    [Fact]
    public void ContentSizeOptionIsRejected() =>
        Assert.Throws<ArgumentException>(() => ZstdSeekable.Compress(new byte[10], new ZstdSeekableOptions { Compression = new ZstdCompressionOptions { ContentSize = 10 } }));

    [Fact]
    public void VerifyAcceptsGoodFile()
    {
        byte[] file = ZstdSeekable.Compress(Sample(50_000), new ZstdSeekableOptions { FrameSize = 9000 });
        new ZstdSeekableReader(new MemoryStream(file)).Verify(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void SkippableFramesInsideTheFileAreListedWithNoContent()
    {
        byte[] a = Zstd.Zstd.Compress(Sample(1000, 1));
        byte[] skip = [0x50, 0x2A, 0x4D, 0x18, 3, 0, 0, 0, 1, 2, 3];
        byte[] b = Zstd.Zstd.Compress(Sample(500, 2));
        var builder = new ZstdSeekTableBuilder(false);
        builder.Add((uint)a.Length, 1000);
        builder.Add((uint)skip.Length, 0);
        builder.Add((uint)b.Length, 500);
        byte[] file = [.. a, .. skip, .. b, .. builder.ToArray()];
        var reader = new ZstdSeekableReader(file);
        reader.Verify(TestContext.Current.CancellationToken);
        Assert.Equal(1500, reader.Length);
        var got = new byte[1500];
        Assert.Equal(1500, reader.Read(0, got));
        Assert.Equal([.. Sample(1000, 1), .. Sample(500, 2)], got);
        Assert.Equal(2, reader.Table.FindFrame(1000));
    }

    // ---- hostile tables ----

    private static byte[] Good() => ZstdSeekable.Compress(Sample(30_000), new ZstdSeekableOptions { FrameSize = 10_000 });

    private static void ExpectFormat(byte[] file, string? expectedContains = null)
    {
        var ex = Assert.Throws<CompressionFormatException>(() => ZstdSeekable.ReadTable(file));
        Assert.Equal("zstd-seekable", ex.Format);
        Assert.InRange(ex.Offset, 0, file.Length);
        if (expectedContains is not null)
        {
            Assert.Contains(expectedContains, ex.Expected, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RejectsBadFooterMagicReservedBitsAndSizes()
    {
        byte[] good = Good();
        byte[] f = (byte[])good.Clone();
        f[^1] ^= 1;
        ExpectFormat(f, "Seekable_Magic_Number");

        f = (byte[])good.Clone();
        f[^5] |= 0x04;
        ExpectFormat(f, "reserved");

        f = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(f.Length - 9), 1000);
        ExpectFormat(f);

        f = (byte[])good.Clone();
        int tableStart = f.Length - (8 + (3 * 12) + 9);
        f[tableStart] ^= 1;
        ExpectFormat(f, "Skippable_Magic_Number");

        f = (byte[])good.Clone();
        f[tableStart + 4]++;
        ExpectFormat(f, "Frame_Size");

        f = (byte[])good.Clone();
        f[tableStart + 8]++; // first entry's compressed size no longer adds up
        ExpectFormat(f, "frames");

        f = (byte[])good.Clone();
        f[tableStart + 9] = 0;
        f[tableStart + 8] = 3; // below the minimum frame size
        ExpectFormat(f);

        ExpectFormat(good.AsSpan(0, 12).ToArray());
        ExpectFormat([]);
    }

    [Fact]
    public void EntryCountIsLimitedBeforeAllocation()
    {
        byte[] f = Good();
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(f.Length - 9), uint.MaxValue);
        var ex = Assert.Throws<CompressionLimitException>(() => ZstdSeekable.ReadTable(f));
        Assert.Equal("MaxFrames", ex.LimitName);
        Assert.Throws<CompressionLimitException>(() => new ZstdSeekableReader(new MemoryStream(Good()), new ZstdSeekableReaderOptions { MaxFrames = 2 }));
        // A count within the limit that cannot fit the file is a format error.
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(f.Length - 9), 100_000);
        ExpectFormat(f, "fits");
    }

    [Fact]
    public void FrameSizeLimitIsCheckedAtOpen()
    {
        byte[] f = Good();
        var ex = Assert.Throws<CompressionLimitException>(() => new ZstdSeekableReader(f, new ZstdSeekableReaderOptions { MaxFrameSize = 5000 }));
        Assert.Equal("MaxFrameSize", ex.LimitName);
    }

    [Fact]
    public void CorruptFramesAreTypedErrors()
    {
        byte[] good = Good();
        // Checksum in the table that does not match the content.
        byte[] f = (byte[])good.Clone();
        f[^(9 + 4)] ^= 0xFF;
        Assert.Throws<CompressionFormatException>(() => ZstdSeekable.Decompress(f));
        // Table claims a different content size.
        f = (byte[])good.Clone();
        int tableStart = f.Length - (8 + (3 * 12) + 9);
        f[tableStart + 12]++;
        Assert.Throws<CompressionFormatException>(() => ZstdSeekable.Decompress(f));
        // Damage inside a frame, at every kind of position.
        var rng = new Random(1);
        for (int i = 0; i < 300; i++)
        {
            f = (byte[])good.Clone();
            f[rng.Next(0, tableStart)] ^= (byte)(1 << rng.Next(8));
            try
            {
                ZstdSeekable.Decompress(f);
            }
            catch (CompressionException)
            {
            }
        }

        // Truncating the file anywhere is a typed error.
        for (int cut = 0; cut < good.Length; cut += 97)
        {
            Assert.ThrowsAny<CompressionException>(() => ZstdSeekable.Decompress(good.AsMemory(0, cut)));
        }
    }

    [Fact]
    public void ReaderOnStreamReportsTruncatedFrame()
    {
        byte[] good = Good();
        var ms = new MemoryStream(good);
        var reader = new ZstdSeekableReader(ms);
        ms.SetLength(ms.Length - 53 - 20); // the table is now cut, but the reader already holds it; frames past the end are missing
        ms.Position = 0;
        var buf = new byte[10];
        // Frame 0 is intact; the last frame's bytes are partly gone.
        Assert.Equal(10, reader.Read(0, buf));
        Assert.Throws<CompressionFormatException>(() => reader.Read(25_000, buf));
    }

    [Fact]
    public void BuilderRoundTripsThroughParse()
    {
        var b = new ZstdSeekTableBuilder(true);
        for (uint i = 0; i < 1000; i++)
        {
            b.Add(100 + i, i * 3, i * 7);
        }

        byte[] table = b.ToArray();
        long compressed = 0;
        for (uint i = 0; i < 1000; i++)
        {
            compressed += 100 + i;
        }

        byte[] file = new byte[compressed + table.Length];
        table.CopyTo(file, compressed);
        ZstdSeekTable t = ZstdSeekTable.Parse(table, file.Length);
        Assert.Equal(1000, t.Count);
        Assert.Equal(7u * 999, t.GetFrame(999).Checksum);
        Assert.Equal(1099, t.GetFrame(999).CompressedSize);
        Assert.Equal(3L * 999 * 1000 / 2, t.DecompressedLength);
        Assert.Equal(-1, t.FindFrame(t.DecompressedLength));
        Assert.Equal(2, t.FindFrame(3));
        Assert.Equal(1, t.FindFrame(2));
    }
}
