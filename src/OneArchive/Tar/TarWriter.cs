using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneArchive.Tar;

/// <summary>
/// Writes tar archives in POSIX ustar or pax form. The bytes depend only on the entries and the options: field widths
/// and the order of pax records are fixed, and nothing host-specific (process id, clock, user database) is added.
/// </summary>
/// <remarks>
/// <para>With <see cref="TarFormat.Pax"/> (the default), a field that does not fit its ustar slot is written as a pax
/// record in an extended header placed before the entry: <c>path</c> (names over 100 bytes or non-ASCII), <c>linkpath</c>,
/// <c>size</c> (over 8 GiB - 1), <c>uid</c> and <c>gid</c> (over 2,097,151), <c>uname</c> and <c>gname</c> (over 31 bytes),
/// <c>mtime</c> (sub-second, negative or beyond the 11-digit field), and <c>atime</c>/<c>ctime</c> when the entry has
/// them. The entry's <see cref="ArchiveEntry.Extras"/> follow, and records are sorted by key. The extended header is named
/// <c>././@PaxHeader</c> with mode 0644, owner 0, time 0.</para>
/// <para>The archive ends with two zero blocks, padded with zeros to <see cref="TarWriterOptions.RecordSize"/>.</para>
/// <para>Data is written in the order of the calls. Directory names get a trailing slash; a missing mode becomes 0644 (0755
/// for directories, 0777 for symbolic links); a missing time is 0.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class TarWriter : ArchiveWriter
{
    private static readonly HashSet<string> s_modelled = new(StringComparer.Ordinal)
    {
        "path", "linkpath", "size", "uid", "gid", "uname", "gname", "mtime", "atime", "ctime", "charset", "hdrcharset",
    };

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly TarWriterOptions _options;
    private readonly byte[] _header = new byte[TarHeader.BlockSize];
    private long _written;
    private bool _finished;
    private bool _faulted;
    private bool _disposed;

    /// <summary>Creates a writer that appends tar data to <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream.</param>
    /// <param name="options">Writing options; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when the writer is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written, or the options are invalid.</exception>
    public TarWriter(Stream stream, TarWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _options = options ?? TarWriterOptions.Default;
        if (_options.Format is not (TarFormat.Pax or TarFormat.Ustar))
        {
            throw new ArgumentException("Only the pax and ustar formats can be written.", nameof(options));
        }

        if (_options.RecordSize < TarHeader.BlockSize || _options.RecordSize % TarHeader.BlockSize != 0)
        {
            throw new ArgumentException("The record size must be a positive multiple of 512.", nameof(options));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public override ArchiveFormat Format => ArchiveFormat.Tar;

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
        bool pax = _options.Format == TarFormat.Pax;
        bool isFile = entry.Kind == ArchiveEntryKind.File;
        long size = isFile ? entry.Size : 0;
        if (size < 0)
        {
            throw new ArgumentException("The entry size is negative.", nameof(entry));
        }

        if (isFile && size > 0 && content is null)
        {
            throw new ArgumentException("A file entry with a size needs content.", nameof(entry));
        }

        string name = entry.Name;
        if (string.IsNullOrEmpty(name) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The entry name must be non-empty and contain no NUL.", nameof(entry));
        }

        if (entry.Kind == ArchiveEntryKind.Directory && !name.EndsWith('/'))
        {
            name += "/";
        }

        if ((entry.Kind is ArchiveEntryKind.Symlink or ArchiveEntryKind.HardLink) && string.IsNullOrEmpty(entry.LinkTarget))
        {
            throw new ArgumentException("A link entry needs a link target.", nameof(entry));
        }

        // Build the header and the pax records it needs, before anything is written.
        var records = new List<KeyValuePair<string, string>>();
        byte[] block = _header;
        Array.Clear(block);

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        bool ascii = Ascii.IsValid(nameBytes);
        if (nameBytes.Length <= TarHeader.NameLen && (ascii || !pax))
        {
            nameBytes.CopyTo(block, TarHeader.NameAt);
        }
        else if (!pax)
        {
            SplitUstarName(nameBytes, block, entry.Name);
        }
        else
        {
            records.Add(new("path", name));
            Truncate(nameBytes, TarHeader.NameLen).CopyTo(block.AsSpan(TarHeader.NameAt));
        }

        UnixFileMode mode = entry.Mode ?? entry.Kind switch
        {
            ArchiveEntryKind.Directory => (UnixFileMode)0b111_101_101,
            ArchiveEntryKind.Symlink => (UnixFileMode)0b111_111_111,
            _ => (UnixFileMode)0b110_100_100,
        };
        TarHeader.TryWriteOctal(block.AsSpan(TarHeader.ModeAt, TarHeader.ModeLen), (int)mode & 0xFFF);

        Number(block, TarHeader.UidAt, TarHeader.UidLen, entry.Uid, TarHeader.MaxOctal7, "uid", "uid", records, pax, entry.Name);
        Number(block, TarHeader.GidAt, TarHeader.GidLen, entry.Gid, TarHeader.MaxOctal7, "gid", "gid", records, pax, entry.Name);
        if (isFile)
        {
            Number(block, TarHeader.SizeAt, TarHeader.SizeLen, size, TarHeader.MaxOctal11, "size", "size", records, pax, entry.Name);
        }
        else
        {
            TarHeader.TryWriteOctal(block.AsSpan(TarHeader.SizeAt, TarHeader.SizeLen), 0);
        }

        // Times: the header holds whole seconds; anything else goes to a pax record.
        if (entry.ModifiedTime is { } modified)
        {
            long seconds = TarHeader.FloorSeconds(modified, out bool fraction);
            if (seconds >= 0 && seconds <= TarHeader.MaxOctal11)
            {
                TarHeader.TryWriteOctal(block.AsSpan(TarHeader.MtimeAt, TarHeader.MtimeLen), seconds);
                if (fraction && pax)
                {
                    records.Add(new("mtime", TarHeader.FormatPaxTime(modified)));
                }
            }
            else if (pax)
            {
                TarHeader.TryWriteOctal(block.AsSpan(TarHeader.MtimeAt, TarHeader.MtimeLen), 0);
                records.Add(new("mtime", TarHeader.FormatPaxTime(modified)));
            }
            else
            {
                throw new ArchiveUnsupportedException(TarHeader.FormatName, $"a modification time outside 1970..2242 in ustar format (entry '{entry.Name}')");
            }
        }
        else
        {
            TarHeader.TryWriteOctal(block.AsSpan(TarHeader.MtimeAt, TarHeader.MtimeLen), 0);
        }

        if (pax)
        {
            if (entry.AccessTime is { } atime)
            {
                records.Add(new("atime", TarHeader.FormatPaxTime(atime)));
            }

            if (entry.ChangeTime is { } ctime)
            {
                records.Add(new("ctime", TarHeader.FormatPaxTime(ctime)));
            }
        }

        block[TarHeader.TypeAt] = entry.Kind switch
        {
            ArchiveEntryKind.File => (byte)'0',
            ArchiveEntryKind.Directory => (byte)'5',
            ArchiveEntryKind.Symlink => (byte)'2',
            ArchiveEntryKind.HardLink => (byte)'1',
            ArchiveEntryKind.CharacterDevice => (byte)'3',
            ArchiveEntryKind.BlockDevice => (byte)'4',
            ArchiveEntryKind.Fifo => (byte)'6',
            _ => throw new ArgumentException("Unknown entry kind.", nameof(entry)),
        };

        if (entry.LinkTarget is { Length: > 0 } target && entry.Kind is ArchiveEntryKind.Symlink or ArchiveEntryKind.HardLink)
        {
            byte[] linkBytes = Encoding.UTF8.GetBytes(target);
            if (linkBytes.Length <= TarHeader.LinkLen && (Ascii.IsValid(linkBytes) || !pax))
            {
                linkBytes.CopyTo(block, TarHeader.LinkAt);
            }
            else if (pax)
            {
                records.Add(new("linkpath", target));
                Truncate(linkBytes, TarHeader.LinkLen).CopyTo(block.AsSpan(TarHeader.LinkAt));
            }
            else
            {
                throw new ArchiveUnsupportedException(TarHeader.FormatName, $"a link target over 100 bytes in ustar format (entry '{entry.Name}')");
            }
        }

        "ustar\0"u8.CopyTo(block.AsSpan(TarHeader.MagicAt));
        "00"u8.CopyTo(block.AsSpan(TarHeader.VersionAt));
        Text(block, TarHeader.UnameAt, TarHeader.UnameLen, entry.UserName, "uname", records, pax, entry.Name);
        Text(block, TarHeader.GnameAt, TarHeader.GnameLen, entry.GroupName, "gname", records, pax, entry.Name);

        bool device = entry.Kind is ArchiveEntryKind.CharacterDevice or ArchiveEntryKind.BlockDevice;
        if (!TarHeader.TryWriteOctal(block.AsSpan(TarHeader.DevMajorAt, TarHeader.DevMajorLen), device ? entry.DeviceMajor : 0) ||
            !TarHeader.TryWriteOctal(block.AsSpan(TarHeader.DevMinorAt, TarHeader.DevMinorLen), device ? entry.DeviceMinor : 0))
        {
            throw new ArchiveUnsupportedException(TarHeader.FormatName, $"a device number above 2097151 (entry '{entry.Name}')");
        }

        if (pax)
        {
            foreach (var extra in entry.Extras)
            {
                if (!s_modelled.Contains(extra.Key) && extra.Key.Length > 0 && extra.Value.Length > 0)
                {
                    records.Add(new(extra.Key, extra.Value));
                }
            }
        }

        SealChecksum(block);

        try
        {
            if (records.Count > 0)
            {
                records.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                await WritePaxHeaderAsync(records, async, cancellationToken).ConfigureAwait(false);
            }

            await WriteAsync(block, async, cancellationToken).ConfigureAwait(false);
            if (isFile && size > 0)
            {
                await CopyContentAsync(content!, size, async, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private static ReadOnlySpan<byte> Truncate(byte[] bytes, int max)
    {
        if (bytes.Length <= max)
        {
            return bytes;
        }

        int cut = max;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
        {
            cut--;
        }

        return bytes.AsSpan(0, cut);
    }

    // ustar: "prefix/name" with name up to 100 bytes and prefix up to 155. Picks the leftmost split that works.
    private static void SplitUstarName(byte[] name, byte[] block, string entryName)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == (byte)'/' && i > 0 && i < name.Length - 1 && name.Length - i - 1 <= TarHeader.NameLen)
            {
                if (i > TarHeader.PrefixLen)
                {
                    break;
                }

                name.AsSpan(0, i).CopyTo(block.AsSpan(TarHeader.PrefixAt));
                name.AsSpan(i + 1).CopyTo(block.AsSpan(TarHeader.NameAt));
                return;
            }
        }

        throw new ArchiveUnsupportedException(TarHeader.FormatName, $"a name that cannot be split into a 155-byte prefix and a 100-byte name in ustar format (entry '{entryName}')");
    }

    private static void Number(byte[] block, int at, int len, long value, long max, string key, string what, List<KeyValuePair<string, string>> records, bool pax, string entryName)
    {
        if (value >= 0 && value <= max)
        {
            TarHeader.TryWriteOctal(block.AsSpan(at, len), value);
        }
        else if (pax && value >= 0)
        {
            TarHeader.TryWriteOctal(block.AsSpan(at, len), 0);
            records.Add(new(key, value.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            throw new ArchiveUnsupportedException(TarHeader.FormatName, $"a {what} of {value} in ustar format (entry '{entryName}')");
        }
    }

    private static void Text(byte[] block, int at, int len, string? value, string key, List<KeyValuePair<string, string>> records, bool pax, string entryName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length < len && (Ascii.IsValid(bytes) || !pax))
        {
            bytes.CopyTo(block, at);
        }
        else if (pax)
        {
            records.Add(new(key, value));
            Truncate(bytes, len - 1).CopyTo(block.AsSpan(at));
        }
        else
        {
            throw new ArchiveUnsupportedException(TarHeader.FormatName, $"a {key} over 31 bytes in ustar format (entry '{entryName}')");
        }
    }

    // Checksum: the sum of all bytes with the checksum field read as spaces; six octal digits, NUL, space.
    private static void SealChecksum(byte[] block)
    {
        uint sum = TarHeader.ComputeChecksum(block, false);
        TarHeader.TryWriteOctal(block.AsSpan(TarHeader.ChecksumAt, 7), sum);
        block[TarHeader.ChecksumAt + 6] = 0;
        block[TarHeader.ChecksumAt + 7] = (byte)' ';
        // TryWriteOctal wrote 6 digits and a NUL into a 7-byte span; the space follows.
    }

    private async ValueTask WritePaxHeaderAsync(List<KeyValuePair<string, string>> records, bool async, CancellationToken ct)
    {
        var data = new MemoryStream();
        foreach (var r in records)
        {
            TarHeader.AppendPaxRecord(data, r.Key, r.Value);
        }

        byte[] header = new byte[TarHeader.BlockSize];
        "././@PaxHeader"u8.CopyTo(header);
        TarHeader.TryWriteOctal(header.AsSpan(TarHeader.ModeAt, TarHeader.ModeLen), 0b110_100_100);
        TarHeader.TryWriteOctal(header.AsSpan(TarHeader.UidAt, TarHeader.UidLen), 0);
        TarHeader.TryWriteOctal(header.AsSpan(TarHeader.GidAt, TarHeader.GidLen), 0);
        TarHeader.TryWriteOctal(header.AsSpan(TarHeader.SizeAt, TarHeader.SizeLen), data.Length);
        TarHeader.TryWriteOctal(header.AsSpan(TarHeader.MtimeAt, TarHeader.MtimeLen), 0);
        header[TarHeader.TypeAt] = (byte)'x';
        "ustar\0"u8.CopyTo(header.AsSpan(TarHeader.MagicAt));
        "00"u8.CopyTo(header.AsSpan(TarHeader.VersionAt));
        TarHeader.TryWriteOctal(header.AsSpan(TarHeader.DevMajorAt, TarHeader.DevMajorLen), 0);
        TarHeader.TryWriteOctal(header.AsSpan(TarHeader.DevMinorAt, TarHeader.DevMinorLen), 0);
        SealChecksum(header);
        await WriteAsync(header, async, ct).ConfigureAwait(false);

        byte[] bytes = data.ToArray();
        await WriteAsync(bytes, async, ct).ConfigureAwait(false);
        await PadAsync(bytes.Length, async, ct).ConfigureAwait(false);
    }

    private async ValueTask CopyContentAsync(Stream content, long size, bool async, CancellationToken ct)
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

        await PadAsync(size, async, ct).ConfigureAwait(false);
    }

    private ValueTask PadAsync(long dataLength, bool async, CancellationToken ct)
    {
        int pad = (int)((TarHeader.BlockSize - (dataLength % TarHeader.BlockSize)) % TarHeader.BlockSize);
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
            await WriteAsync(new byte[2 * TarHeader.BlockSize], async, cancellationToken).ConfigureAwait(false);
            long record = _options.RecordSize;
            long pad = (record - (_written % record)) % record;
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
