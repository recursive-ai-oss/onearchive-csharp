using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Lzx;

namespace RecursiveAI.OneArchive.Cab;

/// <summary>
/// The cabinet checksum of [MS-CAB] 3.1: the XOR of the little-endian 32-bit words, the last partial word read backwards. A data block's
/// checksum covers its data, then cbData and cbUncomp; the data reserve is not part of it (the specification's "header area" is those
/// four bytes, which a reference extractor confirms with reserve areas present).
/// </summary>
internal static class CabChecksum
{
    public static uint Compute(ReadOnlySpan<byte> data, uint seed)
    {
        uint c = seed;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            c ^= BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
        }

        // The remainder bytes, last byte lowest: ab[n-1] | ab[n-2] << 8 | ab[n-3] << 16.
        switch (data.Length - i)
        {
            case 3:
                c ^= (uint)(data[i] << 16 | data[i + 1] << 8 | data[i + 2]);
                break;
            case 2:
                c ^= (uint)(data[i] << 8 | data[i + 1]);
                break;
            case 1:
                c ^= data[i];
                break;
        }

        return c;
    }
}

/// <summary>
/// Decodes one folder's data blocks in order ([MS-CAB] 2.4). MSZIP and LZX carry history from block to block, so the cursor only
/// moves forward; asking for an earlier position starts the folder again.
/// </summary>
internal sealed class CabFolderCursor(Stream file, long origin, long fileLength, CabHeader header, CabFolder folder, bool verify, ExpansionMeter meter) : IDisposable
{
    private const int HistorySize = 32768;

    private readonly byte[] _out = new byte[CabReader.MaxBlockUncompressed];
    private byte[]? _compressed;
    private byte[]? _history;
    private int _historyLength;
    private DeflateDecoder? _inflate;
    private int _inflateHistory = -1;
    private LzxDecoder? _lzx;
    private int _block;
    private long _nextOffset = folder.DataOffset;
    private int _outPos;
    private int _outLength;

    /// <summary>Gets the number of uncompressed bytes of the folder that have been read.</summary>
    public long Position { get; private set; }

    public int Read(Span<byte> buffer)
    {
        try
        {
            if (_outPos == _outLength && !LoadBlock())
            {
                return 0;
            }

            int n = Math.Min(buffer.Length, _outLength - _outPos);
            _out.AsSpan(_outPos, n).CopyTo(buffer);
            _outPos += n;
            Position += n;
            return n;
        }
        catch
        {
            // A decoder that failed cannot go on; the next use starts the folder again from its beginning.
            Restart();
            throw;
        }
    }

    public void SkipTo(long target)
    {
        if (target < Position)
        {
            Restart();
        }

        byte[] scratch = ArrayPool<byte>.Shared.Rent(CabReader.MaxBlockUncompressed);
        try
        {
            while (Position < target)
            {
                int want = (int)Math.Min(scratch.Length, target - Position);
                if (Read(scratch.AsSpan(0, want)) == 0)
                {
                    throw new CompressionFormatException(CabReader.Name, _nextOffset, $"the folder's data up to offset {target}; the folder ends after {Position} bytes", true);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public void Dispose()
    {
        _lzx = null;
        _inflate = null;
    }

    private void Restart()
    {
        _block = 0;
        _nextOffset = folder.DataOffset;
        _outPos = _outLength = 0;
        Position = 0;
        _historyLength = 0;
        _lzx?.Reset();
        _inflate = null;
        _inflateHistory = -1;
    }

    private static CompressionFormatException Bad(long offset, string expected, bool truncated = false) => new(CabReader.Name, offset, expected, truncated);

    private bool LoadBlock()
    {
        if (_block >= folder.BlockCount)
        {
            return false;
        }

        // CFDATA: csum (4), cbData (2), cbUncomp (2), the data reserve, then cbData bytes.
        long at = _nextOffset;
        int headerLength = 8 + header.DataReserveSize;
        if (at + headerLength > fileLength)
        {
            throw Bad(fileLength, $"the header of data block {_block} of folder {folder.Index} (offset {at})", true);
        }

        Span<byte> h = stackalloc byte[8 + 255];
        h = h[..headerLength];
        ReadAt(at, h);
        uint csum = BinaryPrimitives.ReadUInt32LittleEndian(h);
        int compressedLength = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
        int uncompressedLength = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
        if (uncompressedLength == 0)
        {
            throw new ArchiveUnsupportedException(CabReader.Name, $"data block {_block} of folder {folder.Index} continues in the next cabinet; blocks that span cabinets cannot be read from one cabinet");
        }

        if (uncompressedLength > CabReader.MaxBlockUncompressed)
        {
            throw Bad(at + 6, $"an uncompressed block size of at most {CabReader.MaxBlockUncompressed} (found {uncompressedLength})");
        }

        long dataAt = at + headerLength;
        if (dataAt + compressedLength > fileLength)
        {
            throw Bad(fileLength, $"{compressedLength} bytes of data for block {_block} of folder {folder.Index} at offset {dataAt}", true);
        }

        byte[] data = _compressed ??= new byte[ushort.MaxValue];
        ReadAt(dataAt, data.AsSpan(0, compressedLength));
        if (verify && csum != 0)
        {
            uint computed = CabChecksum.Compute(h.Slice(4, 4), CabChecksum.Compute(data.AsSpan(0, compressedLength), 0));
            if (computed != csum)
            {
                throw Bad(at, $"checksum {csum:X8} for data block {_block} of folder {folder.Index}; the data gives {computed:X8}");
            }
        }

        meter.AddCompressed(headerLength + compressedLength);
        Span<byte> output = _out.AsSpan(0, uncompressedLength);
        bool last = _block == folder.BlockCount - 1;
        switch (folder.Compression)
        {
            case CabCompression.None:
                if (compressedLength != uncompressedLength)
                {
                    throw Bad(at + 4, $"equal compressed and uncompressed sizes in a stored block (found {compressedLength} and {uncompressedLength})");
                }

                data.AsSpan(0, compressedLength).CopyTo(output);
                break;
            case CabCompression.MsZip:
                DecodeMsZip(data.AsSpan(0, compressedLength), output, dataAt);
                break;
            case CabCompression.Lzx:
                if (!last && uncompressedLength != CabReader.MaxBlockUncompressed)
                {
                    throw Bad(at + 6, $"32768 uncompressed bytes in every LZX block but the last (found {uncompressedLength} in block {_block} of {folder.BlockCount})");
                }

                DecodeLzx(data.AsSpan(0, compressedLength), output, dataAt);
                break;
            default:
                throw new ArchiveUnsupportedException(CabReader.Name, $"compression type 0x{folder.RawCompressionType:X4} (folder {folder.Index})");
        }

        meter.AddProduced(uncompressedLength);
        _block++;
        _nextOffset = dataAt + compressedLength;
        _outPos = 0;
        _outLength = uncompressedLength;
        return true;
    }

    private void ReadAt(long offset, Span<byte> destination)
    {
        file.Seek(origin + offset, SeekOrigin.Begin);
        int done = 0;
        while (done < destination.Length)
        {
            int n = file.Read(destination[done..]);
            if (n == 0)
            {
                throw Bad(offset + done, "more data; the cabinet ends early", true);
            }

            done += n;
        }
    }

    // [MS-CAB] 2.4 and the MSZIP description: each block is "CK" and a deflate stream whose matches may reach into the previous 32 KiB of output.
    private void DecodeMsZip(ReadOnlySpan<byte> data, Span<byte> output, long dataAt)
    {
        if (data.Length < 2 || data[0] != (byte)'C' || data[1] != (byte)'K')
        {
            throw Bad(dataAt, "the MSZIP block signature 'CK'");
        }

        _history ??= new byte[HistorySize];
        if (_inflate is null || _inflateHistory != _historyLength)
        {
            _inflate = new DeflateDecoder(DeflateFormat.Raw, new DeflateDecompressionOptions
            {
                Dictionary = _history.AsMemory(0, _historyLength),
                MaxOutputSize = CabReader.MaxBlockUncompressed,
            });
            _inflateHistory = _historyLength;
        }
        else
        {
            _inflate.Reset();
        }

        try
        {
            OperationStatus status = _inflate.Decompress(data[2..], output, out int consumed, out int written);
            if (status != OperationStatus.Done || written != output.Length)
            {
                string what = status == OperationStatus.DestinationTooSmall
                    ? $"a deflate block that ends after {output.Length} bytes"
                    : $"{output.Length} bytes from the deflate block (found {written})";
                throw Bad(dataAt + 2 + consumed, what, status == OperationStatus.NeedMoreData);
            }
        }
        catch (CompressionFormatException ex) when (ex.Format != CabReader.Name)
        {
            _inflate = null;
            throw Bad(dataAt + 2 + ex.Offset, "valid MSZIP data: " + ex.Expected, ex.IsTruncated);
        }

        // The next block's history is the last 32 KiB of output so far.
        if (output.Length >= HistorySize)
        {
            output[^HistorySize..].CopyTo(_history);
            _historyLength = HistorySize;
        }
        else
        {
            int keep = Math.Min(_historyLength, HistorySize - output.Length);
            _history.AsSpan(_historyLength - keep, keep).CopyTo(_history);
            output.CopyTo(_history.AsSpan(keep));
            _historyLength = keep + output.Length;
        }
    }

    // [MS-PATCH] 2 as cabinets use it: one 32768-byte frame per data block.
    private void DecodeLzx(ReadOnlySpan<byte> data, Span<byte> output, long dataAt)
    {
        _lzx ??= new LzxDecoder(folder.WindowBits);
        long before = _lzx.TotalConsumed;
        try
        {
            _lzx.DecodeFrame(data, output, out _);
        }
        catch (CompressionFormatException ex) when (ex.Format != CabReader.Name)
        {
            throw Bad(dataAt + Math.Max(0, ex.Offset - before), "valid LZX data: " + ex.Expected, ex.IsTruncated);
        }
    }
}

/// <summary>The content of one entry: a window of its folder's uncompressed stream.</summary>
internal sealed class CabEntryStream(CabFolderCursor cursor, Action<CabFolderCursor>? release, CabEntry entry) : Stream
{
    private long _pos;
    private bool _released;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => entry.Size;

    public override long Position
    {
        get => _pos;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long left = entry.Size - _pos;
        if (left <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > left)
        {
            buffer = buffer[..(int)left];
        }

        cursor.SkipTo(entry.FolderOffset + _pos);
        int n = cursor.Read(buffer);
        if (n == 0)
        {
            throw new CompressionFormatException(CabReader.Name, 0, $"{left} more bytes of '{entry.Name}'; the folder ended", true);
        }

        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => entry.Size + offset,
        };
        if (target < 0 || target > entry.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _pos = target;
        return _pos;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && release is not null && !_released)
        {
            _released = true;
            release(cursor);
        }

        base.Dispose(disposing);
    }
}
