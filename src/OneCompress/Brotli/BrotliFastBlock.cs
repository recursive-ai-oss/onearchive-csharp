using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// One command of a meta-block as the fast parse (qualities 0 to 2) leaves it for the writer: the insert-and-copy symbol, the distance
/// symbol and the extra bits are worked out when the command is found, while its values are still in registers, so the writer makes no
/// pass of its own to compute them (RFC 7932 sections 4 and 5).
/// </summary>
internal struct BrotliFastCommand
{
    /// <summary>Extra bits of the insert length, then (above them) those of the copy length.</summary>
    internal ulong Extra;

    /// <summary>The distance's extra bits in the low 26 bits and their count above them.</summary>
    internal uint DistanceExtra;

    /// <summary>Literals before the copy.</summary>
    internal int Insert;

    /// <summary>Input bytes the copy covers; 0 for the last command of a block that ends in literals.</summary>
    internal int Copy;

    /// <summary>The insert-and-copy symbol (0 to 703).</summary>
    internal ushort Symbol;

    /// <summary>Distance context (copy length 2, 3, 4 and 5 or more) in bits 8 and 9 and the distance symbol below, or <see cref="BrotliFastBlock.NoDistance"/>.</summary>
    internal ushort Distance;

    /// <summary>Number of bits in <see cref="Extra"/>.</summary>
    internal int ExtraBits;
}

/// <summary>
/// The commands of one meta-block from the fast parse, with the histograms and totals the writer needs to build its prefix codes. The
/// block uses one block type per category, no literal contexts and NPOSTFIX = NDIRECT = 0 (RFC 7932 section 9.2).
/// </summary>
internal sealed class BrotliFastBlock
{
    /// <summary>The distance alphabet size: 16 short codes and 48 for NPOSTFIX = NDIRECT = 0.</summary>
    internal const int DistanceAlphabet = 64;

    /// <summary>The distance slot of a command with no explicit distance; symbol 255 of context 3 has no code, so it adds no bits.</summary>
    internal const ushort NoDistance = 0x3FF;

    internal BrotliFastCommand[] Commands = new BrotliFastCommand[4096];
    internal int Count;
    internal readonly int[] CommandHistogram = new int[704];
    internal readonly int[] DistanceHistogram = new int[(4 * DistanceAlphabet) + 1];
    /// <summary>The four last distances after the block's last command, most recent first (RFC 7932 section 4).</summary>
    internal readonly int[] RingAfter = new int[4];
    internal long ExtraBitTotal;
    internal int LiteralCount;

    internal BrotliFastBlock()
    {
        _ = BrotliEncodeTables.InsertInfo(0) + BrotliEncodeTables.CopyInfo(2) + (uint)BrotliEncodeTables.InsertCode(0) + (uint)BrotliCodes.InsertExtra[0];
    }

    internal void Clear()
    {
        Count = 0;
        ExtraBitTotal = 0;
        LiteralCount = 0;
        Array.Clear(CommandHistogram);
        Array.Clear(DistanceHistogram);
    }

    /// <summary>Makes room for one more command.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Grow() => Array.Resize(ref Commands, Commands.Length * 2);

    /// <summary>
    /// Appends a command with a copy of <paramref name="copy"/> bytes (at least 2) at <paramref name="distance"/>, which has the short code
    /// <paramref name="code"/> (0 to 15) or -1 for none (RFC 7932 section 4), after <paramref name="insert"/> literals.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Add(int insert, int copy, int distance, int code)
    {
        if (Count == Commands.Length)
        {
            Grow();
        }

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

        int cc;
        int eBits;
        if (copy < 1024)
        {
            uint info = BrotliEncodeTables.CopyInfo(copy);
            cc = (int)(info & 31);
            eBits = iBits + (int)((info >> 5) & 31);
            extra |= (ulong)(info >> 10) << iBits;
        }
        else
        {
            cc = BrotliEncodeTables.CopyCode(copy);
            eBits = iBits + BrotliCodes.CopyExtra[cc];
            extra |= (ulong)(uint)(copy - BrotliCodes.CopyBase[cc]) << iBits;
        }

        // RFC 7932 section 5: distance code 0 with a small insert and copy is part of the command symbol. The branches here depend on the
        // data and mispredict often, so both outcomes are computed and one is selected.
        bool implicitDistance = (code == 0) & BrotliEncodeTables.CanBeImplicit(ic, cc);
        int symbol = BrotliEncodeTables.CommandSymbol(ic, cc, implicitDistance);
        ref BrotliFastCommand c = ref BrotliEncRaw.Command(Commands, Count);
        int ctx = Math.Min(copy, 5) - 2;
        // RFC 7932 section 4 with NPOSTFIX = NDIRECT = 0: the symbol and extra bits of a distance with no short code.
        uint y = (uint)(distance - 1 + 4);
        int nb = BitOperations.Log2(y) - 1;
        int explicitSymbol = 16 + (2 * (nb - 1)) + (int)((y >> nb) & 1);
        uint explicitExtra = (y & ((1u << nb) - 1)) | ((uint)nb << 26);
        bool isExplicit = code < 0;
        int dsym = isExplicit ? explicitSymbol : code;
        uint dext = isExplicit ? explicitExtra : 0;
        int dbits = isExplicit ? nb : 0;
        c.Distance = implicitDistance ? NoDistance : (ushort)(dsym | (ctx << 8));
        c.DistanceExtra = implicitDistance ? 0 : dext;
        // A command with an implicit distance counts into a spare cell past the real histogram.
        BrotliEncRaw.Count(DistanceHistogram, implicitDistance ? 4 * DistanceAlphabet : (ctx * DistanceAlphabet) + dsym)++;
        ExtraBitTotal += implicitDistance ? 0 : dbits;
        c.Symbol = (ushort)symbol;
        c.Extra = extra;
        c.ExtraBits = eBits;
        c.Insert = insert;
        c.Copy = copy;
        ExtraBitTotal += eBits;
        LiteralCount += insert;
        BrotliEncRaw.Count(CommandHistogram, symbol)++;
        Count++;
    }

    /// <summary>Appends the last command of a block that ends in <paramref name="insert"/> literals: no copy and no distance.</summary>
    internal void AddTail(int insert)
    {
        if (Count == Commands.Length)
        {
            Grow();
        }

        int ic = BrotliEncodeTables.InsertCode(insert);
        int iBits = BrotliCodes.InsertExtra[ic];
        int symbol = BrotliEncodeTables.CommandSymbol(ic, 0, BrotliEncodeTables.CanBeImplicit(ic, 0));
        ref BrotliFastCommand c = ref Commands[Count++];
        c.Symbol = (ushort)symbol;
        c.Extra = (uint)(insert - BrotliCodes.InsertBase[ic]);
        c.ExtraBits = iBits;
        c.Insert = insert;
        c.Copy = 0;
        c.Distance = NoDistance;
        c.DistanceExtra = 0;
        ExtraBitTotal += iBits;
        LiteralCount += insert;
        CommandHistogram[symbol]++;
    }
}
