using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

internal sealed partial class BrotliEncoderCore
{
    // The parse of quality 2: greedy, over a table whose buckets keep the two newest positions of a hash value, each beside the four bytes
    // found there (so a candidate that does not match is refused without touching the input at a far-away position). At each position it
    // scores the bucket's two candidates and takes the better (the scores are the ones of ParseFast). Like ParseFast it uses only the
    // "last distance" short code, and it works out both candidates and selects between them without branches, because which of them is
    // better cannot be predicted.
    //
    // Which positions enter the table: every position that is probed; the one before the next probe after a step over a miss; every
    // position inside a copy shorter than 32 bytes; and the last two of a longer copy. The positions are those of docs/brotli-encoder.md.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertTwoWay(int[] head, byte[] buffer, int p, int shift, ulong mask)
    {
        ulong w = BrotliEncRaw.Load64(buffer, p);
        int slot = (int)(((w & mask) * 0x9E3779B185EBCA87UL) >> shift) << 2;
        BrotliEncRaw.Slot(head, slot + 2) = BrotliEncRaw.Slot(head, slot);
        BrotliEncRaw.Slot(head, slot + 3) = BrotliEncRaw.Slot(head, slot + 1);
        BrotliEncRaw.Slot(head, slot) = p;
        BrotliEncRaw.Slot(head, slot + 1) = (int)(uint)w;
    }

    // The miss path of ParseTwoWay as a loop of its own. Stops at the first probed position where either of the bucket's candidates is
    // inside the window and agrees on the first four bytes; bit 0 of hits says the newer one does, bit 1 the older. Returns that position,
    // or a position past last.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ScanTwoWay(byte[] buffer, int[] head, int pos, int last, int insertStart, int shift, ulong hashMask, int window, int skipShift, out int c0, out int c1, out int hits)
    {
        while (pos <= last)
        {
            ulong word64 = BrotliEncRaw.Load64(buffer, pos);
            uint word = (uint)word64;
            int slot = (int)(((word64 & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 2;
            if (pos + PrefetchAhead <= last)
            {
                // The table is larger than the cache: ask for the bucket a few positions on while this one is searched.
                BrotliEncRaw.Prefetch(head, (int)(((BrotliEncRaw.Load64(buffer, pos + PrefetchAhead) & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 2);
            }

            int n0 = BrotliEncRaw.Slot(head, slot);
            uint w0 = (uint)BrotliEncRaw.Slot(head, slot + 1);
            int n1 = BrotliEncRaw.Slot(head, slot + 2);
            uint w1 = (uint)BrotliEncRaw.Slot(head, slot + 3);
            BrotliEncRaw.Slot(head, slot + 2) = n0;
            BrotliEncRaw.Slot(head, slot + 3) = (int)w0;
            BrotliEncRaw.Slot(head, slot) = pos;
            BrotliEncRaw.Slot(head, slot + 1) = (int)word;
            // One test decides whether this position has any candidate: most have none, and a branch per candidate is what costs here.
            // A candidate that is not negative is never farther back than the position itself, so the window is the only bound that can fail.
            bool hit0 = (w0 == word) & ((n0 | (window - (pos - n0))) >= 0);
            bool hit1 = (w1 == word) & ((n1 | (window - (pos - n1))) >= 0);
            if (hit0 | hit1)
            {
                c0 = n0;
                c1 = n1;
                hits = (hit0 ? 1 : 0) | (hit1 ? 2 : 0);
                return pos;
            }

            int step = 1 + ((pos - insertStart) >> skipShift);
            pos += step;
            if (step > 1 && pos - 1 <= last)
            {
                // The position stepped over just before the next probe is entered; the others are not.
                InsertTwoWay(head, buffer, pos - 1, shift, hashMask);
            }
        }

        c0 = c1 = -1;
        hits = 0;
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ParseTwoWay(int start, int end)
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
        while (pos <= last)
        {
            pos = ScanTwoWay(buffer, head, pos, last, insertStart, shift, hashMask, window, skipShift, out int c0, out int c1, out int hits);
            if (pos > last)
            {
                break;
            }

            int maxLength = end - pos;
            // A candidate that did not agree is measured at position 0 (any readable place will do) and its length is masked away.
            int m0 = -(hits & 1);
            int m1 = -((hits >> 1) & 1);
            int l0 = BrotliEncRaw.CommonLengthWide(buffer, c0 & m0, pos, maxLength) & m0;
            int l1 = BrotliEncRaw.CommonLengthWide(buffer, c1 & m1, pos, maxLength) & m1;
            int d0 = pos - c0;
            int d1 = pos - c1;
            int same0 = ((d0 ^ r0) - 1) >> 31; // -1 when the distance is the last one, else 0
            int same1 = ((d1 ^ r0) - 1) >> 31;
            int s0 = (l0 * LiteralCost) - ((CommandCost & same0) | ((DistanceCost + (16 * DistanceBits(d0))) & ~same0));
            int s1 = (l1 * LiteralCost) - ((CommandCost & same1) | ((DistanceCost + (16 * DistanceBits(d1))) & ~same1));
            // A copy shorter than the hashed bytes is no candidate.
            int bad0 = (l0 - minMatch) >> 31;
            int bad1 = (l1 - minMatch) >> 31;
            s0 = (s0 & ~bad0) | (-1_000_000 & bad0);
            s1 = (s1 & ~bad1) | (-1_000_000 & bad1);
            int older = (s0 - s1) >> 31; // -1 when the older candidate scores strictly better
            int score = s0 + ((s1 - s0) & older);
            if (score <= 0)
            {
                int step = 1 + ((pos - insertStart) >> skipShift);
                pos += step;
                if (step > 1 && pos - 1 <= last)
                {
                    InsertTwoWay(head, buffer, pos - 1, shift, hashMask);
                }

                continue;
            }

            int length = l0 + ((l1 - l0) & older);
            int distance = d0 + ((d1 - d0) & older);
            int same = same0 + ((same1 - same0) & older);
            // RFC 7932 section 4: short code 0 is the last distance; -1 says the distance is coded in full.
            block.Add(pos - insertStart, length, distance, ~same);
            r0 = distance;
            int next = pos + length;
            if (length < sparse)
            {
                for (int p = pos + 1; p < next && p <= last; p++)
                {
                    InsertTwoWay(head, buffer, p, shift, hashMask);
                }
            }
            else
            {
                // Inside a long copy only the last two positions are entered, so that the text after it can find the copy's tail.
                if (next - 2 <= last)
                {
                    InsertTwoWay(head, buffer, next - 2, shift, hashMask);
                }

                if (next - 1 <= last)
                {
                    InsertTwoWay(head, buffer, next - 1, shift, hashMask);
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
