using System.Buffers.Binary;
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
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos));
            int slot = (int)((word * 0x1E35A7BDu) >> shift);
            int candidate = head[slot];
            head[slot] = pos;
            int maxDistance = (int)Math.Min(window, streamBase + pos);
            int length = 0;
            int distance = 0;
            int score = 0;
            if (r0 <= maxDistance && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos - r0)) == word)
            {
                distance = r0;
                length = CommonLengthFrom(pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - CommandCost;
            }
            else if (r1 <= maxDistance && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos - r1)) == word)
            {
                distance = r1;
                length = CommonLengthFrom(pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - RingDistanceCost;
            }
            else if (r2 <= maxDistance && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos - r2)) == word)
            {
                distance = r2;
                length = CommonLengthFrom(pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - RingDistanceCost;
            }
            else if (r3 <= maxDistance && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos - r3)) == word)
            {
                distance = r3;
                length = CommonLengthFrom(pos - distance, pos, 4, end - pos);
                score = (length * LiteralCost) - RingDistanceCost;
            }

            if (candidate >= 0 && pos - candidate <= maxDistance && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(candidate)) == word)
            {
                int l2 = CommonLengthFrom(candidate, pos, 4, end - pos);
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
                pos += 1 + ((pos - insertStart) >> 6);
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
            if (length < 16)
            {
                for (int p = pos + 1; p < Math.Min(next, last + 1); p++)
                {
                    head[(int)((BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(p)) * 0x1E35A7BDu) >> shift)] = p;
                }
            }
            else if (next <= last)
            {
                head[(int)((BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos + 1)) * 0x1E35A7BDu) >> shift)] = pos + 1;
                int a = next - 2;
                head[(int)((BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(a)) * 0x1E35A7BDu) >> shift)] = a;
                a++;
                head[(int)((BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(a)) * 0x1E35A7BDu) >> shift)] = a;
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
