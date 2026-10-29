using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

internal sealed partial class BrotliEncoderCore
{
    // The parse of qualities 0 and 1: greedy, one table slot per hash value, and no lazy step or dictionary. Each probed position looks
    // at its slot once; only a slot whose candidate agrees on the first four bytes leads anywhere, and that candidate is the only copy
    // considered. A copy is coded with the "last distance" code when its distance is the last one used and with an explicit distance
    // otherwise: the other short codes of RFC 7932 section 4 are not used, because they save little on these inputs and every test for
    // them is a branch on the data, which costs more than the bits it saves.
    //
    // Which positions enter the table: every position that is probed, and for a copy the second byte and the last two. Copies are
    // searched for only where eight bytes are inside the block. See docs/brotli-encoder.md.
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

    // The miss path of ParseFast as a loop of its own, so that the few values it needs stay in registers: it enters every position it
    // probes and stops at the first one whose table slot holds a position inside the window that agrees on the first four bytes (the
    // slot's own four bytes are checked before the input at the candidate is touched). A candidate that is not negative is never farther
    // back than the position itself, so the window is the only bound that can fail. Returns that position and the candidate, or a
    // position past last.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ScanFast(byte[] buffer, int[] head, int pos, int last, int insertStart, int shift, ulong hashMask, int window, int prefetchLast, out int candidate)
    {
        while (pos <= last)
        {
            ulong word64 = BrotliEncRaw.Load64(buffer, pos);
            int slot = (int)(((word64 & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 1;
            if (pos <= prefetchLast)
            {
                // The table is larger than the cache: ask for the slot a few positions on while this one is searched.
                BrotliEncRaw.Prefetch(head, (int)(((BrotliEncRaw.Load64(buffer, pos + PrefetchAhead) & hashMask) * 0x9E3779B185EBCA87UL) >> shift) << 1);
            }

            int found = BrotliEncRaw.Slot(head, slot);
            uint foundWord = (uint)BrotliEncRaw.Slot(head, slot + 1);
            BrotliEncRaw.Slot(head, slot) = pos;
            BrotliEncRaw.Slot(head, slot + 1) = (int)(uint)word64;
            if ((foundWord == (uint)word64) & ((found | (window - (pos - found))) >= 0))
            {
                candidate = found;
                return pos;
            }

            // Incompressible stretches are crossed faster the longer they have gone without a copy.
            pos += 1 + ((pos - insertStart) >> FastSkipShift);
        }

        candidate = -1;
        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ParseFast(int start, int end)
    {
        byte[] buffer = _buffer;
        int[] head = _head;
        int shift = 64 - _plan.HashBits;
        ulong hashMask = ulong.MaxValue >> (8 * (8 - _plan.FastHashLength));
        int minMatch = _plan.FastHashLength;
        int window = _window;
        BrotliFastBlock block = _fast;
        // Only the last distance is tracked: it is the only short code this parse uses.
        int[] ring = block.RingAfter;
        _ring.CopyTo(ring, 0);
        int r0 = ring[0];
        int pos = start;
        int insertStart = start;
        int last = end - 8; // the last position with eight bytes inside the block
        int prefetchLast = _plan.HashBits > 15 ? last - PrefetchAhead : -1; // a table of 256 KiB or less stays in the L2 cache
        while (pos <= last)
        {
            pos = ScanFast(buffer, head, pos, last, insertStart, shift, hashMask, window, prefetchLast, out int candidate);
            if (pos > last)
            {
                break;
            }

            int length = BrotliEncRaw.CommonLengthWide(buffer, candidate, pos, end - pos);
            int distance = pos - candidate;
            // A copy at the last distance costs a command symbol only; any other distance costs a symbol and extra bits. Both costs are
            // worked out and one is selected, because which one applies cannot be predicted.
            int same = ((distance ^ r0) - 1) >> 31; // -1 when the distance is the last one, else 0
            int cost = (CommandCost & same) | ((DistanceCost + (16 * DistanceBits(distance))) & ~same);
            int score = (length * LiteralCost) - cost;
            if ((length < minMatch) | (score <= 0))
            {
                pos += 1 + ((pos - insertStart) >> FastSkipShift);
                continue;
            }

            // RFC 7932 section 4: short code 0 is the last distance; -1 says the distance is coded in full.
            block.Add(pos - insertStart, length, distance, ~same);
            r0 = distance;
            int next = pos + length;
            if (next <= last)
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

        ring[0] = r0;
        _hashedUpTo = end;
    }
}
