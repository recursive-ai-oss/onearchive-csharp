using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneArchive.Tar;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive;

/// <summary>The outcome of an extraction: how many entries were created, how many were skipped and how many content bytes were written. Returned by <see cref="Archive.Extract(string, string, ArchiveExtractOptions?, System.Threading.CancellationToken)"/>; a limit or safety failure throws instead and never produces a partial result.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ArchiveExtractResult
{
    internal ArchiveExtractResult(int extracted, int skipped, long bytes)
    {
        EntriesExtracted = extracted;
        EntriesSkipped = skipped;
        BytesWritten = bytes;
    }

    /// <summary>Gets the number of entries that were created (directories, files and links).</summary>
    public int EntriesExtracted { get; }

    /// <summary>Gets the number of entries that were skipped by a filter or by a policy set to skip.</summary>
    public int EntriesSkipped { get; }

    /// <summary>Gets the number of content bytes written (sparse holes are not counted).</summary>
    public long BytesWritten { get; }
}

/// <summary>
/// The extraction engine shared by every container: turns the entries of an <see cref="ArchiveReader"/> into files
/// under a target directory, applying the safety rules in <see cref="ArchiveExtractOptions"/>.
/// </summary>
internal sealed class ArchiveExtractor
{
    private readonly ArchiveExtractOptions _options;
    private readonly string _format;
    private readonly string _root;
    private readonly HashSet<string> _safeDirs = new(StringComparer.Ordinal) { string.Empty };
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<(string Path, UnixFileMode? Mode, DateTimeOffset? Time)> _directories = [];
    private int _extracted;
    private int _skipped;
    private long _bytes;

    public ArchiveExtractor(string targetDirectory, ArchiveExtractOptions options, string format)
    {
        _options = options;
        _format = format;
        string full = Path.GetFullPath(targetDirectory);
        _root = Path.TrimEndingDirectorySeparator(full);
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<ArchiveExtractResult> RunAsync(ArchiveReader reader, bool async, CancellationToken ct)
    {
        while (true)
        {
            ArchiveEntry? entry = async
                ? await reader.GetNextEntryAsync(ct).ConfigureAwait(false)
                : reader.GetNextEntry();
            if (entry is null)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            await ExtractEntryAsync(reader, entry, async, ct).ConfigureAwait(false);
        }

        // Directory permissions and times go last: a read-only directory must not stop its own contents, and
        // writing files changes a directory's time.
        for (int i = _directories.Count - 1; i >= 0; i--)
        {
            var (path, mode, time) = _directories[i];
            ApplyMetadata(path, mode, time, isDirectory: true);
        }

        return new ArchiveExtractResult(_extracted, _skipped, _bytes);
    }

    private ArchiveSafetyException Unsafe(ArchiveEntry entry, string rule, string detail) => new(_format, rule, entry.Name, detail);

    private async ValueTask ExtractEntryAsync(ArchiveReader reader, ArchiveEntry entry, bool async, CancellationToken ct)
    {
        if (_options.Filter is { } filter && !filter(entry))
        {
            _skipped++;
            return;
        }

        if (entry.Kind is ArchiveEntryKind.CharacterDevice or ArchiveEntryKind.BlockDevice or ArchiveEntryKind.Fifo)
        {
            if (_options.SpecialFiles == SpecialFilePolicy.Skip)
            {
                _skipped++;
                return;
            }

            throw Unsafe(entry, "special-file", $"the entry is a {entry.Kind} and device nodes and FIFOs are never created");
        }

        List<string>? parts = Internal.PathRules.Resolve(entry.Name, _options.UnsafePaths == UnsafePathHandling.Sanitize, out string rule, out string detail)
            ?? throw Unsafe(entry, rule, detail);
        if (parts.Count == 0)
        {
            if (entry.Kind == ArchiveEntryKind.Directory)
            {
                // "." names the target directory itself.
                _extracted++;
                return;
            }

            throw Unsafe(entry, "invalid-name", "the name is empty after removing '.' components");
        }

        string parent = EnsureParents(entry, parts);
        string dest = Path.Combine(parent, parts[^1]);
        EnsureInside(entry, dest);
        string relKey = string.Join('/', parts);

        switch (entry.Kind)
        {
            case ArchiveEntryKind.Directory:
                ExtractDirectory(entry, dest, relKey);
                break;
            case ArchiveEntryKind.File:
                await ExtractFileAsync(reader, entry, dest, relKey, async, ct).ConfigureAwait(false);
                break;
            case ArchiveEntryKind.Symlink:
                ExtractSymlink(entry, dest, parts);
                break;
            case ArchiveEntryKind.HardLink:
                ExtractHardLink(entry, dest, relKey);
                break;
            default:
                throw Unsafe(entry, "special-file", "unknown entry kind");
        }
    }

    // Defence in depth: whatever the rules above decided, the final path must lie under the root.
    private void EnsureInside(ArchiveEntry entry, string dest)
    {
        string full = Path.GetFullPath(dest);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw Unsafe(entry, "path-traversal", "the resolved path is outside the target directory");
        }
    }

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
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Creates missing parent directories and checks that none of the existing ones is a link. Returns the parent path.
    private string EnsureParents(ArchiveEntry entry, List<string> parts)
    {
        string current = _root;
        string key = string.Empty;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            current = Path.Combine(current, parts[i]);
            key = key.Length == 0 ? parts[i] : key + "/" + parts[i];
            if (_safeDirs.Contains(key))
            {
                continue;
            }

            if (IsLink(current))
            {
                if (!_options.AllowWriteThroughSymlinks)
                {
                    throw Unsafe(entry, "link-write-through", $"'{key}' is a symbolic link and the entry would be written through it");
                }
            }
            else if (File.Exists(current))
            {
                throw Unsafe(entry, "existing-file", $"'{key}' is a file, not a directory");
            }
            else
            {
                Directory.CreateDirectory(current);
            }

            _safeDirs.Add(key);
        }

        return current;
    }

    private void ExtractDirectory(ArchiveEntry entry, string dest, string relKey)
    {
        if (IsLink(dest))
        {
            if (!_options.AllowWriteThroughSymlinks)
            {
                throw Unsafe(entry, "link-write-through", $"'{relKey}' already exists as a symbolic link");
            }
        }
        else if (File.Exists(dest))
        {
            throw Unsafe(entry, "existing-file", $"'{relKey}' already exists as a file");
        }
        else
        {
            Directory.CreateDirectory(dest);
        }

        _safeDirs.Add(relKey);
        _directories.Add((dest, entry.Mode, entry.ModifiedTime));
        _extracted++;
    }

    // Applies the policy for an existing destination. Returns false when the entry is to be skipped.
    private bool PrepareDestination(ArchiveEntry entry, string dest, string relKey)
    {
        bool link = IsLink(dest);
        if (!link && !File.Exists(dest))
        {
            if (Directory.Exists(dest))
            {
                throw Unsafe(entry, "existing-file", $"'{relKey}' already exists as a directory");
            }

            return true;
        }

        switch (_options.Existing)
        {
            case ExistingFilePolicy.Skip:
                _skipped++;
                return false;
            case ExistingFilePolicy.Overwrite:
                // Replace the link or file itself; never write through a link.
                File.Delete(dest);
                _files.Remove(relKey);
                return true;
            default:
                throw Unsafe(entry, "existing-file", $"'{relKey}' already exists");
        }
    }

    private async ValueTask ExtractFileAsync(ArchiveReader reader, ArchiveEntry entry, string dest, string relKey, bool async, CancellationToken ct)
    {
        if (!PrepareDestination(entry, dest, relKey))
        {
            return;
        }

        bool sparse = entry is TarEntry { SparseFormat: not TarSparseFormat.None };
        Stream content = reader.OpenEntryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        try
        {
            // CreateNew never follows a link that appeared since the check above.
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = async ? FileOptions.Asynchronous : FileOptions.None,
            };
            if (entry.Size > 0 && entry.Size <= _options.Limits.MaxTotalSize)
            {
                options.PreallocationSize = sparse ? 0 : entry.Size;
            }

            var file = new FileStream(dest, options);
            bool complete = false;
            try
            {
                while (true)
                {
                    int n = async
                        ? await content.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)
                        : content.Read(buffer, 0, buffer.Length);
                    if (n == 0)
                    {
                        break;
                    }

                    if (n > _options.Limits.MaxTotalSize - _bytes)
                    {
                        throw new CompressionLimitException(_format, nameof(ArchiveLimits.MaxTotalSize), _options.Limits.MaxTotalSize, _bytes + n);
                    }

                    if (sparse && !buffer.AsSpan(0, n).ContainsAnyExcept((byte)0))
                    {
                        // A hole (or zeros that look like one): leave a gap instead of writing it.
                        file.Seek(n, SeekOrigin.Current);
                    }
                    else if (async)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        _bytes += n;
                    }
                    else
                    {
                        file.Write(buffer, 0, n);
                        _bytes += n;
                    }
                }

                if (sparse)
                {
                    file.SetLength(entry.Size);
                }

                complete = true;
            }
            finally
            {
                if (async)
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    file.Dispose();
                }

                if (!complete)
                {
                    // A failed extraction does not leave half a file that looks like a result.
                    TryDelete(dest);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        ApplyMetadata(dest, entry.Mode, entry.ModifiedTime, isDirectory: false);
        _files[relKey] = dest;
        _extracted++;
    }

    private void ExtractSymlink(ArchiveEntry entry, string dest, List<string> parts)
    {
        if (_options.Symlinks == LinkPolicy.Skip)
        {
            _skipped++;
            return;
        }

        string target = entry.LinkTarget ?? string.Empty;
        if (target.Length == 0 || target.Contains('\0', StringComparison.Ordinal))
        {
            throw Unsafe(entry, "link-escape", "the symbolic link has an empty target or one containing NUL");
        }

        if (_options.Symlinks == LinkPolicy.Safe && !Internal.PathRules.IsSafeSymlinkTarget(target, parts.Count - 1))
        {
            throw Unsafe(entry, "link-escape", $"the symbolic link points to '{Printable(target)}', which is absolute or could leave the target directory");
        }

        string relKey = string.Join('/', parts);
        if (!PrepareDestination(entry, dest, relKey))
        {
            return;
        }

        try
        {
            File.CreateSymbolicLink(dest, target);
        }
        catch (PlatformNotSupportedException e)
        {
            throw new ArchiveUnsupportedException("archive", "symbolic links on this platform", e);
        }

        _extracted++;
    }

    private void ExtractHardLink(ArchiveEntry entry, string dest, string relKey)
    {
        if (_options.HardLinks == LinkPolicy.Skip)
        {
            _skipped++;
            return;
        }

        string target = entry.LinkTarget ?? string.Empty;
        List<string>? targetParts = Internal.PathRules.Resolve(target, _options.UnsafePaths == UnsafePathHandling.Sanitize, out _, out string detail);
        if (targetParts is null || targetParts.Count == 0)
        {
            throw Unsafe(entry, "hardlink-target", $"the hard link target '{Printable(target)}' is not a safe relative path ({detail})");
        }

        string targetKey = string.Join('/', targetParts);
        if (!_files.TryGetValue(targetKey, out string? source) || IsLink(source) || !File.Exists(source))
        {
            throw Unsafe(entry, "hardlink-target", $"the hard link target '{Printable(target)}' is not a file extracted earlier from this archive");
        }

        if (!PrepareDestination(entry, dest, relKey))
        {
            return;
        }

        // A hard link is created as a copy: the platform API for real links is not available to managed code, and a
        // copy cannot be used to reach a file outside the target.
        File.Copy(source, dest, overwrite: false);
        ApplyMetadata(dest, null, entry.ModifiedTime, isDirectory: false);
        _files[relKey] = dest;
        _extracted++;
    }

    private void ApplyMetadata(string path, UnixFileMode? mode, DateTimeOffset? time, bool isDirectory)
    {
        if (_options.RestorePermissions && mode is { } m && !OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser())
        {
            UnixFileMode keep = _options.KeepSpecialModeBits ? (UnixFileMode)0xFFF : (UnixFileMode)0x1FF;
            try
            {
                File.SetUnixFileMode(path, m & keep);
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        if (_options.RestoreTimes && time is { } t)
        {
            try
            {
                if (isDirectory)
                {
                    Directory.SetLastWriteTimeUtc(path, t.UtcDateTime);
                }
                else
                {
                    File.SetLastWriteTimeUtc(path, t.UtcDateTime);
                }
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Printable(string text) => text.Length > 200 ? text[..200] : text;
}
