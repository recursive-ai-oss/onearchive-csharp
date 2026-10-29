using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// The insert stride of the row finders: every sequence reported is a real match, matches far into a large input are still found,
/// and a window that slides along gives what a window that does not slide gives.
/// </summary>
public class ZstdInsertStrideTests
{
    // A random block of 256 KiB repeated with one changed byte per 64 KiB: long matches at a distance inside every level's window.
    private static byte[] RepeatingData(int length)
    {
        const int Period = 1 << 18;
        var random = new Random(83);
        byte[] data = new byte[length];
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
    [InlineData(5, 1)]
    [InlineData(5, 4)]
    [InlineData(7, 4)]
    [InlineData(9, 4)]
    [InlineData(9, 16)]
    public void MatchesAreFoundAndEveryMatchIsReal(int level, int stride)
    {
        byte[] data = RepeatingData(20_000_000);
        ZstdCompressionParameters parameters = ZstdCompressionParameters.Resolve(level, data.Length, 0);
        var finder = new ZstdLazyMatchFinder { InsertStride = stride };
        finder.BeginFrame(parameters, null);
        var store = new ZstdSequenceStore();
        var reps = new ZstdRepeatOffsets { Offset1 = 1, Offset2 = 4, Offset3 = 8 };
        long matched = 0;
        for (int start = 0; start < data.Length; start += 1 << 17)
        {
            int end = Math.Min(data.Length, start + (1 << 17));
            store.Clear();
            finder.FindSequences(data, start, end, 0, reps, store);
            int position = start;
            foreach (ZstdSequence sequence in store.Sequences)
            {
                position += (int)sequence.LiteralLength;
                Assert.True(sequence.Offset >= 1 && sequence.Offset <= position, $"offset {sequence.Offset} at {position}");
                Assert.True(
                    data.AsSpan(position, (int)sequence.MatchLength).SequenceEqual(data.AsSpan(position - (int)sequence.Offset, (int)sequence.MatchLength)),
                    $"match of {sequence.MatchLength} at {position}, offset {sequence.Offset}, is not in the data");
                position += (int)sequence.MatchLength;
                matched += sequence.MatchLength;
                reps.Apply(sequence.Offset, sequence.LiteralLength);
            }
        }

        Assert.True(matched > 0.9 * (data.Length - (1 << 18)), $"level {level} stride {stride}: only {matched} bytes matched");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void ASlidingWindowFindsTheSameSequencesAsOneThatDoesNotSlide(int level)
    {
        // The window is kept the way the streaming encoder keeps it: the history up to one byte more than the window, then the blocks
        // being gathered; when it is full, everything older is dropped and the finder is told.
        byte[] data = RepeatingData(12_000_000);
        ZstdCompressionParameters parameters = ZstdCompressionParameters.Resolve(level, data.Length, 0);
        int windowSize = (int)parameters.WindowSize;
        int capacity = windowSize + 1 + (2 << 17) + 64;

        var plain = new ZstdLazyMatchFinder();
        plain.BeginFrame(parameters, null);
        var sliding = new ZstdLazyMatchFinder();
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
                int keep = Math.Min(windowSize + 1, fill);
                int delta = fill - keep;
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
                Assert.True(a.LiteralLength == b.LiteralLength && a.MatchLength == b.MatchLength && a.Offset == b.Offset, $"level {level}: sequence {i} of the block at {start} differs");
                plainReps.Apply(a.Offset, a.LiteralLength);
                slidingReps.Apply(b.Offset, b.LiteralLength);
            }
        }
    }
}
