using System.Buffers;
using System.Text;

namespace RecursiveAI.OneArchive.Cpio;

/// <summary>
/// Writes cpio archives in the newc, crc or odc format.
/// </summary>
/// <remarks>
/// <para>The output depends only on the entries and options: times are written only when the entry has one (zero
/// otherwise), and inode numbers come from a counter that starts at 1 and increases by one per entry, unless the entry is a
/// <see cref="CpioEntry"/> with an <see cref="CpioEntry.Inode"/>. The archive ends with a <c>TRAILER!!!</c> entry and is
/// padded with zeros to <see cref="CpioWriterOptions.BlockSize"/>.</para>
/// <para>Defaults for missing fields: mode 0644 (files), 0755 (directories), 0777 (symbolic links); link count 2 for a
/// directory and 1 otherwise. Names are written as UTF-8; a directory's trailing <c>/</c> is dropped.</para>
/// <para>Hard links: <see cref="ArchiveEntryKind.HardLink"/> entries are refused, because newc stores the data with the last
/// name of a file and a streaming writer cannot know which that is. To write links, pass <see cref="CpioEntry"/> values with
/// the same <see cref="CpioEntry.Inode"/> and <see cref="CpioEntry.NLink"/> set, empty content for every name but the last, and
/// the data with the last (what GNU cpio writes). Formats other than odc need no further care; odc stores the data under every name.</para>
/// <para>Limits of the fields (odc: 18-bit device, inode, mode, uid, gid, link count; 33-bit size and time; newc: 32 bits) raise
/// <see cref="ArchiveUnsupportedException"/> naming the entry.</para>
/// </remarks>
public sealed class CpioWriter : ArchiveWriter
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly CpioWriterOptions _options;
    private long _written;
    private long _nextInode;
    private bool _finished;
    private bool _faulted;
    private bool _disposed;

    /// <summary>Creates a writer.</summary>
    /// <param name="stream">A writable stream.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the writer is disposed.</param>
    public CpioWriter(Stream stream, CpioWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _options = options ?? CpioWriterOptions.Default;
        if (_options.BlockSize < 1)
        {
            throw new ArgumentException("The block size must be positive.", nameof(options));
        }

        if (!Enum.IsDefined(_options.Format))
        {
            throw new ArgumentException("Unknown cpio format.", nameof(options));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public override ArchiveFormat Format => ArchiveFormat.Cpio;

    /// <inheritdoc />
    protected override async ValueTask WriteEntryAsync(ArchiveEntry entry, Stream? content, bool async, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The archive is already finished.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string name = entry.Name;
        if (string.IsNullOrEmpty(name) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The entry name must be non-empty and contain no NUL.", nameof(entry));
        }

        if (entry.Kind == ArchiveEntryKind.Directory)
        {
            name = name.Length > 1 ? name.TrimEnd('/') : name;
            if (name.Length == 0)
            {
                name = "/";
            }
        }

        if (entry.Kind == ArchiveEntryKind.HardLink)
        {
            throw new ArchiveUnsupportedException(CpioHeader.FormatName, $"hard link entries (entry '{entry.Name}'); write a CpioEntry with an inode and link count, or the file again");
        }

        bool isFile = entry.Kind == ArchiveEntryKind.File;
        byte[]? linkBytes = null;
        long size;
        if (entry.Kind == ArchiveEntryKind.Symlink)
        {
            if (string.IsNullOrEmpty(entry.LinkTarget))
            {
                throw new ArgumentException("A symbolic link needs a link target.", nameof(entry));
            }

            linkBytes = Encoding.UTF8.GetBytes(entry.LinkTarget);
            size = linkBytes.Length;
        }
        else
        {
            size = isFile ? entry.Size : 0;
        }

        if (size < 0)
        {
            throw new ArgumentException("The entry size is negative.", nameof(entry));
        }

        if (isFile && size > 0 && content is null)
        {
            throw new ArgumentException("A file entry with a size needs content.", nameof(entry));
        }

        var cpio = entry as CpioEntry;
        uint type = entry.Kind switch
        {
            ArchiveEntryKind.File => CpioHeader.TypeFile,
            ArchiveEntryKind.Directory => CpioHeader.TypeDirectory,
            ArchiveEntryKind.Symlink => CpioHeader.TypeSymlink,
            ArchiveEntryKind.CharacterDevice => CpioHeader.TypeChar,
            ArchiveEntryKind.BlockDevice => CpioHeader.TypeBlock,
            ArchiveEntryKind.Fifo => cpio is not null && (cpio.RawMode & CpioHeader.TypeMask) == CpioHeader.TypeSocket ? CpioHeader.TypeSocket : CpioHeader.TypeFifo,
            _ => throw new ArgumentException("Unknown entry kind.", nameof(entry)),
        };
        uint perm = entry.Mode is { } m
            ? (uint)m & 0xFFF
            : entry.Kind switch
            {
                ArchiveEntryKind.Directory => 0x1ED,
                ArchiveEntryKind.Symlink => 0x1FF,
                _ => 0x1A4,
            };
        long ino = cpio is { Inode: > 0 } ? cpio.Inode : ++_nextInode;
        long nlink = cpio is { NLink: > 0 } ? cpio.NLink : entry.Kind == ArchiveEntryKind.Directory ? 2 : 1;
        long mtime = entry.ModifiedTime is { } t ? t.ToUnixTimeSeconds() : 0;
        if (mtime < 0)
        {
            throw new ArchiveUnsupportedException(CpioHeader.FormatName, $"a modification time before 1970 (entry '{entry.Name}')");
        }

        bool device = entry.Kind is ArchiveEntryKind.CharacterDevice or ArchiveEntryKind.BlockDevice;
        long rdevMajor = device ? entry.DeviceMajor : 0;
        long rdevMinor = device ? entry.DeviceMinor : 0;
        long devMajor = cpio?.FileSystemDeviceMajor ?? 0;
        long devMinor = cpio?.FileSystemDeviceMinor ?? 0;
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);

        // The crc format puts the byte sum before the data, so it has to be known first.
        uint check = 0;
        Stream? data = content;
        MemoryStream? buffered = null;
        if (_options.Format == CpioFormat.Crc)
        {
            // GNU cpio sums regular files only: a symbolic link's target has a checksum of zero.
            if (isFile && size > 0)
            {
                (check, buffered) = await SumAsync(content!, size, entry.Name, async, cancellationToken).ConfigureAwait(false);
                data = buffered ?? content;
            }
        }

        byte[] header;
        try
        {
            header = BuildHeader(entry.Name, nameBytes.Length + 1, size, type | perm, entry.Uid, entry.Gid, ino, nlink, mtime, devMajor, devMinor, rdevMajor, rdevMinor, check);
        }
        catch
        {
            buffered?.Dispose();
            throw;
        }

        try
        {
            await WriteAsync(header, async, cancellationToken).ConfigureAwait(false);
            await WriteAsync(nameBytes, async, cancellationToken).ConfigureAwait(false);
            await WriteAsync(new byte[1], async, cancellationToken).ConfigureAwait(false);
            if (_options.Format != CpioFormat.Odc)
            {
                await PadAsync(header.Length + nameBytes.Length + 1, async, cancellationToken).ConfigureAwait(false);
            }

            if (linkBytes is not null)
            {
                await WriteAsync(linkBytes, async, cancellationToken).ConfigureAwait(false);
            }
            else if (isFile && size > 0)
            {
                await CopyAsync(data!, size, async, cancellationToken).ConfigureAwait(false);
            }

            if (_options.Format != CpioFormat.Odc)
            {
                await PadAsync(size, async, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    private async ValueTask<(uint Sum, MemoryStream? Buffer)> SumAsync(Stream content, long size, string entryName, bool async, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        MemoryStream? spool = null;
        try
        {
            bool rewind = content.CanSeek;
            long start = rewind ? content.Position : 0;
            if (!rewind)
            {
                if (size > _options.MaxBufferedSize)
                {
                    throw new ArchiveUnsupportedException(CpioHeader.FormatName, $"computing the crc-format checksum of {size} bytes of content that cannot seek (entry '{entryName}'; the limit is CpioWriterOptions.MaxBufferedSize)");
                }

                spool = new MemoryStream((int)size);
            }

            uint sum = 0;
            long left = size;
            while (left > 0)
            {
                int want = (int)Math.Min(buffer.Length, left);
                int n = async
                    ? await content.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false)
                    : content.Read(buffer, 0, want);
                if (n == 0)
                {
                    throw new ArgumentException($"The content ended {left} bytes before the entry's declared size of {size}.", nameof(content));
                }

                sum += CpioHeader.ByteSum(buffer.AsSpan(0, n));
                spool?.Write(buffer, 0, n);
                left -= n;
            }

            if (rewind)
            {
                content.Seek(start, SeekOrigin.Begin);
            }
            else
            {
                spool!.Position = 0;
            }

            return (sum, spool);
        }
        catch
        {
            spool?.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private byte[] BuildHeader(string entryName, long nameSize, long size, long mode, long uid, long gid, long ino, long nlink, long mtime, long devMajor, long devMinor, long rdevMajor, long rdevMinor, uint check)
    {
        bool odc = _options.Format == CpioFormat.Odc;
        byte[] h = new byte[odc ? CpioHeader.OdcHeaderLength : CpioHeader.NewcHeaderLength];
        Encoding.ASCII.GetBytes(CpioHeader.Magic(_options.Format), h);
        bool ok;
        string what;
        if (odc)
        {
            if (rdevMajor > 0x3FF || rdevMinor > 0xFF || rdevMajor < 0 || rdevMinor < 0)
            {
                throw new ArchiveUnsupportedException(CpioHeader.FormatName, $"a device major above 1023 or minor above 255 in odc format (entry '{entryName}')");
            }

            Span<byte> s = h;
            (ok, what) =
                !CpioHeader.PutOctal(s.Slice(6, 6), devMajor) ? (false, "file system device") :
                !CpioHeader.PutOctal(s.Slice(12, 6), ino) ? (false, "inode number") :
                !CpioHeader.PutOctal(s.Slice(18, 6), mode) ? (false, "mode") :
                !CpioHeader.PutOctal(s.Slice(24, 6), uid) ? (false, "uid") :
                !CpioHeader.PutOctal(s.Slice(30, 6), gid) ? (false, "gid") :
                !CpioHeader.PutOctal(s.Slice(36, 6), nlink) ? (false, "link count") :
                !CpioHeader.PutOctal(s.Slice(42, 6), (rdevMajor << 8) | rdevMinor) ? (false, "device number") :
                !CpioHeader.PutOctal(s.Slice(48, 11), mtime) ? (false, "modification time") :
                !CpioHeader.PutOctal(s.Slice(59, 6), nameSize) ? (false, "name length") :
                !CpioHeader.PutOctal(s.Slice(65, 11), size) ? (false, "size") :
                (true, string.Empty);
        }
        else
        {
            Span<byte> s = h;
            (ok, what) =
                !CpioHeader.PutHex(s.Slice(6, 8), ino) ? (false, "inode number") :
                !CpioHeader.PutHex(s.Slice(14, 8), mode) ? (false, "mode") :
                !CpioHeader.PutHex(s.Slice(22, 8), uid) ? (false, "uid") :
                !CpioHeader.PutHex(s.Slice(30, 8), gid) ? (false, "gid") :
                !CpioHeader.PutHex(s.Slice(38, 8), nlink) ? (false, "link count") :
                !CpioHeader.PutHex(s.Slice(46, 8), mtime) ? (false, "modification time") :
                !CpioHeader.PutHex(s.Slice(54, 8), size) ? (false, "size") :
                !CpioHeader.PutHex(s.Slice(62, 8), devMajor) ? (false, "file system device major") :
                !CpioHeader.PutHex(s.Slice(70, 8), devMinor) ? (false, "file system device minor") :
                !CpioHeader.PutHex(s.Slice(78, 8), rdevMajor) ? (false, "device major") :
                !CpioHeader.PutHex(s.Slice(86, 8), rdevMinor) ? (false, "device minor") :
                !CpioHeader.PutHex(s.Slice(94, 8), nameSize) ? (false, "name length") :
                !CpioHeader.PutHex(s.Slice(102, 8), check) ? (false, "checksum") :
                (true, string.Empty);
        }

        if (!ok)
        {
            throw new ArchiveUnsupportedException(CpioHeader.FormatName, $"a {what} that does not fit the {_options.Format} header (entry '{entryName}')");
        }

        return h;
    }

    private async ValueTask CopyAsync(Stream content, long size, bool async, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long left = size;
            while (left > 0)
            {
                int want = (int)Math.Min(buffer.Length, left);
                int n = async
                    ? await content.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false)
                    : content.Read(buffer, 0, want);
                if (n == 0)
                {
                    throw new ArgumentException($"The content ended {left} bytes before the entry's declared size of {size}.", nameof(content));
                }

                await WriteAsync(buffer.AsMemory(0, n), async, ct).ConfigureAwait(false);
                left -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ValueTask PadAsync(long length, bool async, CancellationToken ct)
    {
        int pad = (int)(CpioHeader.Pad4(length) - length);
        return pad == 0 ? ValueTask.CompletedTask : WriteAsync(new byte[pad], async, ct);
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool async, CancellationToken ct)
    {
        if (async)
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        else
        {
            _stream.Write(data.Span);
        }

        _written += data.Length;
    }

    /// <inheritdoc />
    protected override async ValueTask FinishAsync(bool async, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            byte[] trailer = BuildHeader("TRAILER!!!", 11, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);
            await WriteAsync(trailer, async, cancellationToken).ConfigureAwait(false);
            await WriteAsync("TRAILER!!!\0"u8.ToArray(), async, cancellationToken).ConfigureAwait(false);
            if (_options.Format != CpioFormat.Odc)
            {
                await PadAsync(trailer.Length + 11, async, cancellationToken).ConfigureAwait(false);
            }

            long block = _options.BlockSize;
            long pad = (block - (_written % block)) % block;
            while (pad > 0)
            {
                int n = (int)Math.Min(pad, 64 * 1024);
                await WriteAsync(new byte[n], async, cancellationToken).ConfigureAwait(false);
                pad -= n;
            }

            if (async)
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _stream.Flush();
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }

        try
        {
            if (!_finished && !_faulted)
            {
                Finish();
            }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_finished && !_faulted)
            {
                await FinishAsync(true, default).ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
