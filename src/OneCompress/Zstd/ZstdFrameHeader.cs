using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// The parsed header of a Zstandard frame or skippable frame (RFC 8878 §3.1.1.1 and §3.1.2).
/// </summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly struct ZstdFrameHeader
{
    internal ZstdFrameHeader(
        int headerSize,
        bool isSkippable,
        int skippableVariant,
        uint userDataSize,
        long windowSize,
        bool hasContentSize,
        ulong contentSize,
        uint dictionaryId,
        bool hasChecksum,
        bool isSingleSegment)
    {
        HeaderSize = headerSize;
        IsSkippable = isSkippable;
        SkippableVariant = skippableVariant;
        UserDataSize = userDataSize;
        WindowSize = windowSize;
        HasContentSize = hasContentSize;
        ContentSize = contentSize;
        DictionaryId = dictionaryId;
        HasChecksum = hasChecksum;
        IsSingleSegment = isSingleSegment;
    }

    /// <summary>Gets the size in bytes of the header, including the magic number (and the size field of a skippable frame).</summary>
    public int HeaderSize { get; }

    /// <summary>Gets a value indicating whether this is a skippable frame, whose payload follows the header.</summary>
    public bool IsSkippable { get; }

    /// <summary>Gets the low four bits of a skippable frame's magic number (0 to 15), or 0 for a Zstandard frame.</summary>
    public int SkippableVariant { get; }

    /// <summary>Gets the size of a skippable frame's payload, or 0 for a Zstandard frame.</summary>
    public uint UserDataSize { get; }

    /// <summary>Gets the window size the frame needs (for a single-segment frame, its content size); 0 for a skippable frame.</summary>
    public long WindowSize { get; }

    /// <summary>Gets a value indicating whether the header states the decompressed size.</summary>
    public bool HasContentSize { get; }

    /// <summary>Gets the decompressed size stated in the header; meaningful only when <see cref="HasContentSize"/> is true.</summary>
    public ulong ContentSize { get; }

    /// <summary>Gets the dictionary ID, or 0 when the frame names none.</summary>
    public uint DictionaryId { get; }

    /// <summary>Gets a value indicating whether a 4-byte content checksum follows the last block.</summary>
    public bool HasChecksum { get; }

    /// <summary>Gets a value indicating whether the frame is a single segment (it has no window descriptor).</summary>
    public bool IsSingleSegment { get; }

    /// <summary>
    /// Parses a frame header from the start of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Bytes starting at a frame boundary.</param>
    /// <param name="header">The header, when the method returns true.</param>
    /// <param name="bytesNeeded">
    /// When the method returns true, the header size. When it returns false, the number of bytes (counted from
    /// the start of <paramref name="source"/>) the caller must have before trying again.
    /// </param>
    /// <param name="streamOffset">Offset of <paramref name="source"/> in the compressed input, for error messages.</param>
    /// <returns>True when a complete header was parsed; false when <paramref name="source"/> is too short.</returns>
    /// <exception cref="CompressionFormatException">The magic number is unknown or the header sets a reserved bit.</exception>
    public static bool TryParse(ReadOnlySpan<byte> source, out ZstdFrameHeader header, out int bytesNeeded, long streamOffset = 0)
    {
        header = default;
        if (source.Length < 4)
        {
            bytesNeeded = 4;
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if ((magic & 0xFFFFFFF0) == ZstdFormat.SkippableMagicBase)
        {
            // RFC 8878 §3.1.2: magic, then a 32-bit payload size.
            if (source.Length < 8)
            {
                bytesNeeded = 8;
                return false;
            }

            header = new ZstdFrameHeader(8, true, (int)(magic & 15), BinaryPrimitives.ReadUInt32LittleEndian(source[4..]), 0, false, 0, 0, false, false);
            bytesNeeded = 8;
            return true;
        }

        if (magic != ZstdFormat.FrameMagic)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, $"a Zstandard magic number (0xFD2FB528) or a skippable frame magic (0x184D2A5?), found 0x{magic:X8}");
        }

        if (source.Length < 5)
        {
            bytesNeeded = 5;
            return false;
        }

        // RFC 8878 §3.1.1.1.1: Frame_Header_Descriptor.
        byte descriptor = source[4];
        if ((descriptor & 0x08) != 0)
        {
            ZstdFormat.ThrowCorrupt(streamOffset + 4, "a frame header descriptor with the reserved bit clear");
        }

        int fcsFlag = descriptor >> 6;
        bool singleSegment = (descriptor & 0x20) != 0;
        bool hasChecksum = (descriptor & 0x04) != 0;
        int didSize = (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        int fcsSize = fcsFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        int windowDescriptorSize = singleSegment ? 0 : 1;
        int size = 5 + windowDescriptorSize + didSize + fcsSize;
        if (source.Length < size)
        {
            bytesNeeded = size;
            return false;
        }

        int p = 5;
        long windowSize = 0;
        if (!singleSegment)
        {
            // RFC 8878 §3.1.1.1.2: Window_Descriptor.
            int exponent = source[p] >> 3;
            int mantissa = source[p] & 7;
            long windowBase = 1L << (10 + exponent);
            windowSize = windowBase + ((windowBase >> 3) * mantissa);
            p++;
        }

        uint dictionaryId = didSize switch
        {
            0 => 0,
            1 => source[p],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(source[p..]),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(source[p..]),
        };
        p += didSize;

        bool hasContentSize = fcsSize != 0;
        ulong contentSize = fcsSize switch
        {
            0 => 0,
            1 => source[p],
            2 => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(source[p..]) + 256, // RFC 8878 §3.1.1.1.4: the 2-byte form adds 256
            4 => BinaryPrimitives.ReadUInt32LittleEndian(source[p..]),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(source[p..]),
        };

        if (singleSegment)
        {
            windowSize = contentSize > long.MaxValue ? long.MaxValue : (long)contentSize;
        }

        header = new ZstdFrameHeader(size, false, 0, 0, windowSize, hasContentSize, contentSize, dictionaryId, hasChecksum, singleSegment);
        bytesNeeded = size;
        return true;
    }
}
