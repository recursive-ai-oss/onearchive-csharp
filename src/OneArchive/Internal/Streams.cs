using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Internal;

/// <summary>Counts compressed bytes in and bytes produced, and refuses an expansion beyond the ratio limit.</summary>
internal sealed class ExpansionMeter(ArchiveLimits limits, string format)
{
    private long _compressed;
    private long _produced;

    public void AddCompressed(long n) => _compressed += n;

    public void AddProduced(long n)
    {
        _produced += n;
        long ratio = limits.MaxExpansionRatio;
        if (ratio > 0 && _produced > ArchiveLimits.ExpansionRatioGraceBytes)
        {
            long basis = Math.Max(_compressed, 1);
            if (basis <= long.MaxValue / ratio && _produced > basis * ratio)
            {
                throw new CompressionLimitException(format, nameof(ArchiveLimits.MaxExpansionRatio), ratio, _produced / basis);
            }
        }
    }
}

/// <summary>Wraps the compressed source and reports what the decompressor consumed to the meter.</summary>
internal sealed class CountingStream(Stream inner, ExpansionMeter meter, bool leaveOpen) : Stream
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
        int n = inner.Read(buffer);
        meter.AddCompressed(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        meter.AddCompressed(n);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Reports every byte read from the (decompressed) archive stream to the meter.</summary>
internal sealed class ProducedStream(Stream inner, ExpansionMeter meter) : Stream
{
    public Stream Inner => inner;

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
        int n = inner.Read(buffer);
        meter.AddProduced(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        meter.AddProduced(n);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Replays a few bytes that were peeked from a stream, then continues with the stream.</summary>
internal sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream inner, bool leaveOpen) : Stream
{
    private int _at;

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
        if (_at < prefixLength)
        {
            int n = Math.Min(buffer.Length, prefixLength - _at);
            prefix.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }

        return inner.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_at < prefixLength)
        {
            return new ValueTask<int>(Read(buffer.Span));
        }

        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A read-only stream over memory.</summary>
internal sealed class MemoryReadStream(ReadOnlyMemory<byte> data) : Stream
{
    private int _at;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _at;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, data.Length - _at);
        data.Span.Slice(_at, n).CopyTo(buffer);
        _at += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>An entry stream that also owns the reader it came from, and disposes it with the stream.</summary>
internal sealed class ReaderOwnedStream(Stream inner, ArchiveReader owner) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            owner.Dispose();
        }

        base.Dispose(disposing);
    }
}
