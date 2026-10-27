namespace RecursiveAI.OneCompress.Zstd;

/// <summary>Options for writing a seekable Zstandard file.</summary>
public sealed class ZstdSeekableOptions
{
    /// <summary>The default frame size: 1 MiB of content per frame.</summary>
    public const int DefaultFrameSize = 1 << 20;

    /// <summary>The largest frame size: 1 GiB, which keeps every size in the seek table within its 32-bit fields.</summary>
    public const int MaxFrameSize = 1 << 30;

    /// <summary>A shared instance with every default.</summary>
    public static ZstdSeekableOptions Default { get; } = new();

    /// <summary>
    /// How much content goes into each frame, from 1 to <see cref="MaxFrameSize"/> bytes; the last frame holds the rest.
    /// Smaller frames make random access cheaper and compression worse, because no frame sees data before it.
    /// Default <see cref="DefaultFrameSize"/>.
    /// </summary>
    public int FrameSize { get; init; } = DefaultFrameSize;

    /// <summary>
    /// Whether the seek table carries a checksum per frame (the low 32 bits of the XXH64 digest of the frame's content),
    /// which readers verify. Independent of <see cref="ZstdCompressionOptions.Checksum"/>, which adds the frame's own
    /// 32-bit checksum inside each frame. Default true.
    /// </summary>
    public bool Checksums { get; init; } = true;

    /// <summary>
    /// How each frame is compressed: level, dictionary, window, long-distance matching. Every frame is compressed on its
    /// own with the same options, and its header declares its content size when <see cref="ZstdCompressionOptions.WriteContentSize"/>
    /// is set. <see cref="ZstdCompressionOptions.ContentSize"/> must be null because each frame has its own size.
    /// Default <see cref="ZstdCompressionOptions.Default"/>.
    /// </summary>
    public ZstdCompressionOptions Compression { get; init; } = ZstdCompressionOptions.Default;
}

/// <summary>Options for reading a seekable Zstandard file.</summary>
public sealed class ZstdSeekableReaderOptions
{
    /// <summary>The default limit on the content of one frame: 256 MiB.</summary>
    public const long DefaultMaxFrameSize = 256L << 20;

    /// <summary>A shared instance with every default.</summary>
    public static ZstdSeekableReaderOptions Default { get; } = new();

    /// <summary>
    /// The most entries the seek table may declare, checked before the table is read. Default
    /// <see cref="ZstdSeekTable.DefaultMaxFrames"/>.
    /// </summary>
    public int MaxFrames { get; init; } = ZstdSeekTable.DefaultMaxFrames;

    /// <summary>
    /// The most content one frame may declare in the seek table, checked before a buffer for it is allocated; a reader
    /// holds one decoded frame. Default <see cref="DefaultMaxFrameSize"/>.
    /// </summary>
    public long MaxFrameSize { get; init; } = DefaultMaxFrameSize;

    /// <summary>Whether to verify the seek table's per-frame checksums when it has them. Default true.</summary>
    public bool VerifyChecksums { get; init; } = true;

    /// <summary>
    /// Limits and dictionaries for decoding the frames. Its <see cref="ZstdDecompressionOptions.MaxOutputSize"/> applies per
    /// frame. Default <see cref="ZstdDecompressionOptions.Default"/>.
    /// </summary>
    public ZstdDecompressionOptions Decompression { get; init; } = ZstdDecompressionOptions.Default;
}
