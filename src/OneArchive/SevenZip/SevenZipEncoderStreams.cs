using System.Buffers;
using RecursiveAI.OneCompress.Filters;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>A tier-2 <c>Compress</c> step: one call of an encoder's span-based API.</summary>
internal delegate OperationStatus EncodeStep(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock);

/// <summary>Write-only stream base: the members the encoder streams below never support.</summary>
internal abstract class WriteOnlyStream : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(new ReadOnlySpan<byte>(buffer, offset, count));
}

/// <summary>Passes bytes to the archive output and counts them; never closes the output.</summary>
internal sealed class CountingSink(Stream output) : WriteOnlyStream
{
    public long Count { get; private set; }

    public override bool CanWrite => true;

    public override void Flush()
    {
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        output.Write(buffer);
        Count += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count += buffer.Length;
    }
}

/// <summary>
/// Drives an LZMA-family encoder's tier-2 API as a stream. Disposing finishes the encoder and writes the rest to the
/// inner stream, which stays open. The first <c>skip</c> output bytes are dropped: LZMA's 13-byte stand-alone header,
/// which 7z carries in the coder properties instead.
/// </summary>
internal sealed class EncodeStepStream : WriteOnlyStream
{
    private readonly Stream _inner;
    private readonly EncodeStep _step;
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
    private int _skip;
    private bool _finished;

    public EncodeStepStream(Stream inner, EncodeStep step, int skip)
    {
        _inner = inner;
        _step = step;
        _skip = skip;
    }

    public override bool CanWrite => _buffer is not null && !_finished;

    public override void Flush()
    {
    }

    private int Skipped(int written)
    {
        int s = Math.Min(_skip, written);
        _skip -= s;
        return s;
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(EncodeStepStream));
        OperationStatus status;
        do
        {
            status = _step(data, buffer, out int consumed, out int written, false);
            int s = Skipped(written);
            if (written > s)
            {
                _inner.Write(buffer, s, written - s);
            }

            data = data[consumed..];
        }
        while (status == OperationStatus.DestinationTooSmall || data.Length > 0);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(EncodeStepStream));
        OperationStatus status;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            status = _step(data.Span, buffer, out int consumed, out int written, false);
            int s = Skipped(written);
            if (written > s)
            {
                await _inner.WriteAsync(buffer.AsMemory(s, written - s), cancellationToken).ConfigureAwait(false);
            }

            data = data[consumed..];
        }
        while (status == OperationStatus.DestinationTooSmall || data.Length > 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer is { } buffer)
        {
            try
            {
                if (!_finished)
                {
                    _finished = true;
                    OperationStatus status;
                    do
                    {
                        status = _step([], buffer, out _, out int written, true);
                        int s = Skipped(written);
                        if (written > s)
                        {
                            _inner.Write(buffer, s, written - s);
                        }
                    }
                    while (status != OperationStatus.Done);
                }
            }
            finally
            {
                _buffer = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_buffer is { } buffer)
        {
            try
            {
                if (!_finished)
                {
                    _finished = true;
                    OperationStatus status;
                    do
                    {
                        status = _step([], buffer, out _, out int written, true);
                        int s = Skipped(written);
                        if (written > s)
                        {
                            await _inner.WriteAsync(buffer.AsMemory(s, written - s)).ConfigureAwait(false);
                        }
                    }
                    while (status != OperationStatus.Done);
                }
            }
            finally
            {
                _buffer = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Applies a branch or delta filter (compression side) to what is written and passes the result on. A branch filter
/// holds back the few trailing bytes it has not looked at until more data arrives; at the end they go out unconverted,
/// which is what the decoder expects. Disposing writes them, then disposes the next stream.
/// </summary>
internal sealed class FilterEncodeStream : WriteOnlyStream
{
    private readonly Stream _next;
    private readonly BranchFilter? _branch;
    private readonly DeltaFilter? _delta;
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
    private int _held;

    public FilterEncodeStream(Stream next, BranchFilter? branch, DeltaFilter? delta)
    {
        _next = next;
        _branch = branch;
        _delta = delta;
    }

    public override bool CanWrite => _buffer is not null;

    public override void Flush()
    {
    }

    private int Convert(byte[] buffer)
    {
        if (_branch is not null)
        {
            return _branch.Convert(buffer.AsSpan(0, _held));
        }

        _delta!.Convert(buffer.AsSpan(0, _held));
        return _held;
    }

    private void Keep(byte[] buffer, int done)
    {
        _held -= done;
        if (done > 0 && _held > 0)
        {
            Buffer.BlockCopy(buffer, done, buffer, 0, _held);
        }
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(FilterEncodeStream));
        while (data.Length > 0)
        {
            int n = Math.Min(buffer.Length - _held, data.Length);
            data[..n].CopyTo(buffer.AsSpan(_held));
            _held += n;
            data = data[n..];
            int done = Convert(buffer);
            if (done > 0)
            {
                _next.Write(buffer, 0, done);
            }

            Keep(buffer, done);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(FilterEncodeStream));
        while (data.Length > 0)
        {
            int n = Math.Min(buffer.Length - _held, data.Length);
            data.Span[..n].CopyTo(buffer.AsSpan(_held));
            _held += n;
            data = data[n..];
            int done = Convert(buffer);
            if (done > 0)
            {
                await _next.WriteAsync(buffer.AsMemory(0, done), cancellationToken).ConfigureAwait(false);
            }

            Keep(buffer, done);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer is { } buffer)
        {
            _buffer = null;
            try
            {
                if (_held > 0)
                {
                    _next.Write(buffer, 0, _held);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _next.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_buffer is { } buffer)
        {
            _buffer = null;
            try
            {
                if (_held > 0)
                {
                    await _next.WriteAsync(buffer.AsMemory(0, _held)).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                await _next.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Holds back the first bytes of a folder so a compressor can be created knowing the folder's whole size when it is small
/// (the dictionary and match finder then shrink to fit); a longer folder gets a compressor with no size hint once the
/// held bytes pass the threshold. The choice depends only on the data, so the output does too.
/// </summary>
internal sealed class DeferredCompressorStream : WriteOnlyStream
{
    private readonly Func<long, Stream> _create;
    private readonly int _threshold;
    private MemoryStream? _held = new();
    private Stream? _real;

    public DeferredCompressorStream(Func<long, Stream> create, int threshold)
    {
        _create = create;
        _threshold = threshold;
    }

    public override bool CanWrite => true;

    public override void Flush()
    {
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_real is null)
        {
            if (_held!.Length + buffer.Length <= _threshold)
            {
                _held.Write(buffer);
                return;
            }

            _real = _create(-1);
            _real.Write(_held.GetBuffer(), 0, (int)_held.Length);
            _held = null;
        }

        _real.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_real is null)
        {
            if (_held!.Length + buffer.Length <= _threshold)
            {
                _held.Write(buffer.Span);
                return;
            }

            _real = _create(-1);
            await _real.WriteAsync(_held.GetBuffer().AsMemory(0, (int)_held.Length), cancellationToken).ConfigureAwait(false);
            _held = null;
        }

        await _real.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private Stream Finish()
    {
        if (_real is null)
        {
            _real = _create(_held!.Length);
            _real.Write(_held.GetBuffer(), 0, (int)_held.Length);
            _held = null;
        }

        return _real;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && (_real is not null || _held is not null))
        {
            Finish().Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_real is not null || _held is not null)
        {
            await Finish().DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
