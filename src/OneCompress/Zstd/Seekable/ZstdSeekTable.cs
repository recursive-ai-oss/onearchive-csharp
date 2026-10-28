using System.Diagnostics.CodeAnalysis;
using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>One entry of a <see cref="ZstdSeekTable"/>: where a frame lies in the compressed file and in the content.</summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly struct ZstdSeekFrame
{
    internal ZstdSeekFrame(long compressedOffset, long compressedSize, long decompressedOffset, long decompressedSize, uint checksum, bool hasChecksum)
    {
        CompressedOffset = compressedOffset;
        CompressedSize = compressedSize;
        DecompressedOffset = decompressedOffset;
        DecompressedSize = decompressedSize;
        Checksum = checksum;
        HasChecksum = hasChecksum;
    }

    /// <summary>Offset of the frame's first byte in the compressed file.</summary>
    public long CompressedOffset { get; }

    /// <summary>Size of the frame in the compressed file, in bytes.</summary>
    public long CompressedSize { get; }

    /// <summary>Offset of the frame's first byte in the decompressed content.</summary>
    public long DecompressedOffset { get; }

    /// <summary>Size of the frame's content; 0 for a skippable or empty frame.</summary>
    public long DecompressedSize { get; }

    /// <summary>The low 32 bits of the XXH64 digest of the frame's content; meaningful only when <see cref="HasChecksum"/> is true.</summary>
    public uint Checksum { get; }

    /// <summary>True when the table carries checksums.</summary>
    public bool HasChecksum { get; }
}

/// <summary>The decoded footer of a seek table, which says how large the table is before it is read.</summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly struct ZstdSeekTableFooter
{
    internal ZstdSeekTableFooter(int frameCount, bool hasChecksums, byte descriptor)
    {
        FrameCount = frameCount;
        HasChecksums = hasChecksums;
        Descriptor = descriptor;
    }

    /// <summary>The Number_Of_Frames field.</summary>
    public int FrameCount { get; }

    /// <summary>The Checksum_Flag bit of the descriptor.</summary>
    public bool HasChecksums { get; }

    /// <summary>The Seek_Table_Descriptor byte.</summary>
    public byte Descriptor { get; }

    /// <summary>Size of each entry: 8 bytes, or 12 with checksums.</summary>
    public int EntrySize => HasChecksums ? 12 : 8;

    /// <summary>Size of the whole seek table frame, including its 8-byte skippable header and the footer.</summary>
    public long EncodedLength => ZstdSeekTable.FrameHeaderSize + ((long)FrameCount * EntrySize) + ZstdSeekTable.FooterSize;
}

/// <summary>
/// The seek table of a seekable Zstandard file: for every frame, its compressed and decompressed size and optionally a
/// checksum, held as cumulative offsets so a content offset maps to its frame by binary search. The table is the last
/// skippable frame of the file; its layout is in the Zstandard seekable format document (version 0.1.0).
/// </summary>
/// <remarks>Instances are immutable and may be shared between threads.</remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdSeekTable
{
    /// <summary>The format name used in exceptions.</summary>
    public const string FormatName = "zstd-seekable";

    /// <summary>Skippable_Magic_Number of the seek table frame.</summary>
    public const uint SkippableMagic = 0x184D2A5E;

    /// <summary>Seekable_Magic_Number, the last four bytes of every seekable file.</summary>
    public const uint SeekableMagic = 0x8F92EAB1;

    /// <summary>Size of the Skippable_Magic_Number and Frame_Size fields.</summary>
    public const int FrameHeaderSize = 8;

    /// <summary>Size of the seek table footer.</summary>
    public const int FooterSize = 9;

    /// <summary>The default limit on the number of entries a reader accepts (4,194,304).</summary>
    public const int DefaultMaxFrames = 1 << 22;

    private const byte ChecksumFlag = 0x80;
    private const byte ReservedBits = 0x7C;

    // Smallest possible frame: a skippable frame with no content is 8 bytes.
    private const int MinFrameSize = 8;

    private readonly long[] _compressedOffsets;   // Count + 1 cumulative values
    private readonly long[] _decompressedOffsets; // Count + 1 cumulative values
    private readonly uint[]? _checksums;

    private ZstdSeekTable(long[] compressedOffsets, long[] decompressedOffsets, uint[]? checksums, long encodedLength)
    {
        _compressedOffsets = compressedOffsets;
        _decompressedOffsets = decompressedOffsets;
        _checksums = checksums;
        EncodedLength = encodedLength;
    }

    /// <summary>The number of frames the table lists.</summary>
    public int Count => _compressedOffsets.Length - 1;

    /// <summary>True when every entry carries a checksum.</summary>
    public bool HasChecksums => _checksums is not null;

    /// <summary>Total size of the frames in the compressed file (the offset where the seek table starts).</summary>
    public long CompressedLength => _compressedOffsets[^1];

    /// <summary>Total size of the decompressed content.</summary>
    public long DecompressedLength => _decompressedOffsets[^1];

    /// <summary>Size of the seek table frame in the file.</summary>
    public long EncodedLength { get; }

    /// <summary>Returns the entry for a frame.</summary>
    /// <param name="index">The frame number, from 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a frame of the table.</exception>
    public ZstdSeekFrame GetFrame(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new ZstdSeekFrame(
            _compressedOffsets[index],
            _compressedOffsets[index + 1] - _compressedOffsets[index],
            _decompressedOffsets[index],
            _decompressedOffsets[index + 1] - _decompressedOffsets[index],
            _checksums is null ? 0 : _checksums[index],
            _checksums is not null);
    }

    /// <summary>Finds the frame that holds a content offset. Frames with no content are never returned.</summary>
    /// <param name="decompressedOffset">An offset into the decompressed content.</param>
    /// <returns>The frame number, or -1 when the offset is negative or at or past <see cref="DecompressedLength"/>.</returns>
    public int FindFrame(long decompressedOffset)
    {
        if (decompressedOffset < 0 || decompressedOffset >= DecompressedLength)
        {
            return -1;
        }

        // First frame whose end lies beyond the offset.
        int lo = 0;
        int hi = Count - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_decompressedOffsets[mid + 1] > decompressedOffset)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    /// <summary>Validates and decodes the last nine bytes of a seekable file.</summary>
    /// <param name="footer">The last <see cref="FooterSize"/> bytes of the file.</param>
    /// <param name="fileLength">The size of the whole file.</param>
    /// <param name="maxFrames">The most entries to accept.</param>
    /// <returns>The footer, whose <see cref="ZstdSeekTableFooter.EncodedLength"/> is the size of the table frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="footer"/> is not nine bytes long.</exception>
    /// <exception cref="CompressionFormatException">The footer magic is wrong, a reserved descriptor bit is set, or the table would not fit in the file.</exception>
    /// <exception cref="CompressionLimitException">The footer declares more than <paramref name="maxFrames"/> entries.</exception>
    public static ZstdSeekTableFooter ReadFooter(ReadOnlySpan<byte> footer, long fileLength, int maxFrames = DefaultMaxFrames)
    {
        if (footer.Length != FooterSize)
        {
            throw new ArgumentException("The footer must be exactly 9 bytes.", nameof(footer));
        }

        long footerOffset = fileLength - FooterSize;
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[5..]) != SeekableMagic)
        {
            throw new CompressionFormatException(FormatName, footerOffset + 5, "Seekable_Magic_Number 0x8F92EAB1 at the end of the file");
        }

        byte descriptor = footer[4];
        if ((descriptor & ReservedBits) != 0)
        {
            throw new CompressionFormatException(FormatName, footerOffset + 4, "Seek_Table_Descriptor with its reserved bits 6-2 zero");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        if (count > (uint)Math.Max(maxFrames, 0))
        {
            throw new CompressionLimitException(FormatName, "MaxFrames", maxFrames, count);
        }

        var result = new ZstdSeekTableFooter((int)count, (descriptor & ChecksumFlag) != 0, descriptor);
        if (result.EncodedLength > fileLength)
        {
            throw new CompressionFormatException(FormatName, footerOffset, $"a seek table of {count} entries that fits in the {fileLength}-byte file");
        }

        return result;
    }

    /// <summary>Parses a complete seek table frame and checks it against the file it ends.</summary>
    /// <param name="tableFrame">The whole seek table frame: the skippable header, the entries and the footer.</param>
    /// <param name="fileLength">The size of the whole file; the table frame must be its last <c>tableFrame.Length</c> bytes.</param>
    /// <param name="maxFrames">The most entries to accept.</param>
    /// <exception cref="CompressionFormatException">The table is malformed, or its sizes do not add up to the file's length.</exception>
    /// <exception cref="CompressionLimitException">The table has more than <paramref name="maxFrames"/> entries.</exception>
    public static ZstdSeekTable Parse(ReadOnlySpan<byte> tableFrame, long fileLength, int maxFrames = DefaultMaxFrames)
    {
        if (tableFrame.Length < FrameHeaderSize + FooterSize)
        {
            throw new CompressionFormatException(FormatName, Math.Max(0, fileLength - tableFrame.Length), "a seek table of at least 17 bytes", isTruncated: true);
        }

        ZstdSeekTableFooter footer = ReadFooter(tableFrame[^FooterSize..], fileLength, maxFrames);
        long tableStart = fileLength - footer.EncodedLength;
        if (tableFrame.Length != footer.EncodedLength)
        {
            throw new ArgumentException("The table frame length does not match its footer.", nameof(tableFrame));
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(tableFrame) != SkippableMagic)
        {
            throw new CompressionFormatException(FormatName, tableStart, "Skippable_Magic_Number 0x184D2A5E at the start of the seek table");
        }

        uint frameSize = BinaryPrimitives.ReadUInt32LittleEndian(tableFrame[4..]);
        if (frameSize != footer.EncodedLength - FrameHeaderSize)
        {
            throw new CompressionFormatException(FormatName, tableStart + 4, $"Frame_Size {footer.EncodedLength - FrameHeaderSize} for {footer.FrameCount} entries");
        }

        int n = footer.FrameCount;
        var compressed = new long[n + 1];
        var decompressed = new long[n + 1];
        uint[]? checksums = footer.HasChecksums ? new uint[n] : null;
        ReadOnlySpan<byte> entries = tableFrame[FrameHeaderSize..];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> e = entries.Slice(i * footer.EntrySize, footer.EntrySize);
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(e);
            if (c < MinFrameSize)
            {
                throw new CompressionFormatException(FormatName, tableStart + FrameHeaderSize + ((long)i * footer.EntrySize), $"Compressed_Size of frame {i} of at least {MinFrameSize}");
            }

            compressed[i + 1] = compressed[i] + c;
            decompressed[i + 1] = decompressed[i] + BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            if (checksums is not null)
            {
                checksums[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            }
        }

        // The frames must run exactly up to the table; a gap or overlap means the table does not describe this file.
        if (compressed[n] != tableStart)
        {
            throw new CompressionFormatException(FormatName, Math.Min(compressed[n], tableStart), $"the {n} frames to fill the file up to the seek table at offset {tableStart}");
        }

        return new ZstdSeekTable(compressed, decompressed, checksums, footer.EncodedLength);
    }

    /// <summary>Reads the seek table from the end of a seekable stream.</summary>
    /// <param name="source">A readable, seekable stream holding the whole file. Its position is changed.</param>
    /// <param name="maxFrames">The most entries to accept.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> cannot read or seek.</exception>
    /// <exception cref="CompressionFormatException">The file has no valid seek table.</exception>
    /// <exception cref="CompressionLimitException">The table has more than <paramref name="maxFrames"/> entries.</exception>
    public static ZstdSeekTable Read(Stream source, int maxFrames = DefaultMaxFrames)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("A seekable file can only be opened on a readable stream that can seek; read a non-seekable stream sequentially with ZstdStream, which skips the seek table.", nameof(source));
        }

        long length = source.Length;
        if (length < FrameHeaderSize + FooterSize)
        {
            throw new CompressionFormatException(FormatName, length, "a seek table of at least 17 bytes at the end of the file", isTruncated: true);
        }

        Span<byte> footerBytes = stackalloc byte[FooterSize];
        source.Seek(length - FooterSize, SeekOrigin.Begin);
        source.ReadExactly(footerBytes);
        ZstdSeekTableFooter footer = ReadFooter(footerBytes, length, maxFrames);

        // The footer's count was checked against maxFrames and against the file size, so this allocation is bounded by both.
        byte[] frame = new byte[footer.EncodedLength];
        source.Seek(length - footer.EncodedLength, SeekOrigin.Begin);
        source.ReadExactly(frame);
        return Parse(frame, length, maxFrames);
    }

    /// <summary>Reads the seek table from a file held in memory.</summary>
    /// <param name="file">The whole seekable file.</param>
    /// <param name="maxFrames">The most entries to accept.</param>
    /// <exception cref="CompressionFormatException">The file has no valid seek table.</exception>
    /// <exception cref="CompressionLimitException">The table has more than <paramref name="maxFrames"/> entries.</exception>
    public static ZstdSeekTable Read(ReadOnlySpan<byte> file, int maxFrames = DefaultMaxFrames)
    {
        if (file.Length < FrameHeaderSize + FooterSize)
        {
            throw new CompressionFormatException(FormatName, file.Length, "a seek table of at least 17 bytes at the end of the file", isTruncated: true);
        }

        ZstdSeekTableFooter footer = ReadFooter(file[^FooterSize..], file.Length, maxFrames);
        return Parse(file[^(int)footer.EncodedLength..], file.Length, maxFrames);
    }
}

/// <summary>
/// Builds the encoded seek table frame one entry at a time. It keeps the entries in their encoded form, so a table of a
/// million frames costs 8 or 12 MB and nothing else.
/// </summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdSeekTableBuilder
{
    private byte[] _entries = new byte[256];
    private int _used;

    /// <summary>Creates a builder.</summary>
    /// <param name="checksums">Whether entries carry a checksum (sets the descriptor's Checksum_Flag).</param>
    public ZstdSeekTableBuilder(bool checksums)
    {
        HasChecksums = checksums;
    }

    /// <summary>True when entries carry a checksum.</summary>
    public bool HasChecksums { get; }

    /// <summary>The number of entries added.</summary>
    public int Count { get; private set; }

    /// <summary>The size of the encoded seek table frame as it stands.</summary>
    public long EncodedLength => ZstdSeekTable.FrameHeaderSize + _used + ZstdSeekTable.FooterSize;

    /// <summary>Appends an entry.</summary>
    /// <param name="compressedSize">The frame's size in the file.</param>
    /// <param name="decompressedSize">The frame's content size; 0 for a skippable frame.</param>
    /// <param name="checksum">The low 32 bits of the XXH64 digest of the content; ignored when the builder has no checksums.</param>
    /// <exception cref="InvalidOperationException">The table already holds the most entries it can (<see cref="int.MaxValue"/> divided by the entry size).</exception>
    public void Add(uint compressedSize, uint decompressedSize, uint checksum = 0)
    {
        int entrySize = HasChecksums ? 12 : 8;
        if (_used > int.MaxValue / 2 - entrySize)
        {
            throw new InvalidOperationException("The seek table holds the most entries it can.");
        }

        if (_used + entrySize > _entries.Length)
        {
            Array.Resize(ref _entries, Math.Max(_entries.Length * 2, _used + entrySize));
        }

        Span<byte> e = _entries.AsSpan(_used, entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(e, compressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(e[4..], decompressedSize);
        if (HasChecksums)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], checksum);
        }

        _used += entrySize;
        Count++;
    }

    /// <summary>Forgets every entry so the builder can be used again.</summary>
    public void Clear()
    {
        _used = 0;
        Count = 0;
    }

    /// <summary>Writes the complete seek table frame: skippable header, entries, footer.</summary>
    /// <param name="destination">At least <see cref="EncodedLength"/> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public int WriteTo(Span<byte> destination)
    {
        long total = EncodedLength;
        if (destination.Length < total)
        {
            throw new ArgumentException("The destination is too small for the seek table.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, ZstdSeekTable.SkippableMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)(total - ZstdSeekTable.FrameHeaderSize));
        _entries.AsSpan(0, _used).CopyTo(destination[ZstdSeekTable.FrameHeaderSize..]);
        Span<byte> footer = destination.Slice(ZstdSeekTable.FrameHeaderSize + _used, ZstdSeekTable.FooterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, (uint)Count);
        footer[4] = HasChecksums ? (byte)0x80 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(footer[5..], ZstdSeekTable.SeekableMagic);
        return (int)total;
    }

    /// <summary>Writes the complete seek table frame to a buffer writer.</summary>
    /// <param name="destination">Receives the frame.</param>
    public void WriteTo(IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int length = checked((int)EncodedLength);
        WriteTo(destination.GetSpan(length));
        destination.Advance(length);
    }

    /// <summary>Returns the complete seek table frame.</summary>
    public byte[] ToArray()
    {
        byte[] result = new byte[checked((int)EncodedLength)];
        WriteTo(result);
        return result;
    }
}
