using System.Numerics;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Turns the commands of one meta-block into bits: chooses the distance parameters, splits literals, commands and distances into block
/// types, clusters the literal and distance contexts onto prefix codes, builds the codes and writes the meta-block (RFC 7932 section 9.2).
/// </summary>
/// <remarks>
/// A meta-block that would not shrink is written as an uncompressed one (a last meta-block cannot be uncompressed, so it is followed by an
/// empty last meta-block).
/// </remarks>
internal sealed partial class BrotliMetaBlockWriter
{
    private static readonly byte[] s_lut0 = BrotliContextTables.Lut0.ToArray();
    private static readonly byte[] s_lut1 = BrotliContextTables.Lut1.ToArray();
    private static readonly byte[] s_lut2 = BrotliContextTables.Lut2.ToArray();

    // The NPOSTFIX / NDIRECT pairs tried when the plan searches.
    private static readonly (int Postfix, int Direct)[] s_distanceParameters = [(0, 0), (0, 8), (1, 0), (1, 8), (2, 0), (2, 12)];

    private readonly BrotliQualityPlan _plan;

    internal BrotliMetaBlockWriter(BrotliQualityPlan plan) => _plan = plan;

    /// <summary>The literal context of the two bytes before a literal under a context mode (RFC 7932 section 7.1).</summary>
    internal static int LiteralContext(int mode, int p1, int p2) => mode switch
    {
        0 => p1 & 63,
        1 => p1 >> 2,
        2 => s_lut0[p1] | s_lut1[p2],
        _ => (s_lut2[p1] << 3) | s_lut2[p2],
    };

    /// <summary>Writes the meta-block header of an uncompressed meta-block and its bytes.</summary>
    internal static void WriteUncompressed(BrotliBitWriter w, ReadOnlySpan<byte> bytes, bool isLast)
    {
        WriteLengthHeader(w, bytes.Length, false);
        w.Write(1, 1); // ISUNCOMPRESSED
        w.AlignToByte();
        w.WriteBytes(bytes);
        if (isLast)
        {
            WriteEmptyLast(w);
        }
    }

    /// <summary>Writes an empty last meta-block (ISLAST and ISLASTEMPTY) and pads to a byte.</summary>
    internal static void WriteEmptyLast(BrotliBitWriter w)
    {
        w.Write(3, 2);
        w.AlignToByte();
    }

    /// <summary>Writes the empty metadata meta-block that ends a flush at a byte boundary (RFC 7932 section 9.2).</summary>
    internal static void WriteFlushMarker(BrotliBitWriter w)
    {
        // ISLAST = 0, MNIBBLES = 0 (code 3), reserved bit 0, MSKIPBYTES = 0, then zero padding.
        w.Write(0, 1);
        w.Write(3, 2);
        w.Write(0, 1);
        w.Write(0, 2);
        w.AlignToByte();
    }

    // ISLAST (unless isLast), MNIBBLES, MLEN - 1 and, for a block that is not last, left to the caller: ISUNCOMPRESSED.
    private static void WriteLengthHeader(BrotliBitWriter w, int length, bool isLast)
    {
        w.Write(isLast ? 1u : 0u, 1);
        if (isLast)
        {
            w.Write(0, 1); // ISLASTEMPTY
        }

        uint v = (uint)(length - 1);
        int nibbles = v < (1u << 16) ? 4 : v < (1u << 20) ? 5 : 6;
        w.Write((uint)(nibbles - 4), 2);
        w.Write(v, 4 * nibbles);
    }

    /// <summary>Writes the meta-block for <paramref name="length"/> bytes at <paramref name="start"/> of <paramref name="data"/>.</summary>
    /// <returns>True if it was written compressed, in which case <paramref name="ring"/> holds the last distances after it.</returns>
    internal bool Write(BrotliBitWriter w, byte[] data, int start, int length, BrotliCommandList cmds, int[] ring, bool isLast)
    {
        var mark = w.GetMark();
        int[] ringAfter = (int[])ring.Clone();
        WriteCompressed(w, data, start, length, cmds, ringAfter, isLast);
        long compressedBits = w.BitsWritten - mark.Bits;
        long rawBits = 4 + (4 * 4) + 8L * length + 7 + (isLast ? 8 : 0);
        if (compressedBits > rawBits)
        {
            w.Rewind(mark);
            WriteUncompressed(w, data.AsSpan(start, length), isLast);
            return false;
        }

        ringAfter.CopyTo(ring, 0);
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void WriteCompressed(BrotliBitWriter w, byte[] data, int start, int length, BrotliCommandList cmds, int[] ring, bool isLast)
    {
        if (UsesSimplePath)
        {
            WriteSimple(w, data, start, length, cmds, ring, isLast);
            return;
        }

        int nCmd = cmds.Count;
        // ---- Distance codes. The short codes depend only on the ring of last distances. ----
        var shortCode = new sbyte[nCmd];
        int explicitCount = 0;
        int literalCount = 0;
        {
            Span<int> r = stackalloc int[4];
            ring.AsSpan(0, 4).CopyTo(r);
            for (int j = 0; j < nCmd; j++)
            {
                literalCount += cmds.Insert[j];
                if (cmds.Copy[j] == 0)
                {
                    shortCode[j] = -1;
                    continue;
                }

                int d = cmds.Distance[j];
                if (cmds.IsDictionary[j])
                {
                    shortCode[j] = -1;
                    explicitCount++;
                    continue;
                }

                int code = BrotliDistanceCoding.ShortCode(d, r);
                shortCode[j] = (sbyte)code;
                if (d != r[0])
                {
                    r[3] = r[2];
                    r[2] = r[1];
                    r[1] = r[0];
                    r[0] = d;
                }

                if (!(code == 0 && BrotliEncodeTables.CanBeImplicit(BrotliEncodeTables.InsertCode(cmds.Insert[j]), BrotliEncodeTables.CopyCode(cmds.Copy[j]))))
                {
                    explicitCount++;
                }
            }

            r.CopyTo(ring.AsSpan(0, 4));
        }

        // Commands with an explicit distance symbol, and the symbols for the best NPOSTFIX / NDIRECT.
        int postfix = 0;
        int direct = 0;
        if (_plan.SearchDistanceParameters && explicitCount > 0)
        {
            long best = long.MaxValue;
            var hist = new int[BrotliDistanceCoding.AlphabetSize(3, 15 << 3)];
            foreach ((int p, int d) in s_distanceParameters)
            {
                Array.Clear(hist);
                long extraBitsTotal = 0;
                for (int j = 0; j < nCmd; j++)
                {
                    if (cmds.Copy[j] == 0 || !IsExplicit(cmds, shortCode, j))
                    {
                        continue;
                    }

                    if (shortCode[j] >= 0)
                    {
                        hist[shortCode[j]]++;
                    }
                    else
                    {
                        hist[BrotliDistanceCoding.Symbol(cmds.Distance[j], p, d, out int eb, out _)]++;
                        extraBitsTotal += eb;
                    }
                }

                long cost = BrotliEncodeTables.EntropyCost(hist.AsSpan(0, BrotliDistanceCoding.AlphabetSize(p, d)), out _) + (extraBitsTotal << 16);
                if (cost < best)
                {
                    best = cost;
                    postfix = p;
                    direct = d;
                }
            }
        }

        int distanceAlphabet = BrotliDistanceCoding.AlphabetSize(postfix, direct);
        // ---- The symbol streams. ----
        var cmdSymbols = new ushort[nCmd];
        var distSymbols = new ushort[explicitCount];
        var distExtraBits = new byte[explicitCount];
        var distExtra = new int[explicitCount];
        var distContext = new byte[explicitCount];
        var distOfCommand = new int[nCmd];
        {
            int e = 0;
            for (int j = 0; j < nCmd; j++)
            {
                int ic = BrotliEncodeTables.InsertCode(cmds.Insert[j]);
                bool trailing = cmds.Copy[j] == 0;
                int cc = trailing ? 0 : BrotliEncodeTables.CopyCode(cmds.Copy[j]);
                bool implicitDistance = trailing ? BrotliEncodeTables.CanBeImplicit(ic, cc) : shortCode[j] == 0 && BrotliEncodeTables.CanBeImplicit(ic, cc);
                cmdSymbols[j] = (ushort)BrotliEncodeTables.CommandSymbol(ic, cc, implicitDistance);
                distOfCommand[j] = -1;
                if (trailing || implicitDistance)
                {
                    continue;
                }

                distOfCommand[j] = e;
                if (shortCode[j] >= 0)
                {
                    distSymbols[e] = (ushort)shortCode[j];
                }
                else
                {
                    distSymbols[e] = (ushort)BrotliDistanceCoding.Symbol(cmds.Distance[j], postfix, direct, out int eb, out int ex);
                    distExtraBits[e] = (byte)eb;
                    distExtra[e] = ex;
                }

                distContext[e] = (byte)(Math.Min(cmds.Copy[j], 5) - 2);
                e++;
            }
        }
        // ---- The literals with the two bytes before each. ----
        // The block splitter and the search for a context mode want the literals as sequences; the other qualities read the input directly.
        byte[] lit = [];
        byte[] lp1 = [];
        byte[] lp2 = [];
        if (_plan.SplitBlocks)
        {
            lit = new byte[literalCount];
            lp1 = new byte[literalCount];
            lp2 = new byte[literalCount];
            int pos = start;
            int k = 0;
            for (int j = 0; j < nCmd; j++)
            {
                for (int i = 0; i < cmds.Insert[j]; i++, pos++, k++)
                {
                    lit[k] = data[pos];
                    lp1[k] = pos > 0 ? data[pos - 1] : (byte)0;
                    lp2[k] = pos > 1 ? data[pos - 2] : (byte)0;
                }

                pos += cmds.Covered[j];
            }
        }
        // ---- Block splits. ----
        BrotliBlockSplit litSplit = _plan.SplitBlocks ? BrotliBlockSplitter.Split(ToSymbols(lit), 256, _plan.LiteralSegmentMin, _plan.LiteralSegmentMax, _plan.SplitPairwiseLimit) : BrotliBlockSplit.Single(literalCount);
        BrotliBlockSplit cmdSplit = _plan.SplitBlocks && _plan.CommandSegmentMax > 0 ? BrotliBlockSplitter.Split(cmdSymbols, 704, 256, _plan.CommandSegmentMax, _plan.CommandPairwiseLimit) : BrotliBlockSplit.Single(nCmd);
        BrotliBlockSplit distSplit = _plan.SplitBlocks && _plan.CommandSegmentMax > 0 ? BrotliBlockSplitter.Split(distSymbols, distanceAlphabet, 256, _plan.CommandSegmentMax, _plan.CommandPairwiseLimit) : BrotliBlockSplit.Single(explicitCount);
        // ---- Literal contexts: a mode per block type, histograms per (type, context), clustered onto trees. ----
        int contextsPerType = _plan.LiteralContexts ? 64 : 1;
        var modes = ChooseContextModes(litSplit, lit, lp1, lp2);
        var litHist = new int[]?[litSplit.TypeCount * 64];
        if (_plan.SplitBlocks)
        {
            int k = 0;
            for (int b = 0; b < litSplit.BlockCount; b++)
            {
                int t = litSplit.Types[b];
                int mode = modes[t];
                for (int i = 0; i < litSplit.Lengths[b]; i++, k++)
                {
                    int ctx = contextsPerType == 1 ? 0 : LiteralContext(mode, lp1[k], lp2[k]);
                    int[] h = litHist[(t << 6) + ctx] ??= new int[256];
                    h[lit[k]]++;
                }
            }
        }
        else
        {
            // One block type: the literals are the input between the copies.
            int pos = start;
            int mode = modes[0];
            for (int j = 0; j < nCmd; j++)
            {
                for (int i = 0; i < cmds.Insert[j]; i++, pos++)
                {
                    int ctx = contextsPerType == 1 ? 0 : LiteralContext(mode, pos > 0 ? data[pos - 1] : 0, pos > 1 ? data[pos - 2] : 0);
                    int[] h = litHist[ctx] ??= new int[256];
                    h[data[pos]]++;
                }

                pos += cmds.Covered[j];
            }
        }

        int[] litAssign = new int[litHist.Length];
        int litTrees = BrotliClusterer.Cluster(litHist, 256, 256, 8L << 16, litAssign, out int[][] litTreeHist);
        // With no contexts only entry 0 of each type was filled; the others follow it.
        var litMap = new byte[litSplit.TypeCount * 64];
        for (int t = 0; t < litSplit.TypeCount; t++)
        {
            for (int c = 0; c < 64; c++)
            {
                litMap[(t << 6) + c] = (byte)litAssign[(t << 6) + (contextsPerType == 1 ? 0 : c)];
            }
        }
        // ---- Distance contexts. ----
        var distHist = new int[]?[distSplit.TypeCount * 4];
        {
            int e = 0;
            for (int b = 0; b < distSplit.BlockCount; b++)
            {
                for (int i = 0; i < distSplit.Lengths[b]; i++, e++)
                {
                    int[] h = distHist[(distSplit.Types[b] << 2) + distContext[e]] ??= new int[distanceAlphabet];
                    h[distSymbols[e]]++;
                }
            }
        }

        int[] distAssign = new int[distHist.Length];
        int distTrees = BrotliClusterer.Cluster(distHist, distanceAlphabet, 256, 8L << 16, distAssign, out int[][] distTreeHist);
        var distMap = new byte[distHist.Length];
        for (int i = 0; i < distMap.Length; i++)
        {
            distMap[i] = (byte)distAssign[i];
        }
        // ---- Command trees: one per block type. ----
        var cmdHist = new int[cmdSplit.TypeCount][];
        for (int t = 0; t < cmdSplit.TypeCount; t++)
        {
            cmdHist[t] = new int[704];
        }

        {
            int j = 0;
            for (int b = 0; b < cmdSplit.BlockCount; b++)
            {
                for (int i = 0; i < cmdSplit.Lengths[b]; i++, j++)
                {
                    cmdHist[cmdSplit.Types[b]][cmdSymbols[j]]++;
                }
            }
        }
        // ---- Prefix codes. ----
        var litLengths = BuildLengthsFor(litTreeHist, 256);
        var cmdLengths = BuildLengthsFor(cmdHist, 704);
        var distLengths = BuildLengthsFor(distTreeHist, distanceAlphabet);
        var litCodes = AssignFor(litLengths);
        var cmdCodes = AssignFor(cmdLengths);
        var distCodes = AssignFor(distLengths);

        var litBlocks = new BlockCoding(litSplit);
        var cmdBlocks = new BlockCoding(cmdSplit);
        var distBlocks = new BlockCoding(distSplit);
        // ---- Write the header. ----
        WriteLengthHeader(w, length, isLast);
        if (!isLast)
        {
            w.Write(0, 1); // ISUNCOMPRESSED
        }

        litBlocks.WriteHeader(w);
        cmdBlocks.WriteHeader(w);
        distBlocks.WriteHeader(w);
        w.Write((uint)postfix, 2);
        w.Write((uint)(direct >> postfix), 4);
        for (int t = 0; t < litSplit.TypeCount; t++)
        {
            w.Write((uint)modes[t], 2);
        }

        WriteVariableCount(w, litTrees);
        if (litTrees >= 2)
        {
            WriteContextMap(w, litMap, litTrees);
        }

        WriteVariableCount(w, distTrees);
        if (distTrees >= 2)
        {
            WriteContextMap(w, distMap, distTrees);
        }

        for (int i = 0; i < litLengths.Length; i++)
        {
            BrotliPrefixCodeWriter.Write(w, litLengths[i], OnlySymbol(litTreeHist[i]));
        }

        for (int i = 0; i < cmdLengths.Length; i++)
        {
            BrotliPrefixCodeWriter.Write(w, cmdLengths[i], OnlySymbol(cmdHist[i]));
        }

        for (int i = 0; i < distLengths.Length; i++)
        {
            BrotliPrefixCodeWriter.Write(w, distLengths[i], OnlySymbol(distTreeHist[i]));
        }
        // ---- The commands. ----
        EmitCommands(w, data, start, cmds, cmdSymbols, cmdLengths, cmdCodes, cmdHist, litLengths, litCodes, litTreeHist, litMap, modes, contextsPerType,
            distLengths, distCodes, distTreeHist, distMap, distContext, distSymbols, distExtraBits, distExtra, distOfCommand, litBlocks, cmdBlocks, distBlocks);
        if (isLast)
        {
            w.AlignToByte();
        }
    }

    private static ushort[] ToSymbols(byte[] bytes)
    {
        var symbols = new ushort[bytes.Length];
        for (int k = 0; k < symbols.Length; k++)
        {
            symbols[k] = bytes[k];
        }

        return symbols;
    }

    private static bool IsExplicit(BrotliCommandList cmds, sbyte[] shortCode, int j)
    {
        if (cmds.Copy[j] == 0)
        {
            return false;
        }

        if (shortCode[j] != 0)
        {
            return true;
        }

        return !BrotliEncodeTables.CanBeImplicit(BrotliEncodeTables.InsertCode(cmds.Insert[j]), BrotliEncodeTables.CopyCode(cmds.Copy[j]));
    }

    private byte[] ChooseContextModes(BrotliBlockSplit split, byte[] lit, byte[] p1, byte[] p2)
    {
        var modes = new byte[split.TypeCount];
        if (!_plan.LiteralContexts)
        {
            return modes;
        }

        if (!_plan.SearchContextMode)
        {
            Array.Fill(modes, (byte)2);
            return modes;
        }

        var starts = new int[split.BlockCount];
        int acc = 0;
        for (int b = 0; b < split.BlockCount; b++)
        {
            starts[b] = acc;
            acc += split.Lengths[b];
        }

        // One pass over the literals of a type fills the histograms of all four modes (RFC 7932 section 7.1) at once.
        var scratch = new int[4][];
        for (int m = 0; m < 4; m++)
        {
            scratch[m] = new int[64 * 256];
        }

        ReadOnlySpan<byte> lut0 = s_lut0;
        ReadOnlySpan<byte> lut1 = s_lut1;
        ReadOnlySpan<byte> lut2 = s_lut2;
        for (int t = 0; t < split.TypeCount; t++)
        {
            int[] c0 = scratch[0], c1 = scratch[1], c2 = scratch[2], c3 = scratch[3];
            for (int m = 0; m < 4; m++)
            {
                Array.Clear(scratch[m]);
            }

            for (int b = 0; b < split.BlockCount; b++)
            {
                if (split.Types[b] != t)
                {
                    continue;
                }

                int end = starts[b] + split.Lengths[b];
                for (int i = starts[b]; i < end; i++)
                {
                    int a = p1[i];
                    int c = p2[i];
                    int l = lit[i];
                    c0[((a & 63) << 8) + l]++;
                    c1[((a >> 2) << 8) + l]++;
                    c2[((lut0[a] | lut1[c]) << 8) + l]++;
                    c3[((((lut2[a] << 3) | lut2[c])) << 8) + l]++;
                }
            }

            long best = long.MaxValue;
            for (int mode = 0; mode < 4; mode++)
            {
                long cost = 0;
                for (int c = 0; c < 64; c++)
                {
                    ReadOnlySpan<int> h = scratch[mode].AsSpan(c << 8, 256);
                    int nonZero = 0;
                    foreach (int v in h)
                    {
                        if (v > 0)
                        {
                            nonZero++;
                        }
                    }

                    if (nonZero > 0)
                    {
                        cost += BrotliEncodeTables.EntropyCost(h, out _) + BrotliClusterer.HeaderCost(nonZero);
                    }
                }

                if (cost < best)
                {
                    best = cost;
                    modes[t] = (byte)mode;
                }
            }
        }

        return modes;
    }

    // The one symbol a histogram uses, for a code that takes no bits; 0 if it uses none or several.
    private static int OnlySymbol(int[] histogram)
    {
        int found = -1;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] > 0)
            {
                if (found >= 0)
                {
                    return 0;
                }

                found = i;
            }
        }

        return Math.Max(found, 0);
    }

    private static byte[][] BuildLengthsFor(int[][] histograms, int alphabet)
    {
        var result = new byte[histograms.Length][];
        for (int i = 0; i < histograms.Length; i++)
        {
            result[i] = new byte[alphabet];
            BrotliHuffmanCode.BuildLengths(histograms[i].AsSpan(0, alphabet), BrotliHuffmanCode.MaxCodeLength, result[i]);
        }

        return result;
    }

    private static ushort[][] AssignFor(byte[][] lengths)
    {
        var result = new ushort[lengths.Length][];
        for (int i = 0; i < lengths.Length; i++)
        {
            result[i] = new ushort[lengths[i].Length];
            BrotliHuffmanCode.AssignCodes(lengths[i], result[i]);
        }

        return result;
    }

    // RFC 7932 section 9.2: the variable-length code for NBLTYPES and NTREES, 1 to 256.
    internal static void WriteVariableCount(BrotliBitWriter w, int value)
    {
        if (value == 1)
        {
            w.Write(0, 1);
            return;
        }

        w.Write(1, 1);
        if (value == 2)
        {
            w.Write(0, 3);
            return;
        }

        int n = BitOperations.Log2((uint)(value - 1));
        w.Write((uint)n, 3);
        w.Write((uint)(value - 1 - (1 << n)), n);
    }

    // RFC 7932 section 7.3.
    internal static void WriteContextMap(BrotliBitWriter w, byte[] map, int trees)
    {
        var plain = EncodeContextMap(map, trees, false, out int plainRun, out long plainCost);
        var moved = EncodeContextMap(map, trees, true, out int movedRun, out long movedCost);
        bool useMove = movedCost < plainCost;
        var symbols = useMove ? moved : plain;
        int maxRun = useMove ? movedRun : plainRun;

        if (maxRun > 0)
        {
            w.Write(1, 1);
            w.Write((uint)(maxRun - 1), 4);
        }
        else
        {
            w.Write(0, 1);
        }

        int alphabet = trees + maxRun;
        var hist = new int[alphabet];
        foreach (var s in symbols)
        {
            hist[s.Symbol]++;
        }

        var lengths = new byte[alphabet];
        BrotliHuffmanCode.BuildLengths(hist, BrotliHuffmanCode.MaxCodeLength, lengths);
        var codes = new ushort[alphabet];
        BrotliHuffmanCode.AssignCodes(lengths, codes);
        BrotliPrefixCodeWriter.Write(w, lengths, OnlySymbol(hist));
        foreach (var s in symbols)
        {
            w.Write(codes[s.Symbol], lengths[s.Symbol]);
            if (s.ExtraBits > 0)
            {
                w.Write((uint)s.Extra, s.ExtraBits);
            }
        }

        w.Write(useMove ? 1u : 0u, 1);
    }

    private static List<(int Symbol, int Extra, int ExtraBits)> EncodeContextMap(byte[] map, int trees, bool moveToFront, out int maxRun, out long cost)
    {
        var values = (byte[])map.Clone();
        if (moveToFront)
        {
            var list = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                list[i] = (byte)i;
            }

            for (int i = 0; i < values.Length; i++)
            {
                int index = Array.IndexOf(list, values[i]);
                values[i] = (byte)index;
                byte v = list[index];
                Array.Copy(list, 0, list, 1, index);
                list[0] = v;
            }
        }

        int longest = 0;
        for (int i = 0; i < values.Length;)
        {
            if (values[i] != 0)
            {
                i++;
                continue;
            }

            int j = i;
            while (j < values.Length && values[j] == 0)
            {
                j++;
            }

            longest = Math.Max(longest, j - i);
            i = j;
        }

        maxRun = longest < 2 ? 0 : Math.Min(16, BitOperations.Log2((uint)longest));
        var result = new List<(int Symbol, int Extra, int ExtraBits)>();
        for (int i = 0; i < values.Length;)
        {
            if (values[i] != 0)
            {
                result.Add((values[i] + maxRun, 0, 0));
                i++;
                continue;
            }

            int j = i;
            while (j < values.Length && values[j] == 0)
            {
                j++;
            }

            int run = j - i;
            i = j;
            while (run > 0)
            {
                if (run < 2 || maxRun == 0)
                {
                    result.Add((0, 0, 0));
                    run--;
                    continue;
                }

                int s = Math.Min(BitOperations.Log2((uint)run), maxRun);
                int take = Math.Min(run, (1 << (s + 1)) - 1);
                result.Add((s, take - (1 << s), s));
                run -= take;
            }
        }

        var hist = new int[trees + maxRun];
        long extra = 0;
        foreach (var s in result)
        {
            hist[s.Symbol]++;
            extra += s.ExtraBits;
        }

        cost = BrotliEncodeTables.EntropyCost(hist, out _) + (extra << 16);
        return result;
    }

    /// <summary>The block type and block count codes of one category (RFC 7932 section 6).</summary>
    private sealed class BlockCoding
    {
        internal readonly BrotliBlockSplit Split;
        internal readonly int[] TypeSymbol;
        internal byte[] TypeLengths = [];
        internal ushort[] TypeCodes = [];
        internal byte[] CountLengths = [];
        internal ushort[] CountCodes = [];
        private int[] _typeHist = [];
        private int[] _countHist = [];

        internal BlockCoding(BrotliBlockSplit split)
        {
            Split = split;
            int blocks = split.BlockCount;
            TypeSymbol = new int[blocks];
            if (split.TypeCount < 2)
            {
                return;
            }

            var typeHist = new int[split.TypeCount + 2];
            var countHist = new int[26];
            int last = 0;
            int previous = 1;
            for (int b = 0; b < blocks; b++)
            {
                if (b > 0)
                {
                    int t = split.Types[b];
                    int sym = t == ((last + 1) % split.TypeCount) ? 1 : t == previous ? 0 : t + 2;
                    TypeSymbol[b] = sym;
                    typeHist[sym]++;
                    previous = last;
                    last = t;
                }

                countHist[BrotliEncodeTables.BlockCode(split.Lengths[b])]++;
            }

            _typeHist = typeHist;
            _countHist = countHist;
            TypeLengths = new byte[typeHist.Length];
            BrotliHuffmanCode.BuildLengths(typeHist, BrotliHuffmanCode.MaxCodeLength, TypeLengths);
            TypeCodes = new ushort[typeHist.Length];
            BrotliHuffmanCode.AssignCodes(TypeLengths, TypeCodes);
            CountLengths = new byte[26];
            BrotliHuffmanCode.BuildLengths(countHist, BrotliHuffmanCode.MaxCodeLength, CountLengths);
            CountCodes = new ushort[26];
            BrotliHuffmanCode.AssignCodes(CountLengths, CountCodes);
        }

        internal void WriteHeader(BrotliBitWriter w)
        {
            WriteVariableCount(w, Split.TypeCount);
            if (Split.TypeCount < 2)
            {
                return;
            }

            BrotliPrefixCodeWriter.Write(w, TypeLengths, OnlySymbol(_typeHist));
            BrotliPrefixCodeWriter.Write(w, CountLengths, OnlySymbol(_countHist));
            WriteCount(w, Split.Lengths[0]);
        }

        internal void WriteCount(BrotliBitWriter w, int count)
        {
            int code = BrotliEncodeTables.BlockCode(count);
            w.Write(CountCodes[code], CountLengths[code]);
            if (BrotliCodes.BlockExtra[code] > 0)
            {
                w.Write((uint)(count - BrotliCodes.BlockBase[code]), BrotliCodes.BlockExtra[code]);
            }
        }
    }
}
