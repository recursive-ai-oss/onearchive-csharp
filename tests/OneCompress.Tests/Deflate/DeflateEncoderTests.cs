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

    // Short random runs and copies from a few thousand bytes back: many matches of every length, which is what stresses a match finder.
    private static byte[] Matchy(int seed, int size)
    {
        var random = new Random(seed);
        var output = new List<byte>(size + 64);
        while (output.Count < size)
        {
            if (output.Count < 10 || random.Next(3) == 0)
            {
                for (int i = random.Next(1, 20); i > 0; i--)
                {
                    output.Add((byte)random.Next(256));
                }
            }
            else
            {
                int length = random.Next(3, 60);
                int distance = random.Next(1, Math.Min(output.Count, 3000));
                for (int i = 0; i < length; i++)
                {
                    output.Add(output[^distance]);
                }
            }
        }

        return output.GetRange(0, size).ToArray();
    }

    // The one-call path sizes its buffers to the input; the optimal parser once closed a block per run when the sequence buffer was
    // that small, so a short input compressed differently in one call than through the stream.
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void ShortInputGivesTheSameBytesInOneCallAndThroughTheStream(int level)
    {
        foreach (int size in new[] { 3200, 7000, 20_000 })
        {
            byte[] half = Matchy(level, size / 2);
            byte[] data = [.. half, .. half]; // the second half is one long match (and its continuation), which ends a parse run early
            var options = new DeflateOptions { Level = level };
            byte[] whole = Deflate.Deflate.Compress(data, options);
            var encoder = new DeflateEncoder(DeflateFormat.Raw, options);
            var buffer = new byte[DeflateEncoder.GetMaxCompressedLength(DeflateFormat.Raw, size)];
            encoder.Compress(data, buffer, out _, out int written, DeflateFlush.Finish);
            Assert.True(whole.AsSpan().SequenceEqual(buffer.AsSpan(0, written)), $"level {level} size {size}");
        }
    }

    // The binary-tree finder (levels 10 to 12) used to insert positions whose comparisons were cut short by the end of the data;
    // after a flush and more input, the tree then claimed matches that were not there. It showed in about one stream in 200,
    // so many small streams with flushes between the writes are tried.
    [Theory]
    [InlineData(10, DeflateFlush.Sync)]
    [InlineData(12, DeflateFlush.Full)]
    public void RepeatedFlushesAtOptimalLevelsStayDecodable(int level, DeflateFlush mode)
    {
        for (int seed = 0; seed < 400; seed++)
        {
            int size = 6000 + (seed * 37 % 6000);
            int step = 700 + (seed * 53 % 1500);
            byte[] data = Matchy(seed, size);
            var encoder = new DeflateEncoder(DeflateFormat.Raw, new DeflateOptions { Level = level });
            var compressed = new List<byte>();
            var buffer = new byte[1 << 16];
            for (int offset = 0; offset < data.Length; offset += step)
            {
                int take = Math.Min(step, data.Length - offset);
                encoder.Compress(data.AsSpan(offset, take), buffer, out _, out int written, offset + take == data.Length ? DeflateFlush.Finish : mode);
                compressed.AddRange(buffer.AsSpan(0, written).ToArray());
            }

            Assert.True(data.AsSpan().SequenceEqual(Deflate.Deflate.Decompress(compressed.ToArray())), $"level {level} seed {seed}");
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
