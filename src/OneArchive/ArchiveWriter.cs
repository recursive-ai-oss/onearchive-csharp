
namespace RecursiveAI.OneArchive;

/// <summary>
/// The writing interface every container's writer implements. Entries are written in the order given; nothing is
/// reordered, and times, owners and modes are exactly what the entries say.
/// </summary>
/// <remarks>Disposing a writer that was not finished finishes it, unless an earlier call failed.</remarks>
public abstract class ArchiveWriter : IDisposable, IAsyncDisposable
{
    /// <summary>Gets the container format.</summary>
    public abstract ArchiveFormat Format { get; }

    /// <summary>Writes an entry and, for a regular file, its content.</summary>
    /// <param name="entry">The entry. <see cref="ArchiveEntry.Size"/> must be the exact length of <paramref name="content"/>.</param>
    /// <param name="content">The file content; <see langword="null"/> for entries without content.</param>
    /// <exception cref="ArgumentException">The entry cannot be represented, or <paramref name="content"/> does not have <see cref="ArchiveEntry.Size"/> bytes.</exception>
    /// <exception cref="ArchiveUnsupportedException">The format cannot store a property of the entry.</exception>
    public void WriteEntry(ArchiveEntry entry, Stream? content = null) => Internal.Sync.Result(WriteEntryAsync(entry, content, false, default));

    /// <summary>Writes an entry and, for a regular file, its content.</summary>
    /// <param name="entry">The entry. <see cref="ArchiveEntry.Size"/> must be the exact length of <paramref name="content"/>.</param>
    /// <param name="content">The file content; <see langword="null"/> for entries without content.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the entry is written.</returns>
    public ValueTask WriteEntryAsync(ArchiveEntry entry, Stream? content = null, CancellationToken cancellationToken = default) => WriteEntryAsync(entry, content, true, cancellationToken);

    /// <summary>Writes an entry whose content is in memory.</summary>
    /// <param name="entry">The entry; its size must equal <c>content.Length</c>.</param>
    /// <param name="content">The content.</param>
    public void WriteEntry(ArchiveEntry entry, ReadOnlyMemory<byte> content)
    {
        using var stream = new Internal.MemoryReadStream(content);
        WriteEntry(entry, stream);
    }

    /// <summary>Writes the end of the archive. Further writes throw. Calling it twice is harmless.</summary>
    public void Finish() => Internal.Sync.Result(FinishAsync(false, default));

    /// <summary>Writes the end of the archive. Further writes throw. Calling it twice is harmless.</summary>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the end is written.</returns>
    public ValueTask FinishAsync(CancellationToken cancellationToken = default) => FinishAsync(true, cancellationToken);

    /// <summary>Writes one entry.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="content">The content or <see langword="null"/>.</param>
    /// <param name="async">True for asynchronous I/O.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the entry is written.</returns>
    protected abstract ValueTask WriteEntryAsync(ArchiveEntry entry, Stream? content, bool async, CancellationToken cancellationToken);

    /// <summary>Writes the trailer.</summary>
    /// <param name="async">True for asynchronous I/O.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the trailer is written.</returns>
    protected abstract ValueTask FinishAsync(bool async, CancellationToken cancellationToken);

    /// <summary>Releases the writer; finishes the archive first if that has not happened.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected abstract void Dispose(bool disposing);

    /// <summary>Releases the writer asynchronously.</summary>
    /// <returns>A task that completes when the writer is released.</returns>
    protected abstract ValueTask DisposeAsyncCore();

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
