using System.Diagnostics.CodeAnalysis;
using System.Text;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Ar;

/// <summary>
/// Reads ar archives: the common (System V) form, the GNU variant with its <c>//</c> long-name table and <c>/</c> symbol
/// table, and the BSD variant with <c>#1/length</c> names. <c>.deb</c> packages are ar archives and read the same way.
/// </summary>
/// <remarks>
/// <para>Format: the ar(5) and deb(5) man pages and public descriptions of the GNU and BSD name conventions. Every member
/// has a 60-byte header (name 16, mtime 12, uid 6, gid 6, mode 8 octal, size 10, end marker <c>`\n</c>) and its data is padded
/// to an even length. A thin archive (<c>!&lt;thin&gt;</c>) raises <see cref="ArchiveUnsupportedException"/>.</para>
/// <para>The archive has no trailer: it ends where the data ends. A final odd-sized member without its pad byte is accepted.
/// Symbol tables and the long-name table are not interpreted; the long-name table is read into memory (up to
/// <see cref="ArReaderOptions.MaxNameTableSize"/>) to resolve names, and they are returned as entries only if
/// <see cref="ArReaderOptions.IncludeSpecialMembers"/> is set.</para>
/// <para>Errors: <see cref="CompressionFormatException"/> names the offset; limits raise <see cref="CompressionLimitException"/> before the
/// memory is allocated.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ArReader : ArchiveReader
{
    internal const string Name = "ar";
    internal const int HeaderLength = 60;
    internal const int MagicLength = 8;

    private readonly ForwardCursor _cursor;
    private readonly bool _leaveOpen;
    private readonly ArReaderOptions _options;
    private readonly bool _drain;
    private readonly byte[] _header = new byte[HeaderLength];
    private byte[]? _table;
    private bool _started;
    private bool _ended;
    private bool _pad;
    private long _next;
    private int _hidden;
    private RangeEntryStream? _entryStream;
    private ArEntry? _current;

    /// <summary>Creates a reader over a stream positioned at the start of the archive.</summary>
    /// <param name="stream">A readable stream. Seekable streams allow random access.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the reader is disposed.</param>
    public ArReader(Stream stream, ArReaderOptions? options = null, bool leaveOpen = false)
        : this(stream, options, leaveOpen, false)
    {
    }

    internal ArReader(Stream stream, ArReaderOptions? options, bool leaveOpen, bool drainAtEnd)
        : base(ArchiveFormat.Ar, (options ?? ArReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _options = options ?? ArReaderOptions.Default;
        _leaveOpen = leaveOpen;
        _drain = drainAtEnd;
        _cursor = new ForwardCursor(stream, Name);
    }

    /// <inheritdoc />
    protected override string FormatName => Name;

    /// <inheritdoc />
    public override bool SupportsRandomAccess => _cursor.Seekable;

    /// <summary>Reads the next entry.</summary>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new ArEntry? GetNextEntry() => (ArEntry?)base.GetNextEntry();

    /// <summary>Reads the next entry.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new async ValueTask<ArEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (ArEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        ArEntry entry = _current ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        if (entry.MemberKind == ArMemberKind.LongNameTable && !_cursor.Seekable)
        {
            // The table was consumed to resolve names; a forward-only source cannot give it again.
            return new MemoryReadStream(_table ?? []);
        }

        return _entryStream ??= new RangeEntryStream(_cursor, Name, entry.DataOffset, entry.Size, sequential: true, null, entry.HeaderOffset);
    }

    /// <inheritdoc />
    public override Stream OpenEntryStream(ArchiveEntry entry)
    {
        ThrowIfDisposed();
        if (!_cursor.Seekable)
        {
            throw new NotSupportedException("The ar stream cannot seek, so entries can only be read in order.");
        }

        if (entry is not ArEntry ar)
        {
            throw new ArgumentException("The entry was not read by an ArReader.", nameof(entry));
        }

        if (ar.DataOffset < 0 || ar.Size > _cursor.Length - ar.DataOffset)
        {
            throw new ArgumentException("The entry does not belong to this archive.", nameof(entry));
        }

        return new RangeEntryStream(_cursor, Name, ar.DataOffset, ar.Size, sequential: false, null, ar.HeaderOffset);
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

    private async ValueTask<ArchiveEntry?> EndAsync(bool async, CancellationToken ct)
    {
        _ended = true;
        if (_drain)
        {
            await _cursor.DrainAsync(async, ct).ConfigureAwait(false);
        }

        return null;
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

        if (!_started)
        {
            await ReadMagicAsync(async, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            await _cursor.MoveToAsync(async, _next, "the rest of the member's data (the archive ended inside it)", cancellationToken).ConfigureAwait(false);
            if (_pad)
            {
                // The pad byte after odd-sized data may be missing at the very end.
                byte[] one = new byte[1];
                if (await _cursor.ReadFullAsync(async, one, cancellationToken).ConfigureAwait(false) == 0)
                {
                    return await EndAsync(async, cancellationToken).ConfigureAwait(false);
                }

                _pad = false;
            }

            long at = _cursor.Pos;
            int got = await _cursor.ReadFullAsync(async, _header, cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                return await EndAsync(async, cancellationToken).ConfigureAwait(false);
            }

            if (got < HeaderLength)
            {
                throw new CompressionFormatException(Name, at, "a 60-byte member header (the archive ended inside it)", true);
            }

            ArEntry? entry = await ParseAsync(async, at, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            _current = entry;
            return entry;
        }
    }

    private async ValueTask ReadMagicAsync(bool async, CancellationToken ct)
    {
        byte[] magic = new byte[MagicLength];
        int got = await _cursor.ReadFullAsync(async, magic, ct).ConfigureAwait(false);
        if (got < MagicLength)
        {
            throw new CompressionFormatException(Name, 0, "the 8-byte signature \"!<arch>\\n\" (the archive ended)", true);
        }

        if (magic.AsSpan().SequenceEqual("!<thin>\n"u8))
        {
            throw new ArchiveUnsupportedException(Name, "thin archives (!<thin>), whose members are files outside the archive");
        }

        if (!magic.AsSpan().SequenceEqual("!<arch>\n"u8))
        {
            throw new CompressionFormatException(Name, 0, "the signature \"!<arch>\\n\"");
        }

        _started = true;
        _next = MagicLength;
    }

    // A time outside DateTimeOffset's range is unknown, not an error: the entry stays readable.
    private static DateTimeOffset? UnixTime(long seconds) =>
        seconds is >= -62135596800L and <= 253402300799L ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static long Decimal(ReadOnlySpan<byte> field, long offset, string what, bool required)
    {
        ReadOnlySpan<byte> f = field.TrimEnd((byte)' ');
        if (f.IsEmpty)
        {
            if (required)
            {
                throw new CompressionFormatException(Name, offset, $"the {what} as decimal digits");
            }

            return 0;
        }

        long v = 0;
        foreach (byte c in f)
        {
            if (c is < (byte)'0' or > (byte)'9')
            {
                throw new CompressionFormatException(Name, offset, $"the {what} as decimal digits");
            }

            v = v * 10 + (c - '0');
        }

        return v;
    }

    private static long Octal(ReadOnlySpan<byte> field, long offset, string what)
    {
        ReadOnlySpan<byte> f = field.TrimEnd((byte)' ');
        long v = 0;
        foreach (byte c in f)
        {
            if (c is < (byte)'0' or > (byte)'7')
            {
                throw new CompressionFormatException(Name, offset, $"the {what} as octal digits");
            }

            v = v * 8 + (c - '0');
        }

        return v;
    }

    private static bool AllDigits(ReadOnlySpan<byte> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (byte c in s)
        {
            if (c is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return true;
    }

    // Parses the header in _header. Returns null for a special member that is left out.
    private async ValueTask<ArEntry?> ParseAsync(bool async, long at, CancellationToken ct)
    {
        if (_header[58] != (byte)'`' || _header[59] != (byte)'\n')
        {
            throw new CompressionFormatException(Name, at + 58, "the header end marker \"`\\n\"");
        }

        ReadOnlySpan<byte> h = _header;
        long mtime = Decimal(h.Slice(16, 12), at + 16, "modification time", false);
        long uid = Decimal(h.Slice(28, 6), at + 28, "uid", false);
        long gid = Decimal(h.Slice(34, 6), at + 34, "gid", false);
        long mode = Octal(h.Slice(40, 8), at + 40, "mode");
        long size = Decimal(h.Slice(48, 10), at + 48, "size", true);
        long dataStart = at + HeaderLength;
        _next = dataStart + size;
        _pad = (size & 1) != 0;
        if (_next < dataStart)
        {
            throw new CompressionFormatException(Name, at + 48, "a size that fits the archive");
        }

        ReadOnlySpan<byte> field = h[..16].TrimEnd((byte)' ');
        string rawName = Encoding.Latin1.GetString(field);
        ArMemberKind kind = ArMemberKind.Regular;
        ArNameStyle style = ArNameStyle.Inline;
        string name;
        long contentSize = size;
        if (field.StartsWith("#1/"u8) && AllDigits(field[3..]) && field.Length <= 3 + 12)
        {
            long n = Decimal(field[3..], at, "BSD name length", true);
            if (n > Limits.MaxPathLength)
            {
                throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxPathLength), Limits.MaxPathLength, n);
            }

            if (n > size)
            {
                throw new CompressionFormatException(Name, at, "a BSD name length that does not exceed the member's size");
            }

            byte[] nameBytes = new byte[n];
            if (await _cursor.ReadFullAsync(async, nameBytes, ct).ConfigureAwait(false) < n)
            {
                throw new CompressionFormatException(Name, dataStart, "the member's name (the archive ended inside it)", true);
            }

            name = Text(nameBytes.AsSpan().TrimEnd((byte)0), dataStart);
            style = ArNameStyle.BsdInline;
            dataStart += n;
            contentSize = size - n;
            if (name is "__.SYMDEF" or "__.SYMDEF SORTED" or "__.SYMDEF_64" or "__.SYMDEF_64 SORTED")
            {
                kind = ArMemberKind.SymbolTable;
            }
        }
        else if (field.SequenceEqual("//"u8))
        {
            if (size > _options.MaxNameTableSize)
            {
                throw new CompressionLimitException(Name, nameof(ArReaderOptions.MaxNameTableSize), _options.MaxNameTableSize, size);
            }

            byte[] table = new byte[size];
            if (await _cursor.ReadFullAsync(async, table, ct).ConfigureAwait(false) < size)
            {
                throw new CompressionFormatException(Name, dataStart, "the long-name table (the archive ended inside it)", true);
            }

            _table = table;
            kind = ArMemberKind.LongNameTable;
            name = "//";
        }
        else if (field.SequenceEqual("/"u8) || field.SequenceEqual("/SYM64/"u8))
        {
            kind = ArMemberKind.SymbolTable;
            name = rawName;
        }
        else if (field.Length > 1 && field[0] == (byte)'/' && AllDigits(field[1..]))
        {
            if (field.Length > 13)
            {
                throw new CompressionFormatException(Name, at, "a long-name table offset of at most 12 digits");
            }

            long offset = Decimal(field[1..], at, "long-name table offset", true);
            if (_table is null)
            {
                throw new CompressionFormatException(Name, at, "the long-name table (//) before a member that refers to it");
            }

            if (offset >= _table.Length)
            {
                throw new CompressionFormatException(Name, at, "a long-name table offset inside the table");
            }

            ReadOnlySpan<byte> rest = _table.AsSpan((int)offset);
            int end = rest.IndexOf((byte)'\n');
            ReadOnlySpan<byte> entryName = end < 0 ? rest : rest[..end];
            if (entryName.EndsWith((byte)'/'))
            {
                entryName = entryName[..^1];
            }

            if (entryName.Length > Limits.MaxPathLength)
            {
                throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxPathLength), Limits.MaxPathLength, entryName.Length);
            }

            name = Text(entryName, at);
            style = ArNameStyle.GnuTable;
        }
        else
        {
            ReadOnlySpan<byte> inline = field.EndsWith((byte)'/') ? field[..^1] : field;
            name = Text(inline, at);
        }

        if (kind != ArMemberKind.Regular && !_options.IncludeSpecialMembers)
        {
            if (++_hidden > Limits.MaxEntryCount)
            {
                throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxEntryCount), Limits.MaxEntryCount, _hidden);
            }

            return null;
        }

        return new ArEntry
        {
            Name = name,
            Kind = ArchiveEntryKind.File,
            Size = contentSize,
            ModifiedTime = UnixTime(mtime),
            Mode = (UnixFileMode)(mode & 0xFFF),
            Uid = uid,
            Gid = gid,
            MemberKind = kind,
            NameStyle = style,
            HeaderOffset = at,
            DataOffset = dataStart,
            RawName = rawName,
            RawMode = (uint)mode,
        };
    }

    private static string Text(ReadOnlySpan<byte> bytes, long offset)
    {
        if (bytes.IsEmpty)
        {
            throw new CompressionFormatException(Name, offset, "a non-empty member name");
        }

        if (bytes.Contains((byte)0))
        {
            throw new CompressionFormatException(Name, offset, "a member name without NUL bytes");
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
