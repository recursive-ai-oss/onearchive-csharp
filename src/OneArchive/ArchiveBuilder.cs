using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneArchive.Tar;

namespace RecursiveAI.OneArchive;

/// <summary>
/// Collects what goes into a new archive and writes it. Obtain one from <see cref="Archive.Create(string, ArchiveCreateOptions?)"/>.
/// </summary>
/// <remarks>
/// <para>Entries are written in the order they were added. A directory is added as its own entry followed by its
/// contents, with the children of each directory sorted by name (ordinal, UTF-16 code units), so the same tree gives the
/// same archive on every platform. Metadata comes from the file system: modification time (100 ns precision), permission
/// bits (0644 or 0755 where the platform has none), and no owner (OneArchive cannot read user and group ids from managed
/// code, so uid and gid are 0 with no names; build an <see cref="ArchiveEntry"/> yourself to set them). Symbolic links are
/// stored as links unless <see cref="ArchiveCreateOptions.FollowSymlinks"/> is set. Special files such as FIFOs are not detected.</para>
/// <para>File content is read when <see cref="Save"/> runs; a file whose size changed since it was added makes
/// <see cref="Save"/> throw.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ArchiveBuilder
{
    private const int MaxDirectoryDepth = 128;

    private readonly string? _path;
    private readonly Stream? _stream;
    private readonly ArchiveCreateOptions _options;
    private readonly ArchiveFormat _format;
    private readonly ArchiveCompression _compression;
    private readonly List<Item> _items = [];
    private readonly string? _fullOutputPath;
    private bool _saved;

    private readonly record struct Item(ArchiveEntry Entry, string? FilePath, byte[]? Bytes);

    internal ArchiveBuilder(string? path, Stream? stream, ArchiveCreateOptions options)
    {
        _path = path;
        _stream = stream;
        _options = options;
        ArchiveFormat format = options.Format ?? ArchiveFormat.Unknown;
        ArchiveCompression compression = options.Compression ?? ArchiveCompression.None;
        if (path is not null)
        {
            var (nameFormat, nameCompression) = ArchiveDetector.FromFileName(path);
            if (options.Format is null)
            {
                format = nameFormat;
            }

            if (options.Compression is null)
            {
                compression = nameCompression;
            }

            _fullOutputPath = Path.GetFullPath(path);
        }

        if (format == ArchiveFormat.Unknown)
        {
            if (path is not null)
            {
                throw new ArgumentException($"The extension of '{Path.GetFileName(path)}' names no archive format that can be written; use .tar, .tar.zst or .tar.lz4, or set ArchiveCreateOptions.Format.", nameof(path));
            }

            format = ArchiveFormat.Tar;
        }

        if (format != ArchiveFormat.Tar)
        {
            throw new ArchiveUnsupportedException("archive", $"writing the {format} format");
        }

        if (compression is not (ArchiveCompression.None or ArchiveCompression.Zstd or ArchiveCompression.Lz4))
        {
            throw new ArchiveUnsupportedException("archive", $"{compression} compression (not yet available; use zstd or lz4)");
        }

        _format = format;
        _compression = compression;
    }

    /// <summary>Gets the container format that will be written.</summary>
    public ArchiveFormat Format => _format;

    /// <summary>Gets the compression layer that will be written.</summary>
    public ArchiveCompression Compression => _compression;

    /// <summary>Adds a file, or a directory and everything under it.</summary>
    /// <param name="path">The file or directory to add.</param>
    /// <param name="entryName">
    /// The name in the archive. Default: the file or directory name. For a directory, an empty string puts its contents
    /// at the root of the archive. Use <c>/</c> as the separator.
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="FileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    /// <exception cref="InvalidOperationException">The archive was already saved, or directories nest deeper than 128 levels.</exception>
    public ArchiveBuilder Add(string path, string? entryName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ThrowIfSaved();
        string full = Path.GetFullPath(path);
        bool isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full) && !IsLink(full))
        {
            throw new FileNotFoundException("Nothing to add at that path.", path);
        }

        string name = entryName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        name = name.Replace('\\', '/').Trim('/');
        if (name.Length == 0 && !isDirectory)
        {
            throw new ArgumentException("A file needs a non-empty entry name.", nameof(entryName));
        }

        AddPath(full, name, 0);
        return this;
    }

    /// <summary>Adds an entry you describe yourself, with its content in memory.</summary>
    /// <param name="entry">The entry; for a file its <see cref="ArchiveEntry.Size"/> must equal the content length.</param>
    /// <param name="content">The file content; <see langword="null"/> for directories, links and empty files.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The content length differs from the entry's size.</exception>
    public ArchiveBuilder Add(ArchiveEntry entry, byte[]? content = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ThrowIfSaved();
        long length = content?.Length ?? 0;
        if (entry.Kind == ArchiveEntryKind.File && entry.Size != length)
        {
            throw new ArgumentException($"The entry's size is {entry.Size} but the content is {length} bytes.", nameof(content));
        }

        _items.Add(new Item(entry, null, content));
        return this;
    }

    private void ThrowIfSaved() => ObjectDisposedException.ThrowIf(_saved, this);

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void AddPath(string full, string name, int depth)
    {
        if (depth > MaxDirectoryDepth)
        {
            throw new InvalidOperationException("The directory tree is nested deeper than 128 levels (or contains a symbolic link cycle).");
        }

        if (_fullOutputPath is not null && string.Equals(full, _fullOutputPath, StringComparison.Ordinal))
        {
            return;
        }

        bool isDirectory = Directory.Exists(full);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(full) : new FileInfo(full);
        string? linkTarget = info.LinkTarget;
        if (linkTarget is not null && !_options.FollowSymlinks)
        {
            _items.Add(new Item(new ArchiveEntry
            {
                Name = name,
                Kind = ArchiveEntryKind.Symlink,
                LinkTarget = linkTarget.Replace('\\', '/'),
                ModifiedTime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                Mode = (UnixFileMode)0x1FF,
            }, null, null));
            return;
        }

        DateTimeOffset modified = new(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (isDirectory)
        {
            if (name.Length > 0)
            {
                _items.Add(new Item(new ArchiveEntry
                {
                    Name = name,
                    Kind = ArchiveEntryKind.Directory,
                    ModifiedTime = modified,
                    Mode = ModeOf(full, directory: true),
                }, null, null));
            }

            string[] children = Directory.GetFileSystemEntries(full);
            string[] names = new string[children.Length];
            for (int i = 0; i < children.Length; i++)
            {
                names[i] = Path.GetFileName(children[i]);
            }

            Array.Sort(names, children, StringComparer.Ordinal);
            for (int i = 0; i < children.Length; i++)
            {
                AddPath(children[i], name.Length == 0 ? names[i] : name + "/" + names[i], depth + 1);
            }

            return;
        }

        long size = ((FileInfo)info).Length;
        _items.Add(new Item(new ArchiveEntry
        {
            Name = name,
            Kind = ArchiveEntryKind.File,
            Size = size,
            ModifiedTime = modified,
            Mode = ModeOf(full, directory: false),
        }, full, null));
    }

    private static UnixFileMode ModeOf(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser())
        {
            try
            {
                return File.GetUnixFileMode(path);
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return directory ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100;
    }

    private IEnumerable<Item> Ordered()
    {
        if (!_options.Normalize)
        {
            return _items;
        }

        var list = new List<Item>(_items.Count);
        foreach (Item item in _items)
        {
            ArchiveEntry e = item.Entry;
            UnixFileMode mode = e.Kind switch
            {
                ArchiveEntryKind.Directory => (UnixFileMode)0b111_101_101,
                ArchiveEntryKind.Symlink => (UnixFileMode)0x1FF,
                ArchiveEntryKind.File when e.Mode is { } m && (m & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0 => (UnixFileMode)0b111_101_101,
                _ => (UnixFileMode)0b110_100_100,
            };
            list.Add(item with
            {
                Entry = new ArchiveEntry
                {
                    Name = e.Name,
                    Kind = e.Kind,
                    Size = e.Size,
                    ModifiedTime = _options.NormalizedTime,
                    Mode = mode,
                    LinkTarget = e.LinkTarget,
                    DeviceMajor = e.DeviceMajor,
                    DeviceMinor = e.DeviceMinor,
                    Extras = e.Extras,
                },
            });
        }

        return list.OrderBy(static i => i.Entry.Name, StringComparer.Ordinal);
    }

    /// <summary>Writes the archive. It can be called once.</summary>
    /// <param name="cancellationToken">A token that cancels the write; a partly written file is deleted.</param>
    /// <exception cref="InvalidOperationException">The archive was already saved.</exception>
    /// <exception cref="ArgumentException">A file changed size since it was added.</exception>
    public void Save(CancellationToken cancellationToken = default) => Internal.Sync.Result(SaveCoreAsync(false, cancellationToken));

    /// <summary>Writes the archive. It can be called once.</summary>
    /// <param name="cancellationToken">A token that cancels the write; a partly written file is deleted.</param>
    /// <returns>A task that completes when the archive is written.</returns>
    public Task SaveAsync(CancellationToken cancellationToken = default) => SaveCoreAsync(true, cancellationToken).AsTask();

    private async ValueTask SaveCoreAsync(bool async, CancellationToken ct)
    {
        ThrowIfSaved();
        _saved = true;
        bool leaveSink = _stream is not null && _options.LeaveOpen;
        Stream sink = _stream ?? new FileStream(_path!, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = async ? FileOptions.Asynchronous : FileOptions.None,
        });
        bool ok = false;
        try
        {
            Stream top = _compression == ArchiveCompression.None
                ? sink
                : CompressionFilters.OpenCompress(_compression, sink, _options.CompressionLevel, leaveSink);
            var writer = new TarWriter(top, _options.Tar, leaveOpen: top == sink && leaveSink);
            try
            {
                foreach (Item item in Ordered())
                {
                    ct.ThrowIfCancellationRequested();
                    await WriteItemAsync(writer, item, async, ct).ConfigureAwait(false);
                }

                if (async)
                {
                    await writer.FinishAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    writer.Finish();
                }

                ok = true;
            }
            finally
            {
                // Disposing finishes the compressed stream and closes what the builder owns.
                if (async)
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    writer.Dispose();
                }
            }
        }
        catch
        {
            if (!ok && _stream is null)
            {
                try
                {
                    sink.Dispose();
                    File.Delete(_path!);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            throw;
        }
    }

    private static async ValueTask WriteItemAsync(TarWriter writer, Item item, bool async, CancellationToken ct)
    {
        if (item.FilePath is { } file)
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite,
                BufferSize = 64 * 1024,
                Options = (async ? FileOptions.Asynchronous : FileOptions.None) | FileOptions.SequentialScan,
            };
            using var content = new FileStream(file, options);
            if (content.Length != item.Entry.Size)
            {
                throw new ArgumentException($"The file '{file}' changed size after it was added (was {item.Entry.Size}, now {content.Length}).");
            }

            await WriteAsync(writer, item.Entry, content, async, ct).ConfigureAwait(false);
        }
        else if (item.Bytes is { Length: > 0 } bytes)
        {
            using var content = new MemoryReadStream(bytes);
            await WriteAsync(writer, item.Entry, content, async, ct).ConfigureAwait(false);
        }
        else
        {
            await WriteAsync(writer, item.Entry, null, async, ct).ConfigureAwait(false);
        }
    }

    private static ValueTask WriteAsync(TarWriter writer, ArchiveEntry entry, Stream? content, bool async, CancellationToken ct)
    {
        if (async)
        {
            return writer.WriteEntryAsync(entry, content, ct);
        }

        writer.WriteEntry(entry, content);
        return ValueTask.CompletedTask;
    }
}
