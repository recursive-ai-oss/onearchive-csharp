using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Writes the parts of a Zstandard frame around its blocks (RFC 8878 §3.1): the header, the checksum and skippable frames.
/// Blocks from any source (this library's block writer, or a parallel compressor's) can sit between a header and a checksum.
/// </summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class ZstdFrameWriter
{
    /// <summary>The largest frame header: magic, descriptor, window descriptor, 4-byte dictionary ID and 8-byte content size.</summary>
    public const int MaxHeaderSize = ZstdFormat.FrameHeaderSizeMax;

    /// <summary>
    /// Writes a frame header (RFC 8878 §3.1.1.1).
    /// </summary>
    /// <param name="destination">Receives the header, at most <see cref="MaxHeaderSize"/> bytes.</param>
    /// <param name="windowLog">The log of the window size the decoder must provide, 10 to 41. Ignored when the frame is single-segment.</param>
    /// <param name="contentSize">The number of bytes the frame decompresses to, or -1 to leave it out.</param>
    /// <param name="dictionaryId">The dictionary ID to write, or 0 to write none.</param>
    /// <param name="checksum">Whether the frame will end with a content checksum.</param>
    /// <returns>The number of bytes written.</returns>
    /// <remarks>
    /// When the content size is given and fits the window, the frame is written as single-segment: the window descriptor is left out
    /// and the decoder uses the content size as the window, which saves a byte and tells it to allocate exactly what is needed.
    /// </remarks>
    public static int WriteHeader(Span<byte> destination, int windowLog, long contentSize, uint dictionaryId, bool checksum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowLog, ZstdCompressionParameters.MinWindowLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLog, 41);
        bool known = contentSize >= 0;
        bool single = known && contentSize <= (1L << Math.Min(windowLog, 62));
        int fcsFlag;
        int fcsBytes;
        if (!known)
        {
            fcsFlag = 0;
            fcsBytes = 0;
        }
        else if (single && contentSize < 256)
        {
            fcsFlag = 0;
            fcsBytes = 1;
        }
        else if (contentSize < 256 + 65536)
        {
            fcsFlag = 1;
            fcsBytes = 2;
        }
        else if (contentSize <= uint.MaxValue)
        {
            fcsFlag = 2;
            fcsBytes = 4;
        }
        else
        {
            fcsFlag = 3;
            fcsBytes = 8;
        }

        int dictFlag = dictionaryId == 0 ? 0 : dictionaryId < 256 ? 1 : dictionaryId < 65536 ? 2 : 3;
        int dictBytes = dictFlag == 0 ? 0 : dictFlag == 3 ? 4 : dictFlag;
        BinaryPrimitives.WriteUInt32LittleEndian(destination, ZstdFormat.FrameMagic);
        int p = 4;
        destination[p++] = (byte)((fcsFlag << 6) | (single ? 0x20 : 0) | (checksum ? 0x04 : 0) | dictFlag);
        if (!single)
        {
            destination[p++] = (byte)((windowLog - ZstdCompressionParameters.MinWindowLog) << 3);
        }

        for (int i = 0; i < dictBytes; i++)
        {
            destination[p++] = (byte)(dictionaryId >> (8 * i));
        }

        if (fcsBytes == 1)
        {
            destination[p++] = (byte)contentSize;
        }
        else if (fcsBytes == 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[p..], (ushort)(contentSize - 256));
            p += 2;
        }
        else if (fcsBytes == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[p..], (uint)contentSize);
            p += 4;
        }
        else if (fcsBytes == 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination[p..], (ulong)contentSize);
            p += 8;
        }

        return p;
    }

    /// <summary>Writes the content checksum that ends a frame: the low 32 bits of the XXH64 of the content (RFC 8878 §3.1.1.4).</summary>
    /// <param name="destination">Receives 4 bytes.</param>
    /// <param name="contentHash">The XXH64 (seed 0) of the frame's whole content.</param>
    /// <returns>4.</returns>
    public static int WriteChecksum(Span<byte> destination, ulong contentHash)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)contentHash);
        return 4;
    }

    /// <summary>Writes a skippable frame (RFC 8878 §3.1.2): decoders skip it, so it can carry application data between frames.</summary>
    /// <param name="destination">Receives 8 bytes plus the data.</param>
    /// <param name="variant">Which of the 16 magic numbers to use, 0 to 15.</param>
    /// <param name="data">The payload, at most 4 GiB - 1.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteSkippableFrame(Span<byte> destination, int variant, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(variant, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(variant, 15);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, ZstdFormat.SkippableMagicBase + (uint)variant);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)data.Length);
        data.CopyTo(destination[8..]);
        return 8 + data.Length;
    }
}
