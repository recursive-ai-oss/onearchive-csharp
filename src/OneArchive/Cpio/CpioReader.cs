using System.Diagnostics.CodeAnalysis;
using System.Text;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Cpio;

/// <summary>
/// Reads cpio archives in the newc (<c>070701</c>), crc (<c>070702</c>) and odc (<c>070707</c>) formats.
/// </summary>
/// <remarks>
/// <para>Formats: the GNU cpio manual's "new ASCII" and "crc" formats and POSIX.1's cpio ("odc") format. The old binary
/// format raises <see cref="ArchiveUnsupportedException"/>. All entries must use the variant of the first one.</para>
/// <para>Names are decoded as UTF-8. The archive ends with the entry named <c>TRAILER!!!</c>; data after it (the zero padding
/// to a block) is ignored. A symbolic link's data is read as its target, which must not exceed
/// <see cref="ArchiveLimits.MaxPathLength"/>.</para>
/// <para>Hard links: newc and crc store a file with a link count above one once, with the last of its names; the earlier
/// names are headers with size zero (see <see cref="CpioEntry.IsLinkStub"/>). On a seekable stream the reader finds the
/// member with the data and gives each stub that content, so extraction produces complete files; each counts toward
/// <see cref="ArchiveLimits.MaxTotalSize"/>. On a forward-only stream a stub reads as empty.</para>
/// <para>Errors: malformed data raises <see cref="CompressionFormatException"/> naming the offset; limits raise <see cref="CompressionLimitException"/>
/// before any memory is allocated for the value.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class CpioReader : ArchiveReader
{
    private readonly ForwardCursor _cursor;
    private readonly bool _leaveOpen;
    private readonly CpioReaderOptions _options;
    private readonly bool _drain;
    private readonly byte[] _header = new byte[CpioHeader.NewcHeaderLength];
    private CpioFormat? _variant;
    private long _next;
    private bool _ended;
    private RangeEntryStream? _entryStream;
    private CpioEntry? _current;
    private Dictionary<(long, long, long), (long Offset, long Size)>? _links;

    /// <summary>Creates a reader over a stream positioned at the start of the archive.</summary>
    /// <param name="stream">A readable stream. Seekable streams allow random access and hard-link resolution.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the reader is disposed.</param>
    public CpioReader(Stream stream, CpioReaderOptions? options = null, bool leaveOpen = false)
        : this(stream, options, leaveOpen, false)
    {
    }

    internal CpioReader(Stream stream, CpioReaderOptions? options, bool leaveOpen, bool drainAtEnd)
        : base(ArchiveFormat.Cpio, (options ?? CpioReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _options = options ?? CpioReaderOptions.Default;
        _leaveOpen = leaveOpen;
        _drain = drainAtEnd;
        _cursor = new ForwardCursor(stream, CpioHeader.FormatName);
    }

    /// <inheritdoc />
    protected override string FormatName => CpioHeader.FormatName;

    /// <inheritdoc />
    public override bool SupportsRandomAccess => _cursor.Seekable;

    /// <summary>Gets the header variant of the archive, known once the first entry has been read.</summary>
    public CpioFormat? Variant => _variant;

    /// <summary>Reads the next entry.</summary>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new CpioEntry? GetNextEntry() => (CpioEntry?)base.GetNextEntry();

    /// <summary>Reads the next entry.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new async ValueTask<CpioEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (CpioEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        CpioEntry entry = _current ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        return _entryStream ??= Create(entry, sequential: true);
    }

    /// <inheritdoc />
    public override Stream OpenEntryStream(ArchiveEntry entry)
    {
        ThrowIfDisposed();
        if (!_cursor.Seekable)
        {
            throw new NotSupportedException("The cpio stream cannot seek, so entries can only be read in order.");
        }

        if (entry is not CpioEntry cpio)
        {
            throw new ArgumentException("The entry was not read by a CpioReader.", nameof(entry));
        }

        if (cpio.DataOffset < 0 || cpio.Size > _cursor.Length - cpio.DataOffset)
        {
            throw new ArgumentException("The entry does not belong to this archive.", nameof(entry));
        }

        return Create(cpio, sequential: false);
    }

    private RangeEntryStream Create(CpioEntry entry, bool sequential)
    {
        long size = entry.Kind == ArchiveEntryKind.File ? entry.Size : 0;
        uint? sum = _options.VerifyChecksum && entry.Format == CpioFormat.Crc && !entry.ContentFromLaterLink && size > 0 ? entry.Checksum : null;
        return new RangeEntryStream(_cursor, CpioHeader.FormatName, entry.DataOffset, size, sequential, sum, entry.HeaderOffset);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed && !_leaveOpen)
        {
            _cursor.Stream.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (!IsDisposed && !_leaveOpen)
        {
            await _cursor.Stream.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken)
    {
        _entryStream?.Invalidate();
        _entryStream = null;
        _current = null;
        if (_ended)
        {
            return null;
        }

        await _cursor.MoveToAsync(async, _next, "the rest of the entry's data and its padding (the archive ended inside it)", cancellationToken).ConfigureAwait(false);
        long at = _cursor.Pos;
        RawHeader h = await ReadHeaderAsync(async, at, cancellationToken).ConfigureAwait(false);
        if (h.IsTrailer)
        {
            _ended = true;
            if (_drain)
            {
                await _cursor.DrainAsync(async, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        CpioFormat variant = _variant!.Value;
        long dataStart = h.DataStart;
        uint type = h.Mode & CpioHeader.TypeMask;
        ArchiveEntryKind kind = type switch
        {
            CpioHeader.TypeFile => ArchiveEntryKind.File,
            CpioHeader.TypeDirectory => ArchiveEntryKind.Directory,
            CpioHeader.TypeSymlink => ArchiveEntryKind.Symlink,
            CpioHeader.TypeChar => ArchiveEntryKind.CharacterDevice,
            CpioHeader.TypeBlock => ArchiveEntryKind.BlockDevice,
            CpioHeader.TypeFifo or CpioHeader.TypeSocket => ArchiveEntryKind.Fifo,
            _ => throw new CompressionFormatException(CpioHeader.FormatName, at, "a file type (regular, directory, symlink, device, FIFO or socket) in the mode"),
        };

        string? link = null;
        long logicalSize = kind == ArchiveEntryKind.File ? h.Size : 0;
        long contentOffset = dataStart;
        bool stub = false;
        bool fromLater = false;
        if (kind == ArchiveEntryKind.Symlink)
        {
            if (h.Size > Limits.MaxPathLength)
            {
                throw new CompressionLimitException(CpioHeader.FormatName, nameof(ArchiveLimits.MaxPathLength), Limits.MaxPathLength, h.Size);
            }

            byte[] target = new byte[(int)h.Size];
            if (await _cursor.ReadFullAsync(async, target, cancellationToken).ConfigureAwait(false) < target.Length)
            {
                throw new CompressionFormatException(CpioHeader.FormatName, dataStart, "the symbolic link target (the archive ended inside it)", true);
            }

            if (target.Contains((byte)0))
            {
                throw new CompressionFormatException(CpioHeader.FormatName, dataStart, "a symbolic link target without NUL bytes");
            }

            link = Encoding.UTF8.GetString(target);
        }
        else if (kind == ArchiveEntryKind.File && variant != CpioFormat.Odc && h.NLink > 1 && h.Size == 0)
        {
            stub = true;
            if (_cursor.Seekable && _options.ResolveLinkStubs)
            {
                _links ??= await BuildLinkIndexAsync(async, cancellationToken).ConfigureAwait(false);
                if (_links.TryGetValue((h.DevMajor, h.DevMinor, h.Ino), out var found))
                {
                    logicalSize = found.Size;
                    contentOffset = found.Offset;
                    fromLater = true;
                }
            }
        }

        _next = dataStart + CpioHeader.PadData(variant, h.Size);
        if (_next < dataStart)
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at, "an entry size that fits the archive");
        }

        bool device = kind is ArchiveEntryKind.CharacterDevice or ArchiveEntryKind.BlockDevice;
        long major = 0, minor = 0;
        if (device)
        {
            if (variant == CpioFormat.Odc)
            {
                // The old format has one 18-bit device number; GNU cpio and bsdtar both write major * 256 + minor.
                major = h.RdevMajor >> 8;
                minor = h.RdevMajor & 0xFF;
            }
            else
            {
                major = h.RdevMajor;
                minor = h.RdevMinor;
            }
        }

        var entry = new CpioEntry
        {
            Name = h.Name,
            Kind = kind,
            Size = logicalSize,
            ModifiedTime = UnixTime(h.MTime),
            Mode = (UnixFileMode)(h.Mode & 0xFFF),
            Uid = h.Uid,
            Gid = h.Gid,
            LinkTarget = link,
            DeviceMajor = major,
            DeviceMinor = minor,
            Format = variant,
            HeaderOffset = at,
            DataOffset = contentOffset,
            StoredSize = h.Size,
            Inode = h.Ino,
            NLink = h.NLink,
            FileSystemDeviceMajor = h.DevMajor,
            FileSystemDeviceMinor = h.DevMinor,
            RawMode = h.Mode,
            Checksum = h.Check,
            IsLinkStub = stub,
            ContentFromLaterLink = fromLater,
        };
        _current = entry;
        return entry;
    }

    // Finds, in one pass over the headers, where the data of every newc/crc file with a link count above one is.
    // The reader's position is restored afterwards. A header that cannot be read ends the pass: the main loop meets
    // the same error in order and reports it.
    private async ValueTask<Dictionary<(long, long, long), (long Offset, long Size)>> BuildLinkIndexAsync(bool async, CancellationToken ct)
    {
        var index = new Dictionary<(long, long, long), (long Offset, long Size)>();
        long saved = _cursor.Pos;
        CpioFormat? savedVariant = _variant;
        try
        {
            long at = _next;
            for (int i = 0; i < Limits.MaxEntryCount; i++)
            {
                await _cursor.MoveToAsync(async, at, "a header", ct).ConfigureAwait(false);
                RawHeader h;
                try
                {
                    h = await ReadHeaderAsync(async, at, ct).ConfigureAwait(false);
                }
                catch (CompressionFormatException)
                {
                    break;
                }

                if (h.IsTrailer)
                {
                    break;
                }

                if ((h.Mode & CpioHeader.TypeMask) == CpioHeader.TypeFile && h.NLink > 1 && h.Size > 0)
                {
                    index.TryAdd((h.DevMajor, h.DevMinor, h.Ino), (h.DataStart, h.Size));
                }

                at = h.DataStart + CpioHeader.PadData(_variant!.Value, h.Size);
                if (at <= h.DataStart && h.Size > 0)
                {
                    break;
                }
            }
        }
        finally
        {
            _variant = savedVariant;
            await _cursor.MoveToAsync(async, saved, "a header", ct).ConfigureAwait(false);
        }

        return index;
    }

    // A time outside DateTimeOffset's range is unknown, not an error: the entry stays readable.
    private static DateTimeOffset? UnixTime(long seconds) =>
        seconds is >= -62135596800L and <= 253402300799L ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private struct RawHeader
    {
        public bool IsTrailer;
        public string Name;
        public long Ino, Uid, Gid, NLink, MTime, Size, DevMajor, DevMinor, RdevMajor, RdevMinor;
        public uint Mode, Check;
        public long DataStart;
    }

    // Reads and validates one header and its name. Leaves the cursor at the first byte after the name's padding.
    private async ValueTask<RawHeader> ReadHeaderAsync(bool async, long at, CancellationToken ct)
    {
        var h = new RawHeader { Name = string.Empty };
        int got = await _cursor.ReadFullAsync(async, _header.AsMemory(0, 6), ct).ConfigureAwait(false);
        if (got == 0 && _variant is not null && !_options.RequireTrailer)
        {
            h.IsTrailer = true;
            return h;
        }

        if (got < 6)
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at, got == 0 ? "a header or the TRAILER!!! entry (the archive ended)" : "a 6-byte magic number", true);
        }

        CpioFormat variant;
        ReadOnlySpan<byte> magic = _header.AsSpan(0, 6);
        if (magic.SequenceEqual("070701"u8))
        {
            variant = CpioFormat.Newc;
        }
        else if (magic.SequenceEqual("070702"u8))
        {
            variant = CpioFormat.Crc;
        }
        else if (magic.SequenceEqual("070707"u8))
        {
            variant = CpioFormat.Odc;
        }
        else if ((magic[0] == 0xC7 && magic[1] == 0x71) || (magic[0] == 0x71 && magic[1] == 0xC7))
        {
            throw new ArchiveUnsupportedException(CpioHeader.FormatName, "the old binary cpio format (only newc, crc and odc are read)");
        }
        else
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at, "the magic number 070701, 070702 or 070707");
        }

        if (_variant is { } first && first != variant)
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at, $"the magic number of the first entry ({CpioHeader.Magic(first)})");
        }

        _variant = variant;
        int headerLength = variant == CpioFormat.Odc ? CpioHeader.OdcHeaderLength : CpioHeader.NewcHeaderLength;
        if (await _cursor.ReadFullAsync(async, _header.AsMemory(6, headerLength - 6), ct).ConfigureAwait(false) < headerLength - 6)
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at, $"a {headerLength}-byte header (the archive ended inside it)", true);
        }

        long nameSize;
        if (variant == CpioFormat.Odc)
        {
            ReadOnlySpan<byte> b = _header;
            h.Ino = CpioHeader.Octal(b.Slice(12, 6), at + 12, "inode");
            h.DevMajor = CpioHeader.Octal(b.Slice(6, 6), at + 6, "device");
            h.Mode = (uint)CpioHeader.Octal(b.Slice(18, 6), at + 18, "mode");
            h.Uid = CpioHeader.Octal(b.Slice(24, 6), at + 24, "uid");
            h.Gid = CpioHeader.Octal(b.Slice(30, 6), at + 30, "gid");
            h.NLink = CpioHeader.Octal(b.Slice(36, 6), at + 36, "link count");
            h.RdevMajor = CpioHeader.Octal(b.Slice(42, 6), at + 42, "rdev");
            h.MTime = CpioHeader.Octal(b.Slice(48, 11), at + 48, "mtime");
            nameSize = CpioHeader.Octal(b.Slice(59, 6), at + 59, "name size");
            h.Size = CpioHeader.Octal(b.Slice(65, 11), at + 65, "file size");
        }
        else
        {
            ReadOnlySpan<byte> b = _header;
            h.Ino = CpioHeader.Hex(b.Slice(6, 8), at + 6, "inode");
            h.Mode = (uint)CpioHeader.Hex(b.Slice(14, 8), at + 14, "mode");
            h.Uid = CpioHeader.Hex(b.Slice(22, 8), at + 22, "uid");
            h.Gid = CpioHeader.Hex(b.Slice(30, 8), at + 30, "gid");
            h.NLink = CpioHeader.Hex(b.Slice(38, 8), at + 38, "link count");
            h.MTime = CpioHeader.Hex(b.Slice(46, 8), at + 46, "mtime");
            h.Size = CpioHeader.Hex(b.Slice(54, 8), at + 54, "file size");
            h.DevMajor = CpioHeader.Hex(b.Slice(62, 8), at + 62, "device major");
            h.DevMinor = CpioHeader.Hex(b.Slice(70, 8), at + 70, "device minor");
            h.RdevMajor = CpioHeader.Hex(b.Slice(78, 8), at + 78, "rdev major");
            h.RdevMinor = CpioHeader.Hex(b.Slice(86, 8), at + 86, "rdev minor");
            nameSize = CpioHeader.Hex(b.Slice(94, 8), at + 94, "name size");
            h.Check = (uint)CpioHeader.Hex(b.Slice(102, 8), at + 102, "checksum");
        }

        if (nameSize < 1)
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at, "a name size of at least 1 (the name's NUL terminator)");
        }

        // The name must fit the path limit (plus its terminator); checked before the buffer is allocated.
        if (nameSize > (long)Limits.MaxPathLength + 1 && nameSize != CpioHeader.TrailerName.Length + 1)
        {
            throw new CompressionLimitException(CpioHeader.FormatName, nameof(ArchiveLimits.MaxPathLength), Limits.MaxPathLength, nameSize - 1);
        }

        if (_cursor.Seekable && nameSize > _cursor.Length - _cursor.Pos)
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at + headerLength, "the entry's name (the archive ended inside it)", true);
        }

        byte[] name = new byte[nameSize];
        if (await _cursor.ReadFullAsync(async, name, ct).ConfigureAwait(false) < name.Length)
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at + headerLength, "the entry's name (the archive ended inside it)", true);
        }

        if (name[^1] != 0 || name.AsSpan(0, name.Length - 1).Contains((byte)0))
        {
            throw new CompressionFormatException(CpioHeader.FormatName, at + headerLength, "a name with its NUL terminator and no other NUL");
        }

        long consumed = headerLength + nameSize;
        long pad = variant == CpioFormat.Odc ? 0 : CpioHeader.Pad4(consumed) - consumed;
        if (pad > 0)
        {
            if (await _cursor.ReadFullAsync(async, new byte[pad], ct).ConfigureAwait(false) < pad)
            {
                throw new CompressionFormatException(CpioHeader.FormatName, at + consumed, "the padding after the name (the archive ended inside it)", true);
            }
        }

        h.DataStart = at + consumed + pad;
        ReadOnlySpan<byte> nameBytes = name.AsSpan(0, name.Length - 1);
        if (nameBytes.SequenceEqual(CpioHeader.TrailerName))
        {
            h.IsTrailer = true;
            return h;
        }

        h.Name = Encoding.UTF8.GetString(nameBytes);
        return h;
    }
}
