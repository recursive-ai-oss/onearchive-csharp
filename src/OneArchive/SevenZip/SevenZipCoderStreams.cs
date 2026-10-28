using System.Buffers;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Filters;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>Delegate shape of the tier-2 decoders' <c>Decompress</c> (<c>LzmaDecoder</c>, <c>Lzma2Decoder</c>).</summary>
internal delegate OperationStatus StepDecode(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written);

/// <summary>Reads a bare codec stream (no container) through a tier-2 decoder: LZMA and LZMA2 as 7z stores them.</summary>
internal sealed class SevenZipDecoderStream(Stream input, StepDecode decode, Action finish, long errorOffset) : Stream
{
    private byte[]? _in = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _pos;
    private int _end;
    private bool _eof;
    private bool _done;

    public override bool CanRead => true;

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

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_done || buffer.IsEmpty || _in is null)
        {
            return 0;
        }

        while (true)
        {
            OperationStatus status = decode(_in.AsSpan(_pos, _end - _pos), buffer, out int consumed, out int written);
            _pos += consumed;
            if (written > 0)
            {
                return written;
            }

            if (status == OperationStatus.Done)
            {
                _done = true;
                return 0;
            }

            if (status == OperationStatus.DestinationTooSmall)
            {
                continue;
            }

            // NeedMoreData: refill, or report a stream that ended inside the data.
            if (_pos == _end)
            {
                _pos = _end = 0;
            }

            if (_eof)
            {
                finish();
                throw new CompressionFormatException(SevenZipFormat.Name, errorOffset, "the end of the compressed stream; the packed data ended early", true);
            }

            if (_end == _in.Length)
            {
                System.Buffer.BlockCopy(_in, _pos, _in, 0, _end - _pos);
                _end -= _pos;
                _pos = 0;
            }

            int n = input.Read(_in, _end, _in.Length - _end);
            if (n == 0)
            {
                _eof = true;
            }

            _end += n;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_in is not null)
            {
                ArrayPool<byte>.Shared.Return(_in);
                _in = null;
            }

            input.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Applies a branch/call/jump filter while reading (xz format spec 5.3.2; 7z uses the same transforms).</summary>
internal sealed class SevenZipBranchStream(Stream input, BranchFilter filter) : Stream
{
    private byte[]? _buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _pos;
    private int _ready;
    private int _filled;
    private bool _eof;

    public override bool CanRead => true;

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

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _buf is null)
        {
            return 0;
        }

        while (_pos == _ready)
        {
            if (_eof)
            {
                return 0;
            }

            // Keep the bytes the filter has not judged yet, then top the buffer up.
            int tail = _filled - _ready;
            System.Buffer.BlockCopy(_buf, _ready, _buf, 0, tail);
            _pos = _ready = 0;
            _filled = tail;
            int n = input.Read(_buf, _filled, _buf.Length - _filled);
            if (n == 0)
            {
                _eof = true;
                _ready = _filled; // What cannot start an instruction at the very end stays as it is.
            }
            else
            {
                _filled += n;
                _ready = filter.Convert(_buf.AsSpan(0, _filled));
            }
        }

        int take = Math.Min(buffer.Length, _ready - _pos);
        _buf.AsSpan(_pos, take).CopyTo(buffer);
        _pos += take;
        return take;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_buf is not null)
            {
                ArrayPool<byte>.Shared.Return(_buf);
                _buf = null;
            }

            input.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Undoes the Delta filter while reading.</summary>
internal sealed class SevenZipDeltaStream(Stream input, DeltaFilter filter) : Stream
{
    public override bool CanRead => true;

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

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = input.Read(buffer);
        filter.Convert(buffer[..n]);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            input.Dispose();
        }

        base.Dispose(disposing);
    }
}
