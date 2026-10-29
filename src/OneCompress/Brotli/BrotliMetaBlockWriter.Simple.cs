using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

// The writer of the qualities that code a meta-block with one block type per category, no literal contexts and the plain distance
// parameters (NPOSTFIX 0, NDIRECT 0). With so little to decide, the symbols, the histograms and the literal count are gathered in one pass
// over the commands, the bit length of the body is known before it is written, and the commands go out through a bit accumulator that
// lives in locals. The bytes are the ones the general path would write for the same commands.
internal sealed partial class BrotliMetaBlockWriter
{
    private const int SimpleDistanceAlphabet = 64; // 16 + 48: NPOSTFIX 0, NDIRECT 0
    private const ushort NoDistance = 0xFFFF;

    private ushort[] _simpleSymbol = [];
    private ulong[] _simpleExtra = [];
    private byte[] _simpleExtraBits = [];
    private ushort[] _simpleDistance = [];
    private uint[] _simpleDistanceExtra = [];

    private bool UsesSimplePath => !_plan.SplitBlocks && !_plan.LiteralContexts && !_plan.SearchDistanceParameters;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void WriteSimple(BrotliBitWriter w, byte[] data, int start, int length, BrotliCommandList cmds, int[] ring, bool isLast)
    {
        int nCmd = cmds.Count;
        if (_simpleSymbol.Length < nCmd)
        {
            int size = Math.Max(nCmd, 2 * _simpleSymbol.Length);
            _simpleSymbol = new ushort[size];
            _simpleExtra = new ulong[size];
            _simpleExtraBits = new byte[size];
            _simpleDistance = new ushort[size];
            _simpleDistanceExtra = new uint[size];
        }

        ushort[] symbols = _simpleSymbol;
        ulong[] extras = _simpleExtra;
        byte[] extraBits = _simpleExtraBits;
        ushort[] distances = _simpleDistance;
        uint[] distanceExtras = _simpleDistanceExtra;
        int[] insertArray = cmds.Insert;
        int[] copyArray = cmds.Copy;
        int[] distanceArray = cmds.Distance;
        int[] coveredArray = cmds.Covered;
        bool[] dictionaryArray = cmds.IsDictionary;

        var cmdHist = new int[704];
        var distHist = new int[4 * SimpleDistanceAlphabet];
        long extraBitTotal = 0;
        int literalCount = 0;
        int r0 = ring[0], r1 = ring[1], r2 = ring[2], r3 = ring[3];

        // ---- One pass over the commands: symbols, extra bits, histograms. ----
        for (int j = 0; j < nCmd; j++)
        {
            int insert = insertArray[j];
            int copy = copyArray[j];
            literalCount += insert;
            int ic;
            int iBits;
            ulong extra;
            if (insert < 1024)
            {
                uint info = BrotliEncodeTables.InsertInfo(insert);
                ic = (int)(info & 31);
                iBits = (int)((info >> 5) & 31);
                extra = info >> 10;
            }
            else
            {
                ic = BrotliEncodeTables.InsertCode(insert);
                iBits = BrotliCodes.InsertExtra[ic];
                extra = (uint)(insert - BrotliCodes.InsertBase[ic]);
            }

            int eBits = iBits;
            int symbol;
            if (copy == 0)
            {
                // The last command of a block that ends in literals: no copy and no distance.
                symbol = BrotliEncodeTables.CommandSymbol(ic, 0, BrotliEncodeTables.CanBeImplicit(ic, 0));
                distances[j] = NoDistance;
            }
            else
            {
                int cc;
                if (copy < 1024)
                {
                    uint info = BrotliEncodeTables.CopyInfo(copy);
                    cc = (int)(info & 31);
                    int cBits = (int)((info >> 5) & 31);
                    extra |= (ulong)(info >> 10) << iBits;
                    eBits += cBits;
                }
                else
                {
                    cc = BrotliEncodeTables.CopyCode(copy);
                    extra |= (ulong)(uint)(copy - BrotliCodes.CopyBase[cc]) << iBits;
                    eBits += BrotliCodes.CopyExtra[cc];
                }
                int d = distanceArray[j];
                int code;
                if (dictionaryArray[j])
                {
                    code = -1;
                }
                else
                {
                    // RFC 7932 section 4: the short codes name the four last distances and the nearest neighbours of the last two.
                    code = d == r0 ? 0 : d == r1 ? 1 : d == r2 ? 2 : d == r3 ? 3 : BrotliDistanceCoding.ShortCode(d, [r0, r1, r2, r3]);
                    if (d != r0)
                    {
                        r3 = r2;
                        r2 = r1;
                        r1 = r0;
                        r0 = d;
                    }
                }

                bool implicitDistance = code == 0 && BrotliEncodeTables.CanBeImplicit(ic, cc);
                symbol = BrotliEncodeTables.CommandSymbol(ic, cc, implicitDistance);
                if (implicitDistance)
                {
                    distances[j] = NoDistance;
                }
                else
                {
                    int ctx = Math.Min(copy, 5) - 2;
                    int dsym;
                    uint dext = 0;
                    if (code >= 0)
                    {
                        dsym = code;
                    }
                    else
                    {
                        dsym = BrotliDistanceCoding.Symbol(d, 0, 0, out int eb, out int ex);
                        dext = (uint)ex | ((uint)eb << 26);
                        extraBitTotal += eb;
                    }

                    distances[j] = (ushort)(dsym | (ctx << 8));
                    distanceExtras[j] = dext;
                    distHist[(ctx * SimpleDistanceAlphabet) + dsym]++;
                }
            }

            symbols[j] = (ushort)symbol;
            extras[j] = extra;
            extraBits[j] = (byte)eBits;
            extraBitTotal += eBits;
            cmdHist[symbol]++;
        }

        ring[0] = r0;
        ring[1] = r1;
        ring[2] = r2;
        ring[3] = r3;

        // ---- Literal histogram: the input between the copies. ----
        int[]? litHistogram = null;
        if (literalCount > 0)
        {
            litHistogram = new int[256];
            Span<int> h0 = stackalloc int[256];
            Span<int> h1 = stackalloc int[256];
            h0.Clear();
            h1.Clear();
            int pos = start;
            for (int j = 0; j < nCmd; j++)
            {
                int insert = insertArray[j];
                int end = pos + insert;
                ReadOnlySpan<byte> run = data.AsSpan(pos, insert);
                int i = 0;
                for (; i + 1 < run.Length; i += 2)
                {
                    h0[run[i]]++;
                    h1[run[i + 1]]++;
                }

                if (i < run.Length)
                {
                    h0[run[i]]++;
                }

                pos = end + coveredArray[j];
            }

            for (int i = 0; i < 256; i++)
            {
                litHistogram[i] = h0[i] + h1[i];
            }
        }

        // ---- Trees. A single literal histogram clusters to one tree; the four distance contexts may merge. ----
        // One literal histogram is one tree.
        int[][] litTreeHist = [litHistogram ?? new int[256]];
        var distHistograms = new int[]?[4];
        for (int c = 0; c < 4; c++)
        {
            ReadOnlySpan<int> part = distHist.AsSpan(c * SimpleDistanceAlphabet, SimpleDistanceAlphabet);
            if (!part.ContainsAnyExcept(0))
            {
                continue;
            }

            distHistograms[c] = part.ToArray();
        }

        int[] distAssign = new int[4];
        int distTrees = BrotliClusterer.Cluster(distHistograms, SimpleDistanceAlphabet, 256, 8L << 16, distAssign, out int[][] distTreeHist);
        var distMap = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            distMap[i] = (byte)distAssign[i];
        }

        var litLengths = BuildLengthsFor(litTreeHist, 256);
        var cmdHistograms = new[] { cmdHist };
        var cmdLengths = BuildLengthsFor(cmdHistograms, 704);
        var distLengths = BuildLengthsFor(distTreeHist, SimpleDistanceAlphabet);
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

        BrotliPrefixCodeWriter.Write(w, cmdLengths[0], OnlySymbol(cmdHist));
        for (int i = 0; i < distLengths.Length; i++)
        {
            BrotliPrefixCodeWriter.Write(w, distLengths[i], OnlySymbol(distTreeHist[i]));
        }

        // ---- Code tables with the length above the code, so one load gives both. ----
        var litTable = new uint[256];
        long bodyBits = extraBitTotal;
        for (int s = 0; s < 256; s++)
        {
            litTable[s] = litCodes[0][s] | ((uint)litLengths[0][s] << 16);
            bodyBits += (long)litTreeHist[0][s] * litLengths[0][s];
        }

        var cmdTable = new uint[704];
        for (int s = 0; s < 704; s++)
        {
            cmdTable[s] = cmdCodes[0][s] | ((uint)cmdLengths[0][s] << 16);
            bodyBits += (long)cmdHist[s] * cmdLengths[0][s];
        }

        var distTable = new uint[4 * 256];
        for (int c = 0; c < 4; c++)
        {
            int tree = distMap[c];
            for (int s = 0; s < SimpleDistanceAlphabet; s++)
            {
                distTable[(c << 8) + s] = distCodes[tree][s] | ((uint)distLengths[tree][s] << 16);
                bodyBits += (long)distHist[(c * SimpleDistanceAlphabet) + s] * distLengths[tree][s];
            }
        }

        // ---- The commands. ----
        Span<byte> output = w.BeginRaw((int)((bodyBits + 7) >> 3) + 16, out ulong acc, out int accBits);
        var bits = new RawBits(output, acc, accBits);
        int litPos = start;
        for (int j = 0; j < nCmd; j++)
        {
            uint entry = cmdTable[symbols[j]];
            bits.Put(entry & 0xFFFF, (int)(entry >> 16));
            int eb = extraBits[j];
            if (eb > 0)
            {
                ulong extra = extras[j];
                if (eb <= 32)
                {
                    bits.Put((uint)extra, eb);
                }
                else
                {
                    bits.Put((uint)extra, 32);
                    bits.Put((uint)(extra >> 32), eb - 32);
                }
            }

            int litEnd = litPos + insertArray[j];
            ReadOnlySpan<byte> run = data.AsSpan(litPos, litEnd - litPos);
            int i = 0;
            for (; i + 1 < run.Length; i += 2)
            {
                // Two codes of at most 15 bits fit beside the fewer than 32 bits already held.
                uint a = litTable[run[i]];
                uint b = litTable[run[i + 1]];
                bits.PutPair(a, b);
            }

            if (i < run.Length)
            {
                uint a = litTable[run[i]];
                bits.Put(a & 0xFFFF, (int)(a >> 16));
            }

            litPos = litEnd + coveredArray[j];
            ushort dd = distances[j];
            if (dd != NoDistance)
            {
                uint d = distTable[dd];
                bits.Put(d & 0xFFFF, (int)(d >> 16));
                uint dx = distanceExtras[j];
                int dBits = (int)(dx >> 26);
                if (dBits > 0)
                {
                    bits.Put(dx & 0x3FFFFFF, dBits);
                }
            }
        }

        w.EndRaw(bits.Position, bits.Accumulator, bits.AccumulatorBits, bodyBits);
        if (isLast)
        {
            w.AlignToByte();
        }
    }

    // A bit accumulator over a span, kept in a struct so that the writing loop holds it in registers. It has the same layout as
    // BrotliBitWriter.Write: fewer than 32 bits wait in the accumulator between calls and 32 at a time are stored little endian.
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
        internal void Put(uint value, int count)
        {
            _accumulator |= (ulong)value << _bits;
            _bits += count;
            if (_bits >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_output.Slice(_position), (uint)_accumulator);
                _position += 4;
                _accumulator >>= 32;
                _bits -= 32;
            }
        }

        // Two table entries, each a code in the low 16 bits and its length above them.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PutPair(uint first, uint second)
        {
            _accumulator |= (ulong)(first & 0xFFFF) << _bits;
            _bits += (int)(first >> 16);
            _accumulator |= (ulong)(second & 0xFFFF) << _bits;
            _bits += (int)(second >> 16);
            if (_bits >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_output.Slice(_position), (uint)_accumulator);
                _position += 4;
                _accumulator >>= 32;
                _bits -= 32;
            }
        }
    }
}
