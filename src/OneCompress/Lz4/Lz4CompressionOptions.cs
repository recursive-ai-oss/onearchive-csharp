namespace RecursiveAI.OneCompress.Lz4;

/// <summary>The maximum uncompressed size of a block in an LZ4 frame (Frame format, "Block Maximum Size"). The value is the code stored in the frame descriptor.</summary>
public enum Lz4BlockSize
{
    /// <summary>64 KiB.</summary>
    Size64KiB = 4,

    /// <summary>256 KiB.</summary>
    Size256KiB = 5,

    /// <summary>1 MiB.</summary>
    Size1MiB = 6,

    /// <summary>4 MiB, the default.</summary>
    Size4MiB = 7,
}

/// <summary>
/// Settings for LZ4 compression: the level, and for frames the block layout, checksums and dictionary. The same type
/// serves blocks (<see cref="Lz4Block"/>, which uses only <see cref="Level"/> and <see cref="Dictionary"/>), frames
/// (<see cref="Lz4"/>), <see cref="Lz4Encoder"/> and <see cref="Lz4Stream"/>.
/// </summary>
/// <remarks>Options are read when an encoder or stream is created; changing nothing is possible afterwards because every property is init-only.</remarks>
public sealed class Lz4CompressionOptions
{
    /// <summary>The default level: 1, the fast level.</summary>
    public const int DefaultLevel = 1;

    /// <summary>The lowest level: -65536, the fastest and least compressing (the lz4 tool's <c>--fast=65536</c>).</summary>
    public const int MinLevel = -65536;

    /// <summary>The highest level: 12, the slowest and best compressing.</summary>
    public const int MaxLevel = 12;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static Lz4CompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the compression level; the default is <see cref="DefaultLevel"/>. The levels follow the lz4 tool: 1 (and 0) is
    /// the fast level; <c>-N</c> is the tool's <c>--fast=N</c>, which skips ahead faster through data that does not match and
    /// trades ratio for speed (down to <see cref="MinLevel"/>); 2 to 12 search harder, with 10 to 12 choosing matches by the
    /// total size of the block. The exact parameters are in the encoder's documentation page.
    /// </summary>
    public int Level { get; init; } = DefaultLevel;

    /// <summary>Gets the largest block of a frame; the default is <see cref="Lz4BlockSize.Size4MiB"/>. Smaller blocks use less memory and let a reader start sooner.</summary>
    public Lz4BlockSize BlockSize { get; init; } = Lz4BlockSize.Size4MiB;

    /// <summary>
    /// Gets whether each block of a frame is compressed without reference to the one before (the default, as the lz4 tool
    /// does), which lets blocks be decoded separately. False links blocks, so a block may match the previous 64 KiB; that
    /// compresses better, most of all with small blocks.
    /// </summary>
    public bool BlocksIndependent { get; init; } = true;

    /// <summary>Gets whether each block carries a checksum of its compressed bytes; the default is false.</summary>
    public bool BlockChecksum { get; init; }

    /// <summary>Gets whether the frame ends with a checksum of all the uncompressed data; the default is true.</summary>
    public bool ContentChecksum { get; init; } = true;

    /// <summary>
    /// Gets the uncompressed size to write in the frame header, or <see langword="null"/> (the default) for none. The
    /// encoder and stream check that exactly this many bytes arrive. <see cref="Lz4.Compress(ReadOnlySpan{byte}, Lz4CompressionOptions?)"/>
    /// knows the size and writes it unless <see cref="IncludeContentSize"/> is false.
    /// </summary>
    public long? ContentSize { get; init; }

    /// <summary>Gets whether the one-call <see cref="Lz4.Compress(ReadOnlySpan{byte}, Lz4CompressionOptions?)"/> writes the input's size in the header when <see cref="ContentSize"/> is not set; the default is true.</summary>
    public bool IncludeContentSize { get; init; } = true;

    /// <summary>
    /// Gets the dictionary to compress against, or <see langword="null"/> (the default) for none. A frame names it with
    /// <see cref="Lz4Dictionary.Id"/> when that is not zero. With independent blocks every block starts from the
    /// dictionary; with linked blocks only the first does. The reader must supply the same dictionary.
    /// </summary>
    public Lz4Dictionary? Dictionary { get; init; }

    internal void Validate()
    {
        if (Level < MinLevel || Level > MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(Level), Level, "The level must be from -65536 to 12.");
        }

        if (BlockSize is < Lz4BlockSize.Size64KiB or > Lz4BlockSize.Size4MiB)
        {
            throw new ArgumentOutOfRangeException(nameof(BlockSize), BlockSize, "The block size must be one of the four sizes the frame format defines.");
        }

        if (ContentSize is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ContentSize), ContentSize, "The content size must not be negative.");
        }
    }

    internal int BlockBytes => 1 << (8 + (2 * (int)BlockSize));
}
