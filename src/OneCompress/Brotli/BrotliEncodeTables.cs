using System.Numerics;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Maps lengths and counts to the codes of RFC 7932 sections 5 and 6, and holds the integer cost arithmetic the encoder's decisions use.
/// </summary>
internal static class BrotliEncodeTables
{
    private const int LogTableBits = 12;

    // log2(1 + i / 4096) in fixed point with 16 fraction bits, for the mantissa of a number.
    private static readonly int[] s_logMantissa = BuildLogMantissa();

    private static readonly byte[] s_insertCodeSmall = BuildSmall(BrotliCodes.InsertBase, 1024);
    private static readonly byte[] s_copyCodeSmall = BuildSmall(BrotliCodes.CopyBase, 1024);

    // The cell of the insert-and-copy alphabet for (insert code / 8, copy code / 8), RFC 7932 section 5, or -1 if none. The cells for
    // an implicit distance (0 and 1) are handled separately.
    private static readonly sbyte[] s_cell = BuildCells();

    // For lengths below 1024, one word per length: the code, the number of extra bits above it (5 bits each) and the extra bits' value
    // from bit 10, so that a command's codes and extra bits cost two loads.
    private static readonly uint[] s_insertInfo = BuildInfo(BrotliCodes.InsertBase, BrotliCodes.InsertExtra, 1024);
    private static readonly uint[] s_copyInfo = BuildInfo(BrotliCodes.CopyBase, BrotliCodes.CopyExtra, 1024);

    /// <summary>Code, extra bit count and extra bits of an insert length below 1024: <c>code | count &lt;&lt; 5 | value &lt;&lt; 10</c>.</summary>
    internal static uint InsertInfo(int length) => s_insertInfo[length];

    /// <summary>Code, extra bit count and extra bits of a copy length from 2 to 1023: <c>code | count &lt;&lt; 5 | value &lt;&lt; 10</c>.</summary>
    internal static uint CopyInfo(int length) => s_copyInfo[length];

    private static uint[] BuildInfo(int[] bases, ReadOnlySpan<byte> extra, int size)
    {
        var table = new uint[size];
        for (int v = 0; v < size; v++)
        {
            int code = v < bases[0] ? 0 : CodeOf(bases, v);
            table[v] = (uint)code | ((uint)extra[code] << 5) | ((uint)Math.Max(0, v - bases[code]) << 10);
        }

        return table;
    }

    /// <summary>The insert length code of an insert length (RFC 7932 section 5).</summary>
    internal static int InsertCode(int length) => length < 1024 ? s_insertCodeSmall[length] : CodeOf(BrotliCodes.InsertBase, length);

    /// <summary>The copy length code of a copy length of at least 2 (RFC 7932 section 5).</summary>
    internal static int CopyCode(int length) => length < 1024 ? s_copyCodeSmall[length] : CodeOf(BrotliCodes.CopyBase, length);

    /// <summary>The block count code of a block length of at least 1 (RFC 7932 section 6).</summary>
    internal static int BlockCode(int count) => CodeOf(BrotliCodes.BlockBase, count);

    /// <summary>
    /// The insert-and-copy command symbol (0 to 703) for the given codes. With <paramref name="implicitDistance"/> the symbol says that
    /// the distance is the last one used, which only the cells 0 and 1 can say (insert code below 8, copy code below 16).
    /// </summary>
    internal static int CommandSymbol(int insertCode, int copyCode, bool implicitDistance)
    {
        int cell;
        if (implicitDistance)
        {
            cell = copyCode < 8 ? 0 : 1;
        }
        else
        {
            cell = s_cell[((insertCode >> 3) * 3) + (copyCode >> 3)];
        }

        return (cell << 6) | ((insertCode & 7) << 3) | (copyCode & 7);
    }

    /// <summary>True if a command with these codes can use an implicit distance.</summary>
    internal static bool CanBeImplicit(int insertCode, int copyCode) => insertCode < 8 && copyCode < 16;

    private static int CodeOf(int[] bases, int value)
    {
        int lo = 0;
        int hi = bases.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (bases[mid] <= value)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private static byte[] BuildSmall(int[] bases, int size)
    {
        byte[] table = new byte[size];
        for (int v = 0; v < size; v++)
        {
            table[v] = (byte)(v < bases[0] ? 0 : CodeOf(bases, v));
        }

        return table;
    }

    private static sbyte[] BuildCells()
    {
        // Cells 2..10 of the table in RFC 7932 section 5, by (insert range, copy range) with ranges numbered 0..2.
        sbyte[] cells = new sbyte[9];
        cells.AsSpan().Fill(-1);
        ReadOnlySpan<byte> insertBase = [0, 0, 0, 0, 8, 8, 0, 16, 8, 16, 16];
        ReadOnlySpan<byte> copyBase = [0, 8, 0, 8, 0, 8, 16, 0, 16, 8, 16];
        for (int cell = 2; cell < 11; cell++)
        {
            cells[((insertBase[cell] >> 3) * 3) + (copyBase[cell] >> 3)] = (sbyte)cell;
        }

        return cells;
    }

    // ----- Integer cost arithmetic. Costs are bits in fixed point with 16 fraction bits (65536 = one bit); no floating point decides a byte. -----

    /// <summary>One bit in the fixed-point cost unit.</summary>
    internal const int One = 1 << 16;

    private static int[] BuildLogMantissa()
    {
        // Digit by digit: square y in [1, 2) (30 fraction bits); each squaring that reaches 2 contributes the next bit of log2(y).
        int[] table = new int[1 << LogTableBits];
        for (int i = 0; i < table.Length; i++)
        {
            ulong y = (ulong)((1 << LogTableBits) + i) << (30 - LogTableBits);
            int result = 0;
            for (int bit = 15; bit >= 0; bit--)
            {
                y = (y * y) >> 30;
                if (y >= (2UL << 30))
                {
                    y >>= 1;
                    result |= 1 << bit;
                }
            }

            table[i] = result;
        }

        return table;
    }

    /// <summary>log2(x) in 16.16 fixed point for x of at least 1. The error is below 0.0004 bits.</summary>
    internal static int Log2(uint x)
    {
        int n = 31 - BitOperations.LeadingZeroCount(x);
        int index = n >= LogTableBits ? (int)((x >> (n - LogTableBits)) & ((1u << LogTableBits) - 1)) : (int)((x << (LogTableBits - n)) & ((1u << LogTableBits) - 1));
        return (n << 16) + s_logMantissa[index];
    }

    /// <summary>x * log2(x) in 16.16 fixed point, 0 for x of 0.</summary>
    internal static long XLog2X(int x) => x <= 0 ? 0 : (long)x * Log2((uint)x);

    /// <summary>The entropy cost in 1/65536-bit units of coding a histogram with a code that fits it exactly: total * log2(total) - sum count * log2(count).</summary>
    internal static long EntropyCost(ReadOnlySpan<int> histogram, out int total)
    {
        long sum = 0;
        int t = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            int c = histogram[i];
            if (c > 0)
            {
                t += c;
                sum += XLog2X(c);
            }
        }

        total = t;
        return XLog2X(t) - sum;
    }

    /// <summary>log2(ceil position) style helper: the number of bits of a positive value.</summary>
    internal static int BitLength(uint x) => 32 - BitOperations.LeadingZeroCount(x);
}
