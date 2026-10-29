using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

// The meta-block writer of the fast qualities (0 to 2): the parse hands over the commands with their symbols, extra bits and histograms
// already computed (BrotliFastBlock), so what is left is the literal histogram, the prefix codes and the bits.
internal sealed partial class BrotliMetaBlockWriter
{
    /// <summary>Writes the meta-block for <paramref name="length"/> bytes at <paramref name="start"/>, or an uncompressed one if that is smaller.</summary>
    internal static bool Write(BrotliBitWriter w, byte[] data, int start, int length, BrotliFastBlock fb, int[] ring, bool isLast)
    {
        var mark = w.GetMark();
        WriteFast(w, data, start, length, fb, isLast);
        long compressedBits = w.BitsWritten - mark.Bits;
        long rawBits = 4 + (4 * 4) + 8L * length + 7 + (isLast ? 8 : 0);
        if (compressedBits > rawBits)
        {
            w.Rewind(mark);
            WriteUncompressed(w, data.AsSpan(start, length), isLast);
            return false;
        }

        fb.RingAfter.CopyTo(ring, 0);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WriteFast(BrotliBitWriter w, byte[] data, int start, int length, BrotliFastBlock fb, bool isLast)
    {
        BrotliFastCommand[] cmds = fb.Commands;
        int nCmd = fb.Count;
        int literalCount = fb.LiteralCount;
        // Runs of literals are long (on average four a command or more) or short; each gets the loop that suits it. Neither changes a bit written.
        bool longRuns = literalCount >= 4 * nCmd;
        // ---- Literal histogram: the input between the copies. ----
        int[]? litHistogram = null;
        if (literalCount > 0)
        {
            litHistogram = new int[256];
            // Four tables side by side, so that a byte repeated inside a run does not wait on the previous increment of its counter.
            int[] counts = new int[4 * 256];
            int pos = start;
            for (int j = 0; j < nCmd; j++)
            {
                ref BrotliFastCommand c = ref BrotliEncRaw.Command(cmds, j);
                int insert = c.Insert;
                int end = pos + insert;
                if (longRuns)
                {
                    // The first eight literals of a run are always counted, those past its end with an increment of 0 (a mask, not a branch):
                    // the length of a run is the least predictable thing in this loop. The bytes read past the run's end fall on the next bytes of
                    // the block or, at the very end, on the margin of the buffer. Four tables side by side keep a byte repeated inside a run
                    // from waiting on the previous increment of its counter.
                    BrotliEncRaw.Count(counts, BrotliEncRaw.At(data, pos)) += (0 - insert) >> 31 & 1;
                    BrotliEncRaw.Count(counts, 256 + BrotliEncRaw.At(data, pos + 1)) += (1 - insert) >> 31 & 1;
                    BrotliEncRaw.Count(counts, 512 + BrotliEncRaw.At(data, pos + 2)) += (2 - insert) >> 31 & 1;
                    BrotliEncRaw.Count(counts, 768 + BrotliEncRaw.At(data, pos + 3)) += (3 - insert) >> 31 & 1;
                    BrotliEncRaw.Count(counts, BrotliEncRaw.At(data, pos + 4)) += (4 - insert) >> 31 & 1;
                    BrotliEncRaw.Count(counts, 256 + BrotliEncRaw.At(data, pos + 5)) += (5 - insert) >> 31 & 1;
                    BrotliEncRaw.Count(counts, 512 + BrotliEncRaw.At(data, pos + 6)) += (6 - insert) >> 31 & 1;
                    BrotliEncRaw.Count(counts, 768 + BrotliEncRaw.At(data, pos + 7)) += (7 - insert) >> 31 & 1;
                    if (insert > 8)
                    {
                        int i = pos + 8;
                        for (; i + 4 <= end; i += 4)
                        {
                            BrotliEncRaw.Count(counts, BrotliEncRaw.At(data, i))++;
                            BrotliEncRaw.Count(counts, 256 + BrotliEncRaw.At(data, i + 1))++;
                            BrotliEncRaw.Count(counts, 512 + BrotliEncRaw.At(data, i + 2))++;
                            BrotliEncRaw.Count(counts, 768 + BrotliEncRaw.At(data, i + 3))++;
                        }

                        for (; i < end; i++)
                        {
                            BrotliEncRaw.Count(counts, BrotliEncRaw.At(data, i))++;
                        }
                    }
                }
                else
                {
                    // Runs are mostly empty or a byte or two: a loop over each costs less than eight counts.
                    for (int i = pos; i < end; i++)
                    {
                        BrotliEncRaw.Count(counts, BrotliEncRaw.At(data, i))++;
                    }
                }

                pos = end + c.Copy;
            }

            for (int i = 0; i < 256; i++)
            {
                litHistogram[i] = counts[i] + counts[256 + i] + counts[512 + i] + counts[768 + i];
            }
        }

        // ---- Trees. A single literal histogram clusters to one tree; the four distance contexts may merge. ----
        // One literal histogram is one tree.
        int[][] litTreeHist = [litHistogram ?? new int[256]];
        var distHistograms = new int[]?[4];
        for (int c = 0; c < 4; c++)
        {
            ReadOnlySpan<int> part = fb.DistanceHistogram.AsSpan(c * BrotliFastBlock.DistanceAlphabet, BrotliFastBlock.DistanceAlphabet);
            if (!part.ContainsAnyExcept(0))
            {
                continue;
            }

            distHistograms[c] = part.ToArray();
        }

        int[] distAssign = new int[4];
        int distTrees = BrotliClusterer.Cluster(distHistograms, BrotliFastBlock.DistanceAlphabet, 256, 8L << 16, distAssign, out int[][] distTreeHist);
        var distMap = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            distMap[i] = (byte)distAssign[i];
        }

        var litLengths = BuildLengthsFor(litTreeHist, 256);
        var cmdHistograms = new[] { fb.CommandHistogram };
        var cmdLengths = BuildLengthsFor(cmdHistograms, 704);
        var distLengths = BuildLengthsFor(distTreeHist, BrotliFastBlock.DistanceAlphabet);
        var litCodes = AssignFor(litLengths);
        var cmdCodes = AssignFor(cmdLengths);
        var distCodes = AssignFor(distLengths);

        // ---- Header (RFC 7932 section 9.2). One block type of each kind, one literal block type with mode 0. ----
        WriteLengthHeader(w, length, isLast);
        if (!isLast)
        {
            w.Write(0, 1); // ISUNCOMPRESSED
        }

        w.Write(0, 1); // NBLTYPESL = 1
        w.Write(0, 1); // NBLTYPESI = 1
        w.Write(0, 1); // NBLTYPESD = 1
        w.Write(0, 2); // NPOSTFIX
        w.Write(0, 4); // NDIRECT
        w.Write(0, 2); // context mode of the one literal block type
        WriteVariableCount(w, 1);

        WriteVariableCount(w, distTrees);
        if (distTrees >= 2)
        {
            WriteContextMap(w, distMap, distTrees);
        }

        for (int i = 0; i < litLengths.Length; i++)
        {
            BrotliPrefixCodeWriter.Write(w, litLengths[i], OnlySymbol(litTreeHist[i]));
        }

        BrotliPrefixCodeWriter.Write(w, cmdLengths[0], OnlySymbol(fb.CommandHistogram));
        for (int i = 0; i < distLengths.Length; i++)
        {
            BrotliPrefixCodeWriter.Write(w, distLengths[i], OnlySymbol(distTreeHist[i]));
        }

        // ---- Code tables with the length above the code, so one load gives both. ----
        var litTable = new uint[256];
        long bodyBits = fb.ExtraBitTotal;
        for (int s = 0; s < 256; s++)
        {
            litTable[s] = litCodes[0][s] | ((uint)litLengths[0][s] << 16);
            bodyBits += (long)litTreeHist[0][s] * litLengths[0][s];
        }

        var cmdTable = new uint[704];
        for (int s = 0; s < 704; s++)
        {
            cmdTable[s] = cmdCodes[0][s] | ((uint)cmdLengths[0][s] << 16);
            bodyBits += (long)fb.CommandHistogram[s] * cmdLengths[0][s];
        }

        var distTable = new uint[4 * 256];
        for (int c = 0; c < 4; c++)
        {
            int tree = distMap[c];
            for (int s = 0; s < BrotliFastBlock.DistanceAlphabet; s++)
            {
                distTable[(c << 8) + s] = distCodes[tree][s] | ((uint)distLengths[tree][s] << 16);
                bodyBits += (long)fb.DistanceHistogram[(c * BrotliFastBlock.DistanceAlphabet) + s] * distLengths[tree][s];
            }
        }

        // ---- The commands. ----
        Span<byte> output = w.BeginRaw((int)((bodyBits + 7) >> 3) + 16, out ulong acc, out int accBits);
        var bits = new RawBits(output, acc, accBits);
        int litPos = start;
        for (int j = 0; j < nCmd; j++)
        {
            ref BrotliFastCommand c = ref BrotliEncRaw.Command(cmds, j);
            uint entry = cmdTable[c.Symbol];
            int eb = c.ExtraBits;
            ulong extra = c.Extra;
            if (eb <= 32)
            {
                bits.PutCodeAndExtra(entry, (uint)extra, eb);
            }
            else
            {
                bits.PutCodeAndExtra(entry, (uint)extra, 32);
                bits.Put((uint)(extra >> 32), eb - 32);
            }

            int count = c.Insert;
            int litEnd = litPos + count;
            if (longRuns)
            {
                // The usual run is short, and its length is the least predictable thing in the loop, so the first eight literals are always
                // looked up and written, those past the run's end with a length of 0 (a mask, not a branch). The code table has 256 entries
                // and the input is read only inside the run for the bits written; the extra reads fall on the next bytes of the block or,
                // at the very end, on the margin of the buffer.
                uint a0 = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos)) & (uint)((0 - count) >> 31);
                uint a1 = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 1)) & (uint)((1 - count) >> 31);
                uint a2 = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 2)) & (uint)((2 - count) >> 31);
                uint a3 = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 3)) & (uint)((3 - count) >> 31);
                uint a4 = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 4)) & (uint)((4 - count) >> 31);
                uint a5 = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 5)) & (uint)((5 - count) >> 31);
                uint a6 = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 6)) & (uint)((6 - count) >> 31);
                uint a7 = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 7)) & (uint)((7 - count) >> 31);
                bits.PutPair(a0, a1);
                bits.PutPair(a2, a3);
                bits.PutPair(a4, a5);
                bits.PutPair(a6, a7);
                if (count > 8)
                {
                    int i = litPos + 8;
                    for (; i + 1 < litEnd; i += 2)
                    {
                        // Two codes of at most 15 bits fit beside the fewer than 8 bits already held.
                        bits.PutPair(BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, i)), BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, i + 1)));
                    }

                    if (i < litEnd)
                    {
                        uint a = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, i));
                        bits.Put(a & 0xFFFF, (int)(a >> 16));
                    }
                }
            }
            else if (count <= 4)
            {
                // The usual short run: four lookups whether or not the run is that long, the ones past its end with a length of 0. The
                // code table has 256 entries and the input is read only inside the run for the bits written; bytes past it are read from
                // the next bytes of the block or, at the very end, from the margin of the buffer.
                uint a0 = count > 0 ? BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos)) : 0;
                uint a1 = count > 1 ? BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 1)) : 0;
                uint a2 = count > 2 ? BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 2)) : 0;
                uint a3 = count > 3 ? BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, litPos + 3)) : 0;
                if (count > 0)
                {
                    bits.PutPair(a0, a1);
                    bits.PutPair(a2, a3);
                }
            }
            else
            {
                int i = litPos;
                for (; i + 1 < litEnd; i += 2)
                {
                    bits.PutPair(BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, i)), BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, i + 1)));
                }

                if (i < litEnd)
                {
                    uint a = BrotliEncRaw.Entry(litTable, BrotliEncRaw.At(data, i));
                    bits.Put(a & 0xFFFF, (int)(a >> 16));
                }
            }

            litPos = litEnd + c.Copy;
            uint d = distTable[c.Distance];
            uint dx = c.DistanceExtra;
            bits.PutCodeAndExtra(d, dx & 0x3FFFFFF, (int)(dx >> 26));
        }

        w.EndRaw(bits.Position, bits.Accumulator, bits.AccumulatorBits, bodyBits);
        if (isLast)
        {
            w.AlignToByte();
        }
    }

    // A bit accumulator over a span, kept in a struct so that the writing loop holds it in registers. Every call ends by storing the
    // accumulator as one little-endian 64-bit word at the current byte and moving on by the whole bytes it filled, so there is no branch on
    // whether a store is due; fewer than 8 bits wait in the accumulator between calls. The bit stream is the one BrotliBitWriter.Write
    // produces. The span must have 8 bytes beyond the last byte the stream ends in, which BeginRaw provides.
    private ref struct RawBits
    {
        private readonly Span<byte> _output;
        private ulong _accumulator;
        private int _bits;
        private int _position;

        internal RawBits(Span<byte> output, ulong accumulator, int bits)
        {
            _output = output;
            _accumulator = accumulator;
            _bits = bits;
            _position = 0;
        }

        internal readonly int Position => _position;

        internal readonly ulong Accumulator => _accumulator;

        internal readonly int AccumulatorBits => _bits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Store()
        {
            BrotliEncRaw.Store64(_output, _position, _accumulator);
            int whole = _bits >> 3;
            _position += whole;
            _accumulator >>= whole << 3;
            _bits &= 7;
        }

        /// <summary>Appends the low <paramref name="count"/> bits of <paramref name="value"/>; the count is at most 32.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Put(uint value, int count)
        {
            _accumulator |= (ulong)value << _bits;
            _bits += count;
            Store();
        }

        // A prefix code (low 16 bits of the entry, its length above them) followed by up to 32 extra bits: 7 + 15 + 32 bits fit the word.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PutCodeAndExtra(uint entry, uint extra, int extraCount)
        {
            _accumulator |= (ulong)(entry & 0xFFFF) << _bits;
            _bits += (int)(entry >> 16);
            _accumulator |= (ulong)extra << _bits;
            _bits += extraCount;
            Store();
        }

        // Two table entries, each a code in the low 16 bits and its length above them: 7 + 15 + 15 bits.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PutPair(uint first, uint second)
        {
            _accumulator |= (ulong)(first & 0xFFFF) << _bits;
            _bits += (int)(first >> 16);
            _accumulator |= (ulong)(second & 0xFFFF) << _bits;
            _bits += (int)(second >> 16);
            Store();
        }
    }
}
