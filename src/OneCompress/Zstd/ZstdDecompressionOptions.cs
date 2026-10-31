namespace RecursiveAI.OneCompress.Zstd;

/// <summary>Receives the payload of a skippable frame (RFC 8878 §3.1.2).</summary>
/// <param name="variant">The low four bits of the frame's magic number, 0 to 15.</param>
/// <param name="data">The frame's user data. It is valid only during the call.</param>
public delegate void ZstdSkippableFrameHandler(int variant, ReadOnlySpan<byte> data);

/// <summary>
/// Options for decompressing Zstandard data. The limits protect the caller from input that asks for more
/// memory than it should; they are checked before anything is allocated.
/// </summary>
public sealed class ZstdDecompressionOptions
{
    /// <summary>The default for <see cref="MaxWindowSize"/>: 8 MiB, the size every decoder is advised to support.</summary>
    public const long DefaultMaxWindowSize = 8L * 1024 * 1024;

    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>The default for <see cref="MaxSkippableFrameSize"/>: 1 MiB.</summary>
    public const int DefaultMaxSkippableFrameSize = 1 << 20;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static ZstdDecompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest window size, in bytes, a frame may declare. A decoder holds up to twice this much
    /// output in memory for a frame that does not fit a caller-supplied buffer, so this bounds its memory.
    /// Frames made with the <c>--long</c> option or at the highest levels need more than the default of 8 MiB.
    /// A frame above the limit raises <see cref="CompressionLimitException"/>.
    /// </summary>
    public long MaxWindowSize { get; init; } = DefaultMaxWindowSize;

    /// <summary>
    /// Gets the largest total output, in bytes, a decoder produces between resets; a frame whose declared size
    /// exceeds what remains is refused before any memory is allocated. The default is 1 GiB. A decoder that
    /// streams an unbounded sequence of frames needs <see cref="long.MaxValue"/>.
    /// A hit raises <see cref="CompressionLimitException"/>.
    /// </summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>Gets a value indicating whether the content checksum, when a frame carries one, is verified. The default is true.</summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets the dictionary for frames that do not name one (dictionary ID 0). A frame that names an ID is matched
    /// against <see cref="Dictionaries"/> and then this dictionary; a raw-content dictionary (ID 0) also serves
    /// any frame that names an ID nothing else matches.
    /// </summary>
    public ZstdDictionary? Dictionary { get; init; }

    /// <summary>Gets additional dictionaries, chosen by the dictionary ID a frame names. A frame naming an unknown ID raises <see cref="CompressionDictionaryException"/>.</summary>
    public IReadOnlyList<ZstdDictionary>? Dictionaries { get; init; }

    /// <summary>
    /// Gets the callback that receives the payload of each skippable frame. When it is null (the default),
    /// skippable frames are skipped without being read into memory.
    /// </summary>
    public ZstdSkippableFrameHandler? SkippableFrameHandler { get; init; }

    /// <summary>
    /// Gets the largest skippable frame payload that is buffered for <see cref="SkippableFrameHandler"/>. The default is 1 MiB.
    /// Larger frames raise <see cref="CompressionLimitException"/>. Ignored when there is no handler.
    /// </summary>
    public int MaxSkippableFrameSize { get; init; } = DefaultMaxSkippableFrameSize;
}
