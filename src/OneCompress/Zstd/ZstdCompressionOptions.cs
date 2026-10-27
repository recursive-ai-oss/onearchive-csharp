namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Options for compressing to Zstandard. The same options always give the same bytes, on every platform and thread count.
/// </summary>
public sealed class ZstdCompressionOptions
{
    /// <summary>Gets the shared instance with every option at its default.</summary>
    public static ZstdCompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the compression level, numbered as the <c>zstd</c> command line tool numbers them: 1 to 22 are implemented (20 to 22 are the tool's ultra levels, with windows up to 128 MiB: decoding large inputs compressed at these levels needs <see cref="ZstdDecompressionOptions.MaxWindowSize"/> raised above its 8 MiB default), 0 means
    /// the default (3), and negative levels (-1 and below) trade ratio for speed. The default is 3. Other levels throw
    /// <see cref="ArgumentOutOfRangeException"/> when compression starts.
    /// </summary>
    public int Level { get; init; } = 3;

    /// <summary>
    /// Gets a value indicating whether the frame ends with a 32-bit content checksum (XXH64, low half) that decoders verify.
    /// The default is false, as in the C library's one-shot call; the checksum costs 4 bytes per frame.
    /// </summary>
    public bool Checksum { get; init; }

    /// <summary>
    /// Gets a value indicating whether the frame header states the content size when it is known (one-shot calls, or a size
    /// given in <see cref="ContentSize"/>). The default is true. With the size stated, small frames also drop the window
    /// descriptor.
    /// </summary>
    public bool WriteContentSize { get; init; } = true;

    /// <summary>
    /// Gets the total number of bytes that a streaming encoder will receive for a frame, when the caller knows it. It sizes the
    /// window and tables as a one-shot call would, and is written into the header. The encoder throws
    /// <see cref="InvalidOperationException"/> if the frame ends with another count. The default is null (unknown).
    /// </summary>
    public long? ContentSize { get; init; }

    /// <summary>Gets the dictionary to compress with, or <see langword="null"/> for none. The decoder must be given the same dictionary.</summary>
    public ZstdCompressionDictionary? Dictionary { get; init; }

    /// <summary>
    /// Gets a value indicating whether the dictionary's ID is written into the frame header (when the dictionary has one).
    /// The default is true; with false the decoder must be told which dictionary to use.
    /// </summary>
    public bool WriteDictionaryId { get; init; } = true;

    /// <summary>
    /// Gets the window log to use, from 10 to 30, or 0 (the default) to take it from the level and the content size. A larger window
    /// lets matches reach further back and makes decoders hold more memory.
    /// </summary>
    public int WindowLog { get; init; }
}
