using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

internal sealed partial class BrotliEncoderCore
{
    // The parse of qualities 0 and 1: greedy, one table slot per hash value, and no lazy step or dictionary, so the loop keeps everything in
    // locals and probes the table once per position. At each position it tries the four last distances and the table's candidate and takes
    // the one that scores best (the scores are the ones of ParseLazy).
    //
    // Which positions enter the table: every position that is probed; every position inside a copy shorter than 16 bytes; and the second
    // byte and the last two of a longer copy, so that the text after a long copy can still find the copy's own contents. Copies are
    // searched for only where four bytes are inside the block. See docs/brotli-encoder.md.
    // A slot holds a position and, beside it, the four bytes found there, so that a candidate that does not match is refused without
    // touching the input at a far-away position.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Insert(int[] head, byte[] buffer, int p, int shift, ulong mask)
    {
        ulong w = BrotliEncRaw.Load64(buffer, p);
        int slot = (int)(((w & mask) * 0x9E3779B185EBCA87UL) >> shift) << 1;
        BrotliEncRaw.Slot(head, slot) = p;
        BrotliEncRaw.Slot(head, slot + 1) = (int)(uint)w;
    }

    private const int PrefetchAhead = 8;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ParseFast(int start, int end)
    {
        byte[] buffer = _buffer;
        int[] head = _head;
        int shift = 64 - _plan.HashBits;
        ulong hashMask = ulong.MaxValue >> (8 * (8 - _plan.FastHashLength));
        int minMatch = _plan.FastHashLength;
        int window = _window;
        long streamBase = _streamBase;
        int r0 = _ring[0], r1 = _ring[1], r2 = _ring[2], r3 = _ring[3];
        BrotliFastBlock block = _fast;
        int skipShift = _plan.SkipShift;
        int pos = start;
        int insertStart = start;
        int last = end - 8; // the last position with eight bytes inside the block
        while (pos <= last)
        {
            ulong word64 = BrotliEncRaw.Load64(buffer, pos);
            uint word = (uint)word64;
            int slot = (int)(((word64 & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 1;
            if (pos + PrefetchAhead <= last)
            {
                // The table is larger than the L1 cache: ask for the slot a few positions on while this one is searched.
                BrotliEncRaw.Prefetch(head, (int)(((BrotliEncRaw.Load64(buffer, pos + PrefetchAhead) & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 1);
            }

            ref int cell = ref BrotliEncRaw.Slot(head, slot);
            int candidate = cell;
            uint candidateWord = (uint)BrotliEncRaw.Slot(head, slot + 1);
            cell = pos;
            BrotliEncRaw.Slot(head, slot + 1) = (int)word;
            int maxDistance = (int)Math.Min(window, streamBase + pos);
            // One test decides whether this position has a candidate at all: most positions have none, and two separate, data-dependent
            // branches (last distance, table slot) cost more than the arithmetic that merges them.
            int candidateDistance = pos - candidate;
            bool tableHit = (candidateWord == word) & ((candidate | (maxDistance - candidateDistance)) >= 0);
            bool ringHit = (r0 <= maxDistance) & (BrotliEncRaw.Load32(buffer, r0 <= maxDistance ? pos - r0 : pos) == word);
            if (!(tableHit | ringHit))
            {
                // Incompressible stretches are crossed faster the longer they have gone without a copy.
                pos += 1 + ((pos - insertStart) >> skipShift);
                continue;
            }

            int length = 0;
            int distance = 0;
            int score = 0;
            int code = 0; // the short distance code of the copy (RFC 7932 section 4)
            if (ringHit)
            {
                distance = r0;
                length = BrotliEncRaw.CommonLength(buffer, pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - CommandCost;
            }

            if (tableHit)
            {
                int l2 = BrotliEncRaw.CommonLength(buffer, candidate, pos, 4, end - pos);
                int s2 = (l2 * LiteralCost) - DistanceCost - (16 * DistanceBits(candidateDistance));
                if (s2 > score && l2 >= minMatch)
                {
                    length = l2;
                    distance = candidateDistance;
                    score = s2;
                    code = distance == r0 ? 0 : distance == r1 ? 1 : distance == r2 ? 2 : distance == r3 ? 3 : BrotliDistanceCoding.NearCode(distance, r0, r1);
                }
            }

            if (score <= 0)
            {
                // Incompressible stretches are crossed faster the longer they have gone without a copy.
                pos += 1 + ((pos - insertStart) >> skipShift);
                continue;
            }

            block.Add(pos - insertStart, length, distance, code);
            if (distance != r0)
            {
                r3 = r2;
                r2 = r1;
                r1 = r0;
                r0 = distance;
            }

            int next = pos + length;
            if (length < _plan.FullInsertLength)
            {
                for (int p = pos + 1; p < Math.Min(next, last + 1); p++)
                {
                    Insert(head, buffer, p, shift, hashMask);
                }
            }
            else if (next <= last)
            {
                Insert(head, buffer, pos + 1, shift, hashMask);
                int a = next - 2;
                Insert(head, buffer, a, shift, hashMask);
                a++;
                Insert(head, buffer, a, shift, hashMask);
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
}
