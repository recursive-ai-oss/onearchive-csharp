using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>How a Deflate encoder chooses the blocks it writes, beyond the level.</summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum DeflateStrategy
{
    /// <summary>Normal compression: matches and literals, dynamic or fixed or stored blocks, whichever is smallest.</summary>
    Default = 0,

    /// <summary>Literals only, Huffman coded. Fast, and good for data with no repeats (already filtered image rows, for example).</summary>
    HuffmanOnly = 1,

    /// <summary>Matches only at distance 1 (runs of one byte), plus literals. Fast, and good for simple images.</summary>
    RunLength = 2,

    /// <summary>Normal matching, but every block uses the fixed Huffman codes (RFC 1951 section 3.2.6): no table to write, so it suits tiny blocks.</summary>
    FixedCodes = 3,
}

/// <summary>How much a call to the encoder forces out (the flush modes of RFC 1951-based streams).</summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum DeflateFlush
{
    /// <summary>No flush: the encoder buffers input and writes blocks when it chooses.</summary>
    None = 0,

    /// <summary>Everything given so far is written and ends on a byte boundary, followed by an empty stored block (00 00 FF FF), so a reader can decode all of it. Later blocks may still match into earlier data.</summary>
    Sync = 1,

    /// <summary>Like <see cref="Sync"/>, and later blocks never match into earlier data, so the stream can be decoded from this point on its own.</summary>
    Full = 2,

    /// <summary>Ends the stream: the final block, and for zlib and gzip the trailer. No more input is accepted until <see cref="DeflateEncoder.Reset"/>.</summary>
    Finish = 3,
}

/// <summary>
/// Options for compressing to raw Deflate, zlib or gzip. The compressed bytes depend only on the input and these options.
/// </summary>
/// <remarks>
/// <para>Levels 1 to 9 line up with the levels of the common tools and 10 to 12 add an optimal parser, so "level N" compares to
/// the reference at level N. See <c>docs/deflate-encoder.md</c> for what each level does.</para>
/// </remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateOptions
{
    /// <summary>The default for <see cref="Level"/>: 6.</summary>
    public const int DefaultLevel = 6;

    /// <summary>The lowest level: stored blocks only.</summary>
    public const int MinLevel = 0;

    /// <summary>The highest level: 12.</summary>
    public const int MaxLevel = 12;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static DeflateOptions Default { get; } = new();

    /// <summary>
    /// Gets the compression level, 0 to 12. 0 stores without compressing; 1 is fastest; 1 to 3 take the first match found;
    /// 4 to 9 compare against the match one byte later (lazy matching) with deeper searches; 10 to 12 search every position and
    /// choose the cheapest sequence of literals and matches. The default is 6.
    /// </summary>
    public int Level { get; init; } = DefaultLevel;

    /// <summary>Gets the block strategy. The default is <see cref="DeflateStrategy.Default"/>.</summary>
    public DeflateStrategy Strategy { get; init; }

    /// <summary>
    /// Gets a preset dictionary: bytes both sides know, which the data may match into (the last 32768 bytes matter). For zlib the
    /// header names it by its Adler-32 (FDICT); for raw Deflate nothing in the stream names it. gzip has no dictionary and rejects one.
    /// The default is none.
    /// </summary>
    public ReadOnlyMemory<byte> Dictionary { get; init; }

    /// <summary>Gets the original file name for the gzip header (FNAME), written as ISO 8859-1; <see langword="null"/> for none. It may not contain a NUL or characters above U+00FF. Ignored by raw Deflate and zlib.</summary>
    public string? GZipFileName { get; init; }

    /// <summary>Gets the comment for the gzip header (FCOMMENT), written as ISO 8859-1; <see langword="null"/> for none. The same limits as <see cref="GZipFileName"/>.</summary>
    public string? GZipComment { get; init; }

    /// <summary>Gets the extra field for the gzip header (FEXTRA): sub-fields as RFC 1952 section 2.3.1.1 describes, at most 65535 bytes. The default is none.</summary>
    public ReadOnlyMemory<byte> GZipExtra { get; init; }

    /// <summary>Gets the modification time for the gzip header (MTIME): seconds since 1970-01-01 UTC, or 0 for none. The default is 0, so the output does not depend on the clock.</summary>
    public uint GZipModificationTime { get; init; }

    /// <summary>Gets the operating system byte for the gzip header (OS). The default is 255 (unknown), so the output does not depend on the host.</summary>
    public byte GZipOperatingSystem { get; init; } = 255;

    /// <summary>Gets a value indicating whether the gzip header marks the data as probably text (FTEXT). The default is false.</summary>
    public bool GZipIsText { get; init; }

    /// <summary>Gets a value indicating whether the gzip header carries a CRC-16 of itself (FHCRC). The default is false.</summary>
    public bool GZipHeaderCrc { get; init; }

    internal void Validate()
    {
        if (Level is < MinLevel or > MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(Level), Level, "The level must be from 0 to 12.");
        }

        if (!Enum.IsDefined(Strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(Strategy), Strategy, "Unknown strategy.");
        }

        if (GZipExtra.Length > 65535)
        {
            throw new ArgumentException("The gzip extra field is at most 65535 bytes.", nameof(GZipExtra));
        }

        CheckText(GZipFileName, nameof(GZipFileName));
        CheckText(GZipComment, nameof(GZipComment));
    }

    private static void CheckText(string? text, string name)
    {
        if (text is null)
        {
            return;
        }

        foreach (char c in text)
        {
            if (c == '\0' || c > '\u00FF')
            {
                throw new ArgumentException("gzip header text is ISO 8859-1 without NUL.", name);
            }
        }
    }
}
