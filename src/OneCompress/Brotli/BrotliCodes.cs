namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// The value ranges of the length, block count and distance codes (RFC 7932 sections 4 to 6). Each table lists the extra bits per code
/// as the RFC does; the base values follow from them because the ranges are contiguous.
/// </summary>
internal static class BrotliCodes
{
    // RFC 7932 section 5: insert length codes 0..23 and copy length codes 0..23.
    private static readonly byte[] s_insertExtra = [0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 12, 14, 24];
    private static readonly byte[] s_copyExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24];

    // RFC 7932 section 6: block count codes 0..25.
    private static readonly byte[] s_blockExtra = [2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 7, 8, 9, 10, 11, 12, 13, 24];

    internal static readonly int[] InsertBase = Bases(s_insertExtra, 0);
    internal static readonly int[] CopyBase = Bases(s_copyExtra, 2);
    internal static readonly int[] BlockBase = Bases(s_blockExtra, 1);

    internal static ReadOnlySpan<byte> InsertExtra => s_insertExtra;

    internal static ReadOnlySpan<byte> CopyExtra => s_copyExtra;

    internal static ReadOnlySpan<byte> BlockExtra => s_blockExtra;

    /// <summary>For each insert-and-copy length code 0..703 the insert length code.</summary>
    internal static readonly byte[] InsertCodeOf = new byte[704];

    /// <summary>For each insert-and-copy length code 0..703 the copy length code.</summary>
    internal static readonly byte[] CopyCodeOf = new byte[704];

    static BrotliCodes()
    {
        // RFC 7932 section 5: each cell of 64 codes pairs a range of eight insert length codes with a range of eight copy length codes.
        // Cells 0 and 1 (codes 0..127) also imply that the distance is the last one used.
        ReadOnlySpan<byte> insertBase = [0, 0, 0, 0, 8, 8, 0, 16, 8, 16, 16];
        ReadOnlySpan<byte> copyBase = [0, 8, 0, 8, 0, 8, 16, 0, 16, 8, 16];
        for (int code = 0; code < 704; code++)
        {
            int cell = code >> 6;
            InsertCodeOf[code] = (byte)(insertBase[cell] + ((code >> 3) & 7));
            CopyCodeOf[code] = (byte)(copyBase[cell] + (code & 7));
        }
    }

    private static int[] Bases(byte[] extra, int first)
    {
        var bases = new int[extra.Length];
        int value = first;
        for (int i = 0; i < extra.Length; i++)
        {
            bases[i] = value;
            value += 1 << extra[i];
        }

        return bases;
    }
}
