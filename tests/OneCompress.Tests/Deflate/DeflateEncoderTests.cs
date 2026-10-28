using System.Buffers;
using System.IO.Compression;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

public class DeflateEncoderTests
{
    private static readonly DeflateFormat[] Formats = [DeflateFormat.Raw, DeflateFormat.ZLib, DeflateFormat.GZip];

    public static TheoryData<int, DeflateFormat> LevelsAndFormats()
    {
        var data = new TheoryData<int, DeflateFormat>();
        for (int level = 0; level <= 12; level++)
        {
            foreach (DeflateFormat format in Formats)
            {
                data.Add(level, format);
            }
        }

        return data;
    }

    private static byte[] Compress(DeflateFormat format, byte[] data, DeflateOptions options) => format switch
    {
        DeflateFormat.Raw => Deflate.Deflate.Compress(data, options),
        DeflateFormat.ZLib => ZLib.Compress(data, options),
        _ => GZip.Compress(data, options),
    };

    private static byte[] Decompress(DeflateFormat format, byte[] data, DeflateDecompressionOptions? options = null) => format switch
    {
        DeflateFormat.Raw => Deflate.Deflate.Decompress(data, options),
        DeflateFormat.ZLib => ZLib.Decompress(data, options),
        _ => GZip.Decompress(data, options),
    };

    // The BCL decoders, as an independent check.
    private static byte[] BclDecompress(DeflateFormat format, byte[] data)
    {
        using var input = new MemoryStream(data);
        using Stream s = format switch
        {
            DeflateFormat.Raw => new System.IO.Compression.DeflateStream(input, CompressionMode.Decompress),
            DeflateFormat.ZLib => new ZLibStream(input, CompressionMode.Decompress),
            _ => new GZipStream(input, CompressionMode.Decompress),
        };
        var output = new MemoryStream();
        s.CopyTo(output);
        return output.ToArray();
    }

    [Theory]
    [MemberData(nameof(LevelsAndFormats))]
    public void RoundTripsEveryInputShape(int level, DeflateFormat format)
    {
        var options = new DeflateOptions { Level = level };
        foreach ((string name, byte[] data) in DeflateTestData.Inputs())
        {
            byte[] compressed = Compress(format, data, options);
            Assert.True(compressed.Length <= DeflateEncoder.GetMaxCompressedLength(format, data.Length, options), name);
            Assert.True(data.AsSpan().SequenceEqual(Decompress(format, compressed)), $"{name} level {level} {format}: our decoder");
            Assert.True(data.AsSpan().SequenceEqual(BclDecompress(format, compressed)), $"{name} level {level} {format}: BCL decoder");
        }
    }

    [Theory]
    [InlineData(DeflateStrategy.HuffmanOnly)]
    [InlineData(DeflateStrategy.RunLength)]
    [InlineData(DeflateStrategy.FixedCodes)]
    public void StrategiesRoundTrip(DeflateStrategy strategy)
    {
        foreach (int level in new[] { 1, 6, 11 })
        {
            var options = new DeflateOptions { Level = level, Strategy = strategy };
            foreach ((string name, byte[] data) in DeflateTestData.Inputs())
            {
                byte[] compressed = Deflate.Deflate.Compress(data, options);
                Assert.True(data.AsSpan().SequenceEqual(BclDecompress(DeflateFormat.Raw, compressed)), $"{name} level {level} {strategy}");
            }
        }
    }

    [Fact]
    public void OutputDoesNotDependOnHowInputOrOutputIsSplit()
    {
        byte[] data = DeflateTestData.Inputs().Single(i => i.Name == "mixed300k").Data;
        foreach (int level in new[] { 0, 1, 4, 6, 9, 12 })
        {
            var options = new DeflateOptions { Level = level };
            byte[] whole = ZLib.Compress(data, options);
            foreach ((int inputPiece, int outputPiece) in new[] { (1000, 7), (65536, 100000), (299999, 1), (4093, 4096) })
            {
                var encoder = new DeflateEncoder(DeflateFormat.ZLib, options);
                var output = new List<byte>();
                var buffer = new byte[outputPiece];
                int offset = 0;
                while (true)
                {
                    int take = Math.Min(inputPiece, data.Length - offset);
                    bool last = offset + take == data.Length;
                    OperationStatus status = encoder.Compress(data.AsSpan(offset, take), buffer, out int consumed, out int written, last ? DeflateFlush.Finish : DeflateFlush.None);
                    output.AddRange(buffer.AsSpan(0, written).ToArray());
                    offset += consumed;
                    if (status == OperationStatus.Done)
                    {
                        break;
                    }

                    Assert.True(status is OperationStatus.NeedMoreData or OperationStatus.DestinationTooSmall);
                }

                Assert.True(whole.AsSpan().SequenceEqual(output.ToArray()), $"level {level} pieces {inputPiece}/{outputPiece}");
            }
        }
    }

    [Theory]
    [InlineData(DeflateFlush.Sync)]
    [InlineData(DeflateFlush.Full)]
    public void FlushMakesEverythingSoFarDecodable(DeflateFlush mode)
    {
        byte[] data = DeflateTestData.Text(9, 150_000);
        foreach (int level in new[] { 1, 6, 12 })
        {
            var encoder = new DeflateEncoder(DeflateFormat.Raw, new DeflateOptions { Level = level });
            var compressed = new List<byte>();
            var buffer = new byte[1 << 20];
            int[] cuts = [0, 1, 5000, 70_000, 70_001, 150_000];
            for (int i = 1; i < cuts.Length; i++)
            {
                OperationStatus s = encoder.Compress(data.AsSpan(cuts[i - 1], cuts[i] - cuts[i - 1]), buffer, out _, out int written, mode);
                Assert.Equal(OperationStatus.Done, s);
                compressed.AddRange(buffer.AsSpan(0, written).ToArray());

                // Everything given so far decodes from what was written so far.
                var decoder = new DeflateDecoder(DeflateFormat.Raw);
                var output = new byte[cuts[i] + 1];
                decoder.Decompress(compressed.ToArray(), output, out _, out int produced);
                Assert.Equal(cuts[i], produced);
                Assert.True(data.AsSpan(0, produced).SequenceEqual(output.AsSpan(0, produced)));
            }

            encoder.Compress([], buffer, out _, out int tail, DeflateFlush.Finish);
            compressed.AddRange(buffer.AsSpan(0, tail).ToArray());
            Assert.True(data.AsSpan().SequenceEqual(Deflate.Deflate.Decompress(compressed.ToArray())));
        }
    }

    [Fact]
    public void FullFlushLetsDecodingStartAtTheFlushPoint()
    {
        byte[] data = DeflateTestData.Text(10, 100_000);
        var encoder = new DeflateEncoder(DeflateFormat.Raw, new DeflateOptions { Level = 6 });
        var buffer = new byte[1 << 20];
        encoder.Compress(data.AsSpan(0, 40_000), buffer, out _, out int first, DeflateFlush.Full);
        encoder.Compress(data.AsSpan(40_000), buffer.AsSpan(first), out _, out int second, DeflateFlush.Finish);

        // Decoding only the part after the full flush works without the first part.
        var tailDecoder = new DeflateDecoder(DeflateFormat.Raw);
        var output = new byte[100_000];
        tailDecoder.Decompress(buffer.AsSpan(first, second), output, out _, out int produced);
        Assert.Equal(60_000, produced);
        Assert.True(data.AsSpan(40_000).SequenceEqual(output.AsSpan(0, produced)));
    }

    [Theory]
    [InlineData(DeflateFormat.Raw)]
    [InlineData(DeflateFormat.ZLib)]
    public void DictionaryStreamsRoundTrip(DeflateFormat format)
    {
        byte[] dictionary = DeflateTestData.Text(11, 20_000);
        byte[] data = DeflateTestData.Text(11, 20_000).Concat(DeflateTestData.Text(12, 5_000)).ToArray();
        foreach (int level in new[] { 0, 1, 6, 12 })
        {
            byte[] compressed = Compress(format, data, new DeflateOptions { Level = level, Dictionary = dictionary });
            byte[] plain = Compress(format, data, new DeflateOptions { Level = level });
            if (level >= 4)
            {
                Assert.True(compressed.Length < plain.Length - 1000, $"level {level}");
            }

            byte[] output = Decompress(format, compressed, new DeflateDecompressionOptions { Dictionary = dictionary });
            Assert.True(data.AsSpan().SequenceEqual(output));
        }
    }

    [Fact]
    public void GZipHeaderFieldsAreWritten()
    {
        var options = new DeflateOptions
        {
            Level = 9,
            GZipFileName = "a.txt",
            GZipComment = "hello",
            GZipExtra = new byte[] { (byte)'A', (byte)'b', 2, 0, 9, 9 },
            GZipModificationTime = 1_700_000_000,
            GZipOperatingSystem = 3,
            GZipIsText = true,
            GZipHeaderCrc = true,
        };
        byte[] data = DeflateTestData.Text(13, 3000);
        byte[] compressed = GZip.Compress(data, options);
        var decoder = new DeflateDecoder(DeflateFormat.GZip);
        var output = new byte[4000];
        decoder.Decompress(compressed, output, out _, out int written);
        Assert.Equal(3000, written);
        GZipHeader header = decoder.CurrentGZipHeader;
        Assert.Equal("a.txt", header.GetName());
        Assert.Equal("hello", header.GetComment());
        Assert.Equal(1_700_000_000u, header.ModificationTime);
        Assert.Equal(3, header.OperatingSystem);
        Assert.True(header.IsText);
        Assert.Equal(2, header.ExtraFlags);
        Assert.Equal(6, header.Extra.Length);
        Assert.True(data.AsSpan().SequenceEqual(BclDecompress(DeflateFormat.GZip, compressed)));
    }

    [Fact]
    public void ReusedEncoderAllocatesNothingInSteadyState()
    {
        byte[] data = DeflateTestData.Text(14, 400_000);
        var output = new byte[(int)DeflateEncoder.GetMaxCompressedLength(DeflateFormat.GZip, data.Length)];
        foreach (int level in new[] { 1, 6, 9, 12 })
        {
            var encoder = new DeflateEncoder(DeflateFormat.GZip, new DeflateOptions { Level = level });
            // Warm up until a call allocates nothing: tiered compilation of the hot loops can allocate on this thread the first few times.
            for (int warm = 0; warm < 20; warm++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                encoder.Reset();
                encoder.Compress(data, output, out _, out _, DeflateFlush.Finish);
                if (warm >= 2 && GC.GetAllocatedBytesForCurrentThread() - b <= 64)
                {
                    break;
                }
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            encoder.Reset();
            OperationStatus status = encoder.Compress(data, output, out _, out int written, DeflateFlush.Finish);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(OperationStatus.Done, status);
            Assert.True(written > 0);
            Assert.True(after - before <= 64, $"level {level}: {after - before} bytes");
        }
    }

    [Fact]
    public void HuffmanBuilderMakesCompleteLimitedCodes()
    {
        var builder = new DeflateHuffmanBuilder();
        var random = new Random(5);
        var lengths = new byte[286];
        for (int round = 0; round < 300; round++)
        {
            var frequencies = new uint[286];
            int used = 2 + random.Next(284);
            for (int i = 0; i < used; i++)
            {
                // Skewed enough that unlimited Huffman lengths pass 15.
                frequencies[random.Next(286)] = (uint)(1L << random.Next(24)) + (uint)random.Next(5);
            }

            int maxBits = round % 3 == 0 ? 15 : 12;
            if (frequencies.Count(f => f != 0) > (1 << maxBits))
            {
                continue;
            }

            builder.BuildLengths(frequencies, maxBits, lengths);
            double kraft = 0;
            for (int i = 0; i < 286; i++)
            {
                Assert.Equal(frequencies[i] != 0, lengths[i] != 0);
                Assert.True(lengths[i] <= maxBits);
                if (lengths[i] != 0)
                {
                    kraft += Math.Pow(2, -lengths[i]);
                }
            }

            if (frequencies.Count(f => f != 0) > 1)
            {
                Assert.Equal(1.0, kraft);
            }
        }
    }

    [Theory]
    [InlineData(DeflateFormat.Raw)]
    [InlineData(DeflateFormat.ZLib)]
    [InlineData(DeflateFormat.GZip)]
    public async Task EncoderStreamWritesWhatTheEncoderWrites(DeflateFormat format)
    {
        byte[] data = DeflateTestData.Text(15, 250_000);
        var options = new DeflateOptions { Level = 5 };
        byte[] expected = Compress(format, data, options);

        var sync = new MemoryStream();
        using (var s = new DeflateEncoderStream(sync, format, options, leaveOpen: true))
        {
            for (int offset = 0; offset < data.Length; offset += 7777)
            {
                s.Write(data, offset, Math.Min(7777, data.Length - offset));
            }
        }

        Assert.True(expected.AsSpan().SequenceEqual(sync.ToArray()));

        var asyncOut = new MemoryStream();
        await using (var s = new DeflateEncoderStream(asyncOut, format, options, leaveOpen: true))
        {
            await s.WriteAsync(data.AsMemory(0, 100_000), TestContext.Current.CancellationToken);
            await s.FlushAsync(TestContext.Current.CancellationToken);
            await s.WriteAsync(data.AsMemory(100_000), TestContext.Current.CancellationToken);
        }

        Assert.True(data.AsSpan().SequenceEqual(BclDecompress(format, asyncOut.ToArray())));
    }

    [Fact]
    public void EmptyStreamsAreValid()
    {
        foreach (DeflateFormat format in Formats)
        {
            byte[] compressed = Compress(format, [], DeflateOptions.Default);
            Assert.Empty(Decompress(format, compressed));
            Assert.Empty(BclDecompress(format, compressed));
        }
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeflateEncoder(DeflateFormat.Raw, new DeflateOptions { Level = 13 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeflateEncoder(DeflateFormat.Raw, new DeflateOptions { Level = -1 }));
        Assert.Throws<ArgumentException>(() => new DeflateEncoder(DeflateFormat.GZip, new DeflateOptions { Dictionary = new byte[] { 1 } }));
        Assert.Throws<ArgumentException>(() => new DeflateEncoder(DeflateFormat.GZip, new DeflateOptions { GZipFileName = "a\0b" }));
        var encoder = new DeflateEncoder(DeflateFormat.Raw);
        var buffer = new byte[100];
        Assert.Equal(OperationStatus.Done, encoder.Compress([], buffer, out _, out _, DeflateFlush.Finish));
        Assert.Throws<InvalidOperationException>(() => encoder.Compress(new byte[] { 1 }, buffer, out _, out _, DeflateFlush.None));
        encoder.Reset();
        Assert.Equal(OperationStatus.NeedMoreData, encoder.Compress(new byte[] { 1 }, buffer, out _, out _, DeflateFlush.None));
    }
}
