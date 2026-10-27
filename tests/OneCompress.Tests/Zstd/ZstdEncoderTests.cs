using System.Buffers;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Round trips of the Zstandard encoder through this library's decoder; the zstd tool is exercised in the interop tests.</summary>
public class ZstdEncoderTests
{
    public static TheoryData<int> Levels() => new() { -131072, -50, -7, -5, -3, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 19, 22 };

    internal static byte[] Text(int length, int seed)
    {
        // Words drawn from a small vocabulary: compressible with many short and long matches.
        string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "zstandard", "frame", "block", "literal", "sequence"];
        var random = new Random(seed);
        var sb = new System.Text.StringBuilder();
        while (sb.Length < length)
        {
            sb.Append(words[random.Next(words.Length)]).Append(random.Next(8) == 0 ? ".\n" : " ");
            if (random.Next(40) == 0)
            {
                sb.Append(random.Next(100000));
            }
        }

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    internal static byte[] RandomBytes(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    internal static IEnumerable<(string Name, byte[] Data)> Inputs()
    {
        yield return ("empty", []);
        yield return ("one", [42]);
        yield return ("seven", [1, 2, 3, 4, 5, 6, 7]);
        yield return ("eight-zeros", new byte[8]);
        yield return ("text100", Text(100, 1));
        yield return ("text2000", Text(2000, 2));
        yield return ("text70000", Text(70000, 3));
        yield return ("text131071", Text(131071, 4));
        yield return ("text131072", Text(131072, 5));
        yield return ("text131073", Text(131073, 6));
        yield return ("text700000", Text(700000, 7));
        yield return ("random1000", RandomBytes(1000, 8));
        yield return ("random300000", RandomBytes(300000, 9));
        yield return ("zeros300000", new byte[300000]);
        byte[] mixed = new byte[400000];
        Text(150000, 10).CopyTo(mixed, 0);
        RandomBytes(100000, 11).CopyTo(mixed, 150000);
        Text(150000, 10).CopyTo(mixed, 250000);
        yield return ("mixed400000", mixed);
        byte[] periodic = new byte[200000];
        for (int i = 0; i < periodic.Length; i++)
        {
            periodic[i] = (byte)((i % 251) ^ (i % 7));
        }

        yield return ("periodic200000", periodic);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void EveryLevelRoundTripsEveryInput(int level)
    {
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = level, Checksum = true });
        foreach ((string name, byte[] data) in Inputs())
        {
            byte[] frame = new byte[ZstdEncoder.GetMaxCompressedLength(data.Length)];
            int n = encoder.CompressFrame(data, frame);
            byte[] back = Zstd.Zstd.Decompress(frame.AsSpan(0, n));
            Assert.True(back.AsSpan().SequenceEqual(data), $"level {level}, input {name}");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OptionsChangeTheFrameButNotTheContent(bool checksum, bool contentSize)
    {
        byte[] data = Text(50000, 12);
        byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 2, Checksum = checksum, WriteContentSize = contentSize });
        Assert.True(ZstdDecoder.TryReadFrameHeader(frame, out ZstdFrameHeader header));
        Assert.Equal(contentSize, header.HasContentSize);
        Assert.Equal(data, Zstd.Zstd.Decompress(frame));
    }

    [Fact]
    public void SameInputAndOptionsGiveSameBytesWhateverTheEncoderDidBefore()
    {
        var options = new ZstdCompressionOptions { Level = 3 };
        var shared = new ZstdEncoder(options);
        foreach ((string name, byte[] data) in Inputs())
        {
            byte[] expected = Zstd.Zstd.Compress(data, options);
            byte[] buffer = new byte[ZstdEncoder.GetMaxCompressedLength(data.Length)];
            int n = shared.CompressFrame(data, buffer);
            Assert.True(expected.AsSpan().SequenceEqual(buffer.AsSpan(0, n)), name);
        }
    }

    [Fact]
    public void MidLevelsFindMoreThanTheFastLevels()
    {
        // Repeated stretches at distances of a few bytes up to several hundred kilobytes: every strategy has something to find.
        byte[] data = Text(700_000, 21);
        int fast = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 1 }).Length;
        for (int level = 5; level <= 15; level++)
        {
            byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level });
            Assert.True(frame.Length < fast, $"level {level}: {frame.Length} bytes against {fast} at level 1");
            Assert.True(Zstd.Zstd.Decompress(frame).AsSpan().SequenceEqual(data), $"level {level}");
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(13)]
    [InlineData(15)]
    public void EveryMatchTheMidFindersReportIsReal(int level)
    {
        // Parses many short blocks of repetitive data with repeats far longer than the comparison cap, so that the ends of blocks,
        // takeovers of old tree nodes and stale table entries all occur, and checks every reported match against the bytes.
        for (int seed = 1; seed <= 60; seed++)
        {
            var random = new Random(seed);
            byte[] data = new byte[500_000];
            byte[] unit = new byte[1 + random.Next(random.Next(3) == 0 ? 400 : 6000)];
            for (int i = 0; i < data.Length; i += unit.Length)
            {
                if (i == 0 || random.Next(4) == 0)
                {
                    for (int k = 0; k < unit.Length; k++)
                    {
                        unit[k] = (byte)('a' + random.Next(3));
                    }
                }

                Array.Copy(unit, 0, data, i, Math.Min(unit.Length, data.Length - i));
                for (int m = random.Next(500); m < unit.Length && i + m < data.Length; m += 50 + random.Next(600))
                {
                    data[i + m] = (byte)('a' + random.Next(3));
                }
            }

            ZstdCompressionParameters parameters = ZstdCompressionParameters.Resolve(level, -1, 0);
            var finder = new ZstdLazyMatchFinder();
            finder.BeginFrame(parameters, null);
            var store = new ZstdSequenceStore();
            var reps = new ZstdRepeatOffsets { Offset1 = 1, Offset2 = 4, Offset3 = 8 };
            int start = 0;
            while (start < data.Length)
            {
                int end = Math.Min(data.Length, start + 100 + random.Next(random.Next(4) == 0 ? 120_000 : 3000));
                store.Clear();
                finder.FindSequences(data, start, end, 0, reps, store);
                int position = start;
                foreach (ZstdSequence sequence in store.Sequences)
                {
                    position += (int)sequence.LiteralLength;
                    Assert.True(sequence.Offset >= 1 && sequence.Offset <= position, $"level {level} seed {seed}: offset {sequence.Offset} at {position}");
                    ReadOnlySpan<byte> match = data.AsSpan(position, (int)sequence.MatchLength);
                    Assert.True(match.SequenceEqual(data.AsSpan(position - (int)sequence.Offset, (int)sequence.MatchLength)), $"level {level} seed {seed}: match of {sequence.MatchLength} at {position}, offset {sequence.Offset}, is not in the data");
                    position += (int)sequence.MatchLength;
                    Assert.True(position <= end);
                    reps.Apply(sequence.Offset, sequence.LiteralLength);
                }

                start = end;
            }
        }
    }

    [Theory]
    [InlineData(5, 20_000_000, 21, 18, 19, 3, 5, 2, ZstdStrategy.Greedy)]
    [InlineData(9, 20_000_000, 22, 20, 21, 4, 5, 16, ZstdStrategy.Lazy2)]
    [InlineData(12, 20_000_000, 22, 22, 23, 6, 5, 32, ZstdStrategy.Lazy2)]
    [InlineData(15, 20_000_000, 22, 23, 23, 6, 5, 32, ZstdStrategy.BinaryTreeLazy2)]
    [InlineData(16, 2_000_000_000, 22, 22, 22, 5, 5, 48, ZstdStrategy.BinaryTreeOptimal)]
    [InlineData(19, 2_000_000_000, 23, 24, 22, 7, 3, 256, ZstdStrategy.BinaryTreeUltra2)]
    [InlineData(22, 2_000_000_000, 27, 27, 25, 9, 3, 999, ZstdStrategy.BinaryTreeUltra2)]
    [InlineData(17, 262_144, 18, 19, 19, 8, 3, 256, ZstdStrategy.BinaryTreeUltra)]
    [InlineData(20, 131_072, 17, 18, 17, 7, 3, 512, ZstdStrategy.BinaryTreeUltra2)]
    [InlineData(16, 16_384, 14, 15, 15, 5, 3, 48, ZstdStrategy.BinaryTreeUltra2)]
    [InlineData(13, 16_384, 14, 15, 14, 5, 3, 32, ZstdStrategy.BinaryTreeUltra)]
    [InlineData(13, 262_144, 18, 18, 19, 4, 4, 16, ZstdStrategy.BinaryTreeOptimal)]
    [InlineData(4, 262_144, 18, 16, 17, 3, 5, 2, ZstdStrategy.Greedy)]
    [InlineData(4, 131_072, 17, 17, 17, 2, 4, 0, ZstdStrategy.DoubleFast)]
    [InlineData(4, 4097, 13, 13, 14, 4, 4, 2, ZstdStrategy.Greedy)]
    [InlineData(9, 16_384, 14, 15, 14, 5, 4, 8, ZstdStrategy.BinaryTreeLazy2)]
    [InlineData(7, 131_072, 17, 16, 17, 3, 4, 8, ZstdStrategy.Lazy2)]
    [InlineData(12, 262_144, 18, 19, 19, 7, 4, 12, ZstdStrategy.BinaryTreeLazy2)]
    public void LevelTablesFollowTheReferenceForEachSizeClass(int level, long size, int windowLog, int chainLog, int hashLog, int searchLog, int minMatch, int targetLength, ZstdStrategy strategy)
    {
        // Values read off `zstd --show-default-cparams` for sources of these sizes (the window shrinks to the source for small ones).
        ZstdCompressionParameters p = ZstdCompressionParameters.Resolve(level, size, 0);
        Assert.Equal((windowLog, chainLog, hashLog, searchLog, minMatch, targetLength, strategy), (p.WindowLog, p.ChainLog, p.HashLog, p.SearchLog, p.MinMatch, p.TargetLength, p.Strategy));
    }

    [Fact]
    public void LevelsAboveTheImplementedRangeAreRejectedClearly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdEncoder(new ZstdCompressionOptions { Level = 23 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdEncoder(new ZstdCompressionOptions { Level = -131073 }));
    }

    [Fact]
    public void DestinationThatIsTooSmallIsReportedNotOverrun()
    {
        byte[] data = RandomBytes(5000, 13);
        Assert.False(Zstd.Zstd.TryCompress(data, new byte[100], out int written));
        Assert.Equal(0, written);
        Assert.True(Zstd.Zstd.TryCompress(data, new byte[ZstdEncoder.GetMaxCompressedLength(data.Length)], out written));
        Assert.True(written > 5000);
    }

    [Fact]
    public void EmptyAndTinyFramesMatchTheFormatsShortestForms()
    {
        Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x00, 0x01, 0x00, 0x00 }, Zstd.Zstd.Compress([]));
        byte[] one = Zstd.Zstd.Compress([7]);
        Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x01, 0x09, 0x00, 0x00, 0x07 }, one);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void StreamingInAnyPiecesMakesTheSameBytesAsOneShot(int level)
    {
        foreach ((string name, byte[] data) in Inputs())
        {
            var options = new ZstdCompressionOptions { Level = level, ContentSize = data.Length, Checksum = level % 2 == 0 };
            byte[] expected = Zstd.Zstd.Compress(data, options);
            foreach (int piece in new[] { 1, 997, 65536, 200000 })
            {
                if (piece == 1 && data.Length > 3000)
                {
                    continue;
                }

                Assert.True(expected.AsSpan().SequenceEqual(StreamCompress(data, options, piece, 70001)), $"{name} piece {piece} level {level}");
            }
        }
    }

    [Fact]
    public void StreamingWithSmallOutputBuffersDeliversEverything()
    {
        byte[] data = Text(400000, 14);
        var options = new ZstdCompressionOptions { Level = 1, ContentSize = data.Length };
        byte[] expected = Zstd.Zstd.Compress(data, options);
        Assert.True(expected.AsSpan().SequenceEqual(StreamCompress(data, options, 12345, 7)));
    }

    [Fact]
    public void StreamingWithoutAKnownSizeStillRoundTrips()
    {
        foreach ((string name, byte[] data) in Inputs())
        {
            byte[] frame = StreamCompress(data, new ZstdCompressionOptions { Level = 3, Checksum = true }, 40000, 33333);
            Assert.True(ZstdDecoder.TryReadFrameHeader(frame, out ZstdFrameHeader header));
            Assert.False(header.HasContentSize);
            Assert.True(Zstd.Zstd.Decompress(frame).AsSpan().SequenceEqual(data), name);
        }
    }

    [Fact]
    public void ALongStreamSlidesItsWindow()
    {
        // More input than the window plus buffer, with data repeated at distances inside and outside the window.
        byte[] unit = Text(300000, 15);
        byte[] data = new byte[unit.Length * 8];
        for (int i = 0; i < 8; i++)
        {
            unit.CopyTo(data, i * unit.Length);
        }

        foreach (int level in new[] { -3, 1, 3, 5, 6, 8, 12, 15 })
        {
            byte[] frame = StreamCompress(data, new ZstdCompressionOptions { Level = level }, 100000, 100000);
            Assert.True(Zstd.Zstd.Decompress(frame).AsSpan().SequenceEqual(data), $"level {level}");
        }
    }

    [Fact]
    public void FlushMakesTheOutputSoFarDecodable()
    {
        byte[] data = Text(100000, 16);
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = 3 });
        byte[] output = new byte[300000];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data.AsSpan(0, 50000), output, out int c1, out int w1, false));
        Assert.Equal(50000, c1);
        Assert.Equal(OperationStatus.Done, encoder.Flush(output.AsSpan(w1), out int w2));
        var decoder = new ZstdDecoder();
        OperationStatus status = decoder.Decompress(output.AsSpan(0, w1 + w2), new byte[60000], out _, out int produced);
        Assert.Equal(50000, produced);
        Assert.NotEqual(OperationStatus.InvalidData, status);
    }

    [Fact]
    public void WrongPledgedSizeThrows()
    {
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { ContentSize = 10 });
        byte[] output = new byte[1000];
        Assert.Throws<InvalidOperationException>(() => encoder.Compress(new byte[11], output, out _, out _, true));
    }

    [Fact]
    public void ABlockOfOneByteValueBecomesAnRleBlock()
    {
        byte[] frame = Zstd.Zstd.Compress(new byte[100000], new ZstdCompressionOptions { Level = 1 });
        Assert.True(frame.Length < 20, frame.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IncompressibleDataCostsOnlyBlockHeaders()
    {
        byte[] data = RandomBytes(300000, 17);
        foreach (int level in new[] { -7, 1, 4 })
        {
            byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level });
            Assert.True(frame.Length <= data.Length + 30, $"level {level}: {frame.Length}");
        }
    }

    [Fact]
    public void SkippableFramesBetweenFramesAreSkippedByTheDecoder()
    {
        byte[] data = Text(5000, 18);
        byte[] frame = Zstd.Zstd.Compress(data);
        byte[] skip = new byte[8 + 5];
        Assert.Equal(13, ZstdFrameWriter.WriteSkippableFrame(skip, 7, [1, 2, 3, 4, 5]));
        byte[] joined = [.. skip, .. frame, .. skip, .. frame];
        Assert.Equal([.. data, .. data], Zstd.Zstd.Decompress(joined));
    }

    [Fact]
    public void EveryLevelRoundTripsWithAFormattedDictionary()
    {
        byte[] dictionaryBytes = File.ReadAllBytes(ZstdTestData.Path("dict16.zdict"));
        var compressionDictionary = ZstdCompressionDictionary.Parse(dictionaryBytes);
        Assert.True(compressionDictionary.HasEntropyTables);
        var decompression = new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(dictionaryBytes) };
        foreach (int level in new[] { -5, -1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })
        {
            var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = level, Dictionary = compressionDictionary, Checksum = true });
            foreach ((string name, byte[] data) in Inputs())
            {
                byte[] frame = new byte[ZstdEncoder.GetMaxCompressedLength(data.Length)];
                int n = encoder.CompressFrame(data, frame);
                Assert.True(ZstdDecoder.TryReadFrameHeader(frame, out ZstdFrameHeader header));
                Assert.Equal(compressionDictionary.Id, header.DictionaryId);
                byte[] back = Zstd.Zstd.Decompress(frame.AsSpan(0, n), decompression);
                Assert.True(back.AsSpan().SequenceEqual(data), $"level {level}, input {name}");
            }
        }
    }

    [Fact]
    public void ARawContentDictionaryHelpsAndRoundTrips()
    {
        byte[] content = Text(20000, 19);
        var dictionary = ZstdCompressionDictionary.FromContent(content, 77);
        byte[] payload = Text(3000, 19);
        byte[] without = Zstd.Zstd.Compress(payload, new ZstdCompressionOptions { Level = 3 });
        byte[] with = Zstd.Zstd.Compress(payload, new ZstdCompressionOptions { Level = 3, Dictionary = dictionary });
        Assert.True(with.Length < without.Length / 2, $"{with.Length} vs {without.Length}");
        byte[] back = Zstd.Zstd.Decompress(with, new ZstdDecompressionOptions { Dictionary = ZstdDictionary.FromContent(content, 77) });
        Assert.Equal(payload, back);
    }

    [Fact]
    public void ADictionaryDoesNotChangeWhatTheFrameDecodesToWhenTheStreamIsLong()
    {
        byte[] content = Text(30000, 20);
        var dictionary = ZstdCompressionDictionary.FromContent(content, 5);
        byte[] data = Text(900000, 20);
        foreach (int level in new[] { 1, 3 })
        {
            var options = new ZstdCompressionOptions { Level = level, Dictionary = dictionary, ContentSize = data.Length };
            byte[] frame = StreamCompress(data, options, 77777, 55555);
            byte[] back = Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { Dictionary = ZstdDictionary.FromContent(content, 5) });
            Assert.True(back.AsSpan().SequenceEqual(data), $"level {level}");
        }
    }

    [Fact]
    public void ReusedEncoderWithADictionaryAllocatesNothingPerFrame()
    {
        var dictionary = ZstdCompressionDictionary.Parse(File.ReadAllBytes(ZstdTestData.Path("dict16.zdict")));
        byte[] payload = Text(200, 21);
        foreach (int level in new[] { -1, 1, 3 })
        {
            var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = level, Dictionary = dictionary });
            byte[] output = new byte[ZstdEncoder.GetMaxCompressedLength(payload.Length)];

            long Batch()
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++)
                {
                    encoder.CompressFrame(payload, output);
                }

                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            // The runtime allocates a little while it warms up; wait for clean batches, then require them.
            int clean = 0;
            var warmUp = System.Diagnostics.Stopwatch.StartNew();
            while (clean < 4 || warmUp.ElapsedMilliseconds < 1500)
            {
                long a = Batch();
                clean = a == 0 ? clean + 1 : 0;
                Thread.Sleep(30);
                Assert.True(warmUp.ElapsedMilliseconds < 30_000, $"allocations never settled at level {level}: {a} bytes per 1000 frames");
            }

            var per = new List<long>();
            for (int i = 0; i < 8; i++)
            {
                per.Add(Batch());
            }

            Assert.True(per.Count(a => a == 0) >= 5 && per.Max() < 10_000, $"level {level}: " + string.Join(",", per));
        }
    }

    [Fact]
    public async Task ZstdStreamCompressesWhatIsWrittenAndDecompressesItBack()
    {
        byte[] data = Text(500000, 22);
        foreach (bool async in new[] { false, true })
        {
            var sink = new MemoryStream();
            ZstdStream compressor = new(sink, new ZstdCompressionOptions { Level = 2, Checksum = true }, leaveOpen: true);
            for (int pos = 0; pos < data.Length; pos += 33333)
            {
                int n = Math.Min(33333, data.Length - pos);
                if (async)
                {
                    await compressor.WriteAsync(data.AsMemory(pos, n), TestContext.Current.CancellationToken);
                }
                else
                {
                    compressor.Write(data, pos, n);
                }

                if (pos == 33333 * 3)
                {
                    compressor.Flush();
                }
            }

            if (async)
            {
                await compressor.DisposeAsync();
            }
            else
            {
                compressor.Dispose();
            }

            Assert.False(compressor.CanWrite);
            sink.Position = 0;
            using var decompressor = new ZstdStream(sink);
            var restored = new MemoryStream();
            await decompressor.CopyToAsync(restored, TestContext.Current.CancellationToken);
            Assert.True(restored.ToArray().AsSpan().SequenceEqual(data));
        }
    }

    [Fact]
    public void AnEmptyStreamWritesAnEmptyFrame()
    {
        var sink = new MemoryStream();
        using (var compressor = new ZstdStream(sink, new ZstdCompressionOptions(), leaveOpen: true))
        {
            Assert.Throws<NotSupportedException>(() => compressor.Read(new byte[1], 0, 1));
        }

        Assert.Empty(Zstd.Zstd.Decompress(sink.ToArray()));
    }

    internal static byte[] StreamCompress(byte[] data, ZstdCompressionOptions options, int inputPiece, int outputBuffer)
    {
        var encoder = new ZstdEncoder(options);
        var result = new MemoryStream();
        byte[] output = new byte[outputBuffer];
        int pos = 0;
        while (true)
        {
            int n = Math.Min(inputPiece, data.Length - pos);
            bool last = pos + n == data.Length;
            int taken = 0;
            while (true)
            {
                OperationStatus status = encoder.Compress(data.AsSpan(pos + taken, n - taken), output, out int consumed, out int written, last);
                taken += consumed;
                result.Write(output, 0, written);
                if (status == OperationStatus.Done)
                {
                    Assert.Equal(n, taken);
                    break;
                }
            }

            pos += n;
            if (last)
            {
                break;
            }
        }

        return result.ToArray();
    }
}
