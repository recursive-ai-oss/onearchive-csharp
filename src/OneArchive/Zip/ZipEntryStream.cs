using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneArchive.Zip;

/// <summary>
/// A read buffer over the archive stream that tracks the archive offset of its next byte. In a seekable source each buffer
/// seeks before it reads, so several entry streams can share one file.
/// </summary>
internal sealed class ZipInputBuffer : IDisposable
{
    public const int Size = 64 * 1024;

    private readonly Stream _stream;
    private readonly bool _seekable;
    private readonly long _origin;
    private byte[]? _buf;
    private int _start;
    private int _end;
    private long _fetchPos;      // archive offset of the next byte to read from the stream

    public ZipInputBuffer(Stream stream, long origin, long position)
    {
        _stream = stream;
        _seekable = stream.CanSeek;
        _origin = origin;
        _fetchPos = position;
        _buf = ArrayPool<byte>.Shared.Rent(Size);
    }

    /// <summary>Gets the archive offset of the first buffered byte.</summary>
    public long Offset => _fetchPos - (_end - _start);

    public int Available => _end - _start;

    public ReadOnlySpan<byte> Span => _buf.AsSpan(_start, _end - _start);

    public void Consume(int n) => _start += n;

    /// <summary>Reads more input after the buffered bytes. Returns false at the end of the stream.</summary>
    public async ValueTask<bool> FillAsync(bool async, CancellationToken ct)
    {
        byte[] buf = _buf ?? throw new ObjectDisposedException(nameof(ZipInputBuffer));
        if (_start == _end)
        {
            _start = _end = 0;
        }
        else if (_start > 0)
        {
            Buffer.BlockCopy(buf, _start, buf, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == buf.Length)
        {
            return true; // full: the caller has unconsumed bytes to work on
        }

        if (_seekable && _stream.Position != _origin + _fetchPos)
        {
            _stream.Seek(_origin + _fetchPos, SeekOrigin.Begin);
        }

        int n = async
            ? await _stream.ReadAsync(buf.AsMemory(_end), ct).ConfigureAwait(false)
            : _stream.Read(buf, _end, buf.Length - _end);
        _end += n;
        _fetchPos += n;
        return n > 0;
    }

    /// <summary>Makes at least <paramref name="n"/> (at most <see cref="Size"/>) bytes available if the stream has them.</summary>
    public async ValueTask<bool> EnsureAsync(int n, bool async, CancellationToken ct)
    {
        while (Available < n)
        {
            if (!await FillAsync(async, ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Copies the next <paramref name="dest"/>.Length bytes out. Returns false when the stream ends first.</summary>
    public async ValueTask<bool> ReadExactAsync(byte[] dest, bool async, CancellationToken ct)
    {
        int done = 0;
        while (done < dest.Length)
        {
            if (Available == 0 && !await FillAsync(async, ct).ConfigureAwait(false))
            {
                return false;
            }

            int n = Math.Min(Available, dest.Length - done);
            Span.Slice(0, n).CopyTo(dest.AsSpan(done));
            Consume(n);
            done += n;
        }

        return true;
    }

    /// <summary>Discards <paramref name="count"/> bytes. Returns false when the stream ends first.</summary>
    public async ValueTask<bool> SkipAsync(long count, bool async, CancellationToken ct)
    {
        while (count > 0)
        {
            if (Available == 0)
            {
                if (_seekable && count > Size)
                {
                    // No need to read what is skipped; the next fill seeks. The end is checked by the next read.
                    _fetchPos += count;
                    _start = _end = 0;
                    return _fetchPos <= _stream.Length - _origin;
                }

                if (!await FillAsync(async, ct).ConfigureAwait(false))
                {
                    return false;
                }
            }

            int n = (int)Math.Min(Available, count);
            Consume(n);
            count -= n;
        }

        return true;
    }

    public void Dispose()
    {
        byte[]? buf = _buf;
        _buf = null;
        if (buf is not null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}

/// <summary>
/// The content of one zip entry: decodes the entry's data as it is read, checks the declared size and CRC-32, meters the
/// expansion, and, for an entry read forward-only, consumes the data descriptor so that the reader finds the next header.
/// </summary>
internal sealed class ZipEntryStream : Stream
{
    private readonly ZipInputBuffer _input;
    private readonly bool _ownsInput;
    private readonly ZipMethod _method;
    private readonly long _size;             // declared uncompressed size, -1 when unknown
    private readonly uint _expectedCrc;
    private readonly bool _verify;
    private readonly bool _descriptor;       // read a data descriptor at the end
    private readonly bool _descriptorZip64;
    private readonly ExpansionMeter _meter;
    private readonly ZipReader _owner;
    private readonly DeflateDecoder? _deflate;
    private readonly ZstdDecoder? _zstd;
    private readonly ZipLzmaDecoder? _lzma;
    private readonly RecursiveAI.OneCompress.Xz.XzDecoder? _xz;
    private readonly RecursiveAI.OneCompress.BZip2.BZip2Decoder? _bzip2;
    private long _compRemaining;             // -1 when the compressed size is unknown (the decoder finds the end)
    private long _consumed;
    private long _produced;
    private Crc32 _crc;
    private bool _done;
    private bool _invalid;

    public ZipEntryStream(
        ZipInputBuffer input, bool ownsInput, ZipMethod method, long compressedSize, long size, uint crc, bool verify,
        bool descriptor, bool descriptorZip64, ExpansionMeter meter, ZipReader owner, ArchiveLimits limits)
    {
        _input = input;
        _ownsInput = ownsInput;
        _method = method;
        _compRemaining = compressedSize;
        _size = size;
        _expectedCrc = crc;
        _verify = verify;
        _descriptor = descriptor;
        _descriptorZip64 = descriptorZip64;
        _meter = meter;
        _owner = owner;
        switch (method)
        {
            case ZipMethod.Store:
                break;
            case ZipMethod.Deflate:
                _deflate = new DeflateDecoder(DeflateFormat.Raw, new DeflateDecompressionOptions { MaxOutputSize = long.MaxValue });
                break;
            case ZipMethod.BZip2:
                _bzip2 = new RecursiveAI.OneCompress.BZip2.BZip2Decoder(new RecursiveAI.OneCompress.BZip2.BZip2DecompressionOptions { MaxOutputSize = long.MaxValue });
                break;
            case ZipMethod.Zstd:
                _zstd = new ZstdDecoder(new ZstdDecompressionOptions { MaxWindowSize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });
                break;
            case ZipMethod.Lzma:
                _lzma = new ZipLzmaDecoder(size, input.Offset, new RecursiveAI.OneCompress.Lzma.LzmaDecompressionOptions { MaxDictionarySize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });
                break;
            case ZipMethod.Xz:
                _xz = new RecursiveAI.OneCompress.Xz.XzDecoder(new RecursiveAI.OneCompress.Xz.XzDecompressionOptions { MaxDictionarySize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });
                break;
            default:
                throw new ArchiveUnsupportedException(ZipFormat.Name, $"compression method {(int)method} ({ZipFormat.MethodName((int)method)})");
        }
    }

    public bool Completed => _done;

    public void Invalidate() => _invalid = true;

    public override bool CanRead => !_invalid;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Sync.Result(ReadCoreAsync(buffer.AsMemory(offset, count), false, default));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, true, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadCoreAsync(buffer.AsMemory(offset, count), true, cancellationToken).AsTask();
    }

    private CompressionFormatException Truncated() =>
        ZipFormat.Bad(_input.Offset, "the entry's data (the archive ended inside it)", truncated: true);

    private void Account(Span<byte> data)
    {
        int n = data.Length;
        if (_verify)
        {
            _crc.Append(data);
        }

        _produced += n;
        if (_size >= 0)
        {
            if (_produced > _size)
            {
                throw ZipFormat.Bad(_input.Offset, $"entry data of the declared {_size} bytes (it decodes to more)");
            }
        }
        else
        {
            _owner.NoteUnannouncedBytes(n);
        }

        _meter.AddProduced(n);
    }

    private void AccountCompressed(int n)
    {
        _consumed += n;
        if (_compRemaining >= 0)
        {
            _compRemaining -= n;
        }

        _meter.AddCompressed(n);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> dest, bool async, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_invalid, this);
        if (dest.Length == 0 || _done)
        {
            return 0;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_method == ZipMethod.Store)
            {
                if (_compRemaining == 0)
                {
                    await FinishAsync(async, ct).ConfigureAwait(false);
                    return 0;
                }

                if (_input.Available == 0 && !await _input.FillAsync(async, ct).ConfigureAwait(false))
                {
                    throw Truncated();
                }

                int n = (int)Math.Min(Math.Min(dest.Length, _input.Available), _compRemaining);
                _input.Span[..n].CopyTo(dest.Span);
                _input.Consume(n);
                AccountCompressed(n);
                Account(dest.Span[..n]);
                if (_compRemaining == 0)
                {
                    await FinishAsync(async, ct).ConfigureAwait(false);
                }

                return n;
            }

            int cap = dest.Length;
            if (_size >= 0 && _size - _produced + 1 < cap)
            {
                cap = (int)(_size - _produced + 1);
            }

            ReadOnlySpan<byte> src = _input.Span;
            if (_compRemaining >= 0 && src.Length > _compRemaining)
            {
                src = src[..(int)_compRemaining];
            }

            System.Buffers.OperationStatus status;
            int consumed, written;
            if (_deflate is not null)
            {
                status = _deflate.Decompress(src, dest.Span[..cap], out consumed, out written);
            }
            else if (_bzip2 is not null)
            {
                status = _bzip2.Decompress(src, dest.Span[..cap], out consumed, out written);
            }
            else if (_lzma is not null)
            {
                status = _lzma.Decompress(src, dest.Span[..cap], out consumed, out written);
            }
            else if (_xz is not null)
            {
                status = _xz.Decompress(src, dest.Span[..cap], out consumed, out written);
            }
            else
            {
                status = _zstd!.Decompress(src, dest.Span[..cap], out consumed, out written);
            }

            _input.Consume(consumed);
            AccountCompressed(consumed);
            Account(dest.Span[..written]);

            if (status == System.Buffers.OperationStatus.Done)
            {
                if (_compRemaining > 0)
                {
                    if (_zstd is null)
                    {
                        throw ZipFormat.Bad(_input.Offset, "the entry's compressed data to end with the compressed stream");
                    }

                    // zstd: another frame (or a skippable frame) follows within the entry.
                    if (written > 0)
                    {
                        return written;
                    }

                    continue;
                }

                await FinishAsync(async, ct).ConfigureAwait(false);
                return written;
            }

            if (written > 0)
            {
                return written;
            }

            if (status == System.Buffers.OperationStatus.NeedMoreData)
            {
                if (_compRemaining == 0)
                {
                    throw ZipFormat.Bad(_input.Offset, "the end of the compressed stream (the entry's data ended inside it)", truncated: true);
                }

                if (consumed == 0 && src.Length > 0)
                {
                    throw ZipFormat.Bad(_input.Offset, "progress in the compressed stream");
                }

                if (_input.Available == 0 && !await _input.FillAsync(async, ct).ConfigureAwait(false))
                {
                    throw Truncated();
                }
            }
            else if (consumed == 0)
            {
                throw ZipFormat.Bad(_input.Offset, "progress in the compressed stream");
            }
        }
    }

    private async ValueTask FinishAsync(bool async, CancellationToken ct)
    {
        if (_done)
        {
            return;
        }

        if (_size >= 0 && _produced != _size)
        {
            throw ZipFormat.Bad(_input.Offset, $"entry data of the declared {_size} bytes, but it decodes to {_produced}");
        }

        if (_descriptor)
        {
            await ReadDescriptorAsync(async, ct).ConfigureAwait(false);
        }
        else if (_verify && _crc.GetCurrentValue() != _expectedCrc)
        {
            throw ZipFormat.Bad(_input.Offset, $"entry data with CRC-32 0x{_expectedCrc:X8}, found 0x{_crc.GetCurrentValue():X8}");
        }

        _done = true;
        if (_ownsInput)
        {
            _input.Dispose();
        }
    }

    // APPNOTE 4.3.9: crc-32, compressed size, uncompressed size, with an optional leading signature; the sizes are 8
    // bytes when the local header has a zip64 field. Writers differ, so every layout is tried against the values we
    // know from decoding; the one that matches is the descriptor.
    private async ValueTask ReadDescriptorAsync(bool async, CancellationToken ct)
    {
        await _input.EnsureAsync(24, async, ct).ConfigureAwait(false);
        ReadOnlySpan<byte> d = _input.Span;
        uint crc = _crc.GetCurrentValue();
        (bool sig, bool wide)[] layouts = _descriptorZip64
            ? [(true, true), (false, true), (true, false), (false, false)]
            : [(true, false), (false, false), (true, true), (false, true)];
        foreach ((bool sig, bool wide) in layouts)
        {
            int p = 0;
            if (sig)
            {
                if (d.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(d) != ZipFormat.DescriptorSignature)
                {
                    continue;
                }

                p = 4;
            }

            int len = p + 4 + (wide ? 16 : 8);
            if (d.Length < len)
            {
                continue;
            }

            uint c = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
            ulong comp = wide ? BinaryPrimitives.ReadUInt64LittleEndian(d[(p + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 4)..]);
            ulong size = wide ? BinaryPrimitives.ReadUInt64LittleEndian(d[(p + 12)..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 8)..]);
            bool matches = comp == (ulong)_consumed && size == (ulong)_produced && (!_verify || c == crc);
            if (matches || (!_verify && sig))
            {
                _input.Consume(len);
                return;
            }
        }

        throw ZipFormat.Bad(_input.Offset, $"a data descriptor with CRC-32 0x{crc:X8}, compressed size {_consumed} and size {_produced}");
    }

    /// <summary>Moves the input to the end of the entry without delivering its content.</summary>
    public async ValueTask DrainAsync(bool async, CancellationToken ct)
    {
        if (_done)
        {
            return;
        }

        if (!_descriptor && _compRemaining >= 0)
        {
            if (!await _input.SkipAsync(_compRemaining, async, ct).ConfigureAwait(false))
            {
                throw Truncated();
            }

            _meter.AddCompressed(_compRemaining);
            _compRemaining = 0;
            _done = true;
            return;
        }

        byte[] scratch = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (await ReadCoreAsync(scratch, async, ct).ConfigureAwait(false) > 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsInput)
        {
            _input.Dispose();
        }

        base.Dispose(disposing);
    }
}
