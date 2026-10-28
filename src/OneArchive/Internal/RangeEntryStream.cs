using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Internal;

/// <summary>
/// The content of one archive member: a contiguous byte range of the archive. Reads sequentially when the source cannot
/// seek (and tells the cursor how far it got), and by seeking otherwise. An optional byte-sum check (cpio "crc" format)
/// runs when the range is read from its start to its end in order.
/// </summary>
internal sealed class RangeEntryStream : Stream
{
    private readonly ForwardCursor _cursor;
    private readonly long _dataStart;   // relative to the cursor's origin
    private readonly long _size;
    private readonly bool _sequential;
    private readonly string _format;
    private readonly uint? _expectedSum;
    private readonly long _headerOffset;
    private long _position;
    private uint _sum;
    private bool _sumValid;
    private bool _invalid;

    public RangeEntryStream(ForwardCursor cursor, string format, long dataStart, long size, bool sequential, uint? expectedSum, long headerOffset)
    {
        _cursor = cursor;
        _format = format;
        _dataStart = dataStart;
        _size = size;
        _sequential = sequential && !cursor.Seekable;
        _expectedSum = expectedSum;
        _headerOffset = headerOffset;
        _sumValid = expectedSum is not null;
    }

    public void Invalidate() => _invalid = true;

    public override bool CanRead => !_invalid;

    public override bool CanSeek => _cursor.Seekable && !_invalid;

    public override bool CanWrite => false;

    public override long Length => _size;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The entry was opened on a source that cannot seek.");
        }

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _size + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("Seek before the start of the entry.");
        }

        _position = Math.Min(target, _size);
        _sumValid = false;
        return target;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = Plan(buffer.Length);
        if (n == 0)
        {
            return 0;
        }

        Prepare();
        int got = _cursor.Stream.Read(buffer[..n]);
        return Commit(buffer, got);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int n = Plan(buffer.Length);
        if (n == 0)
        {
            return 0;
        }

        Prepare();
        int got = await _cursor.Stream.ReadAsync(buffer[..n], cancellationToken).ConfigureAwait(false);
        return Commit(buffer.Span, got);
    }

    private int Plan(int count)
    {
        ObjectDisposedException.ThrowIf(_invalid, this);
        if (count == 0 || _position >= _size)
        {
            return 0;
        }

        return (int)Math.Min(count, _size - _position);
    }

    private void Prepare()
    {
        if (_cursor.Seekable)
        {
            long at = _cursor.Origin + _dataStart + _position;
            if (_cursor.Stream.Position != at)
            {
                _cursor.Stream.Seek(at, SeekOrigin.Begin);
            }
        }
    }

    private int Commit(Span<byte> buffer, int got)
    {
        if (got == 0)
        {
            throw new CompressionFormatException(_format, _dataStart + _position, "the entry's data (the archive ended inside it)", true);
        }

        if (_sumValid)
        {
            uint s = _sum;
            foreach (byte b in buffer[..got])
            {
                s += b;
            }

            _sum = s;
        }

        _position += got;
        if (_sequential)
        {
            _cursor.NoteConsumed(got);
        }

        if (_sumValid && _position == _size && _sum != _expectedSum)
        {
            throw new CompressionFormatException(_format, _headerOffset, $"entry data whose byte sum is {_expectedSum:X8} as the header says (it is {_sum:X8})");
        }

        return got;
    }
}
