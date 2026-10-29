using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

// The command loop of the general meta-block writer. Codes go out through a bit accumulator held in locals, from tables that pack a
// code with its length, and literals are written a run at a time inside one block type so that the context mode is chosen once per run
// instead of once per literal.
internal sealed partial class BrotliMetaBlockWriter
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void EmitCommands(
        BrotliBitWriter w, byte[] data, int start, BrotliCommandList cmds,
        ushort[] cmdSymbols, byte[][] cmdLengths, ushort[][] cmdCodes, int[][] cmdHist,
        byte[][] litLengths, ushort[][] litCodes, int[][] litTreeHist, byte[] litMap, byte[] modes, int contextsPerType,
        byte[][] distLengths, ushort[][] distCodes, int[][] distTreeHist, byte[] distMap, byte[] distContext, ushort[] distSymbols,
        byte[] distExtraBits, int[] distExtra, int[] distOfCommand,
        BlockCoding litBlocks, BlockCoding cmdBlocks, BlockCoding distBlocks)
    {
        int nCmd = cmds.Count;
        int[] insertArray = cmds.Insert;
        int[] copyArray = cmds.Copy;
        int[] coveredArray = cmds.Covered;

        // One word per symbol: the code in the low 16 bits, its length above.
        uint[][] litTable = Pack(litLengths, litCodes);
        uint[][] cmdTable = Pack(cmdLengths, cmdCodes);
        uint[][] distTable = Pack(distLengths, distCodes);

        // An upper bound on the bits to be written, so that the output can be reserved once: the symbols' code lengths (exact, as each
        // tree's histogram is the sum of what it codes), the extra bits, and at most 64 bits for each block switch.
        long bound = 64 + (64L * (litBlocks.Split.BlockCount + cmdBlocks.Split.BlockCount + distBlocks.Split.BlockCount));
        bound += TreeBits(litTreeHist, litLengths) + TreeBits(distTreeHist, distLengths) + TreeBits(cmdHist, cmdLengths);
        for (int j = 0; j < nCmd; j++)
        {
            bound += BrotliCodes.InsertExtra[BrotliCodes.InsertCodeOf[cmdSymbols[j]]] + BrotliCodes.CopyExtra[BrotliCodes.CopyCodeOf[cmdSymbols[j]]];
        }

        for (int e = 0; e < distExtraBits.Length; e++)
        {
            bound += distExtraBits[e];
        }

        Span<byte> output = w.BeginRaw((int)((bound + 7) >> 3) + 16, out ulong acc, out int accBits);
        var bits = new RawBits(output, acc, accBits);
        long startBits = accBits;

        var wi = new RawBlockWalker(cmdBlocks);
        var wl = new RawBlockWalker(litBlocks);
        var wd = new RawBlockWalker(distBlocks);
        int litPos = start;
        for (int j = 0; j < nCmd; j++)
        {
            wi.Next(ref bits);
            int symbol = cmdSymbols[j];
            uint entry = cmdTable[wi.Type][symbol];
            bits.Put(entry & 0xFFFF, (int)(entry >> 16));
            int ic = BrotliCodes.InsertCodeOf[symbol];
            int cc = BrotliCodes.CopyCodeOf[symbol];
            int insert = insertArray[j];
            int iBits = BrotliCodes.InsertExtra[ic];
            if (iBits > 0)
            {
                bits.Put((uint)(insert - BrotliCodes.InsertBase[ic]), iBits);
            }

            int copy = copyArray[j];
            if (copy != 0 && BrotliCodes.CopyExtra[cc] > 0)
            {
                bits.Put((uint)(copy - BrotliCodes.CopyBase[cc]), BrotliCodes.CopyExtra[cc]);
            }

            int remaining = insert;
            int p = litPos;
            while (remaining > 0)
            {
                wl.Advance(ref bits);
                int chunk = Math.Min(remaining, wl.Left);
                if (chunk <= 0)
                {
                    throw new InvalidOperationException("Unreachable: a literal block ran out of room.");
                }

                wl.Left -= chunk;
                int type = wl.Type;
                if (contextsPerType == 1)
                {
                    uint[] table = litTable[litMap[type << 6]];
                    for (int i = 0; i < chunk; i++)
                    {
                        uint e = table[data[p + i]];
                        bits.Put(e & 0xFFFF, (int)(e >> 16));
                    }
                }
                else
                {
                    EmitLiterals(ref bits, data, p, chunk, modes[type], litMap.AsSpan(type << 6, 64), litTable);
                }

                p += chunk;
                remaining -= chunk;
            }

            litPos += insert + coveredArray[j];

            int d = distOfCommand[j];
            if (d >= 0)
            {
                wd.Next(ref bits);
                int tree = distMap[(wd.Type << 2) + distContext[d]];
                uint e = distTable[tree][distSymbols[d]];
                bits.Put(e & 0xFFFF, (int)(e >> 16));
                int eb = distExtraBits[d];
                if (eb > 0)
                {
                    bits.Put((uint)distExtra[d], eb);
                }
            }
        }

        long written = ((long)bits.Position * 8) + bits.AccumulatorBits - startBits;
        w.EndRaw(bits.Position, bits.Accumulator, bits.AccumulatorBits, written);
    }

    // The literals of one run inside one block type: each is coded by the tree its context maps to (RFC 7932 section 7.1).
    private static void EmitLiterals(ref RawBits bits, byte[] data, int p, int count, int mode, ReadOnlySpan<byte> map, uint[][] litTable)
    {
        int p1 = p > 0 ? data[p - 1] : 0;
        int p2 = p > 1 ? data[p - 2] : 0;
        byte[] lut0 = s_lut0;
        byte[] lut1 = s_lut1;
        byte[] lut2 = s_lut2;
        for (int i = 0; i < count; i++)
        {
            int ctx = mode switch
            {
                0 => p1 & 63,
                1 => p1 >> 2,
                2 => lut0[p1] | lut1[p2],
                _ => (lut2[p1] << 3) | lut2[p2],
            };
            int c = data[p + i];
            uint e = litTable[map[ctx]][c];
            bits.Put(e & 0xFFFF, (int)(e >> 16));
            p2 = p1;
            p1 = c;
        }
    }

    private static uint[][] Pack(byte[][] lengths, ushort[][] codes)
    {
        var result = new uint[lengths.Length][];
        for (int t = 0; t < lengths.Length; t++)
        {
            var table = new uint[lengths[t].Length];
            for (int s = 0; s < table.Length; s++)
            {
                table[s] = codes[t][s] | ((uint)lengths[t][s] << 16);
            }

            result[t] = table;
        }

        return result;
    }

    private static long TreeBits(int[][] histograms, byte[][] lengths)
    {
        long total = 0;
        for (int t = 0; t < histograms.Length; t++)
        {
            int[] h = histograms[t];
            byte[] l = lengths[t];
            for (int s = 0; s < l.Length; s++)
            {
                total += (long)h[s] * l[s];
            }
        }

        return total;
    }

    /// <summary>Follows the blocks of one category while elements are written, writing each block switch where it falls.</summary>
    private struct RawBlockWalker
    {
        private readonly BlockCoding _coding;
        private int _block;

        internal int Left;

        internal RawBlockWalker(BlockCoding coding)
        {
            _coding = coding;
            _block = 0;
            Left = coding.Split.Lengths[0];
        }

        internal readonly int Type => _coding.Split.Types[_block];

        // Called before each element: switches block when the current one is used up, then counts the element.
        internal void Next(ref RawBits bits)
        {
            Advance(ref bits);
            Left--;
        }

        // Switches to the next block if the current one is used up; the caller counts the elements it takes.
        internal void Advance(ref RawBits bits)
        {
            if (Left == 0 && _block + 1 < _coding.Split.BlockCount)
            {
                _block++;
                int sym = _coding.TypeSymbol[_block];
                bits.Put(_coding.TypeCodes[sym], _coding.TypeLengths[sym]);
                int count = _coding.Split.Lengths[_block];
                int code = BrotliEncodeTables.BlockCode(count);
                bits.Put(_coding.CountCodes[code], _coding.CountLengths[code]);
                if (BrotliCodes.BlockExtra[code] > 0)
                {
                    bits.Put((uint)(count - BrotliCodes.BlockBase[code]), BrotliCodes.BlockExtra[code]);
                }

                Left = count;
            }
        }
    }
}
