using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// The header of an LZ4 frame (Frame Format, "Frame Descriptor"): the flags, the block size, and the optional content
/// size and dictionary ID.
/// </summary>
public readonly struct Lz4FrameHeader
{
    internal Lz4FrameHeader(bool blocksIndependent, bool hasBlockChecksum, bool hasContentChecksum, long contentSize, uint? dictionaryId, int blockMaxSize, int length)
    {
        BlocksIndependent = blocksIndependent;
        HasBlockChecksum = hasBlockChecksum;
        HasContentChecksum = hasContentChecksum;
        ContentSizeOrMinusOne = contentSize;
        DictionaryId = dictionaryId;
        BlockMaxSize = blockMaxSize;
        Length = length;
    }

    /// <summary>Gets a value indicating whether blocks are independent (true) or each may refer to the 64 KiB before it (false, "linked blocks").</summary>
    public bool BlocksIndependent { get; }

    /// <summary>Gets a value indicating whether each data block is followed by an xxHash32 of its compressed bytes.</summary>
    public bool HasBlockChecksum { get; }

    /// <summary>Gets a value indicating whether an xxHash32 of the decoded content follows the end mark.</summary>
    public bool HasContentChecksum { get; }

    /// <summary>Gets the decoded size the header declares, or <see langword="null"/> when it does not say.</summary>
    public long? ContentSize => ContentSizeOrMinusOne < 0 ? null : ContentSizeOrMinusOne;

    internal long ContentSizeOrMinusOne { get; }

    /// <summary>Gets the dictionary ID the frame names, or <see langword="null"/> when it names none.</summary>
    public uint? DictionaryId { get; }

    /// <summary>Gets the largest decoded block size the frame uses: 64 KiB, 256 KiB, 1 MiB or 4 MiB.</summary>
    public int BlockMaxSize { get; }

    /// <summary>Gets the size of the header in bytes, magic number included (7 to 19).</summary>
    public int Length { get; }

    /// <summary>
    /// Parses the header at the start of <paramref name="source"/>, which begins with the frame's magic number 0x184D2204.
    /// </summary>
    /// <param name="source">Bytes starting at a frame boundary.</param>
    /// <param name="header">The header, when the method returns <see cref="OperationStatus.Done"/>.</param>
    /// <param name="streamOffset">The position of <paramref name="source"/> in the caller's input, added to the offsets in errors.</param>
    /// <returns><see cref="OperationStatus.Done"/>, or <see cref="OperationStatus.NeedMoreData"/> when <paramref name="source"/> ends inside the header.</returns>
    /// <exception cref="CompressionFormatException">The magic number, the version, a reserved bit, the block size or the header checksum is wrong.</exception>
    public static OperationStatus TryParse(ReadOnlySpan<byte> source, out Lz4FrameHeader header, long streamOffset = 0)
    {
        header = default;
        if (source.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(source) != Lz4Format.FrameMagic)
        {
            Lz4Format.ThrowInvalid(streamOffset, "the frame magic number 0x184D2204");
        }

        if (source.Length < 6)
        {
            return OperationStatus.NeedMoreData;
        }

        int flg = source[4];
        int bd = source[5];

        // Frame format, "FLG byte": version in bits 7-6 must be 01; bit 1 is reserved.
        if ((flg >> 6) != 1)
        {
            Lz4Format.ThrowInvalid(streamOffset + 4, "frame version 1 (FLG bits 7-6 equal to 01)");
        }

        if ((flg & 0x02) != 0)
        {
            Lz4Format.ThrowInvalid(streamOffset + 4, "FLG reserved bit 1 to be zero");
        }

        // "BD byte": bit 7 and bits 3-0 are reserved; bits 6-4 give the block maximum size, valid from 4 to 7.
        if ((bd & 0x8F) != 0)
        {
            Lz4Format.ThrowInvalid(streamOffset + 5, "BD reserved bits to be zero");
        }

        int sizeId = (bd >> 4) & 7;
        if (sizeId < 4)
        {
            Lz4Format.ThrowInvalid(streamOffset + 5, "a block maximum size code from 4 to 7");
        }

        bool hasContentSize = (flg & 0x08) != 0;
        bool hasDictionaryId = (flg & 0x01) != 0;
        int length = 4 + 2 + (hasContentSize ? 8 : 0) + (hasDictionaryId ? 4 : 0) + 1;
        if (source.Length < length)
        {
            return OperationStatus.NeedMoreData;
        }

        // "Header Checksum": the second byte of xxh32 of the descriptor (FLG through the optional fields), seed 0.
        byte expected = (byte)(XxHash32.Compute(source.Slice(4, length - 5)) >> 8);
        if (source[length - 1] != expected)
        {
            Lz4Format.ThrowInvalid(streamOffset + length - 1, $"header checksum 0x{expected:X2}");
        }

        int at = 6;
        long contentSize = -1;
        if (hasContentSize)
        {
            ulong declared = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(at));
            if (declared > long.MaxValue)
            {
                Lz4Format.ThrowInvalid(streamOffset + at, "a content size below 2^63");
            }

            contentSize = (long)declared;
            at += 8;
        }

        uint? dictionaryId = hasDictionaryId ? BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(at)) : null;
        header = new Lz4FrameHeader(
            blocksIndependent: (flg & 0x20) != 0,
            hasBlockChecksum: (flg & 0x10) != 0,
            hasContentChecksum: (flg & 0x04) != 0,
            contentSize,
            dictionaryId,
            blockMaxSize: 1 << (8 + (2 * sizeId)),
            length);
        return OperationStatus.Done;
    }
}
