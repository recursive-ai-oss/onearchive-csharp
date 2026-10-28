using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tar;

/// <summary>
/// The content of one tar entry as a read-only stream. Sparse files read as their full size, with zeros in the holes.
/// Over a seekable source the stream is seekable and independent of the reader's position; over a forward-only source
/// it reads forward from where the reader stands and reports each byte it takes to the reader.
/// </summary>
internal sealed class TarEntryStream : Stream
{
    private readonly Stream _base;
    private readonly bool _seekable;
    private readonly long _dataStart;       // where the stored data begins in _base's coordinates
    private readonly long _size;            // logical size
    private readonly long[]? _offsets;      // sparse: region offsets in the logical file
    private readonly long[]? _lengths;
    private readonly long[]? _stored;       // sparse: where each region starts within the stored data
    private readonly TarReader? _owner;     // sequential mode: the reader to tell about consumed bytes
    private readonly ExpansionMeter? _meter;
    private readonly long _baseOrigin;
    private long _position;
    private int _region;
    private bool _invalid;

    public TarEntryStream(Stream baseStream, bool seekable, long dataStart, long baseOrigin, long size, IReadOnlyList<TarSparseRegion>? regions, TarReader? owner, ExpansionMeter? meter)
    {
        _base = baseStream;
        _seekable = seekable;
        _dataStart = dataStart;
        _baseOrigin = baseOrigin;
        _size = size;
        _owner = owner;
        _meter = meter;
        if (regions is { Count: > 0 })
        {
            int n = regions.Count;
            _offsets = new long[n];
            _lengths = new long[n];
            _stored = new long[n];
            long stored = 0;
            for (int i = 0; i < n; i++)
            {
                _offsets[i] = regions[i].Offset;
                _lengths[i] = regions[i].Length;
                _stored[i] = stored;
                stored += regions[i].Length;
            }
        }
        else if (regions is not null)
        {
            // Sparse with no stored data: all hole.
            _offsets = [];
            _lengths = [];
            _stored = [];
        }
    }

    public void Invalidate() => _invalid = true;

    public override bool CanRead => !_invalid;

    public override bool CanSeek => _seekable && !_invalid;

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
        _region = 0;
        return target;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!Plan(buffer.Length, out bool hole, out int n, out long basePos))
        {
            return 0;
        }

        if (hole)
        {
            buffer[..n].Clear();
            Commit(n, true);
            return n;
        }

        PrepareBase(basePos);
        int got = _base.Read(buffer[..n]);
        if (got == 0)
        {
            throw Truncated(basePos);
        }

        Commit(got, false);
        return got;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Plan(buffer.Length, out bool hole, out int n, out long basePos))
        {
            return 0;
        }

        if (hole)
        {
            buffer.Span[..n].Clear();
            Commit(n, true);
            return n;
        }

        PrepareBase(basePos);
        int got = await _base.ReadAsync(buffer[..n], cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            throw Truncated(basePos);
        }

        Commit(got, false);
        return got;
    }

    private CompressionFormatException Truncated(long basePos) =>
        TarHeader.Bad(basePos - _baseOrigin, "the entry's data (the archive ended inside it)", truncated: true);

    // Decides what the next read returns: a run of zeros (a hole) or bytes from the archive at basePos.
    private bool Plan(int count, out bool hole, out int n, out long basePos)
    {
        ObjectDisposedException.ThrowIf(_invalid, this);
        hole = false;
        n = 0;
        basePos = 0;
        if (count == 0 || _position >= _size)
        {
            return false;
        }

        long remaining = _size - _position;
        if (_offsets is null)
        {
            n = (int)Math.Min(count, remaining);
            basePos = _dataStart + _position;
            return true;
        }

        while (_region < _offsets.Length && _offsets[_region] + _lengths![_region] <= _position)
        {
            _region++;
        }

        if (_region >= _offsets.Length || _position < _offsets[_region])
        {
            long holeEnd = _region >= _offsets.Length ? _size : _offsets[_region];
            hole = true;
            n = (int)Math.Min(count, holeEnd - _position);
            return true;
        }

        long end = _offsets[_region] + _lengths![_region];
        n = (int)Math.Min(count, end - _position);
        basePos = _dataStart + _stored![_region] + (_position - _offsets[_region]);
        return true;
    }

    private void PrepareBase(long basePos)
    {
        if (_seekable)
        {
            if (_base.Position != basePos)
            {
                _base.Seek(basePos, SeekOrigin.Begin);
            }
        }
    }

    private void Commit(int n, bool hole)
    {
        _position += n;
        if (hole)
        {
            _meter?.AddProduced(n);
        }
        else
        {
            _owner?.NoteConsumed(n);
        }
    }
}
