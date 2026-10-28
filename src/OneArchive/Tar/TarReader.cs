using RecursiveAI.OneArchive.Internal;
using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tar;

/// <summary>
/// Reads tar archives: V7, ustar, pax (extended and global headers) and GNU (long names and links, sparse files in the
/// old GNU, 0.0, 0.1 and 1.0 forms). Every header field is exposed through <see cref="TarEntry"/>.
/// </summary>
/// <remarks>
/// <para>Offsets in errors and entries count bytes of the tar stream from where the reader started (for a compressed
/// archive, of the decompressed stream). The reader never allocates a size the archive chose without checking it
/// against <see cref="TarReaderOptions"/> and <see cref="ArchiveLimits"/> first.</para>
/// <para>When the stream can seek, entries can also be opened out of order with <see cref="OpenEntryStream(ArchiveEntry)"/>.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class TarReader : ArchiveReader
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _seekable;
    private readonly long _origin;
    private readonly long _length;
    private readonly TarReaderOptions _options;
    private readonly ExpansionMeter? _meter;
    private readonly byte[] _block = new byte[TarHeader.BlockSize];
    private readonly List<KeyValuePair<string, string>> _global = [];
    private byte[]? _scratch;
    private long _pos;
    private long _nextHeader;
    private bool _ended;
    private TarEntryStream? _entryStream;
    private TarEntry? _current;

    /// <summary>Creates a reader over <paramref name="stream"/>, starting at its current position.</summary>
    /// <param name="stream">A readable stream of tar data.</param>
    /// <param name="options">Reading options; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when the reader is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public TarReader(Stream stream, TarReaderOptions? options = null, bool leaveOpen = false)
        : this(stream, options, leaveOpen, null)
    {
    }

    internal TarReader(Stream stream, TarReaderOptions? options, bool leaveOpen, ExpansionMeter? meter)
        : base(ArchiveFormat.Tar, (options ?? TarReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _options = options ?? TarReaderOptions.Default;
        _meter = meter;
        _seekable = stream.CanSeek;
        if (_seekable)
        {
            _origin = stream.Position;
            _length = stream.Length - _origin;
        }
    }

    /// <inheritdoc />
    protected override string FormatName => TarHeader.FormatName;

    /// <inheritdoc />
    public override bool SupportsRandomAccess => _seekable;

    /// <summary>Gets the global pax records seen so far, in order. Each applies to the entries after the header that carried it.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GlobalPaxRecords => _global;

    /// <summary>Advances to the next entry, skipping the rest of the current one.</summary>
    /// <returns>The entry, or <see langword="null"/> at the end of the archive.</returns>
    /// <exception cref="CompressionFormatException">The archive is malformed or truncated.</exception>
    /// <exception cref="CompressionLimitException">A limit was exceeded.</exception>
    /// <exception cref="ArchiveUnsupportedException">The archive is a multi-volume tar.</exception>
    public new TarEntry? GetNextEntry() => (TarEntry?)base.GetNextEntry();

    /// <summary>Advances to the next entry, skipping the rest of the current one.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> at the end of the archive.</returns>
    public new async ValueTask<TarEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (TarEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        TarEntry entry = _current ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        return _entryStream ??= Create(entry, sequential: true);
    }

    /// <inheritdoc />
    public override Stream OpenEntryStream(ArchiveEntry entry)
    {
        ThrowIfDisposed();
        if (!_seekable)
        {
            throw new NotSupportedException("The tar stream cannot seek, so entries can only be read in order.");
        }

        if (entry is not TarEntry tar)
        {
            throw new ArgumentException("The entry was not read by a TarReader.", nameof(entry));
        }

        if (tar.DataOffset < 0 || tar.StoredSize > _length - tar.DataOffset)
        {
            throw new ArgumentException("The entry does not belong to this archive.", nameof(entry));
        }

        return Create(tar, sequential: false);
    }

    private TarEntryStream Create(TarEntry entry, bool sequential)
    {
        bool hasData = entry.Kind == ArchiveEntryKind.File;
        bool sparse = entry.SparseFormat != TarSparseFormat.None;
        long size = hasData ? entry.Size : 0;
        return new TarEntryStream(
            _stream,
            _seekable,
            _origin + entry.DataOffset,
            _origin,
            size,
            hasData && sparse ? entry.SparseRegions : null,
            sequential && !_seekable ? this : null,
            _meter);
    }

    internal void NoteConsumed(int n) => _pos += n;

    // After the last entry of a compressed archive: read the stream to its end (the padding after the end marker), so
    // that the decompressor's own checks run and a truncated stream is reported.
    internal async ValueTask CompleteAsync(bool async, CancellationToken ct)
    {
        if (_meter is null || !_ended)
        {
            return;
        }

        _scratch ??= new byte[8192];
        while (true)
        {
            int n = async
                ? await _stream.ReadAsync(_scratch, ct).ConfigureAwait(false)
                : _stream.Read(_scratch, 0, _scratch.Length);
            if (n == 0)
            {
                return;
            }

            _pos += n;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed && !_leaveOpen)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (!IsDisposed && !_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    // ---- low-level reading ----

    private async ValueTask<int> ReadFullAsync(bool async, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = async
                ? await _stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false)
                : _stream.Read(buffer.Span[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
            _pos += n;
        }

        return total;
    }

    // Moves to an absolute position of the tar stream (never backwards unless the source can seek).
    private async ValueTask MoveToAsync(bool async, long target, CancellationToken ct)
    {
        if (_seekable)
        {
            if (target > _length)
            {
                throw TarHeader.Bad(_length, "the rest of the entry's data and its padding (the archive ended inside it)", truncated: true);
            }

            _stream.Seek(_origin + target, SeekOrigin.Begin);
            _pos = target;
            return;
        }

        if (target < _pos)
        {
            throw new InvalidOperationException("A forward-only tar stream cannot move backwards.");
        }

        _scratch ??= new byte[8192];
        while (_pos < target)
        {
            int want = (int)Math.Min(_scratch.Length, target - _pos);
            int n = await ReadFullAsync(async, _scratch.AsMemory(0, want), ct).ConfigureAwait(false);
            if (n < want)
            {
                throw TarHeader.Bad(_pos, "the rest of the entry's data and its padding (the archive ended inside it)", truncated: true);
            }
        }
    }

    private static long Padded(long size, long at)
    {
        if (size > long.MaxValue - at - (TarHeader.BlockSize - 1))
        {
            throw TarHeader.Bad(at, "an entry size that fits the archive");
        }

        return (size + TarHeader.BlockSize - 1) / TarHeader.BlockSize * TarHeader.BlockSize;
    }

    // ---- the entry state machine ----

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

        await MoveToAsync(async, _nextHeader, cancellationToken).ConfigureAwait(false);

        long firstHeader = -1;
        List<KeyValuePair<string, string>>? local = null;
        byte[]? longName = null;
        byte[]? longLink = null;
        while (true)
        {
            long at = _pos;
            int got = await ReadFullAsync(async, _block, cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                if (firstHeader >= 0)
                {
                    throw TarHeader.Bad(at, "the header of the entry that the extended header applies to", truncated: true);
                }

                if (_options.RequireEndOfArchive && !_options.IgnoreZeroBlocks)
                {
                    throw TarHeader.Bad(at, "a header block or the end-of-archive zero block", truncated: true);
                }

                _ended = true;
                return null;
            }

            if (got < TarHeader.BlockSize)
            {
                throw TarHeader.Bad(at, "a 512-byte header block", truncated: true);
            }

            if (TarHeader.IsZero(_block))
            {
                if (_options.IgnoreZeroBlocks)
                {
                    continue;
                }

                if (firstHeader >= 0)
                {
                    throw TarHeader.Bad(at, "the header of the entry that the extended header applies to");
                }

                _ended = true;
                return null;
            }

            VerifyChecksum(at);
            if (firstHeader < 0)
            {
                firstHeader = at;
            }

            byte type = _block[TarHeader.TypeAt];
            if (type == 0)
            {
                type = (byte)'0';
            }

            switch (type)
            {
                case (byte)'x':
                case (byte)'X':
                {
                    byte[] data = await ReadExtendedAsync(async, at, cancellationToken).ConfigureAwait(false);
                    (local ??= []).AddRange(TarHeader.ParsePax(data, at + TarHeader.BlockSize));
                    continue;
                }

                case (byte)'g':
                {
                    byte[] data = await ReadExtendedAsync(async, at, cancellationToken).ConfigureAwait(false);
                    _global.AddRange(TarHeader.ParsePax(data, at + TarHeader.BlockSize));
                    continue;
                }

                case (byte)'L':
                    longName = await ReadExtendedAsync(async, at, cancellationToken).ConfigureAwait(false);
                    continue;
                case (byte)'K':
                    longLink = await ReadExtendedAsync(async, at, cancellationToken).ConfigureAwait(false);
                    continue;
                case (byte)'V':
                {
                    // A volume label: no content that matters.
                    long size = TarHeader.ParseNumber(_block.AsSpan(TarHeader.SizeAt, TarHeader.SizeLen), at + TarHeader.SizeAt, "size");
                    await MoveToAsync(async, _pos + Padded(Math.Max(size, 0), at), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                case (byte)'M':
                    throw new ArchiveUnsupportedException(TarHeader.FormatName, "multi-volume archives (type M)");
                default:
                    break;
            }

            TarEntry entry = await BuildEntryAsync(async, at, firstHeader, type, local, longName, longLink, cancellationToken).ConfigureAwait(false);
            _current = entry;
            return entry;
        }
    }

    private void VerifyChecksum(long at)
    {
        long stored;
        try
        {
            stored = TarHeader.ParseNumber(_block.AsSpan(TarHeader.ChecksumAt, TarHeader.ChecksumLen), at + TarHeader.ChecksumAt, "header checksum");
        }
        catch (CompressionFormatException)
        {
            stored = -1;
        }

        if (stored != TarHeader.ComputeChecksum(_block, false) && stored != (int)TarHeader.ComputeChecksum(_block, true))
        {
            throw TarHeader.Bad(at, "a valid header checksum (this is not a tar header, or it is corrupt)");
        }
    }

    // Reads the data of an extended-header or long-name entry (its size is in the header just read) and its padding.
    private async ValueTask<byte[]> ReadExtendedAsync(bool async, long headerAt, CancellationToken ct)
    {
        long size = TarHeader.ParseNumber(_block.AsSpan(TarHeader.SizeAt, TarHeader.SizeLen), headerAt + TarHeader.SizeAt, "size");
        if (size < 0)
        {
            throw TarHeader.Bad(headerAt + TarHeader.SizeAt, "a non-negative size");
        }

        if (size > _options.MaxExtendedHeaderSize)
        {
            throw new CompressionLimitException(TarHeader.FormatName, nameof(TarReaderOptions.MaxExtendedHeaderSize), _options.MaxExtendedHeaderSize, size);
        }

        byte[] data = new byte[(int)size];
        int got = await ReadFullAsync(async, data, ct).ConfigureAwait(false);
        if (got < data.Length)
        {
            throw TarHeader.Bad(_pos, "the extended header's data (the archive ended inside it)", truncated: true);
        }

        long pad = Padded(size, _pos) - size;
        if (pad > 0)
        {
            await MoveToAsync(async, _pos + pad, ct).ConfigureAwait(false);
        }

        return data;
    }

    private static Dictionary<string, string> Merge(List<KeyValuePair<string, string>> global, List<KeyValuePair<string, string>>? local)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in global)
        {
            Apply(merged, r);
        }

        if (local is not null)
        {
            foreach (var r in local)
            {
                Apply(merged, r);
            }
        }

        return merged;

        static void Apply(Dictionary<string, string> d, KeyValuePair<string, string> r)
        {
            // POSIX: a record with an empty value removes the keyword.
            if (r.Value.Length == 0)
            {
                d.Remove(r.Key);
            }
            else
            {
                d[r.Key] = r.Value;
            }
        }
    }

    private static long Decimal(string text, long offset, string key)
    {
        long v = 0;
        if (text.Length == 0 || text.Length > 19)
        {
            throw TarHeader.Bad(offset, $"pax {key} as a decimal number");
        }

        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                throw TarHeader.Bad(offset, $"pax {key} as a decimal number");
            }

            v = checked((v * 10) + (c - '0'));
        }

        return v;
    }

    private static readonly HashSet<string> s_modelled = new(StringComparer.Ordinal)
    {
        "path", "linkpath", "size", "uid", "gid", "uname", "gname", "mtime", "atime", "ctime", "charset", "hdrcharset",
    };

    // The fixed fields of a header block, parsed before anything else is read into the block buffer.
    private sealed class Fixed
    {
        public TarFormat Format;
        public long HeaderSize, Mode, Uid, Gid, DevMajor, DevMinor, RealSize = -1;
        public uint Checksum;
        public string StoredName = "", StoredLink = "", Name = "", Link = "", Magic = "", Version = "";
        public string? UserName, GroupName;
        public DateTimeOffset? Modified, Accessed, Changed;
        public TarSparseFormat Sparse;
        public List<TarSparseRegion>? Regions;
        public bool MoreSparseBlocks, SparseDone;
    }

    private Fixed ParseFixed(long headerAt, byte type, byte[]? longName, byte[]? longLink)
    {
        ReadOnlySpan<byte> h = _block;
        var f = new Fixed();
        ReadOnlySpan<byte> magic = h.Slice(TarHeader.MagicAt, TarHeader.MagicLen);
        ReadOnlySpan<byte> version = h.Slice(TarHeader.VersionAt, TarHeader.VersionLen);
        if (magic[..5].SequenceEqual("ustar"u8))
        {
            f.Format = magic[5] == 0 && version.SequenceEqual("00"u8) ? TarFormat.Ustar : (magic[5] == ' ' ? TarFormat.Gnu : TarFormat.Ustar);
        }
        else
        {
            f.Format = TarFormat.V7;
        }

        f.HeaderSize = TarHeader.ParseNumber(h.Slice(TarHeader.SizeAt, TarHeader.SizeLen), headerAt + TarHeader.SizeAt, "size");
        if (f.HeaderSize < 0)
        {
            throw TarHeader.Bad(headerAt + TarHeader.SizeAt, "a non-negative size");
        }

        f.Mode = TarHeader.ParseNumber(h.Slice(TarHeader.ModeAt, TarHeader.ModeLen), headerAt + TarHeader.ModeAt, "mode");
        f.Uid = TarHeader.ParseNumber(h.Slice(TarHeader.UidAt, TarHeader.UidLen), headerAt + TarHeader.UidAt, "uid");
        f.Gid = TarHeader.ParseNumber(h.Slice(TarHeader.GidAt, TarHeader.GidLen), headerAt + TarHeader.GidAt, "gid");
        long mtime = TarHeader.ParseNumber(h.Slice(TarHeader.MtimeAt, TarHeader.MtimeLen), headerAt + TarHeader.MtimeAt, "modification time");
        if (f.Mode < 0 || f.Uid < 0 || f.Gid < 0)
        {
            throw TarHeader.Bad(headerAt + TarHeader.ModeAt, "a non-negative mode, uid and gid");
        }

        f.Checksum = (uint)TarHeader.ParseNumber(h.Slice(TarHeader.ChecksumAt, TarHeader.ChecksumLen), headerAt + TarHeader.ChecksumAt, "header checksum");

        // The ustar prefix is only a prefix in ustar headers; GNU headers use those bytes for other fields.
        f.StoredName = Internal.PathRules.DecodeName(TarHeader.CString(h.Slice(TarHeader.NameAt, TarHeader.NameLen)));
        if (f.Format == TarFormat.Ustar)
        {
            ReadOnlySpan<byte> prefix = TarHeader.CString(h.Slice(TarHeader.PrefixAt, TarHeader.PrefixLen));
            if (prefix.Length > 0)
            {
                f.StoredName = Internal.PathRules.DecodeName(prefix) + "/" + f.StoredName;
            }
        }

        f.StoredLink = Internal.PathRules.DecodeName(TarHeader.CString(h.Slice(TarHeader.LinkAt, TarHeader.LinkLen)));
        f.Name = longName is null ? f.StoredName : Internal.PathRules.DecodeName(TarHeader.CString(longName));
        f.Link = longLink is null ? f.StoredLink : Internal.PathRules.DecodeName(TarHeader.CString(longLink));
        f.UserName = TarHeader.CString(h.Slice(TarHeader.UnameAt, TarHeader.UnameLen)) is { Length: > 0 } un ? Internal.PathRules.DecodeName(un) : null;
        f.GroupName = TarHeader.CString(h.Slice(TarHeader.GnameAt, TarHeader.GnameLen)) is { Length: > 0 } gn ? Internal.PathRules.DecodeName(gn) : null;
        f.Modified = TarHeader.FromUnixSeconds(mtime, headerAt + TarHeader.MtimeAt, "modification time");
        if (f.Format == TarFormat.Gnu)
        {
            if (h[TarHeader.GnuAtimeAt] != 0)
            {
                long t = TarHeader.ParseNumber(h.Slice(TarHeader.GnuAtimeAt, TarHeader.GnuTimeLen), headerAt + TarHeader.GnuAtimeAt, "access time");
                f.Accessed = TarHeader.FromUnixSeconds(t, headerAt + TarHeader.GnuAtimeAt, "access time");
            }

            if (h[TarHeader.GnuCtimeAt] != 0)
            {
                long t = TarHeader.ParseNumber(h.Slice(TarHeader.GnuCtimeAt, TarHeader.GnuTimeLen), headerAt + TarHeader.GnuCtimeAt, "change time");
                f.Changed = TarHeader.FromUnixSeconds(t, headerAt + TarHeader.GnuCtimeAt, "change time");
            }
        }

        if (type == (byte)'3' || type == (byte)'4')
        {
            f.DevMajor = TarHeader.ParseNumber(h.Slice(TarHeader.DevMajorAt, TarHeader.DevMajorLen), headerAt + TarHeader.DevMajorAt, "device major number");
            f.DevMinor = TarHeader.ParseNumber(h.Slice(TarHeader.DevMinorAt, TarHeader.DevMinorLen), headerAt + TarHeader.DevMinorAt, "device minor number");
        }

        f.Magic = Internal.PathRules.DecodeName(TarHeader.CString(magic));
        f.Version = Internal.PathRules.DecodeName(version);

        // Old GNU sparse map: the first four regions are in the header.
        if (type == (byte)'S')
        {
            f.Sparse = TarSparseFormat.OldGnu;
            f.RealSize = TarHeader.ParseNumber(h.Slice(TarHeader.GnuRealSizeAt, TarHeader.GnuRealSizeLen), headerAt + TarHeader.GnuRealSizeAt, "real size");
            f.Regions = [];
            f.MoreSparseBlocks = h[TarHeader.GnuIsExtendedAt] != 0;
            f.SparseDone = AddOldSparse(f.Regions, h.Slice(TarHeader.GnuSparseAt, TarHeader.GnuSparseCount * TarHeader.GnuSparseEntryLen), TarHeader.GnuSparseCount, headerAt + TarHeader.GnuSparseAt);
        }

        return f;
    }

    private async ValueTask<TarEntry> BuildEntryAsync(
        bool async, long headerAt, long firstHeader, byte type,
        List<KeyValuePair<string, string>>? local, byte[]? longName, byte[]? longLink, CancellationToken ct)
    {
        Fixed f = ParseFixed(headerAt, type, longName, longLink);
        TarFormat format = f.Format;
        long headerSize = f.HeaderSize, uid = f.Uid, gid = f.Gid, modeValue = f.Mode, realSize = f.RealSize, devMajor = f.DevMajor, devMinor = f.DevMinor;
        string name = f.Name, link = f.Link, storedName = f.StoredName, storedLink = f.StoredLink;
        string? uname = f.UserName, gname = f.GroupName;
        DateTimeOffset? modified = f.Modified, accessed = f.Accessed, changed = f.Changed;
        uint checksum = f.Checksum;
        string magicText = f.Magic, versionText = f.Version;
        TarSparseFormat sparse = f.Sparse;
        List<TarSparseRegion>? regions = f.Regions;
        {
            bool more = f.MoreSparseBlocks;
            bool done = f.SparseDone;
            while (more)
            {
                long extAt = _pos;
                int got = await ReadFullAsync(async, _block, ct).ConfigureAwait(false);
                if (got < TarHeader.BlockSize)
                {
                    throw TarHeader.Bad(extAt, "a GNU sparse extension block", truncated: true);
                }

                more = _block[TarHeader.GnuExtIsExtendedAt] != 0;
                if (!done)
                {
                    done = AddOldSparse(regions!, _block.AsSpan(0, TarHeader.GnuExtSparseCount * TarHeader.GnuSparseEntryLen), TarHeader.GnuExtSparseCount, extAt);
                }
            }
        }

        // Pax records: global ones first, then this entry's own.
        Dictionary<string, string>? pax = null;
        if (_global.Count > 0 || local is not null)
        {
            pax = Merge(_global, local);
            if (format != TarFormat.Gnu)
            {
                format = TarFormat.Pax;
            }
        }

        long paxAt = firstHeader;
        if (pax is not null)
        {
            if (pax.TryGetValue("path", out string? p))
            {
                name = p;
            }

            if (pax.TryGetValue("linkpath", out string? lp))
            {
                link = lp;
            }

            if (pax.TryGetValue("size", out string? sz))
            {
                headerSize = Decimal(sz, paxAt, "size");
            }

            if (pax.TryGetValue("uid", out string? u))
            {
                uid = Decimal(u, paxAt, "uid");
            }

            if (pax.TryGetValue("gid", out string? g))
            {
                gid = Decimal(g, paxAt, "gid");
            }

            if (pax.TryGetValue("uname", out string? un2))
            {
                uname = un2;
            }

            if (pax.TryGetValue("gname", out string? gn2))
            {
                gname = gn2;
            }

            if (pax.TryGetValue("mtime", out string? mt))
            {
                modified = TarHeader.ParsePaxTime(mt, paxAt, "mtime");
            }

            if (pax.TryGetValue("atime", out string? at2))
            {
                accessed = TarHeader.ParsePaxTime(at2, paxAt, "atime");
            }

            if (pax.TryGetValue("ctime", out string? ct2))
            {
                changed = TarHeader.ParsePaxTime(ct2, paxAt, "ctime");
            }
        }

        // The kind, and whether data follows the header. POSIX: unknown type flags are regular files.
        ArchiveEntryKind kind = type switch
        {
            (byte)'1' => ArchiveEntryKind.HardLink,
            (byte)'2' => ArchiveEntryKind.Symlink,
            (byte)'3' => ArchiveEntryKind.CharacterDevice,
            (byte)'4' => ArchiveEntryKind.BlockDevice,
            (byte)'5' or (byte)'D' => ArchiveEntryKind.Directory,
            (byte)'6' => ArchiveEntryKind.Fifo,
            _ => ArchiveEntryKind.File,
        };
        bool hasData = kind == ArchiveEntryKind.File || type == (byte)'D';
        long stored = hasData ? headerSize : 0;

        // GNU sparse in pax form (GNU tar manual, "Sparse Formats").
        if (sparse == TarSparseFormat.None && pax is not null && hasData && kind == ArchiveEntryKind.File)
        {
            if (pax.TryGetValue("GNU.sparse.major", out string? major) && pax.TryGetValue("GNU.sparse.minor", out string? minor) && major == "1" && minor == "0")
            {
                sparse = TarSparseFormat.Pax10;
            }
            else if (pax.ContainsKey("GNU.sparse.map"))
            {
                sparse = TarSparseFormat.Pax01;
            }
            else if (local is not null && local.Exists(static r => r.Key == "GNU.sparse.offset"))
            {
                sparse = TarSparseFormat.Pax00;
            }

            if (sparse != TarSparseFormat.None)
            {
                string? real = pax.GetValueOrDefault("GNU.sparse.realsize") ?? pax.GetValueOrDefault("GNU.sparse.size");
                if (real is null)
                {
                    throw TarHeader.Bad(paxAt, "a GNU.sparse.realsize record on a sparse file");
                }

                realSize = Decimal(real, paxAt, "GNU.sparse.realsize");
                if (pax.TryGetValue("GNU.sparse.name", out string? sn))
                {
                    name = sn;
                }

                regions = [];
                if (sparse == TarSparseFormat.Pax01)
                {
                    ParseSparseMap(regions, pax["GNU.sparse.map"], paxAt);
                }
                else if (sparse == TarSparseFormat.Pax00)
                {
                    long? offset = null;
                    foreach (var r in local!)
                    {
                        if (r.Key == "GNU.sparse.offset")
                        {
                            offset = Decimal(r.Value, paxAt, "GNU.sparse.offset");
                        }
                        else if (r.Key == "GNU.sparse.numbytes" && offset is { } o)
                        {
                            regions.Add(new TarSparseRegion(o, Decimal(r.Value, paxAt, "GNU.sparse.numbytes")));
                            offset = null;
                            if (regions.Count > _options.MaxSparseRegions)
                            {
                                throw new CompressionLimitException(TarHeader.FormatName, nameof(TarReaderOptions.MaxSparseRegions), _options.MaxSparseRegions, regions.Count);
                            }
                        }
                    }
                }
                else
                {
                    // 1.0: the map is the first blocks of the data. The header's size includes them.
                    stored = await ReadSparse10MapAsync(async, regions, stored, ct).ConfigureAwait(false);
                }
            }
        }

        long entrySize = hasData && kind == ArchiveEntryKind.File ? stored : 0;
        if (sparse != TarSparseFormat.None)
        {
            ValidateRegions(regions!, stored, realSize, headerAt);
            entrySize = realSize;
        }

        long dataOffset = _pos;
        long padded = Padded(stored, dataOffset);
        _nextHeader = dataOffset + padded;

        // A name with a trailing slash is a directory, whatever the type flag says (POSIX; GNU tar does the same).
        string trimmed = name.TrimEnd('/');
        if (trimmed.Length == 0 && name.Length > 0)
        {
            trimmed = "/";
        }

        if (trimmed.Length == 0)
        {
            throw TarHeader.Bad(headerAt + TarHeader.NameAt, "a non-empty entry name");
        }

        if (kind == ArchiveEntryKind.File && trimmed.Length != name.Length && sparse == TarSparseFormat.None)
        {
            kind = ArchiveEntryKind.Directory;
            entrySize = 0;
        }

        Dictionary<string, string>? extras = null;
        if (pax is not null)
        {
            foreach (var r in pax)
            {
                if (!s_modelled.Contains(r.Key) && !r.Key.StartsWith("GNU.sparse.", StringComparison.Ordinal))
                {
                    (extras ??= new Dictionary<string, string>(StringComparer.Ordinal))[r.Key] = r.Value;
                }
            }
        }

        return new TarEntry
        {
            Name = trimmed,
            Kind = kind,
            Size = entrySize,
            ModifiedTime = modified,
            AccessTime = accessed,
            ChangeTime = changed,
            Mode = (UnixFileMode)(int)(modeValue & 0xFFF),
            Uid = uid,
            Gid = gid,
            UserName = uname,
            GroupName = gname,
            LinkTarget = kind is ArchiveEntryKind.Symlink or ArchiveEntryKind.HardLink ? link : null,
            DeviceMajor = devMajor,
            DeviceMinor = devMinor,
            Extras = extras is null ? new Dictionary<string, string>() : extras,
            Format = format,
            TypeFlag = type,
            HeaderOffset = firstHeader,
            DataOffset = dataOffset,
            StoredSize = stored,
            HeaderSize = headerSize,
            Checksum = checksum,
            StoredName = storedName,
            StoredLinkName = storedLink,
            Magic = magicText,
            Version = versionText,
            RawMode = (uint)Math.Min(modeValue, uint.MaxValue),
            SparseFormat = sparse,
            SparseRegions = regions is null ? [] : regions,
            PaxRecords = pax is null ? new Dictionary<string, string>() : pax,
        };
    }

    // Adds the entries of a GNU sparse map; returns true when an unused (all-zero) slot ended the list.
    private bool AddOldSparse(List<TarSparseRegion> regions, ReadOnlySpan<byte> map, int count, long offset)
    {
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = map.Slice(i * TarHeader.GnuSparseEntryLen, TarHeader.GnuSparseEntryLen);
            long at = offset + (i * TarHeader.GnuSparseEntryLen);
            if (!e.ContainsAnyExcept((byte)0))
            {
                return true;
            }

            long o = TarHeader.ParseNumber(e[..12], at, "sparse region offset");
            long n = TarHeader.ParseNumber(e.Slice(12, 12), at + 12, "sparse region size");
            if (o < 0 || n < 0)
            {
                throw TarHeader.Bad(at, "a non-negative sparse region");
            }

            regions.Add(new TarSparseRegion(o, n));
            if (regions.Count > _options.MaxSparseRegions)
            {
                throw new CompressionLimitException(TarHeader.FormatName, nameof(TarReaderOptions.MaxSparseRegions), _options.MaxSparseRegions, regions.Count);
            }
        }

        return false;
    }

    // GNU.sparse.map: "offset,length,offset,length,..."
    private void ParseSparseMap(List<TarSparseRegion> regions, string map, long at)
    {
        string[] parts = map.Split(',');
        if (parts.Length % 2 != 0)
        {
            throw TarHeader.Bad(at, "an even number of values in GNU.sparse.map");
        }

        if (parts.Length / 2 > _options.MaxSparseRegions)
        {
            throw new CompressionLimitException(TarHeader.FormatName, nameof(TarReaderOptions.MaxSparseRegions), _options.MaxSparseRegions, parts.Length / 2);
        }

        for (int i = 0; i < parts.Length; i += 2)
        {
            regions.Add(new TarSparseRegion(Decimal(parts[i], at, "GNU.sparse.map"), Decimal(parts[i + 1], at, "GNU.sparse.map")));
        }
    }

    // Sparse 1.0: "numblocks\n" then offset and length on lines of their own, padded to a block. Returns the stored
    // data size left after the map.
    private async ValueTask<long> ReadSparse10MapAsync(bool async, List<TarSparseRegion> regions, long headerStored, CancellationToken ct)
    {
        long mapAt = _pos;
        long limit = Padded(headerStored, mapAt);
        var text = new List<byte>();
        long numbers = -1;      // total numbers expected, once the first line is known
        int newlines = 0;
        long consumed = 0;
        byte[] block = new byte[TarHeader.BlockSize];
        while (numbers < 0 || newlines < 1 + numbers)
        {
            if (consumed + TarHeader.BlockSize > limit)
            {
                throw TarHeader.Bad(mapAt + consumed, "a complete GNU sparse 1.0 map within the entry's data");
            }

            int got = await ReadFullAsync(async, block, ct).ConfigureAwait(false);
            if (got < block.Length)
            {
                throw TarHeader.Bad(mapAt + consumed, "the GNU sparse map (the archive ended inside it)", truncated: true);
            }

            consumed += block.Length;
            foreach (byte b in block)
            {
                text.Add(b);
                if (b == '\n')
                {
                    newlines++;
                }
            }

            if (numbers < 0 && newlines >= 1)
            {
                int nl = text.IndexOf((byte)'\n');
                long count = Decimal(System.Text.Encoding.ASCII.GetString([.. text.GetRange(0, nl)]), mapAt, "sparse region count");
                if (count > _options.MaxSparseRegions)
                {
                    throw new CompressionLimitException(TarHeader.FormatName, nameof(TarReaderOptions.MaxSparseRegions), _options.MaxSparseRegions, count);
                }

                numbers = count * 2;
            }
        }

        string[] lines = System.Text.Encoding.ASCII.GetString([.. text]).Split('\n');
        for (long i = 0; i < numbers; i += 2)
        {
            regions.Add(new TarSparseRegion(Decimal(lines[1 + i], mapAt, "sparse region offset"), Decimal(lines[2 + i], mapAt, "sparse region size")));
        }

        return headerStored - consumed;
    }

    private static void ValidateRegions(List<TarSparseRegion> regions, long stored, long realSize, long at)
    {
        if (realSize < 0 || stored < 0)
        {
            throw TarHeader.Bad(at, "a non-negative sparse file size");
        }

        long end = 0;
        long sum = 0;
        foreach (var r in regions)
        {
            if (r.Offset < end || r.Length < 0 || r.Offset > realSize || r.Length > realSize - r.Offset)
            {
                throw TarHeader.Bad(at, "sparse regions in order, without overlap, inside the file's real size");
            }

            end = r.Offset + r.Length;
            sum += r.Length;
        }

        if (sum != stored)
        {
            throw TarHeader.Bad(at, "sparse regions whose sizes add up to the stored data size");
        }
    }
}
