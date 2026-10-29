using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// The segment selection of COVER (Liao, Petri, Moffat and Wirth 2016, section 3): the samples laid end to end are cut into
/// epochs, and from each epoch the segment whose distinct d-mers carry the most weight is taken; a taken segment's d-mers stop
/// counting for the epochs after it.
/// </summary>
/// <remarks>
/// Positions here are "compact" positions: the samples being trained on laid end to end. When part of the samples is held out, the
/// training samples are a few runs of the original data, and a run table maps a compact position to where its byte is, so the held-out
/// part is never copied out.
/// </remarks>
internal static class ZstdSegmentSelector
{
    /// <summary>Maps an original position to its d-mer slot, or to the one past the last slot (which always has no weight) where no d-mer starts.</summary>
    private interface IPositionSlots
    {
        int Slot(int position);
    }

    private readonly struct HashedSlots(ZstdDmerIndex index) : IPositionSlots
    {
        private readonly byte[] _data = index.Set.Data;
        private readonly ulong[] _valid = index.Valid!;
        private readonly int _d = index.DmerSize;
        private readonly int _shift = 64 - index.HashBits;
        private readonly int _none = index.Slots;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Slot(int position) =>
            ((_valid[position >> 6] >> (position & 63)) & 1) == 0 ? _none : (int)((ZstdDmerCounts.Key(_data, position, _d) * ZstdDmerCounts.Multiplier) >> _shift);
    }

    private readonly struct ExactSlots(int[] ids, int none) : IPositionSlots
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Slot(int position) => ids[position] is var id and >= 0 ? id : none;
    }

    /// <summary>
    /// A window's cell for each slot: how often the d-mer is in the window, and its weight. Windows up to 65535 positions keep both
    /// in 32 bits (a position then touches one cache line of the table); longer ones, which no practical segment size reaches, use 64.
    /// </summary>
    private interface ICellLayout<TCell>
        where TCell : unmanaged
    {
        static abstract TCell Make(ushort weight);

        static abstract TCell Increment(TCell cell);

        static abstract TCell Decrement(TCell cell);

        /// <summary>Gets the weight when the count is 0, else 0, without a branch: which d-mers repeat is not predictable.</summary>
        static abstract uint WeightIfNoCount(TCell cell);

        static abstract TCell ClearCount(TCell cell);
    }

    private readonly struct NarrowCell : ICellLayout<uint>
    {
        public static uint Make(ushort weight) => (uint)weight << 16;

        public static uint Increment(uint cell) => cell + 1;

        public static uint Decrement(uint cell) => cell - 1;

        // (count - 1) >> 31 is all ones exactly when the count is 0.
        public static uint WeightIfNoCount(uint cell) => (cell >> 16) & (uint)(((int)(cell & 0xFFFF) - 1) >> 31);

        public static uint ClearCount(uint cell) => cell & 0xFFFF0000;
    }

    private readonly struct WideCell : ICellLayout<ulong>
    {
        public static ulong Make(ushort weight) => (ulong)weight << 32;

        public static ulong Increment(ulong cell) => cell + 1;

        public static ulong Decrement(ulong cell) => cell - 1;

        public static uint WeightIfNoCount(ulong cell) => (uint)(cell >> 32) & (uint)(((long)(cell & 0xFFFFFFFF) - 1) >> 63);

        public static ulong ClearCount(ulong cell) => cell & 0xFFFFFFFF00000000UL;
    }

    private readonly record struct Segment(int Start, int Length, long Score, int Epoch);

    /// <summary>
    /// Chooses the content for a dictionary of <paramref name="dictionarySize"/> bytes from the samples behind <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// Every comparison is on integers and ties go to the earliest position, then the earlier epoch, so the result depends only on the
    /// samples and the parameters. The segments are written in order of increasing score: the most useful content ends up nearest the
    /// data it serves and gets the smallest offsets.
    /// </remarks>
    public static byte[] Select(ZstdDmerIndex index, ZstdSegmentScore score, int segmentSize, int dictionarySize, ZstdTrainingScratch scratch, CancellationToken cancellationToken)
    {
        ushort[] baseWeights = index.Weights(score);
        int slotCount = index.Slots;
        List<Segment> chosen;
        if (segmentSize <= ushort.MaxValue)
        {
            ZstdTrainingScratch.Fit(ref scratch.Cells, slotCount + 1);
            for (int i = 0; i < slotCount; i++)
            {
                scratch.Cells[i] = NarrowCell.Make(baseWeights[i]);
            }

            scratch.Cells[slotCount] = 0;
            chosen = Dispatch<uint, NarrowCell>(index, segmentSize, dictionarySize, scratch.Cells, scratch, cancellationToken);
        }
        else
        {
            ZstdTrainingScratch.Fit(ref scratch.WideCells, slotCount + 1);
            for (int i = 0; i < slotCount; i++)
            {
                scratch.WideCells[i] = WideCell.Make(baseWeights[i]);
            }

            scratch.WideCells[slotCount] = 0;
            chosen = Dispatch<ulong, WideCell>(index, segmentSize, dictionarySize, scratch.WideCells, scratch, cancellationToken);
        }

        int n = index.TrainLength;
        if (chosen.Count == 0)
        {
            // Nothing scored (for instance, no d-mer occurs in two samples under a zero weight): take the start of the samples.
            byte[] start = new byte[Math.Max(Math.Min(dictionarySize, n), 0)];
            Copy(index, 0, start);
            return start;
        }

        // Least valuable first.
        chosen.Sort(static (a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Epoch.CompareTo(b.Epoch));
        int total = 0;
        foreach (Segment s in chosen)
        {
            total += s.Length;
        }

        byte[] content = new byte[total];
        int w = 0;
        foreach (Segment s in chosen)
        {
            Copy(index, s.Start, content.AsSpan(w, s.Length));
            w += s.Length;
        }

        if (content.Length >= 8)
        {
            return content;
        }

        byte[] fallback = new byte[Math.Min(Math.Max(dictionarySize, 8), n)];
        Copy(index, 0, fallback);
        return fallback;
    }

    // Copies destination.Length bytes of the training samples from compact position `start`.
    private static void Copy(ZstdDmerIndex index, int start, Span<byte> destination)
    {
        int[] runs = index.Runs;
        int r = FindRun(runs, start);
        int written = 0;
        while (written < destination.Length)
        {
            int compactStart = runs[3 * r];
            int from = runs[(3 * r) + 1] + (start + written - compactStart);
            int take = Math.Min(destination.Length - written, compactStart + runs[(3 * r) + 2] - (start + written));
            index.Set.Data.AsSpan(from, take).CopyTo(destination[written..]);
            written += take;
            r++;
        }
    }

    // The run holding compact position c; runs are (compact start, original start, length) triples in order.
    private static int FindRun(int[] runs, int c)
    {
        int lo = 0;
        int hi = (runs.Length / 3) - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (runs[3 * mid] <= c)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private static List<Segment> Dispatch<TCell, TLayout>(ZstdDmerIndex index, int k, int dictionarySize, TCell[] cells, ZstdTrainingScratch scratch, CancellationToken cancellationToken)
        where TCell : unmanaged
        where TLayout : struct, ICellLayout<TCell> =>
        index.ExactIds is null
            ? Scan<HashedSlots, TCell, TLayout>(new HashedSlots(index), index, k, dictionarySize, cells, scratch, cancellationToken)
            : Scan<ExactSlots, TCell, TLayout>(new ExactSlots(index.ExactIds, index.Slots), index, k, dictionarySize, cells, scratch, cancellationToken);

    private static List<Segment> Scan<TSlots, TCell, TLayout>(TSlots slots, ZstdDmerIndex index, int k, int dictionarySize, TCell[] cells, ZstdTrainingScratch scratch, CancellationToken cancellationToken)
        where TSlots : struct, IPositionSlots
        where TCell : unmanaged
        where TLayout : struct, ICellLayout<TCell>
    {
        int d = index.DmerSize;
        int n = index.TrainLength;
        int[] runs = index.Runs;
        int runCount = runs.Length / 3;

        // The cell after the last slot belongs to positions with no d-mer and keeps weight 0. The ring holds the window's slots; a
        // window spans at most k bytes, so k + 1 entries rounded up to a power of two suffice.
        int ringSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(k + 2, 2));
        ZstdTrainingScratch.Fit(ref scratch.Ring, ringSize);
        int[] ring = scratch.Ring;
        int ringMask = ringSize - 1;

        // As many epochs as there is room for segments, but never an epoch shorter than a segment.
        int epochs = Math.Clamp((dictionarySize + k - 1) / k, 1, Math.Max(1, n / k));
        int epochLength = n / epochs;
        int remaining = dictionarySize;
        var chosen = new List<Segment>();
        for (int e = 0; e < epochs && remaining >= d; e++)
        {
            if ((e & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int e0 = e * epochLength;
            int e1 = e == epochs - 1 ? n : e0 + epochLength;
            int window = Math.Min(Math.Min(k, remaining), e1 - e0);
            if (window < d)
            {
                continue;
            }

            int span = window - d;
            int last = e1 - d;
            long score = 0;
            long best = 0;
            int bestStart = -1;
            int r = FindRun(runs, e0);
            int hi = e0;

            // Growing the window: the first span + 1 positions are only added. A d-mer's weight joins the score when it enters the
            // window for the first time (its count goes from 0 to 1) and leaves it when the count returns to 0.
            int fill = Math.Min(last, e0 + span);
            while (hi <= fill)
            {
                int runEnd = runs[3 * r] + runs[(3 * r) + 2];
                int offset = runs[(3 * r) + 1] - runs[3 * r];
                int stop = Math.Min(fill, runEnd - 1);
                for (; hi <= stop; hi++)
                {
                    int slot = slots.Slot(hi + offset);
                    TCell cell = cells[slot];
                    cells[slot] = TLayout.Increment(cell);
                    score += TLayout.WeightIfNoCount(cell);
                    ring[hi & ringMask] = slot;
                }

                if (hi == runEnd && r + 1 < runCount)
                {
                    r++;
                }
            }

            if (fill == e0 + span && score > best)
            {
                best = score;
                bestStart = e0;
            }

            // Sliding: one position enters and one leaves. A window is evaluated once it holds its full width; the first best
            // (earliest) wins ties.
            while (hi <= last)
            {
                int runEnd = runs[3 * r] + runs[(3 * r) + 2];
                int offset = runs[(3 * r) + 1] - runs[3 * r];
                int stop = Math.Min(last, runEnd - 1);
                for (; hi <= stop; hi++)
                {
                    int slot = slots.Slot(hi + offset);
                    TCell cell = cells[slot];
                    cells[slot] = TLayout.Increment(cell);
                    score += TLayout.WeightIfNoCount(cell);
                    ring[hi & ringMask] = slot;
                    int old = ring[(hi - span - 1) & ringMask];
                    TCell oldCell = TLayout.Decrement(cells[old]);
                    cells[old] = oldCell;
                    score -= TLayout.WeightIfNoCount(oldCell);
                    if (score > best)
                    {
                        best = score;
                        bestStart = hi - span;
                    }
                }

                if (hi == runEnd && r + 1 < runCount)
                {
                    r++;
                }
            }

            // Leave the counts clean for the next epoch: only the final window's d-mers are still counted.
            for (int p = Math.Max(e0, last - span); p <= last; p++)
            {
                int slot = ring[p & ringMask];
                cells[slot] = TLayout.ClearCount(cells[slot]);
            }

            if (bestStart < 0)
            {
                continue;
            }

            // The d-mers of the chosen segment are covered now: they stop counting for every later epoch.
            int cover = FindRun(runs, bestStart);
            for (int p = bestStart; p <= bestStart + span; p++)
            {
                while (p >= runs[3 * cover] + runs[(3 * cover) + 2])
                {
                    cover++;
                }

                cells[slots.Slot(p + runs[(3 * cover) + 1] - runs[3 * cover])] = default;
            }

            chosen.Add(new Segment(bestStart, window, best, e));
            remaining -= window;
        }

        return chosen;
    }
}
