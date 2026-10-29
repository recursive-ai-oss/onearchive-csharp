using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

internal sealed partial class BrotliEncoderCore
{
    // The parse of quality 2: greedy, over a table whose buckets keep the two newest positions of a hash value, each beside the four bytes
    // found there (so a candidate that does not match is refused without touching the input at a far-away position). At each position it
    // scores the four last distances and the bucket's two candidates and takes the best (the scores are the ones of ParseFast).
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ParseTwoWay(int start, int end)
    {
        byte[] buffer = _buffer;
        int[] head = _head;
        int shift = 64 - _plan.HashBits;
        ulong hashMask = ulong.MaxValue >> (8 * (8 - _plan.FastHashLength));
        int minMatch = _plan.FastHashLength;
        int window = _window;
        long streamBase = _streamBase;
        BrotliFastBlock block = _fast;
        int skipShift = _plan.SkipShift;
        int sparse = _plan.SparseInsert;
        int r0 = _ring[0], r1 = _ring[1], r2 = _ring[2], r3 = _ring[3];
        int pos = start;
        int insertStart = start;
        int last = end - 8; // the last position with eight bytes inside the block
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

            int c0 = BrotliEncRaw.Slot(head, slot);
            uint w0 = (uint)BrotliEncRaw.Slot(head, slot + 1);
            int c1 = BrotliEncRaw.Slot(head, slot + 2);
            uint w1 = (uint)BrotliEncRaw.Slot(head, slot + 3);
            BrotliEncRaw.Slot(head, slot + 2) = c0;
            BrotliEncRaw.Slot(head, slot + 3) = (int)w0;
            BrotliEncRaw.Slot(head, slot) = pos;
            BrotliEncRaw.Slot(head, slot + 1) = (int)word;
            int maxDistance = (int)Math.Min(window, streamBase + pos);
            // One test decides whether this position has any candidate: most have none, and a branch per candidate is what costs here.
            int d0 = pos - c0;
            int d1 = pos - c1;
            bool hit0 = (w0 == word) & ((c0 | (maxDistance - d0)) >= 0);
            bool hit1 = (w1 == word) & ((c1 | (maxDistance - d1)) >= 0);
            bool ring0 = (r0 <= maxDistance) & (BrotliEncRaw.Load32(buffer, r0 <= maxDistance ? pos - r0 : pos) == word);
            bool ring1 = (r1 <= maxDistance) & (BrotliEncRaw.Load32(buffer, r1 <= maxDistance ? pos - r1 : pos) == word);
            bool ring2 = (r2 <= maxDistance) & (BrotliEncRaw.Load32(buffer, r2 <= maxDistance ? pos - r2 : pos) == word);
            bool ring3 = (r3 <= maxDistance) & (BrotliEncRaw.Load32(buffer, r3 <= maxDistance ? pos - r3 : pos) == word);
            if (!(hit0 | hit1 | ring0 | ring1 | ring2 | ring3))
            {
                int step = 1 + ((pos - insertStart) >> skipShift);
                pos += step;
                if (step > 1 && pos - 1 <= last)
                {
                    // The position stepped over just before the next probe is entered; the others are not.
                    InsertTwoWay(head, buffer, pos - 1, shift, hashMask);
                }

                continue;
            }

            int length = 0;
            int distance = 0;
            int score = 0;
            int maxLength = end - pos;
            if (ring0)
            {
                length = BrotliEncRaw.CommonLength(buffer, pos - r0, pos, 4, maxLength);
                distance = r0;
                score = (length * LiteralCost) - CommandCost;
            }

            if (ring1)
            {
                Better(buffer, pos, maxLength, r1, RingDistanceCost, ref length, ref distance, ref score);
            }

            if (ring2)
            {
                Better(buffer, pos, maxLength, r2, RingDistanceCost, ref length, ref distance, ref score);
            }

            if (ring3)
            {
                Better(buffer, pos, maxLength, r3, RingDistanceCost, ref length, ref distance, ref score);
            }

            if (hit0)
            {
                Better(buffer, pos, maxLength, d0, DistanceCost + (16 * DistanceBits(d0)), ref length, ref distance, ref score, minMatch);
            }

            if (hit1)
            {
                Better(buffer, pos, maxLength, d1, DistanceCost + (16 * DistanceBits(d1)), ref length, ref distance, ref score, minMatch);
            }

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

            // RFC 7932 section 4: the short code that names the distance, if one does.
            int code = distance == r0 ? 0 : distance == r1 ? 1 : distance == r2 ? 2 : distance == r3 ? 3 : BrotliDistanceCoding.NearCode(distance, r0, r1);
            block.Add(pos - insertStart, length, distance, code);
            if (distance != r0)
            {
                r3 = r2;
                r2 = r1;
                r1 = r0;
                r0 = distance;
            }

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

        block.RingAfter[0] = r0;
        block.RingAfter[1] = r1;
        block.RingAfter[2] = r2;
        block.RingAfter[3] = r3;
        _hashedUpTo = end;
    }

    // Keeps the copy at <distance> if it scores better than the best so far; of equal scores the longer wins.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Better(byte[] buffer, int pos, int maxLength, int distance, int cost, ref int length, ref int bestDistance, ref int score, int minLength = 4)
    {
        int l = BrotliEncRaw.CommonLength(buffer, pos - distance, pos, 4, maxLength);
        int s = (l * LiteralCost) - cost;
        if (l >= minLength && (s > score || (s == score && l > length)))
        {
            length = l;
            bestDistance = distance;
            score = s;
        }
    }
}
