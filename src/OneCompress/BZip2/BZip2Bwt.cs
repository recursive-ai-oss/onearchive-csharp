
namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// The inverse Burrows-Wheeler transform as bzip2 uses it (Burrows and Wheeler, DEC SRC report 124, 1994). A block holds the
/// last column of the sorted rotations of the original block, and the row number at which the original block appears.
/// </summary>
/// <remarks>
/// The decoder builds a "transformation vector" in one array of 32-bit words: the low 8 bits of word <c>i</c> are the last
/// column's byte <c>i</c>, and <see cref="BuildVector"/> fills the upper 24 bits of word <c>j</c> with the row that follows
/// row <c>j</c>. Reading the original then takes one load per byte: see <see cref="Next"/>.
/// </remarks>
public static class BZip2Bwt
{
    /// <summary>The largest block bzip2 writes, in bytes (level 9: 9 x 100 000), after the first run-length stage.</summary>
    public const int MaxBlockSize = 900_000;

    /// <summary>
    /// Fills in the transformation vector. Before the call the low 8 bits of <c>vector[i]</c> hold byte <c>i</c> of the last column and
    /// the upper 24 bits are zero; after it the upper 24 bits of <c>vector[j]</c> hold the index of the last-column entry that is
    /// the same occurrence of its byte as the <c>j</c>-th byte of the sorted (first) column.
    /// </summary>
    /// <param name="vector">The first <paramref name="length"/> words are used.</param>
    /// <param name="length">The block length, at most 2^24 - 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, above 2^24 - 1 or above the vector's length.</exception>
    public static void BuildVector(Span<uint> vector, int length)
    {
        if ((uint)length > 0xFFFFFF || length > vector.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        Span<int> start = stackalloc int[256];
        for (int i = 0; i < length; i++)
        {
            start[(int)(vector[i] & 0xFF)]++;
        }

        int sum = 0;
        for (int b = 0; b < 256; b++)
        {
            int c = start[b];
            start[b] = sum;
            sum += c;
        }

        for (int i = 0; i < length; i++)
        {
            int b = (int)(vector[i] & 0xFF);
            vector[start[b]++] |= (uint)i << 8;
        }
    }

    /// <summary>The row that follows row <paramref name="row"/>; the byte of the original that the step yields is <c>vector[next] &amp; 0xFF</c>.</summary>
    /// <param name="vector">A vector prepared by <see cref="BuildVector"/>.</param>
    /// <param name="row">The current row; start from the block's original row number.</param>
    /// <returns>The next row.</returns>
    public static int Next(ReadOnlySpan<uint> vector, int row) => (int)(vector[row] >> 8);

    /// <summary>Undoes the transform for a whole block.</summary>
    /// <param name="lastColumn">The block's last column (the bytes the move-to-front stage produced).</param>
    /// <param name="originalRow">The row number of the original block, 0 to <c>lastColumn.Length - 1</c>.</param>
    /// <param name="output">Receives the original block; it must be at least as long as <paramref name="lastColumn"/>.</param>
    /// <exception cref="ArgumentException">The block is empty, longer than 2^24 - 1, or <paramref name="output"/> is too short.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="originalRow"/> is outside the block.</exception>
    public static void Inverse(ReadOnlySpan<byte> lastColumn, int originalRow, Span<byte> output)
    {
        int n = lastColumn.Length;
        if (n == 0 || n > 0xFFFFFF)
        {
            throw new ArgumentException("The block must hold 1 to 16777215 bytes.", nameof(lastColumn));
        }

        if (output.Length < n)
        {
            throw new ArgumentException("The output is shorter than the block.", nameof(output));
        }

        if ((uint)originalRow >= (uint)n)
        {
            throw new ArgumentOutOfRangeException(nameof(originalRow));
        }

        uint[] vector = new uint[n];
        for (int i = 0; i < n; i++)
        {
            vector[i] = lastColumn[i];
        }

        BuildVector(vector, n);
        int row = originalRow;
        for (int i = 0; i < n; i++)
        {
            row = Next(vector, row);
            output[i] = (byte)vector[row];
        }
    }
}
