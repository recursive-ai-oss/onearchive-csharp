namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Finds the matches of a block. This is the seam between parsing strategies and the entropy stage: every strategy turns a
/// stretch of a window into a <see cref="ZstdSequenceStore"/>, and <see cref="ZstdBlockWriter"/> codes it, whatever strategy
/// made it.
/// </summary>
/// <remarks>
/// <para>A finder keeps its search tables across the blocks of a frame. The window is one contiguous buffer: history
/// (bytes of earlier blocks), then the block. Positions are indexes into it. When the caller slides the window to make room it
/// calls <see cref="Slide"/>, so stored positions stay right.</para>
/// <para>A finder must make its choices from the input and parameters alone, never from timing, thread count or processor features.</para>
/// </remarks>
public abstract class ZstdMatchFinder
{
    /// <summary>Prepares for a new frame: empties the search tables and attaches the dictionary, if any.</summary>
    /// <param name="parameters">The parameters to search with.</param>
    /// <param name="dictionary">The dictionary whose content precedes the frame's data, or <see langword="null"/>.</param>
    public abstract void BeginFrame(ZstdCompressionParameters parameters, ZstdCompressionDictionary? dictionary);

    /// <summary>
    /// Finds the sequences of one block and appends them to <paramref name="store"/>; the literals after the last match
    /// are appended as well.
    /// </summary>
    /// <param name="window">The history followed by the block. Bytes up to <paramref name="blockEnd"/> are valid; matches never reach past it.</param>
    /// <param name="blockStart">Index in <paramref name="window"/> of the block's first byte.</param>
    /// <param name="blockEnd">Index just past the block's last byte.</param>
    /// <param name="windowStartPosition">How many bytes of the frame precede <c>window[0]</c> (nonzero once the window has slid).</param>
    /// <param name="repeatOffsets">The repeat offsets in force at the start of the block; the finder may use them to find cheap matches.</param>
    /// <param name="store">Receives the sequences and literals; it is not cleared by this method.</param>
    public abstract void FindSequences(
        ReadOnlySpan<byte> window,
        int blockStart,
        int blockEnd,
        long windowStartPosition,
        ZstdRepeatOffsets repeatOffsets,
        ZstdSequenceStore store);

    /// <summary>Tells the finder that the window was moved down by <paramref name="delta"/> bytes: stored positions below it are gone.</summary>
    /// <param name="delta">The number of bytes removed from the front of the window.</param>
    public abstract void Slide(int delta);

    /// <summary>
    /// Adds the positions <paramref name="firstPosition"/> to <paramref name="endPosition"/> of <paramref name="window"/> to the search tables without
    /// parsing them, so later blocks can match into them. Used for history that was not parsed by this finder: the overlap
    /// in front of a parallel job, and the tail of a long match found elsewhere. Bytes past <c>window.Length</c> are never read,
    /// so a caller passes a window that ends where its valid bytes end. The default does nothing, which only costs ratio.
    /// </summary>
    /// <param name="window">The window; its length is the end of the valid bytes.</param>
    /// <param name="firstPosition">Index of the first position to add.</param>
    /// <param name="endPosition">Index just past the last position to add.</param>
    public virtual void Prime(ReadOnlySpan<byte> window, int firstPosition, int endPosition)
    {
    }

    /// <summary>Creates the finder for a strategy.</summary>
    internal static ZstdMatchFinder Create(ZstdStrategy strategy) => strategy switch
    {
        ZstdStrategy.Fast or ZstdStrategy.DoubleFast => new ZstdFastMatchFinder(),
        >= ZstdStrategy.BinaryTreeOptimal => new ZstdOptimalMatchFinder(),
        >= ZstdStrategy.Greedy => new ZstdLazyMatchFinder(),
        _ => throw new NotSupportedException($"Strategy {strategy} is not implemented."),
    };
}
