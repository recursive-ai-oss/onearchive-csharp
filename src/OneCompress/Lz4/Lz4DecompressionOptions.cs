using System.Diagnostics.CodeAnalysis;
namespace RecursiveAI.OneCompress.Lz4;

/// <summary>Receives the payload of a skippable frame (Frame format, "Skippable Frames").</summary>
/// <param name="variant">The low four bits of the frame's magic number (0 to 15).</param>
/// <param name="data">The user data; valid only during the call.</param>
[Experimental("ONECOMPRESS002", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public delegate void Lz4SkippableFrameHandler(int variant, ReadOnlySpan<byte> data);

/// <summary>Limits, checks and dictionaries for LZ4 frame decoding. Read once when a decoder or stream is created.</summary>
[Experimental("ONECOMPRESS002", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Lz4DecompressionOptions
{
    /// <summary>The default for <see cref="MaxBlockSize"/>: 8 MiB, which admits the 4 MiB frame blocks and the 8 MiB legacy blocks.</summary>
    public const int DefaultMaxBlockSize = 8 << 20;

    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>The default for <see cref="MaxSkippableFrameSize"/>: 1 MiB.</summary>
    public const int DefaultMaxSkippableFrameSize = 1 << 20;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static Lz4DecompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest decoded block size, in bytes, a frame may declare. The decoder reserves a buffer of up to twice
    /// this plus 64 KiB for a frame that does not decode straight into the caller's buffer. The frame format tops out at
    /// 4 MiB; legacy frames use 8 MiB. A frame above the limit raises <see cref="CompressionLimitException"/>.
    /// </summary>
    public int MaxBlockSize { get; init; } = DefaultMaxBlockSize;

    /// <summary>
    /// Gets the largest total output, in bytes, a decoder produces between resets; a frame whose declared content size
    /// exceeds what remains is refused before any memory is allocated. A decoder that streams an unbounded sequence of
    /// frames needs <see cref="long.MaxValue"/>. A hit raises <see cref="CompressionLimitException"/>.
    /// </summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>
    /// Gets a value indicating whether block checksums and content checksums are verified when a frame carries them.
    /// The default is true. The frame header checksum is always verified.
    /// </summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets the dictionary for frames that name none, and for frames that name an ID no entry of <see cref="Dictionaries"/>
    /// has when this dictionary's <see cref="Lz4Dictionary.Id"/> is zero or equal to the named ID.
    /// </summary>
    public Lz4Dictionary? Dictionary { get; init; }

    /// <summary>Gets additional dictionaries, chosen by the dictionary ID a frame names. A frame naming an ID nothing matches raises <see cref="CompressionDictionaryException"/>.</summary>
    public IReadOnlyList<Lz4Dictionary>? Dictionaries { get; init; }

    /// <summary>
    /// Gets the callback that receives the payload of each skippable frame. When it is null (the default), skippable
    /// frames are skipped without being read into memory.
    /// </summary>
    public Lz4SkippableFrameHandler? SkippableFrameHandler { get; init; }

    /// <summary>Gets the largest skippable frame payload that is buffered for <see cref="SkippableFrameHandler"/>. The default is 1 MiB. Larger frames raise <see cref="CompressionLimitException"/>. Ignored when there is no handler.</summary>
    public int MaxSkippableFrameSize { get; init; } = DefaultMaxSkippableFrameSize;
}
