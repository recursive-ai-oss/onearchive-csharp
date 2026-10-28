using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive;

/// <summary>
/// The sequential reading interface every container's reader implements. It applies the entry-count, total-size and
/// path limits of <see cref="ArchiveLimits"/> to whatever the format reader finds, so a container only parses.
/// </summary>
/// <remarks>
/// <para>Call <see cref="GetNextEntry"/> until it returns <see langword="null"/>. After each entry, <see cref="OpenEntryStream()"/> gives the
/// content; whatever is not read is skipped by the next call. A reader is not thread-safe.</para>
/// <para>Errors: malformed data raises <see cref="CompressionFormatException"/> (format, byte offset, what was expected),
/// a limit raises <see cref="CompressionLimitException"/>, and an unsupported feature raises <see cref="ArchiveUnsupportedException"/>.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public abstract class ArchiveReader : IDisposable, IAsyncDisposable
{
    private readonly ArchiveLimits _limits;
    private long _declaredTotal;
    private int _count;
    private bool _disposed;

    /// <summary>Initializes the shared state.</summary>
    /// <param name="format">The container format.</param>
    /// <param name="limits">The limits to apply; <see langword="null"/> for the defaults.</param>
    protected ArchiveReader(ArchiveFormat format, ArchiveLimits? limits)
    {
        Format = format;
        _limits = limits ?? ArchiveLimits.Default;
    }

    /// <summary>Gets the container format.</summary>
    public ArchiveFormat Format { get; }

    /// <summary>Gets the entry most recently returned by <see cref="GetNextEntry"/>, or <see langword="null"/> before the first call and after the last.</summary>
    public ArchiveEntry? CurrentEntry { get; private set; }

    /// <summary>Gets the limits in force.</summary>
    protected ArchiveLimits Limits => _limits;

    /// <summary>Gets a value indicating whether <see cref="OpenEntryStream(ArchiveEntry)"/> works: the source can seek and the format can address entries directly.</summary>
    public virtual bool SupportsRandomAccess => false;

    /// <summary>Gets a value indicating whether the reader has been disposed.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>Advances to the next entry, skipping the rest of the current one.</summary>
    /// <returns>The entry, or <see langword="null"/> at the end of the archive.</returns>
    /// <exception cref="CompressionFormatException">The archive is malformed or truncated.</exception>
    /// <exception cref="CompressionLimitException">A limit was exceeded.</exception>
    /// <exception cref="ArchiveUnsupportedException">The archive uses an unsupported feature.</exception>
    public ArchiveEntry? GetNextEntry() => Internal.Sync.Result(NextCoreAsync(false, default));

    /// <summary>Advances to the next entry, skipping the rest of the current one.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> at the end of the archive.</returns>
    /// <exception cref="CompressionFormatException">The archive is malformed or truncated.</exception>
    /// <exception cref="CompressionLimitException">A limit was exceeded.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public ValueTask<ArchiveEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) => NextCoreAsync(true, cancellationToken);

    /// <summary>Opens the content of the current entry. Empty for entries that have no content.</summary>
    /// <returns>A read-only stream that ends at the entry's size. Disposing it does not close the archive.</returns>
    /// <exception cref="InvalidOperationException">There is no current entry.</exception>
    public abstract Stream OpenEntryStream();

    /// <summary>Opens the content of an entry this reader returned earlier, without reading the entries between.</summary>
    /// <param name="entry">An entry returned by this reader.</param>
    /// <returns>A read-only, seekable stream over the entry's content.</returns>
    /// <exception cref="NotSupportedException"><see cref="SupportsRandomAccess"/> is false.</exception>
    /// <exception cref="ArgumentException">The entry did not come from this reader.</exception>
    public virtual Stream OpenEntryStream(ArchiveEntry entry) => throw new NotSupportedException("This archive reader cannot open entries out of order.");

    /// <summary>Reads the next entry's header and its metadata records.</summary>
    /// <param name="async">True when the call should use asynchronous I/O.</param>
    /// <param name="cancellationToken">The cancellation token (honoured when <paramref name="async"/> is true).</param>
    /// <returns>The entry or <see langword="null"/> at the end.</returns>
    protected abstract ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken);

    /// <summary>Throws if the reader was disposed.</summary>
    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private async ValueTask<ArchiveEntry?> NextCoreAsync(bool async, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        CurrentEntry = null;
        ArchiveEntry? entry = await ReadNextEntryAsync(async, cancellationToken).ConfigureAwait(false);
        if (entry is not null)
        {
            Admit(entry);
        }

        CurrentEntry = entry;
        return entry;
    }

    // Limits that apply to every format. The declared size is added before any content is read.
    private void Admit(ArchiveEntry entry)
    {
        string format = FormatName;
        if (++_count > _limits.MaxEntryCount)
        {
            throw new CompressionLimitException(format, nameof(ArchiveLimits.MaxEntryCount), _limits.MaxEntryCount, _count);
        }

        if (entry.Name.Length > _limits.MaxPathLength)
        {
            throw new CompressionLimitException(format, nameof(ArchiveLimits.MaxPathLength), _limits.MaxPathLength, entry.Name.Length);
        }

        if (entry.LinkTarget is { } target && target.Length > _limits.MaxPathLength)
        {
            throw new CompressionLimitException(format, nameof(ArchiveLimits.MaxPathLength), _limits.MaxPathLength, target.Length);
        }

        int depth = Internal.PathRules.Depth(entry.Name);
        if (depth > _limits.MaxPathDepth)
        {
            throw new CompressionLimitException(format, nameof(ArchiveLimits.MaxPathDepth), _limits.MaxPathDepth, depth);
        }

        if (entry.Size > _limits.MaxTotalSize - _declaredTotal)
        {
            throw new CompressionLimitException(format, nameof(ArchiveLimits.MaxTotalSize), _limits.MaxTotalSize, _declaredTotal + entry.Size);
        }

        _declaredTotal += entry.Size;
    }

    /// <summary>Gets the format's name as used in error messages.</summary>
    protected virtual string FormatName => Format.ToString().ToLowerInvariant();

    /// <summary>Releases the reader and, unless it was told to leave it open, the stream under it.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing) => _disposed = true;

    /// <summary>Releases the reader's resources asynchronously.</summary>
    /// <returns>A task that completes when the resources are released.</returns>
    protected virtual ValueTask DisposeAsyncCore()
    {
        Dispose(true);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
