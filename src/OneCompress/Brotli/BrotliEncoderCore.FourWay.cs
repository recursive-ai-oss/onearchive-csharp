using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

internal sealed partial class BrotliEncoderCore
{
    // The parse of quality 3: the greedy parse of quality 2 over buckets of the four newest positions of a hash value, each beside the four
    // bytes found there. A position scores every candidate of its bucket that agrees on the first four bytes (the scores are the ones of
    // ParseTwoWay and ParseFast) and takes the best; on a tie the newer one wins. Like them it uses only the "last distance" short code.
    //
    // Which positions enter the table: every position that is probed; the one before the next probe after a step over a miss; every
    // position inside a copy shorter than SparseInsert bytes; and the last two of a longer copy.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertFourWay(int[] head, byte[] buffer, int p, int shift, ulong mask)
    {
        ulong w = BrotliEncRaw.Load64(buffer, p);
        int slot = (int)(((w & mask) * 0x9E3779B185EBCA87UL) >> shift) << 3;
        ShiftBucket(head, slot);
        BrotliEncRaw.Slot(head, slot) = p;
        BrotliEncRaw.Slot(head, slot + 1) = (int)(uint)w;
    }

    // Moves the three newest entries of a bucket one place older; the oldest falls out.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ShiftBucket(int[] head, int slot)
    {
        BrotliEncRaw.Slot(head, slot + 7) = BrotliEncRaw.Slot(head, slot + 5);
        BrotliEncRaw.Slot(head, slot + 6) = BrotliEncRaw.Slot(head, slot + 4);
        BrotliEncRaw.Slot(head, slot + 5) = BrotliEncRaw.Slot(head, slot + 3);
        BrotliEncRaw.Slot(head, slot + 4) = BrotliEncRaw.Slot(head, slot + 2);
        BrotliEncRaw.Slot(head, slot + 3) = BrotliEncRaw.Slot(head, slot + 1);
        BrotliEncRaw.Slot(head, slot + 2) = BrotliEncRaw.Slot(head, slot);
    }

    // The miss path of ParseFourWay as a loop of its own. Stops at the first probed position where one of the bucket's candidates is inside
    // the window and agrees on the first four bytes; bit i of hits says the i-th newest one does. Returns that position, or a position
    // past last. The bucket is in c[0..3] positions order, newest first.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ScanFourWay(byte[] buffer, int[] head, int pos, int last, int insertStart, int shift, ulong hashMask, int window, int skipShift, Span<int> c, out int hits)
    {
        while (pos <= last)
        {
            ulong word64 = BrotliEncRaw.Load64(buffer, pos);
            uint word = (uint)word64;
            int slot = (int)(((word64 & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 3;
            if (pos + PrefetchAhead <= last)
            {
                // The table is larger than the cache: ask for the bucket a few positions on while this one is searched.
                BrotliEncRaw.Prefetch(head, (int)(((BrotliEncRaw.Load64(buffer, pos + PrefetchAhead) & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 3);
            }

            int n0 = BrotliEncRaw.Slot(head, slot);
            int n1 = BrotliEncRaw.Slot(head, slot + 2);
            int n2 = BrotliEncRaw.Slot(head, slot + 4);
            int n3 = BrotliEncRaw.Slot(head, slot + 6);
            // A candidate that is not negative is never farther back than the position itself, so the window is the only bound that can fail.
            bool hit0 = ((uint)BrotliEncRaw.Slot(head, slot + 1) == word) & ((n0 | (window - (pos - n0))) >= 0);
            bool hit1 = ((uint)BrotliEncRaw.Slot(head, slot + 3) == word) & ((n1 | (window - (pos - n1))) >= 0);
            bool hit2 = ((uint)BrotliEncRaw.Slot(head, slot + 5) == word) & ((n2 | (window - (pos - n2))) >= 0);
            bool hit3 = ((uint)BrotliEncRaw.Slot(head, slot + 7) == word) & ((n3 | (window - (pos - n3))) >= 0);
            ShiftBucket(head, slot);
            BrotliEncRaw.Slot(head, slot) = pos;
            BrotliEncRaw.Slot(head, slot + 1) = (int)word;
            if (hit0 | hit1 | hit2 | hit3)
            {
                c[0] = n0;
                c[1] = n1;
                c[2] = n2;
                c[3] = n3;
                hits = (hit0 ? 1 : 0) | (hit1 ? 2 : 0) | (hit2 ? 4 : 0) | (hit3 ? 8 : 0);
                return pos;
            }

            int step = 1 + ((pos - insertStart) >> skipShift);
            pos += step;
            if (step > 1 && pos - 1 <= last)
            {
                // The position stepped over just before the next probe is entered; the others are not.
                InsertFourWay(head, buffer, pos - 1, shift, hashMask);
            }
        }

        hits = 0;
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ParseFourWay(int start, int end)
    {
        byte[] buffer = _buffer;
        int[] head = _head;
        int shift = 64 - _plan.HashBits;
        ulong hashMask = ulong.MaxValue >> (8 * (8 - _plan.FastHashLength));
        int minMatch = _plan.FastHashLength;
        int window = _window;
        BrotliFastBlock block = _fast;
        int skipShift = _plan.SkipShift;
        int sparse = _plan.SparseInsert;
        int[] ring = block.RingAfter;
        _ring.CopyTo(ring, 0);
        int r0 = ring[0];
        int pos = start;
        int insertStart = start;
        int last = end - 8; // the last position with eight bytes inside the block
        Span<int> cand = stackalloc int[4];
        while (pos <= last)
        {
            pos = ScanFourWay(buffer, head, pos, last, insertStart, shift, hashMask, window, skipShift, cand, out int hits);
            if (pos > last)
            {
                break;
            }

            int maxLength = end - pos;
            int bestScore = 0;
            int length = 0;
            int distance = 0;
            int bestSame = 0;
            for (int i = 0; i < 4; i++)
            {
                if (((hits >> i) & 1) == 0)
                {
                    continue;
                }

                int candidate = cand[i];
                int l = BrotliEncRaw.CommonLengthWide(buffer, candidate, pos, maxLength);
                int d = pos - candidate;
                int same = ((d ^ r0) - 1) >> 31; // -1 when the distance is the last one, else 0
                int score = (l * LiteralCost) - ((CommandCost & same) | ((DistanceCost + (16 * DistanceBits(d))) & ~same));
                // A copy shorter than the hashed bytes is no candidate; the newer of two equal scores is kept.
                if (l >= minMatch && score > bestScore)
                {
                    bestScore = score;
                    length = l;
                    distance = d;
                    bestSame = same;
                }
            }

            if (bestScore <= 0)
            {
                int step = 1 + ((pos - insertStart) >> skipShift);
                pos += step;
                if (step > 1 && pos - 1 <= last)
                {
                    InsertFourWay(head, buffer, pos - 1, shift, hashMask);
                }

                continue;
            }

            // RFC 7932 section 4: short code 0 is the last distance; -1 says the distance is coded in full.
            block.Add(pos - insertStart, length, distance, ~bestSame);
            r0 = distance;
            int next = pos + length;
            if (length < sparse)
            {
                for (int p = pos + 1; p < next && p <= last; p++)
                {
                    InsertFourWay(head, buffer, p, shift, hashMask);
                }
            }
            else
            {
                // Inside a long copy only the last two positions are entered, so that the text after it can find the copy's tail.
                if (next - 2 <= last)
                {
                    InsertFourWay(head, buffer, next - 2, shift, hashMask);
                }

                if (next - 1 <= last)
                {
                    InsertFourWay(head, buffer, next - 1, shift, hashMask);
                }
            }

            pos = next;
            insertStart = pos;
        }

        if (insertStart < end)
        {
            block.AddTail(end - insertStart);
        }

        ring[0] = r0;
        _hashedUpTo = end;
    }
}
