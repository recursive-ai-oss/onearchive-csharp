using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// The row layouts and insert strides of the greedy, lazy and lazy2 finder: whatever the layout, every sequence it reports is a real
/// match, matches far into a large input are still found (positions are stored modulo 2^24 in the packed layout), and a window that
/// slides along gives what a window that does not slide gives.
/// </summary>
public class ZstdRowLayoutTests
{
    // A random block of 256 KiB repeated with one changed byte per 64 KiB: long matches at a distance inside every level's window,
    // over 40 MB so that the positions pass 2^24 and the packed rows have to sweep out stale entries.
    private static byte[] RepeatingData()
    {
        const int Period = 1 << 18;
        var random = new Random(83);
        byte[] data = new byte[40_000_000];
        random.NextBytes(data.AsSpan(0, Period));
        for (int i = Period; i < data.Length; i += Period)
        {
            Array.Copy(data, i - Period, data, i, Math.Min(Period, data.Length - i));
            for (int m = random.Next(65536); m < Period && i + m < data.Length; m += 65536)
            {
                data[i + m] ^= (byte)(1 + random.Next(255));
            }
        }

        return data;
    }

    [Theory]
    [InlineData(5, false, 1)]
    [InlineData(5, false, 4)]
    [InlineData(5, true, 1)]
    [InlineData(7, true, 4)]
    [InlineData(9, false, 4)]
    [InlineData(9, true, 4)]
    [InlineData(9, true, 1)]
    public void FarMatchesAreFoundAndEveryMatchIsReal(int level, bool packed, int stride)
    {
        byte[] data = RepeatingData();
        ZstdCompressionParameters parameters = ZstdCompressionParameters.Resolve(level, data.Length, 0);
        var finder = new ZstdLazyMatchFinder { PackedRows = packed, InsertStride = stride };
        finder.BeginFrame(parameters, null);
        var store = new ZstdSequenceStore();
        var reps = new ZstdRepeatOffsets { Offset1 = 1, Offset2 = 4, Offset3 = 8 };
        long matched = 0;
        for (int start = 0; start < data.Length; start += 1 << 17)
        {
            int end = Math.Min(data.Length, start + (1 << 17));
            store.Clear();
            finder.FindSequences(data, start, end, 0, reps, store);
            matched += CheckSequences(data, start, end, 0, store, ref reps);
        }

        Assert.True(matched > 0.9 * (data.Length - (1 << 18)), $"level {level} packed {packed} stride {stride}: only {matched} bytes matched");
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(9, true)]
    [InlineData(9, false)]
    public void ASlidingWindowFindsTheSameSequencesAsOneThatDoesNotSlide(int level, bool packed)
    {
        // The window is kept the way the streaming encoder keeps it: the history up to one byte more than the window, then the blocks
        // being gathered; when it is full, everything older is dropped, the finder is told, and positions count from the new start.
        byte[] data = RepeatingData().AsSpan(0, 24_000_000).ToArray();
        ZstdCompressionParameters parameters = ZstdCompressionParameters.Resolve(level, data.Length, 0);
        int windowSize = (int)parameters.WindowSize;
        int capacity = windowSize + 1 + (2 << 17) + 64;

        var plain = new ZstdLazyMatchFinder { PackedRows = packed };
        plain.BeginFrame(parameters, null);
        var sliding = new ZstdLazyMatchFinder { PackedRows = packed };
        sliding.BeginFrame(parameters, null);
        var plainStore = new ZstdSequenceStore();
        var slidingStore = new ZstdSequenceStore();
        var plainReps = new ZstdRepeatOffsets { Offset1 = 1, Offset2 = 4, Offset3 = 8 };
        var slidingReps = plainReps;
        byte[] buffer = new byte[capacity];
        int fill = 0;
        long windowStart = 0;
        for (int start = 0; start < data.Length; start += 1 << 17)
        {
            int length = Math.Min(1 << 17, data.Length - start);
            if (fill + length > capacity - 64)
            {
                int blockStart = fill;
                int keep = Math.Min(windowSize + 1, blockStart);
                int delta = blockStart - keep;
                Buffer.BlockCopy(buffer, delta, buffer, 0, fill - delta);
                fill -= delta;
                windowStart += delta;
                sliding.Slide(delta);
            }

            Array.Copy(data, start, buffer, fill, length);
            plainStore.Clear();
            plain.FindSequences(data, start, start + length, 0, plainReps, plainStore);
            slidingStore.Clear();
            sliding.FindSequences(buffer, fill, fill + length, windowStart, slidingReps, slidingStore);
            fill += length;

            Assert.Equal(plainStore.Sequences.Length, slidingStore.Sequences.Length);
            for (int i = 0; i < plainStore.Sequences.Length; i++)
            {
                ZstdSequence a = plainStore.Sequences[i];
                ZstdSequence b = slidingStore.Sequences[i];
                Assert.True(a.LiteralLength == b.LiteralLength && a.MatchLength == b.MatchLength && a.Offset == b.Offset, $"level {level} packed {packed}: sequence {i} of the block at {start} differs");
                plainReps.Apply(a.Offset, a.LiteralLength);
                slidingReps.Apply(b.Offset, b.LiteralLength);
            }
        }
    }

    private static long CheckSequences(byte[] data, int start, int end, int windowStart, ZstdSequenceStore store, ref ZstdRepeatOffsets reps)
    {
        long matched = 0;
        int position = start;
        foreach (ZstdSequence sequence in store.Sequences)
        {
            position += (int)sequence.LiteralLength;
            Assert.True(sequence.Offset >= 1 && sequence.Offset <= position - windowStart, $"offset {sequence.Offset} at {position}");
            Assert.True(
                data.AsSpan(position, (int)sequence.MatchLength).SequenceEqual(data.AsSpan(position - (int)sequence.Offset, (int)sequence.MatchLength)),
                $"match of {sequence.MatchLength} at {position}, offset {sequence.Offset}, is not in the data");
            position += (int)sequence.MatchLength;
            matched += sequence.MatchLength;
            Assert.True(position <= end);
            reps.Apply(sequence.Offset, sequence.LiteralLength);
        }

        return matched;
    }
}
