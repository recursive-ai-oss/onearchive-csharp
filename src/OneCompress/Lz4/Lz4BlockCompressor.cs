namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// Compresses data into LZ4 blocks: the match-finding half of the encoder, over a window the caller manages. A window is
/// the history followed by the bytes to compress; the compressor remembers where it has seen byte sequences and finds matches
/// among them. <see cref="Lz4FastBlockCompressor"/> (hash table, levels 1 and below) and
/// <see cref="Lz4HighBlockCompressor"/> (hash chains and optimal parsing, levels 2 to 12) are the two implementations;
/// <see cref="Create"/> picks one from a level. The frame encoder and <see cref="Lz4Block"/> are built on these.
/// </summary>
/// <remarks>
/// <para>Output depends only on the bytes given and the level, never on the machine or on what the instance compressed
/// before: <see cref="Reset"/> (or a block with no history) makes an instance behave like a new one.</para>
/// <para>Instances own their tables, which are allocated once; they are not thread-safe.</para>
/// </remarks>
public abstract class Lz4BlockCompressor
{
    /// <summary>The largest input one block can hold: 2,113,929,216 bytes (the block format's limit for a single block).</summary>
    public const int MaxInputLength = 0x7E000000;

    /// <summary>Creates the compressor for an LZ4 level.</summary>
    /// <param name="level">1 (or 0) for the default fast level, -N for the fast level that skips N times faster (the lz4 tool's <c>--fast=N</c>), 2 to 12 for high compression.</param>
    /// <returns>A new compressor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside <see cref="Lz4CompressionOptions.MinLevel"/> to <see cref="Lz4CompressionOptions.MaxLevel"/>.</exception>
    public static Lz4BlockCompressor Create(int level)
    {
        if (level < Lz4CompressionOptions.MinLevel || level > Lz4CompressionOptions.MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "The level must be from -65536 to 12.");
        }

        if (level == 2)
        {
            // Level 2: the fast finder with a 2^17-entry table that also indexes every second position inside matches.
            return new Lz4FastBlockCompressor(1, 17, 2);
        }

        return level > 2 ? new Lz4HighBlockCompressor(level) : new Lz4FastBlockCompressor(level <= 0 ? 1 - level : 1);
    }

    /// <summary>
    /// Compresses <c>window[historyLength..]</c> into one block. Bytes before <paramref name="historyLength"/> are history
    /// the block may reference; they must be the bytes the compressor has seen (see <see cref="Rebase"/>), or the
    /// <see cref="SetDictionary"/> dictionary stands in when <paramref name="historyLength"/> is zero.
    /// </summary>
    /// <param name="window">The history followed by the bytes to compress. The block ends where the window ends.</param>
    /// <param name="historyLength">How many bytes at the start of the window are history, from 0 to the window's length. Zero starts a block with no history except the dictionary.</param>
    /// <param name="destination">Receives the block; its length must be at least <see cref="Lz4BlockWriter.GetMaxCompressedLength"/> of the number of bytes compressed.</param>
    /// <returns>The size of the block in bytes; at least 1 (an empty input is the single byte 0x00).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="historyLength"/> is out of range.</exception>
    /// <exception cref="ArgumentException">The window exceeds <see cref="MaxInputLength"/>, or <paramref name="destination"/> is too small.</exception>
    public abstract int Compress(ReadOnlySpan<byte> window, int historyLength, Span<byte> destination);

    /// <summary>
    /// Forgets every byte seen so far, so the next block cannot reference anything but the dictionary. The next call must
    /// pass a history length of zero.
    /// </summary>
    public abstract void Reset();

    /// <summary>
    /// Tells the compressor that the caller moved the window: the first <paramref name="removed"/> bytes were dropped and
    /// the rest now start at index 0. Matches into dropped bytes are forgotten. Call it between blocks when the window
    /// would otherwise outgrow its buffer; keeping the last 64 KiB loses nothing a match can reach.
    /// </summary>
    /// <param name="removed">The number of bytes dropped from the front of the window, from 0 to the bytes seen.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="removed"/> is out of range.</exception>
    public abstract void Rebase(int removed);

    /// <summary>
    /// Sets the dictionary that blocks compressed with a history length of zero start from, or clears it. The dictionary
    /// is prepared once per <see cref="Lz4Dictionary"/> object and shared, so setting the same dictionary again costs little.
    /// </summary>
    /// <param name="dictionary">The dictionary, or <see langword="null"/> for none.</param>
    public abstract void SetDictionary(Lz4Dictionary? dictionary);

    internal static void CheckArguments(ReadOnlySpan<byte> window, int historyLength, Span<byte> destination)
    {
        int windowLength = window.Length;
        int destinationLength = destination.Length;
        if ((uint)historyLength > (uint)windowLength)
        {
            throw new ArgumentOutOfRangeException(nameof(historyLength), historyLength, "The history length must be from 0 to the window length.");
        }

        if (windowLength > MaxInputLength)
        {
            throw new ArgumentException("The window is larger than one block can hold.", nameof(window));
        }

        if (destinationLength < Lz4BlockWriter.GetMaxCompressedLength(windowLength - historyLength))
        {
            throw new ArgumentException("The destination must hold Lz4BlockWriter.GetMaxCompressedLength of the input.", nameof(destination));
        }
    }
}
