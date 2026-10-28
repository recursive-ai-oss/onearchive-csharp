namespace RecursiveAI.OneArchive.Internal;

/// <summary>
/// Presents one compressed file (<c>.gz</c>, <c>.zst</c>, <c>.lz4</c>) as an archive with a single file entry. The stream
/// passed in is already decompressed; this reader only names the entry.
/// </summary>
/// <remarks>The entry's <see cref="ArchiveEntry.Size"/> is 0: the size of the content is not known until it has been read.</remarks>
internal sealed class SingleStreamReader : ArchiveReader
{
    private sealed class Window(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly string _name;
    private readonly DateTimeOffset? _modified;
    private bool _returned;

    public SingleStreamReader(Stream decompressed, string entryName, DateTimeOffset? modified, ArchiveLimits limits, bool leaveOpen)
        : base(ArchiveFormat.SingleStream, limits)
    {
        _stream = decompressed;
        _name = entryName;
        _modified = modified;
        _leaveOpen = leaveOpen;
    }

    protected override string FormatName => "single-stream";

    protected override ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken)
    {
        if (_returned)
        {
            return new ValueTask<ArchiveEntry?>((ArchiveEntry?)null);
        }

        _returned = true;
        return new ValueTask<ArchiveEntry?>(new ArchiveEntry { Name = _name, Kind = ArchiveEntryKind.File, ModifiedTime = _modified });
    }

    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        if (CurrentEntry is null)
        {
            throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        }

        return new Window(_stream);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed && !_leaveOpen)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (!IsDisposed && !_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

/// <summary>Writes the content of its one file entry into a compression stream; the container is the compression format itself.</summary>
internal sealed class SingleStreamWriter : ArchiveWriter
{
    private Stream? _compressed;
    private readonly Func<ArchiveEntry?, Stream>? _open;

    /// <summary>Writes into an already opened compression stream.</summary>
    public SingleStreamWriter(Stream compressed) => _compressed = compressed;

    /// <summary>Opens the compression stream when the entry (or the end of an empty archive) is known, so a format whose header carries the file name (gzip FNAME) can use it.</summary>
    public SingleStreamWriter(Func<ArchiveEntry?, Stream> open) => _open = open;

    private bool _written;
    private bool _finished;
    private bool _disposed;

    public override ArchiveFormat Format => ArchiveFormat.SingleStream;

    protected override async ValueTask WriteEntryAsync(ArchiveEntry entry, Stream? content, bool async, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The archive was already finished.");
        }

        if (entry.Kind != ArchiveEntryKind.File)
        {
            throw new ArchiveUnsupportedException("single-stream", $"{entry.Kind} entries (a compressed single file holds one regular file)");
        }

        if (_written)
        {
            throw new ArchiveUnsupportedException("single-stream", "more than one entry (a compressed single file holds exactly one; use tar or zip for several)");
        }

        _written = true;
        Stream compressed = _compressed ??= _open!(entry);
        long total = 0;
        if (content is not null)
        {
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    int n = async
                        ? await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)
                        : content.Read(buffer, 0, buffer.Length);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                    if (async)
                    {
                        await compressed.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        compressed.Write(buffer, 0, n);
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (total != entry.Size)
        {
            throw new ArgumentException($"The content is {total} bytes but the entry's size is {entry.Size}.", nameof(content));
        }
    }

    protected override ValueTask FinishAsync(bool async, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _finished = true;
        _compressed ??= _open!(null);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _compressed?.Dispose();
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_compressed is not null)
        {
            await _compressed.DisposeAsync().ConfigureAwait(false);
        }
    }
}
