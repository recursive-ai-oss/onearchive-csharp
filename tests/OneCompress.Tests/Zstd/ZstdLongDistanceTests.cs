using System.Buffers;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Long-distance matching and jobs: round trips, and the rule that threads, call splits and window sliding never change the bytes.</summary>
public class ZstdLongDistanceTests
{
    private static readonly ZstdDecompressionOptions Big = new() { MaxWindowSize = 1L << 30, MaxOutputSize = 1L << 31 };

    private static byte[] Decode(byte[] frame) => Zstd.Zstd.Decompress(frame, Big);

    // Text with far repeats: pieces of the same text appear again after other data.
    private static byte[] FarRepeats(int total, int seed)
    {
        byte[] a = ZstdEncoderTests.RandomBytes(300_000, seed);
        byte[] b = ZstdEncoderTests.Text(700_000, seed + 1);
        var data = new List<byte>(total + 1_000_000);
        var random = new Random(seed);
        while (data.Count < total)
        {
            data.AddRange(random.Next(3) == 0 ? a : b);
            data.AddRange(ZstdEncoderTests.RandomBytes(random.Next(50_000, 400_000), random.Next()));
        }

        return [.. data.Take(total)];
    }

    private static byte[] Stream(byte[] data, ZstdCompressionOptions options, int piece)
    {
        var encoder = new ZstdEncoder(options);
        var output = new List<byte>();
        byte[] buffer = new byte[100_000];
        int position = 0;
        do
        {
            int length = Math.Min(piece, data.Length - position);
            bool final = position + length == data.Length;
            int consumed = 0;
            OperationStatus status;
            do
            {
                status = encoder.Compress(data.AsSpan(position + consumed, length - consumed), buffer, out int taken, out int written, final);
                consumed += taken;
                output.AddRange(buffer.AsSpan(0, written).ToArray());
            }
            while (status == OperationStatus.DestinationTooSmall || consumed < length);
            position += length;
        }
        while (position < data.Length);
        return [.. output];
    }

    [Fact]
    public void LongMatchesReachFarBeyondTheLevelsWindow()
    {
        byte[] repeated = ZstdEncoderTests.RandomBytes(600_000, 1);
        byte[] filler = ZstdEncoderTests.RandomBytes(5_000_000, 2);
        byte[] data = [.. repeated, .. filler, .. repeated];
        byte[] without = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 1, WindowLog = 24 });
        byte[] with = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 1, WindowLog = 24, LongDistanceMatching = true });
        Assert.True(with.Length < without.Length - 500_000, $"{with.Length} vs {without.Length}");
        Assert.Equal(data, Decode(with));
        Assert.Equal(data, Decode(without));
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void LongDistanceRoundTripsAtEveryLevel(int level)
    {
        byte[] data = FarRepeats(4_000_000, level + 10);
        var options = new ZstdCompressionOptions { Level = level, LongDistanceMatching = true, WindowLog = 23, Checksum = true };
        byte[] frame = Zstd.Zstd.Compress(data, options);
        Assert.Equal(data, Decode(frame));
    }

    [Fact]
    public void LongDistanceDefaultsToWindow27AndShrinksForSmallInput()
    {
        byte[] data = FarRepeats(2_000_000, 5);
        byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 3, LongDistanceMatching = true });
        Assert.Equal(data, Decode(frame));
        Assert.True(Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { MaxWindowSize = 1L << 21 }).Length == data.Length);
    }

    [Fact]
    public void WindowLog31IsWrittenAndThisDecoderRefusesItByItsLimit()
    {
        // The header states 2^31, which the tool decodes with --long=31; this decoder keeps its windows below 2^30 bytes (the memory of two windows must fit an array).
        byte[] data = FarRepeats(1_500_000, 6);
        byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 1, LongDistanceMatching = true, WindowLog = 31, WriteContentSize = false });
        Assert.Equal((31 - 10) << 3, frame[5]);
        Assert.Throws<CompressionLimitException>(() => Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { MaxWindowSize = 1L << 31 }));
        byte[] frame29 = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 1, LongDistanceMatching = true, WindowLog = 29, WriteContentSize = false });
        Assert.Equal(data, Decode(frame29));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void JobsGiveTheSameBytesOnAnyNumberOfThreads(int level, bool longDistance)
    {
        byte[] data = [.. FarRepeats(3_500_000, 20 + level), .. ZstdEncoderTests.Text(1_000_000, 8)];
        ZstdCompressionOptions Make(int workers) => new()
        {
            Level = level,
            LongDistanceMatching = longDistance,
            WindowLog = 22,
            JobSize = 512 * 1024,
            Workers = workers,
            Checksum = true,
        };

        byte[] one = Zstd.Zstd.Compress(data, Make(1));
        Assert.Equal(data, Decode(one));
        foreach (int workers in new[] { 2, 3, 8, 0 })
        {
            Assert.True(one.AsSpan().SequenceEqual(Zstd.Zstd.Compress(data, Make(workers))), $"workers {workers}");
        }
    }

    [Fact]
    public void AutoJobSizeAndDictionaryAndNoContentSize()
    {
        byte[] data = ZstdEncoderTests.Text(9_000_000, 3);
        var dictionary = ZstdCompressionDictionary.FromContent(data.AsSpan(0, 30_000), 77);
        var options = new ZstdCompressionOptions { Level = 3, JobSize = ZstdCompressionOptions.AutoJobSize, Workers = 4, Dictionary = dictionary, WriteContentSize = false };
        byte[] frame = Zstd.Zstd.Compress(data, options);
        var decode = new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(dictionary.Content.Span), MaxWindowSize = 1L << 26 };
        Assert.Equal(data, Zstd.Zstd.Decompress(frame, decode));
        Assert.True(frame.AsSpan().SequenceEqual(Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 3, JobSize = ZstdCompressionOptions.AutoJobSize, Workers = 1, Dictionary = dictionary, WriteContentSize = false })));
    }

    [Fact]
    public void OneJobIsTheSameAsNoJobs()
    {
        byte[] data = ZstdEncoderTests.Text(900_000, 4);
        foreach (bool longDistance in new[] { false, true })
        {
            byte[] plain = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 3, LongDistanceMatching = longDistance });
            byte[] jobs = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 3, LongDistanceMatching = longDistance, JobSize = 1 << 20, Workers = 4 });
            Assert.True(plain.AsSpan().SequenceEqual(jobs));
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 600_000)]
    [InlineData(true, 600_000)]
    public void StreamingGivesTheBytesOfOneShot(bool longDistance, int jobSize)
    {
        // A window of 2^20 on 6 MB of data makes the stream's buffer slide several times.
        byte[] data = FarRepeats(6_000_000, 31);
        var options = new ZstdCompressionOptions
        {
            Level = 3,
            WindowLog = 20,
            LongDistanceMatching = longDistance,
            JobSize = jobSize,
            Workers = 3,
            ContentSize = data.Length,
            Checksum = true,
        };
        byte[] oneShot = Zstd.Zstd.Compress(data, options);
        Assert.Equal(data, Decode(oneShot));
        foreach (int piece in new[] { 777_777, 3_000_001, 100_003 })
        {
            Assert.True(oneShot.AsSpan().SequenceEqual(Stream(data, options, piece)), $"piece {piece}");
        }
    }

    [Fact]
    public void FlushInAJobStreamDecodes()
    {
        byte[] data = FarRepeats(2_500_000, 41);
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = 1, JobSize = 256 * 1024, Workers = 2, LongDistanceMatching = true, WindowLog = 22 });
        var output = new List<byte>();
        byte[] buffer = new byte[1 << 20];
        for (int position = 0; position < data.Length; position += 700_000)
        {
            int length = Math.Min(700_000, data.Length - position);
            bool final = position + length == data.Length;
            int consumed = 0;
            while (consumed < length)
            {
                _ = encoder.Compress(data.AsSpan(position + consumed, length - consumed), buffer, out int taken, out int written, final);
                consumed += taken;
                output.AddRange(buffer.AsSpan(0, written).ToArray());
            }

            if (!final)
            {
                Assert.Equal(OperationStatus.Done, encoder.Flush(buffer, out int flushed));
                output.AddRange(buffer.AsSpan(0, flushed).ToArray());
            }
        }

        Assert.Equal(OperationStatus.Done, encoder.Compress([], buffer, out _, out int tail, true));
        output.AddRange(buffer.AsSpan(0, tail).ToArray());
        Assert.Equal(data, Decode([.. output]));
    }

    [Fact]
    public void RunsAndEmptyAndTinyInputsWithJobs()
    {
        foreach (int length in new[] { 0, 1, 7, 1000, 300_000, 1_100_000 })
        {
            byte[] data = new byte[length];
            if (length > 10)
            {
                data.AsSpan(5, length - 10).Fill(9);
            }

            foreach (bool longDistance in new[] { false, true })
            {
                byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = 1, JobSize = 128 * 1024, Workers = 4, LongDistanceMatching = longDistance });
                Assert.Equal(data, Decode(frame));
            }
        }
    }

    [Fact]
    public void BadParallelOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdEncoder(new ZstdCompressionOptions { Workers = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdEncoder(new ZstdCompressionOptions { JobSize = -2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdEncoder(new ZstdCompressionOptions { LongDistanceMinMatch = 5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdEncoder(new ZstdCompressionOptions { WindowLog = 32 }));
    }
}
