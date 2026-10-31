
namespace RecursiveAI.OneCompress.Lzx;

/// <summary>Limits for LZX decompression.</summary>
public sealed class LzxDecompressionOptions
{
    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>The options with every default.</summary>
    public static LzxDecompressionOptions Default { get; } = new();

    /// <summary>
    /// The most bytes a decoder may produce in total. A frame that would pass it raises <see cref="CompressionLimitException"/>
    /// before decoding. Default <see cref="DefaultMaxOutputSize"/>.
    /// </summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;
}
