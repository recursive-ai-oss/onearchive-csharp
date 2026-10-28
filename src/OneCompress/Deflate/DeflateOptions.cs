using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>The wrapper around the Deflate data (RFC 1951 compressed blocks).</summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum DeflateFormat
{
    /// <summary>Raw Deflate (RFC 1951): a sequence of blocks with no header and no checksum.</summary>
    Raw = 0,

    /// <summary>zlib (RFC 1950): a two-byte header, an optional preset-dictionary ID, the blocks, and an Adler-32 of the output.</summary>
    ZLib = 1,

    /// <summary>gzip (RFC 1952): a member header with optional fields, the blocks, a CRC-32 and the output length modulo 2^32. A file may hold several members.</summary>
    GZip = 2,
}

/// <summary>
/// Options for decompressing Deflate, zlib and gzip data. The limits protect the caller from input that asks for more
/// output or memory than it should.
/// </summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateDecompressionOptions
{
    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>The default for <see cref="MaxHeaderFieldSize"/>: 65535 bytes, the most a gzip FEXTRA field can hold.</summary>
    public const int DefaultMaxHeaderFieldSize = 65535;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static DeflateDecompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest total output, in bytes, a decoder produces between resets. Output that would pass it raises
    /// <see cref="CompressionLimitException"/> before it is written; a decoder with a destination buffer never writes past the limit.
    /// The default is 1 GiB; a decoder that streams an unbounded sequence of gzip members needs <see cref="long.MaxValue"/>.
    /// </summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>Gets a value indicating whether the Adler-32 (zlib), the CRC-32 and length (gzip) and the gzip header CRC-16 are verified. The default is true.</summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets the preset dictionary: bytes that count as output preceding the stream, so matches may reach into them (the last 32768 bytes matter).
    /// For zlib, a stream that sets FDICT names its dictionary by Adler-32; it is accepted when this dictionary's Adler-32
    /// matches, and otherwise raises <see cref="CompressionDictionaryException"/> (also when none is given). Raw Deflate has
    /// no way to name a dictionary: this one is used as given. gzip streams cannot use a dictionary. The default is none.
    /// </summary>
    public ReadOnlyMemory<byte> Dictionary { get; init; }

    /// <summary>
    /// Gets the longest gzip header field (FEXTRA data, FNAME or FCOMMENT, excluding the terminator) a decoder keeps.
    /// A longer field raises <see cref="CompressionLimitException"/>. The default is 65535.
    /// </summary>
    public int MaxHeaderFieldSize { get; init; } = DefaultMaxHeaderFieldSize;

    /// <summary>
    /// Gets a value indicating whether input after the end of the stream is ignored by <see cref="Deflate"/>, <see cref="ZLib"/>,
    /// <see cref="GZip"/> and the read-mode stream, instead of raising <see cref="CompressionFormatException"/>. For gzip, trailing bytes
    /// that do not start a member (1F 8B) are the ignored part. The default is false.
    /// </summary>
    public bool AllowTrailingData { get; init; }
}

/// <summary>
/// The fields of a gzip member header (RFC 1952 section 2.3). The text and byte fields are owned by the decoder that produced
/// the value and stay valid until that decoder starts its next member or is reset.
/// </summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly struct GZipHeader
{
    internal GZipHeader(byte flags, uint modificationTime, byte extraFlags, byte operatingSystem, ReadOnlyMemory<byte> extra, ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> comment)
    {
        Flags = flags;
        ModificationTime = modificationTime;
        ExtraFlags = extraFlags;
        OperatingSystem = operatingSystem;
        Extra = extra;
        NameBytes = name;
        CommentBytes = comment;
    }

    /// <summary>Gets the FLG byte: bit 0 FTEXT, 1 FHCRC, 2 FEXTRA, 3 FNAME, 4 FCOMMENT.</summary>
    public byte Flags { get; }

    /// <summary>Gets a value indicating whether the producer flagged the data as probably text (FTEXT).</summary>
    public bool IsText => (Flags & 1) != 0;

    /// <summary>Gets MTIME: seconds since 1970-01-01 UTC when the file was last modified, or 0 when unavailable.</summary>
    public uint ModificationTime { get; }

    /// <summary>Gets XFL, the extra flags (2 means best compression, 4 means fastest).</summary>
    public byte ExtraFlags { get; }

    /// <summary>Gets OS: 0 FAT, 3 Unix, 7 Macintosh, 11 NTFS, 255 unknown (RFC 1952 section 2.3.1).</summary>
    public byte OperatingSystem { get; }

    /// <summary>Gets the FEXTRA data (sub-fields as RFC 1952 section 2.3.1.1 describes), empty when absent.</summary>
    public ReadOnlyMemory<byte> Extra { get; }

    /// <summary>Gets the original file name's bytes (ISO 8859-1 by RFC 1952), without the terminating zero; empty when absent.</summary>
    public ReadOnlyMemory<byte> NameBytes { get; }

    /// <summary>Gets the comment's bytes (ISO 8859-1 by RFC 1952), without the terminating zero; empty when absent.</summary>
    public ReadOnlyMemory<byte> CommentBytes { get; }

    /// <summary>Gets a value indicating whether the header has an FNAME field (it may be empty).</summary>
    public bool HasName => (Flags & 8) != 0;

    /// <summary>Gets a value indicating whether the header has an FCOMMENT field (it may be empty).</summary>
    public bool HasComment => (Flags & 16) != 0;

    /// <summary>Gets the file name decoded as ISO 8859-1, or <see langword="null"/> when there is no FNAME field.</summary>
    /// <returns>The name.</returns>
    public string? GetName() => HasName ? System.Text.Encoding.Latin1.GetString(NameBytes.Span) : null;

    /// <summary>Gets the comment decoded as ISO 8859-1, or <see langword="null"/> when there is no FCOMMENT field.</summary>
    /// <returns>The comment.</returns>
    public string? GetComment() => HasComment ? System.Text.Encoding.Latin1.GetString(CommentBytes.Span) : null;
}
