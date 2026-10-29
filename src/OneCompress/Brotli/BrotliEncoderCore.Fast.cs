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

    // After a position with no copy, the parse advances by 1 + (bytes since the last copy >> FastSkipShift).
    private const int FastSkipShift = 6;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ParseFast(int start, int end)
    {
        byte[] buffer = _buffer;
        int[] head = _head;
        int shift = 64 - _plan.HashBits;
        ulong hashMask = ulong.MaxValue >> (8 * (8 - _plan.FastHashLength));
        int minMatch = _plan.FastHashLength;
        int window = _window;
        // A position can look back at most as far as there is stream before it, and never past the window. Once the stream base reaches the
        // window the first bound can no longer bind, so the base is clamped to keep the sum within an int.
        int streamBase = (int)Math.Min(_streamBase, window);
        BrotliFastBlock block = _fast;
        // Only the last distance is needed on the path that finds nothing; the others live in the block's ring and change at copies only.
        int[] ring = block.RingAfter;
        _ring.CopyTo(ring, 0);
        int r0 = ring[0];
        int pos = start;
        int insertStart = start;
        int last = end - 8; // the last position with eight bytes inside the block
        int prefetchLast = _plan.HashBits > 15 ? last - PrefetchAhead : -1; // a table of 256 KiB or less stays in the L2 cache
        while (pos <= last)
        {
            ulong word64 = BrotliEncRaw.Load64(buffer, pos);
            uint word = (uint)word64;
            int slot = (int)(((word64 & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 1;
            if (pos <= prefetchLast)
            {
                // The table is larger than the cache: ask for the slot a few positions on while this one is searched.
                BrotliEncRaw.Prefetch(head, (int)(((BrotliEncRaw.Load64(buffer, pos + PrefetchAhead) & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 1);
            }

            int candidate = BrotliEncRaw.Slot(head, slot);
            uint candidateWord = (uint)BrotliEncRaw.Slot(head, slot + 1);
            BrotliEncRaw.Slot(head, slot) = pos;
            BrotliEncRaw.Slot(head, slot + 1) = (int)word;
            int maxDistance = Math.Min(window, pos + streamBase);
            // One test decides whether this position has a candidate at all: most positions have none, and two separate, data-dependent
            // branches (last distance, table slot) cost more than the arithmetic that merges them.
            int candidateDistance = pos - candidate;
            bool tableHit = (candidateWord == word) & ((candidate | (maxDistance - candidateDistance)) >= 0);
            if (!tableHit)
            {
                // Incompressible stretches are crossed faster the longer they have gone without a copy.
                pos += 1 + ((pos - insertStart) >> FastSkipShift);
                continue;
            }

            bool ringHit = (r0 <= maxDistance) & (BrotliEncRaw.Load32(buffer, r0 <= maxDistance ? pos - r0 : pos) == word);
            int length = 0;
            int distance = 0;
            int score = 0;
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
                }
            }

            if (score <= 0)
            {
                pos += 1 + ((pos - insertStart) >> FastSkipShift);
                continue;
            }

            // RFC 7932 section 4: the short code that names the distance, if one does.
            int r1 = ring[1], r2 = ring[2], r3 = ring[3];
            int code = distance == r0 ? 0 : distance == r1 ? 1 : distance == r2 ? 2 : distance == r3 ? 3 : BrotliDistanceCoding.NearCode(distance, r0, r1);
            block.Add(pos - insertStart, length, distance, code);
            if (distance != r0)
            {
                ring[3] = r2;
                ring[2] = r1;
                ring[1] = r0;
                ring[0] = distance;
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

        _hashedUpTo = end;
    }
}
