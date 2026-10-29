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
    private static void Insert(int[] head, byte[] buffer, int p, int shift)
    {
        uint w = BrotliEncRaw.Load32(buffer, p);
        int slot = (int)((w * 0x1E35A7BDu) >> shift) << 1;
        BrotliEncRaw.Slot(head, slot) = p;
        BrotliEncRaw.Slot(head, slot + 1) = (int)w;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ParseFast(int start, int end)
    {
        byte[] buffer = _buffer;
        int[] head = _head;
        int shift = _hashShift;
        int window = _window;
        long streamBase = _streamBase;
        int r0 = _ring[0], r1 = _ring[1], r2 = _ring[2], r3 = _ring[3];
        int pos = start;
        int insertStart = start;
        int last = end - 4; // the last position with four bytes inside the block
        while (pos <= last)
        {
            uint word = BrotliEncRaw.Load32(buffer, pos);
            int slot = (int)((word * 0x1E35A7BDu) >> shift) << 1;
            ref int cell = ref BrotliEncRaw.Slot(head, slot);
            int candidate = cell;
            uint candidateWord = (uint)BrotliEncRaw.Slot(head, slot + 1);
            cell = pos;
            BrotliEncRaw.Slot(head, slot + 1) = (int)word;
            int maxDistance = (int)Math.Min(window, streamBase + pos);
            int length = 0;
            int distance = 0;
            int score = 0;
            if (r0 <= maxDistance && BrotliEncRaw.Load32(buffer, pos - r0) == word)
            {
                distance = r0;
                length = BrotliEncRaw.CommonLength(buffer, pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - CommandCost;
            }
            else if (r1 <= maxDistance && BrotliEncRaw.Load32(buffer, pos - r1) == word)
            {
                distance = r1;
                length = BrotliEncRaw.CommonLength(buffer, pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - RingDistanceCost;
            }
            else if (r2 <= maxDistance && BrotliEncRaw.Load32(buffer, pos - r2) == word)
            {
                distance = r2;
                length = BrotliEncRaw.CommonLength(buffer, pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - RingDistanceCost;
            }
            else if (r3 <= maxDistance && BrotliEncRaw.Load32(buffer, pos - r3) == word)
            {
                distance = r3;
                length = BrotliEncRaw.CommonLength(buffer, pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - RingDistanceCost;
            }

            if (candidateWord == word && candidate >= 0 && pos - candidate <= maxDistance)
            {
                int l2 = BrotliEncRaw.CommonLength(buffer, candidate, pos, 4, end - pos);
                int d2 = pos - candidate;
                int s2 = (l2 * LiteralCost) - DistanceCost - (16 * DistanceBits(d2));
                if (s2 > score)
                {
                    length = l2;
                    distance = d2;
                    score = s2;
                }
            }

            if (score <= 0)
            {
                // Incompressible stretches are crossed faster the longer they have gone without a copy.
                pos += 1 + ((pos - insertStart) >> _plan.SkipShift);
                continue;
            }

            _commands.Add(pos - insertStart, length, distance, length, false);
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
                    Insert(head, buffer, p, shift);
                }
            }
            else if (next <= last)
            {
                Insert(head, buffer, pos + 1, shift);
                int a = next - 2;
                Insert(head, buffer, a, shift);
                a++;
                Insert(head, buffer, a, shift);
            }

            pos = next;
            insertStart = pos;
        }

        if (insertStart < end)
        {
            _commands.Add(end - insertStart, 0, 0, 0, false);
        }

        _hashedUpTo = end;
    }
}
