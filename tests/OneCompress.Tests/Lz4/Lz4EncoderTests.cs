using System.Buffers;
using RecursiveAI.OneCompress.Lz4;
using Lz4Api = RecursiveAI.OneCompress.Lz4.Lz4;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Round trips, determinism and argument checks for the LZ4 encoders (blocks, frames, encoder, stream).</summary>
public class Lz4EncoderTests
{
    // Inputs that exercise the parsers: empty, tiny, random, repetitive, text-like, runs, and data with long-distance repeats.
    internal static IEnumerable<(string Name, byte[] Data)> Inputs()
    {
        yield return ("empty", []);
        for (int n = 1; n <= 40; n += n < 20 ? 1 : 7)
        {
            yield return ("text" + n, Text(n, 1));
        }

        yield return ("random-100k", Random(100_000, 2));
        yield return ("zeros-300k", new byte[300_000]);
        yield return ("text-1m", Text(1_000_000, 3));
        yield return ("text-200k", Text(200_000, 4));
        yield return ("runs", Runs(200_000, 5));
        yield return ("mixed", Mixed(500_000, 6));
        yield return ("period3", Enumerable.Range(0, 70_000).Select(i => (byte)"abc"[i % 3]).ToArray());
    }

    internal static byte[] Random(int n, ulong seed)
    {
        byte[] b = new byte[n];
        ulong s = seed * 0x9E3779B97F4A7C15UL + 1;
        for (int i = 0; i < n; i++)
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            b[i] = (byte)(s >> 24);
        }

        return b;
    }

    internal static byte[] Text(int n, ulong seed)
    {
        string[] words = ["tick", "entity", "position", "health", "state", "item", "count", "chunk", "the", "of", "and", "walking", "idle", "{\"id\":", "\",\"v\":", "[0,0,1]"];
        byte[] r = Random(n + 64, seed);
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (sb.Length < n)
        {
            sb.Append(words[r[i++ % r.Length] % words.Length]).Append(r[i++ % r.Length] % 5 == 0 ? (char)('0' + (r[i % r.Length] % 10)) : ' ');
        }

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, n));
    }

    internal static byte[] Runs(int n, ulong seed)
    {
        byte[] r = Random(n, seed);
        byte[] b = new byte[n];
        int i = 0;
        while (i < n)
        {
            int len = 1 + (r[i] * 3);
            byte v = r[(i + 1) % n];
            for (int k = 0; k < len && i < n; k++)
            {
                b[i++] = v;
            }
        }

        return b;
    }

    internal static byte[] Mixed(int n, ulong seed)
    {
        byte[] t = Text(n / 2, seed);
        byte[] r = Random(n / 4, seed + 1);
        byte[] b = new byte[n];
        t.CopyTo(b, 0);
        r.CopyTo(b, t.Length);
        // The rest repeats earlier parts at distances beyond and within 64 KiB.
        for (int i = t.Length + r.Length; i < n; i++)
        {
            b[i] = b[i - (i % 2 == 0 ? 70_000 : 3000) % i];
        }

        return b;
    }

    internal static Lz4Dictionary MakeDictionary(int size = 8000, uint id = 0) => new(Text(size, 99), id);

    public static TheoryData<int> Levels() => new(-65536, -100, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);

    [Theory]
    [MemberData(nameof(Levels))]
    public void BlockRoundTripsAtEveryLevel(int level)
    {
        var options = new Lz4CompressionOptions { Level = level };
        foreach ((string name, byte[] data) in Inputs())
        {
            // The slowest levels get the smaller inputs; behavior on the large ones is covered by the others.
            if (level >= 10 && data.Length > 300_000)
            {
                continue;
            }

            byte[] block = Lz4Block.Compress(data, options);
            Assert.True(block.Length <= Lz4Block.GetMaxCompressedLength(data.Length), name);
            byte[] back = Lz4Block.Decompress(block);
            Assert.True(data.AsSpan().SequenceEqual(back), $"{name} level {level}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(12)]
    public void BlockWithDictionaryRoundTrips(int level)
    {
        Lz4Dictionary dictionary = MakeDictionary();
        var options = new Lz4CompressionOptions { Level = level, Dictionary = dictionary };
        var decode = new Lz4BlockOptions { Dictionary = dictionary };
        foreach ((string name, byte[] data) in Inputs())
        {
            if (data.Length > 300_000)
            {
                continue;
            }

            byte[] block = Lz4Block.Compress(data, options);
            byte[] back;
            try
            {
                back = Lz4Block.Decompress(block, decode);
            }
            catch (CompressionException e)
            {
                throw new InvalidOperationException($"{name} level {level}: {e.Message}", e);
            }

            Assert.True(data.AsSpan().SequenceEqual(back), $"{name} level {level}");
        }

        // A dictionary of similar text actually helps.
        byte[] sample = Text(300, 77);
        int with = Lz4Block.Compress(sample, options).Length;
        int without = Lz4Block.Compress(sample, new Lz4CompressionOptions { Level = level }).Length;
        Assert.True(with < without, $"dictionary did not help: {with} vs {without}");
    }

    [Fact]
    public void EmptyBlockIsTheSingleZeroByte()
    {
        Assert.Equal(new byte[] { 0 }, Lz4Block.Compress([]));
        Assert.Equal(new byte[] { 0 }, Lz4Block.Compress([], new Lz4CompressionOptions { Level = 9 }));
    }

    [Fact]
    public void TryCompressReportsASmallDestination()
    {
        byte[] data = Text(5000, 1);
        byte[] block = Lz4Block.Compress(data);
        byte[] exact = new byte[block.Length];
        Assert.True(Lz4Block.TryCompress(data, exact, out int n));
        Assert.Equal(block.Length, n);
        Assert.Equal(block, exact);
        Assert.False(Lz4Block.TryCompress(data, new byte[block.Length - 1], out _));
        Assert.Throws<ArgumentException>(() => Lz4Block.Compress(data, new byte[block.Length - 1]));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(6, true)]
    [InlineData(9, false)]
    [InlineData(12, true)]
    public void ReusedCompressorWithADictionaryMatchesAFreshOne(int level, bool largeDictionary)
    {
        // Small blocks keep the dictionary's index in place between calls; a block must not see traces of earlier ones.
        Lz4Dictionary dictionary = largeDictionary ? new Lz4Dictionary(Text(80_000, 9)) : MakeDictionary();
        var options = new Lz4CompressionOptions { Level = level, Dictionary = dictionary };
        Lz4BlockCompressor reused = Lz4BlockCompressor.Create(level);
        reused.SetDictionary(dictionary);
        byte[] scratch = new byte[Lz4BlockWriter.GetMaxCompressedLength(200_000)];
        var payloads = new List<byte[]>();
        foreach (int size in new[] { 1, 13, 40, 300, 5000, 20_000, 65_535, 65_536, 65_537, 150_000, 700, 65_536 })
        {
            payloads.Add(Text(size, (ulong)size));
            payloads.Add(size >= 1000 ? Mixed(size, (ulong)size + 1) : Random(size, (ulong)size + 1));
        }

        foreach (byte[] data in payloads.Concat(payloads))
        {
            int n = reused.Compress(data, 0, scratch);
            byte[] fresh = Lz4Block.Compress(data, options);
            Assert.True(scratch.AsSpan(0, n).SequenceEqual(fresh), $"{data.Length} bytes, level {level}");
            Assert.True(Lz4Block.Decompress(fresh, new Lz4BlockOptions { Dictionary = dictionary }).AsSpan().SequenceEqual(data));
        }

        // A block that continues another one (history) after parked blocks, and a switch to no dictionary and back.
        byte[] first = Text(3000, 31);
        byte[] second = Text(3000, 32);
        byte[] joined = [.. first, .. second];
        int a = reused.Compress(first, 0, scratch);
        byte[] freshFirst = Lz4Block.Compress(first, options);
        Assert.True(scratch.AsSpan(0, a).SequenceEqual(freshFirst));
        Lz4BlockCompressor linkedFresh = Lz4BlockCompressor.Create(level);
        linkedFresh.SetDictionary(dictionary);
        linkedFresh.Compress(first, 0, scratch);
        byte[] linkedExpected = new byte[scratch.Length];
        int expectedLength = linkedFresh.Compress(joined, first.Length, linkedExpected);
        int b = reused.Compress(joined, first.Length, scratch);
        Assert.True(scratch.AsSpan(0, b).SequenceEqual(linkedExpected.AsSpan(0, expectedLength)), "linked block after a dictionary block");

        reused.SetDictionary(null);
        int c = reused.Compress(first, 0, scratch);
        Assert.True(scratch.AsSpan(0, c).SequenceEqual(Lz4Block.Compress(first, new Lz4CompressionOptions { Level = level })));
        reused.SetDictionary(dictionary);
        int d = reused.Compress(first, 0, scratch);
        Assert.True(scratch.AsSpan(0, d).SequenceEqual(freshFirst));
    }

    [Fact]
    public void ReusedCompressorMatchesAFreshOne()
    {
        // Output depends on the input and the level alone, however the instance was used before.
        foreach (int level in new[] { 1, -4, 3, 9, 12 })
        {
            Lz4BlockCompressor reused = Lz4BlockCompressor.Create(level);
            byte[] scratch = new byte[Lz4BlockWriter.GetMaxCompressedLength(400_000)];
            foreach ((string name, byte[] data) in Inputs().Where(i => i.Data.Length <= 200_000))
            {
                int n = reused.Compress(data, 0, scratch);
                byte[] fresh = Lz4Block.Compress(data, new Lz4CompressionOptions { Level = level });
                Assert.True(scratch.AsSpan(0, n).SequenceEqual(fresh), $"{name} level {level}");
            }
        }
    }

    [Theory]
    [InlineData(1, 65536)]
    [InlineData(-5, 65536)]
    [InlineData(4, 65536)]
    [InlineData(11, 65536)]
    [InlineData(1, 3000)]
    [InlineData(-5, 100)]
    [InlineData(4, 3000)]
    [InlineData(9, 100)]
    [InlineData(11, 3000)]
    [InlineData(2, 65536)]
    [InlineData(2, 3000)]
    [InlineData(6, 65536)]
    [InlineData(6, 3000)]
    [InlineData(8, 100)]
    public void HistoryAndRebaseKeepBlocksDecodable(int level, int keep)
    {
        // Linked blocks by hand: each block sees the bytes before it; the window slides by dropping its front.
        Lz4BlockCompressor compressor = Lz4BlockCompressor.Create(level);
        byte[] data = Text(400_000, 12);
        byte[] window = new byte[200_000];
        byte[] scratch = new byte[Lz4BlockWriter.GetMaxCompressedLength(70_000)];
        var output = new List<byte>();
        int have = 0;
        int pos = 0;
        while (pos < data.Length)
        {
            int take = Math.Min(50_000, data.Length - pos);
            if (have + take > window.Length)
            {
                Buffer.BlockCopy(window, have - keep, window, 0, keep);
                compressor.Rebase(have - keep);
                have = keep;
            }

            data.AsSpan(pos, take).CopyTo(window.AsSpan(have));
            int history = have;
            have += take;
            pos += take;
            int n = compressor.Compress(window.AsSpan(0, have), history, scratch);

            // Decode against the same history.
            byte[] buffer = new byte[have];
            window.AsSpan(0, history).CopyTo(buffer);
            Assert.True(Lz4BlockDecoder.TryDecode(scratch.AsSpan(0, n), buffer, history, out int written));
            Assert.Equal(take, written);
            output.AddRange(buffer.AsSpan(history, written).ToArray());
        }

        Assert.True(data.AsSpan().SequenceEqual(output.ToArray()));
    }

    public static IEnumerable<object[]> FrameConfigurations()
    {
        foreach (int level in new[] { 1, -3, 3, 10 })
        {
            foreach (bool independent in new[] { true, false })
            {
                foreach (Lz4BlockSize size in new[] { Lz4BlockSize.Size64KiB, Lz4BlockSize.Size256KiB, Lz4BlockSize.Size4MiB })
                {
                    yield return [level, independent, size, level == 3, !independent];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(FrameConfigurations))]
    public void FrameRoundTripsWithEveryOption(int level, bool independent, Lz4BlockSize size, bool blockChecksum, bool dictionary)
    {
        var options = new Lz4CompressionOptions
        {
            Level = level,
            BlocksIndependent = independent,
            BlockSize = size,
            BlockChecksum = blockChecksum,
            ContentChecksum = level != -3,
            Dictionary = dictionary ? MakeDictionary(id: 77) : null,
        };
        var decode = new Lz4DecompressionOptions { Dictionary = options.Dictionary };
        foreach ((string name, byte[] data) in Inputs().Where(i => i.Data.Length <= (level == 10 ? 300_000 : 2_000_000)))
        {
            byte[] frame = Lz4Api.Compress(data, options);
            Assert.True(frame.Length <= Lz4Encoder.GetMaxCompressedLength(data.Length, options), name);
            Assert.True(Lz4Decoder.TryGetDecompressedSize(frame, out long declared) && declared == data.Length, name);
            byte[] back = Lz4Api.Decompress(frame, decode);
            Assert.True(data.AsSpan().SequenceEqual(back), $"{name} {level} independent={independent} {size}");
            Assert.True(Lz4FrameHeader.TryParse(frame, out Lz4FrameHeader header) == OperationStatus.Done);
            Assert.Equal(independent, header.BlocksIndependent);
            Assert.Equal(blockChecksum, header.HasBlockChecksum);
            Assert.Equal(options.ContentChecksum, header.HasContentChecksum);
            Assert.Equal(dictionary ? 77u : (uint?)null, header.DictionaryId);
        }
    }

    [Fact]
    public void IncompressibleBlocksAreStored()
    {
        byte[] data = Random(300_000, 8);
        byte[] frame = Lz4Api.Compress(data, new Lz4CompressionOptions { BlockSize = Lz4BlockSize.Size64KiB });
        Assert.True(frame.Length <= Lz4Encoder.GetMaxCompressedLength(data.Length, new Lz4CompressionOptions { BlockSize = Lz4BlockSize.Size64KiB }));
        Assert.True(frame.Length > data.Length);
        Assert.True(frame.Length < data.Length + 100);
        Assert.True(data.AsSpan().SequenceEqual(Lz4Api.Decompress(frame)));
    }

    [Fact]
    public void ContentSizeIsWrittenOnRequestOnly()
    {
        byte[] data = Text(1000, 3);
        Assert.True(Lz4Decoder.TryGetDecompressedSize(Lz4Api.Compress(data), out long size) && size == 1000);
        Assert.False(Lz4Decoder.TryGetDecompressedSize(Lz4Api.Compress(data, new Lz4CompressionOptions { IncludeContentSize = false }), out _));

        // The streaming encoder checks a size it was told.
        var encoder = new Lz4Encoder(new Lz4CompressionOptions { ContentSize = 1000 });
        byte[] output = new byte[5000];
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, output, out _, out int written, isFinalBlock: true));
        Assert.True(Lz4Decoder.TryGetDecompressedSize(output.AsSpan(0, written), out size) && size == 1000);
        var wrong = new Lz4Encoder(new Lz4CompressionOptions { ContentSize = 999 });
        Assert.Throws<InvalidOperationException>(() => wrong.Compress(data, output, out _, out _, isFinalBlock: true));
        var tooLittle = new Lz4Encoder(new Lz4CompressionOptions { ContentSize = 2000 });
        Assert.Throws<InvalidOperationException>(() => tooLittle.Compress(data, output, out _, out _, isFinalBlock: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FrameBytesDoNotDependOnHowInputAndOutputAreSplit(bool independent)
    {
        byte[] data = Mixed(700_000, 9);
        var options = new Lz4CompressionOptions { Level = 3, BlockSize = Lz4BlockSize.Size64KiB, BlocksIndependent = independent, BlockChecksum = true, IncludeContentSize = false };
        byte[] whole = Lz4Api.Compress(data, options);
        foreach ((int inPiece, int outPiece) in new[] { (1, 4096), (7, 5000), (1000, 4096), (65536, 100_000), (65537, 4097), (300_000, 9000) })
        {
            var encoder = new Lz4Encoder(options);
            var result = new MemoryStream();
            byte[] buffer = new byte[outPiece];
            int pos = 0;
            while (true)
            {
                int take = Math.Min(inPiece, data.Length - pos);
                bool last = pos + take == data.Length;
                OperationStatus status = encoder.Compress(data.AsSpan(pos, take), buffer, out int c, out int w, last);
                result.Write(buffer, 0, w);
                pos += c;
                if (status == OperationStatus.Done && pos == data.Length)
                {
                    break;
                }
            }

            Assert.True(whole.AsSpan().SequenceEqual(result.ToArray()), $"in {inPiece} out {outPiece}");
        }
    }

    [Fact]
    public void FlushEndsTheBlockAndKeepsTheFrameOpen()
    {
        byte[] data = Text(30_000, 21);
        var encoder = new Lz4Encoder(new Lz4CompressionOptions { BlocksIndependent = false });
        byte[] buffer = new byte[100_000];
        var result = new MemoryStream();
        for (int part = 0; part < 3; part++)
        {
            Assert.Equal(OperationStatus.Done, encoder.Compress(data.AsSpan(part * 10_000, 10_000), buffer, out _, out int w, isFinalBlock: false));
            result.Write(buffer, 0, w);
            Assert.Equal(OperationStatus.Done, encoder.Flush(buffer, out w));
            result.Write(buffer, 0, w);

            // What was flushed decodes without the rest of the frame.
            var decoder = new Lz4Decoder();
            byte[] back = new byte[40_000];
            decoder.Decompress(result.ToArray(), back, out _, out int produced);
            Assert.Equal((part + 1) * 10_000, produced);
            Assert.True(data.AsSpan(0, produced).SequenceEqual(back.AsSpan(0, produced)));
        }

        Assert.Equal(OperationStatus.Done, encoder.Compress(default, buffer, out _, out int last, isFinalBlock: true));
        result.Write(buffer, 0, last);
        Assert.True(data.AsSpan().SequenceEqual(Lz4Api.Decompress(result.ToArray())));
    }

    [Fact]
    public void EncoderStartsNewFramesAfterAFinishedOne()
    {
        var encoder = new Lz4Encoder();
        byte[] buffer = new byte[10_000];
        var all = new MemoryStream();
        var expected = new MemoryStream();
        for (int i = 0; i < 5; i++)
        {
            byte[] data = Text(100 + (i * 500), (ulong)i);
            Assert.Equal(OperationStatus.Done, encoder.Compress(data, buffer, out _, out int w, isFinalBlock: true));
            all.Write(buffer, 0, w);
            expected.Write(data);
        }

        Assert.True(expected.ToArray().AsSpan().SequenceEqual(Lz4Api.Decompress(all.ToArray())));
    }

    [Fact]
    public void SmallDestinationsReportDestinationTooSmall()
    {
        byte[] data = Text(100_000, 5);
        var encoder = new Lz4Encoder(new Lz4CompressionOptions { BlockSize = Lz4BlockSize.Size64KiB });
        byte[] buffer = new byte[10];
        OperationStatus status = encoder.Compress(data, buffer, out int consumed, out int written, isFinalBlock: true);
        Assert.Equal(OperationStatus.DestinationTooSmall, status);
        Assert.Equal(10, written);
        Assert.True(consumed <= data.Length);
    }

    [Fact]
    public async Task StreamRoundTripsSyncAndAsync()
    {
        byte[] data = Mixed(900_000, 14);
        foreach (bool linked in new[] { false, true })
        {
            var options = new Lz4CompressionOptions { Level = 4, BlocksIndependent = !linked, BlockSize = Lz4BlockSize.Size256KiB };
            var sync = new MemoryStream();
            using (var stream = new Lz4Stream(sync, options, leaveOpen: true))
            {
                Assert.True(stream.CanWrite);
                Assert.False(stream.CanRead);
                for (int pos = 0; pos < data.Length; pos += 33_333)
                {
                    stream.Write(data, pos, Math.Min(33_333, data.Length - pos));
                    if (pos % 3 == 0)
                    {
                        stream.Flush();
                    }
                }
            }

            Assert.True(data.AsSpan().SequenceEqual(Lz4Api.Decompress(sync.ToArray())));

            var asyncOut = new MemoryStream();
            await using (var stream = new Lz4Stream(asyncOut, options, leaveOpen: true))
            {
                for (int pos = 0; pos < data.Length; pos += 50_000)
                {
                    await stream.WriteAsync(data.AsMemory(pos, Math.Min(50_000, data.Length - pos)), TestContext.Current.CancellationToken);
                }

                await stream.FlushAsync(TestContext.Current.CancellationToken);
            }

            Assert.True(data.AsSpan().SequenceEqual(Lz4Api.Decompress(asyncOut.ToArray())));
        }
    }

    [Fact]
    public void StreamClosesTheInnerStreamUnlessToldNotTo()
    {
        var inner = new MemoryStream();
        new Lz4Stream(inner, new Lz4CompressionOptions()).Dispose();
        Assert.False(inner.CanWrite);
        Assert.True(Lz4Api.Decompress(inner.ToArray()).Length == 0);

        var kept = new MemoryStream();
        new Lz4Stream(kept, new Lz4CompressionOptions(), leaveOpen: true).Dispose();
        Assert.True(kept.CanWrite);
        Assert.Throws<NotSupportedException>(() => new Lz4Stream(new MemoryStream(), new Lz4CompressionOptions()).Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => new Lz4Stream(new MemoryStream(Lz4Api.Compress([1, 2, 3]))).Write(new byte[1], 0, 1));
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Lz4Encoder(new Lz4CompressionOptions { Level = 13 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Lz4Encoder(new Lz4CompressionOptions { Level = -65537 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Lz4Encoder(new Lz4CompressionOptions { BlockSize = (Lz4BlockSize)3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4Block.Compress([1], new Lz4CompressionOptions { Level = 99 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4BlockCompressor.Create(13));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Lz4HighBlockCompressor(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Lz4FastBlockCompressor(0));
        Assert.Throws<ArgumentNullException>(() => new Lz4Stream(new MemoryStream(), (Lz4CompressionOptions)null!));
        Assert.Throws<ArgumentException>(() => new Lz4Stream(new MemoryStream(new byte[1], writable: false), new Lz4CompressionOptions()));
        Lz4BlockCompressor c = Lz4BlockCompressor.Create(1);
        Assert.Throws<ArgumentException>(() => c.Compress(new byte[100], 0, new byte[10]));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Compress(new byte[100], 101, new byte[200]));
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Rebase(1));
        Assert.Throws<ArgumentException>(() => Lz4BlockWriter.WriteSequence(new byte[3], new byte[1], 5, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4BlockWriter.WriteSequence(new byte[10], [], 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4BlockWriter.WriteSequence(new byte[10], [], 70000, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4BlockWriter.WriteSequence(new byte[10], [], 5, 3));
    }

    [Fact]
    public void BlockWriterMatchesTheFormat()
    {
        // 20 literals, a match of 300 bytes at offset 7, then 6 final literals.
        byte[] literals = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        byte[] seq = new byte[(int)Lz4BlockWriter.GetSequenceLength(20, 300)];
        int n = Lz4BlockWriter.WriteSequence(seq, literals, 7, 300);
        Assert.Equal(seq.Length, n);
        Assert.Equal(0xFF, seq[0]);
        Assert.Equal(5, seq[1]);                       // 20 literals = 15 + 5
        Assert.True(seq.AsSpan(2, 20).SequenceEqual(literals));
        Assert.Equal(new byte[] { 7, 0 }, seq.AsSpan(22, 2).ToArray());
        Assert.Equal(new byte[] { 255, 26 }, seq.AsSpan(24, 2).ToArray());   // 300 - 4 = 296 = 15 + 255 + 26
        byte[] last = new byte[7];
        Assert.Equal(7, Lz4BlockWriter.WriteLastLiterals(last, new byte[6]));
        Assert.Equal(0x60, last[0]);

        // The pieces form a block the decoder accepts.
        byte[] block = [.. seq, .. last];
        byte[] decoded = Lz4Block.Decompress(block);
        Assert.Equal(20 + 300 + 6, decoded.Length);
    }

    // Found by fuzzing: with a dictionary and linked blocks, a block compressed straight from the caller's memory (the first,
    // when the whole input is given at once) left the next block unable to see it, so one call and many small calls
    // produced different frames. The last 12 bytes repeat bytes from the first block, as a short match.
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(12)]
    public void LinkedBlocksWithDictionaryDoNotDependOnHowTheInputArrives(int level)
    {
        byte[] data = new byte[65_536 + 12];
        Random(65_536, 11).CopyTo(data, 0);
        data.AsSpan(3_000, 12).CopyTo(data.AsSpan(65_536));
        var dictionary = new Lz4Dictionary(Random(6_000, 12), 0x4242);
        Lz4CompressionOptions Options(int? size) => new()
        {
            Level = level,
            BlockSize = Lz4BlockSize.Size64KiB,
            BlocksIndependent = false,
            IncludeContentSize = size is null,
            ContentSize = size,
            Dictionary = dictionary,
        };

        byte[] whole = Lz4Api.Compress(data, Options(null));
        var encoder = new Lz4Encoder(Options(data.Length));
        var pieces = new MemoryStream();
        byte[] buffer = new byte[1 << 20];
        for (int pos = 0; pos < data.Length;)
        {
            int take = Math.Min(1_000, data.Length - pos);
            encoder.Compress(data.AsSpan(pos, take), buffer, out int consumed, out int written, pos + take == data.Length);
            pieces.Write(buffer, 0, written);
            pos += consumed;
        }

        Assert.Equal(whole, pieces.ToArray());
        Assert.Equal(data, Lz4Api.Decompress(whole, new Lz4DecompressionOptions { Dictionary = dictionary }));
    }
}
