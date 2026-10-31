
namespace RecursiveAI.OneCompress.Brotli;

/// <summary>Options for compressing to brotli (RFC 7932).</summary>
/// <remarks>
/// The output depends only on the input and these options: not on the platform, the processor or how the input is cut into calls (a
/// flush is the one thing that changes it, because it ends a meta-block). The options are the two the <c>brotli</c> tool has, with its
/// numbering and defaults.
/// </remarks>
public sealed class BrotliOptions
{
    /// <summary>The default quality, 11, as the <c>brotli</c> tool.</summary>
    public const int DefaultQuality = 11;

    /// <summary>The default window size in bits, 22, as the <c>brotli</c> tool.</summary>
    public const int DefaultWindowBits = 22;

    /// <summary>Gets the shared default options.</summary>
    public static BrotliOptions Default { get; } = new();

    /// <summary>
    /// Gets the quality, 0 to 11. Higher qualities search harder and compress better and slower: 0 and 1 are the fastest, 5 to 9 add
    /// block splitting, context modelling and the static dictionary, and 10 and 11 add a cost-based parse. The default is 11.
    /// </summary>
    public int Quality
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 11);
            field = value;
        }
    } = DefaultQuality;

    /// <summary>
    /// Gets the window size as a power of two, 10 to 24: a copy may reach back <c>2^WindowBits - 16</c> bytes, and a decoder needs a window
    /// that large. Larger windows find more matches in big inputs and cost the encoder and the decoder memory. The default is 22 (4 MiB).
    /// </summary>
    public int WindowBits
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 10);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 24);
            field = value;
        }
    } = DefaultWindowBits;
}
