namespace RecursiveAI.OneArchive.Iso;

/// <summary>The content of a file: its extents read in order from the image, seekable.</summary>
internal sealed class IsoEntryStream : Stream
{
    private readonly Stream _image;
    private readonly long _origin;
    private readonly IsoExtent[] _extents;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;
    private bool _closed;

    public IsoEntryStream(Stream image, long origin, IReadOnlyList<IsoExtent> extents)
    {
        _image = image;
        _origin = origin;
        _extents = [.. extents];
        _starts = new long[_extents.Length];
        long sum = 0;
        for (int i = 0; i < _extents.Length; i++)
        {
            _starts[i] = sum;
            sum += _extents[i].Length;
        }

        _length = sum;
    }

    public override bool CanRead => !_closed;

    public override bool CanSeek => !_closed;

    public override bool CanWrite => false;

    public override long Length => _length;

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
        ObjectDisposedException.ThrowIf(_closed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("Seek before the start of the entry.");
        }

        _position = target;
        return target;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        int index = FindExtent(_position);
        long within = _position - _starts[index];
        int want = (int)Math.Min(buffer.Length, _extents[index].Length - within);
        long at = _origin + (_extents[index].Location * IsoFormat.SectorSize) + within;
        if (_image.Position != at)
        {
            _image.Seek(at, SeekOrigin.Begin);
        }

        int got = _image.Read(buffer[..want]);
        if (got == 0)
        {
            throw IsoFormat.Bad(at - _origin, "the file's data (the image ended inside it)", truncated: true);
        }

        _position += got;
        return got;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_closed, this);
        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        int index = FindExtent(_position);
        long within = _position - _starts[index];
        int want = (int)Math.Min(buffer.Length, _extents[index].Length - within);
        long at = _origin + (_extents[index].Location * IsoFormat.SectorSize) + within;
        if (_image.Position != at)
        {
            _image.Seek(at, SeekOrigin.Begin);
        }

        int got = await _image.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            throw IsoFormat.Bad(at - _origin, "the file's data (the image ended inside it)", truncated: true);
        }

        _position += got;
        return got;
    }

    // The last extent whose start is at or before the position (zero-length extents are skipped by taking the last such start).
    private int FindExtent(long position)
    {
        int lo = 0;
        int hi = _extents.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_starts[mid] <= position)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        base.Dispose(disposing);
    }
}
