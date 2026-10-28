using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Zip;

/// <summary>
/// Reads zip archives (PKWARE APPNOTE 6.3.10): zip64, data descriptors, UTF-8 and code page 437 names, Unicode path,
/// extended timestamp and NTFS time fields, Unix modes and symbolic links. Entries with the methods store, deflate and
/// zstd (93) can be read; others are listed and raise <see cref="ArchiveUnsupportedException"/> when opened.
/// </summary>
/// <remarks>
/// <para>A seekable stream is read through its central directory, which is authoritative: the directory is read and
/// checked as a whole on the first call (entries that share or overlap local headers are rejected), a local header
/// that disagrees with its directory entry is an error, and entries can be opened in any order. A stream that cannot
/// seek (or <see cref="ZipReaderOptions.ForceStreaming"/>) is read forward through the local headers; then the central
/// directory is never read, symbolic links and Unix modes are not known, and an entry whose sizes follow its data
/// (bit 3) reports <see cref="ZipEntry.IsSizeKnown"/> false.</para>
/// <para>Offsets in errors count bytes from where the reader started. Spanned (multi-disk) archives are not supported.
/// Entry streams are forward-only.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZipReader : ArchiveReader
{
    private sealed class Rec
    {
        public ushort VersionMadeBy, VersionNeeded, Flags, Method, InternalAttributes;
        public uint Crc, ExternalAttributes;
        public long CompressedSize, Size, LocalOffset;
        public byte[] RawName = [];
        public List<ZipExtraField> Extra = [];
        public string Name = string.Empty, Comment = string.Empty;
        public DateTimeOffset? Modified, Accessed, Changed;
        public bool Zip64, SizeKnown = true;
        public long Boundary;
    }

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _seekable;
    private readonly long _origin;
    private readonly ZipReaderOptions _options;
    private readonly ExpansionMeter _meter;
    private readonly bool _useDirectory;

    // Central directory mode.
    private List<ZipEntry>? _entries;
    private Dictionary<long, ZipEntry>? _byOffset;
    private int _next;
    private string _comment = string.Empty;

    // Forward-only mode.
    private ZipInputBuffer? _buf;
    private bool _ended;
    private bool _first = true;
    private ZipEntryStream? _entryStream;
    private Rec? _currentRec;
    private bool _currentZip64Descriptor;
    private long _knownTotal;
    private long _unknownTotal;

    /// <summary>Creates a reader over <paramref name="stream"/>, starting at its current position.</summary>
    /// <param name="stream">A readable stream of zip data. If it can seek, the central directory is used.</param>
    /// <param name="options">Reading options; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when the reader is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public ZipReader(Stream stream, ZipReaderOptions? options = null, bool leaveOpen = false)
        : base(ArchiveFormat.Zip, (options ?? ZipReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _options = options ?? ZipReaderOptions.Default;
        _seekable = stream.CanSeek;
        _origin = _seekable ? stream.Position : 0;
        _useDirectory = _seekable && !_options.ForceStreaming;
        _meter = new ExpansionMeter(Limits, ZipFormat.Name);
    }

    /// <inheritdoc />
    protected override string FormatName => ZipFormat.Name;

    /// <inheritdoc />
    public override bool SupportsRandomAccess => _useDirectory;

    /// <summary>Gets the archive comment. Available after the first <see cref="GetNextEntry"/>; empty for forward-only reads, which never see the end record.</summary>
    public string ArchiveComment => _comment;

    /// <summary>Advances to the next entry, skipping the rest of the current one.</summary>
    /// <returns>The entry, or <see langword="null"/> at the end of the archive.</returns>
    /// <exception cref="CompressionFormatException">The archive is malformed or truncated.</exception>
    /// <exception cref="CompressionLimitException">A limit was exceeded.</exception>
    /// <exception cref="ArchiveUnsupportedException">The archive is spanned over several files.</exception>
    public new ZipEntry? GetNextEntry() => (ZipEntry?)base.GetNextEntry();

    /// <summary>Advances to the next entry, skipping the rest of the current one.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> at the end of the archive.</returns>
    public new async ValueTask<ZipEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (ZipEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <exception cref="ArchiveUnsupportedException">The entry is encrypted or uses a method that is not supported.</exception>
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        ZipEntry entry = (ZipEntry?)CurrentEntry ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        if (_useDirectory)
        {
            return Sync.Result(OpenFromDirectoryAsync(entry, false, default));
        }

        return _entryStream ??= BuildForwardStream(entry);
    }

    /// <inheritdoc />
    public override Stream OpenEntryStream(ArchiveEntry entry)
    {
        ThrowIfDisposed();
        if (!_useDirectory)
        {
            throw new NotSupportedException("The zip stream is read forward-only, so entries can only be read in order.");
        }

        if (entry is not ZipEntry zip || _byOffset is null || !_byOffset.TryGetValue(zip.LocalHeaderOffset, out ZipEntry? known) || !ReferenceEquals(known, zip))
        {
            throw new ArgumentException("The entry was not read by this ZipReader.", nameof(entry));
        }

        return Sync.Result(OpenFromDirectoryAsync(zip, false, default));
    }

    internal void NoteUnannouncedBytes(int n)
    {
        _unknownTotal += n;
        if (_knownTotal + _unknownTotal > Limits.MaxTotalSize)
        {
            throw new CompressionLimitException(ZipFormat.Name, nameof(ArchiveLimits.MaxTotalSize), Limits.MaxTotalSize, _knownTotal + _unknownTotal);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _entryStream?.Invalidate();
            _buf?.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (!IsDisposed)
        {
            _entryStream?.Invalidate();
            _buf?.Dispose();
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken)
    {
        if (_useDirectory)
        {
            if (_entries is null)
            {
                await LoadDirectoryAsync(async, cancellationToken).ConfigureAwait(false);
            }

            return _next < _entries!.Count ? _entries[_next++] : null;
        }

        return await ReadNextForwardAsync(async, cancellationToken).ConfigureAwait(false);
    }

    // ---- entries from the central directory ----

    private async ValueTask ReadAtAsync(long position, byte[] dest, bool async, CancellationToken ct)
    {
        _stream.Seek(_origin + position, SeekOrigin.Begin);
        int got = async
            ? await _stream.ReadAtLeastAsync(dest, dest.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false)
            : _stream.ReadAtLeast(dest, dest.Length, throwOnEndOfStream: false);
        if (got < dest.Length)
        {
            throw ZipFormat.Bad(position + got, "more data (the archive ended early)", truncated: true);
        }
    }

    private async ValueTask LoadDirectoryAsync(bool async, CancellationToken ct)
    {
        long length = _stream.Length - _origin;
        if (length < ZipFormat.EndRecordSize)
        {
            throw ZipFormat.Bad(0, "an end of central directory record (the data is too short to be a zip archive)", truncated: true);
        }

        // APPNOTE 4.3.16: the end record is the last structure; only its comment (up to 65535 bytes) may follow it.
        int scan = (int)Math.Min(length, ZipFormat.EndRecordSize + 65535);
        long scanStart = length - scan;
        byte[] tail = new byte[scan];
        await ReadAtAsync(scanStart, tail, async, ct).ConfigureAwait(false);
        int found = -1;
        int fallback = -1;
        for (int i = scan - ZipFormat.EndRecordSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != ZipFormat.EndSignature)
            {
                continue;
            }

            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (i + ZipFormat.EndRecordSize + commentLength == scan)
            {
                found = i;
                break;
            }

            if (i + ZipFormat.EndRecordSize + commentLength < scan && fallback < 0)
            {
                fallback = i;
            }
        }

        if (found < 0)
        {
            found = fallback;
        }

        if (found < 0)
        {
            throw ZipFormat.Bad(0, "an end of central directory record (PK\\5\\6) in the last 65557 bytes; this is not a zip archive, or it is truncated");
        }

        long endPos = scanStart + found;
        ReadOnlySpan<byte> e = tail.AsSpan(found);
        uint disk = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
        uint dirDisk = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
        ulong entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
        ulong total = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
        ulong dirSize = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
        ulong dirOffset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
        int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(e[20..]);
        // The end record has no encoding flag: take UTF-8 when the bytes are valid UTF-8, otherwise the legacy encoding.
        _comment = System.Text.Unicode.Utf8.IsValid(e.Slice(22, commentLen))
            ? System.Text.Encoding.UTF8.GetString(e.Slice(22, commentLen))
            : ZipFormat.DecodeName(e.Slice(22, commentLen), 0, default, _options.LegacyNameEncoding);

        bool sentinel = disk == ZipFormat.Max16 || dirDisk == ZipFormat.Max16 || entriesOnDisk == ZipFormat.Max16 || total == ZipFormat.Max16
            || dirSize == ZipFormat.Max32 || dirOffset == ZipFormat.Max32;
        long dirEnd = endPos;
        bool zip64 = false;
        if (endPos >= ZipFormat.Zip64LocatorSize)
        {
            byte[] loc = new byte[ZipFormat.Zip64LocatorSize];
            await ReadAtAsync(endPos - ZipFormat.Zip64LocatorSize, loc, async, ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(loc) == ZipFormat.Zip64LocatorSignature)
            {
                // APPNOTE 4.3.15: the locator names the disk and the offset of the zip64 end record.
                ulong recordOffset = BinaryPrimitives.ReadUInt64LittleEndian(loc.AsSpan(8));
                long recordPos = recordOffset <= (ulong)length ? (long)recordOffset : -1;
                byte[] rec = new byte[ZipFormat.Zip64EndRecordSize];
                bool ok = false;
                // The offset may be off by the length of data prepended to the archive; the record also sits right before the locator.
                foreach (long candidate in new[] { recordPos, endPos - ZipFormat.Zip64LocatorSize - ZipFormat.Zip64EndRecordSize })
                {
                    if (candidate >= 0 && candidate + ZipFormat.Zip64EndRecordSize <= length)
                    {
                        await ReadAtAsync(candidate, rec, async, ct).ConfigureAwait(false);
                        if (BinaryPrimitives.ReadUInt32LittleEndian(rec) == ZipFormat.Zip64EndSignature)
                        {
                            dirEnd = candidate;
                            ok = true;
                            break;
                        }
                    }
                }

                if (ok)
                {
                    // APPNOTE 4.3.14.
                    zip64 = true;
                    disk = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(16));
                    dirDisk = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(20));
                    entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(24));
                    total = BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
                    dirSize = BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(40));
                    dirOffset = BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
                }
            }
        }

        if (sentinel && !zip64)
        {
            throw ZipFormat.Bad(endPos, "a zip64 end of central directory record and locator (the end record holds 0xFFFF/0xFFFFFFFF markers)");
        }

        if (disk != 0 || dirDisk != 0 || entriesOnDisk != total)
        {
            throw new ArchiveUnsupportedException(ZipFormat.Name, "archives spanned over several files");
        }

        if (total > (ulong)Limits.MaxEntryCount)
        {
            throw new CompressionLimitException(ZipFormat.Name, nameof(ArchiveLimits.MaxEntryCount), Limits.MaxEntryCount, total > long.MaxValue ? long.MaxValue : (long)total);
        }

        if (dirSize > (ulong)dirEnd || total > dirSize / ZipFormat.CentralHeaderSize)
        {
            throw ZipFormat.Bad(endPos, $"a central directory of {dirSize} bytes that fits before the end record and holds {total} entries");
        }

        if (dirSize > (ulong)_options.MaxCentralDirectorySize)
        {
            throw new CompressionLimitException(ZipFormat.Name, nameof(ZipReaderOptions.MaxCentralDirectorySize), _options.MaxCentralDirectorySize, (long)dirSize);
        }

        // Data may precede the archive (a self-extractor stub): the stored offsets are then relative to the archive's own start.
        long adjust = 0;
        if (dirOffset > (ulong)dirEnd)
        {
            throw ZipFormat.Bad(endPos, "a central directory offset inside the archive");
        }

        if ((long)dirOffset + (long)dirSize != dirEnd)
        {
            byte[] probe = new byte[4];
            await ReadAtAsync((long)dirOffset, probe, async, ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(probe) != ZipFormat.CentralSignature && dirEnd - (long)dirSize - (long)dirOffset > 0)
            {
                adjust = dirEnd - (long)dirSize - (long)dirOffset;
            }
        }

        long dirStart = (long)dirOffset + adjust;
        var raws = new List<Rec>((int)total);
        using (var buf = new ZipInputBuffer(_stream, _origin, dirStart))
        {
            for (ulong i = 0; i < total; i++)
            {
                if (buf.Offset - dirStart > (long)dirSize - ZipFormat.CentralHeaderSize)
                {
                    throw ZipFormat.Bad(buf.Offset, "the central directory to hold all its entries");
                }

                raws.Add(await ReadDirectoryRecordAsync(buf, adjust, dirStart, async, ct).ConfigureAwait(false));
            }
        }

        BuildEntries(raws, dirStart);
        await ResolveLinksAsync(async, ct).ConfigureAwait(false);
    }

    private async ValueTask<Rec> ReadDirectoryRecordAsync(ZipInputBuffer buf, long adjust, long dirStart, bool async, CancellationToken ct)
    {
        long at = buf.Offset;
        if (!await buf.EnsureAsync(ZipFormat.CentralHeaderSize, async, ct).ConfigureAwait(false))
        {
            throw ZipFormat.Bad(at, "a central directory header (the archive ended inside the directory)", truncated: true);
        }

        ReadOnlySpan<byte> h = buf.Span;
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != ZipFormat.CentralSignature)
        {
            throw ZipFormat.Bad(at, "a central directory header signature (PK\\1\\2)");
        }

        var r = new Rec
        {
            VersionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),
            VersionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]),
            Method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]),
            Crc = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]),
            InternalAttributes = BinaryPrimitives.ReadUInt16LittleEndian(h[36..]),
            ExternalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(h[38..]),
        };
        ushort dosTime = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
        ushort dosDate = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
        uint comp32 = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
        int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
        ushort diskStart = BinaryPrimitives.ReadUInt16LittleEndian(h[34..]);
        uint offset32 = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);
        buf.Consume(ZipFormat.CentralHeaderSize);

        byte[] name = new byte[nameLen];
        byte[] extra = new byte[extraLen];
        byte[] comment = new byte[commentLen];
        if (!await buf.ReadExactAsync(name, async, ct).ConfigureAwait(false) || !await buf.ReadExactAsync(extra, async, ct).ConfigureAwait(false)
            || !await buf.ReadExactAsync(comment, async, ct).ConfigureAwait(false))
        {
            throw ZipFormat.Bad(at, "the name, extra field and comment of a central directory header (the archive ended inside them)", truncated: true);
        }

        r.RawName = name;
        r.Extra = ZipFormat.ParseExtra(extra, at + ZipFormat.CentralHeaderSize + nameLen);

        // APPNOTE 4.5.3: the zip64 field holds, in this order, exactly those of size, compressed size, offset and disk
        // whose header field is saturated.
        ulong size = size32, comp = comp32, offset = offset32;
        if (size32 == ZipFormat.Max32 || comp32 == ZipFormat.Max32 || offset32 == ZipFormat.Max32 || diskStart == ZipFormat.Max16)
        {
            ReadOnlySpan<byte> z = ZipFormat.Find(r.Extra, ZipFormat.ExtraZip64);
            int p = 0;
            r.Zip64 = ZipFormat.Has(r.Extra, ZipFormat.ExtraZip64);
            if (size32 == ZipFormat.Max32)
            {
                size = NextU64(z, ref p, at);
            }

            if (comp32 == ZipFormat.Max32)
            {
                comp = NextU64(z, ref p, at);
            }

            if (offset32 == ZipFormat.Max32)
            {
                offset = NextU64(z, ref p, at);
            }

            if (diskStart == ZipFormat.Max16)
            {
                if (z.Length - p < 4)
                {
                    throw ZipFormat.Bad(at, "the 4-byte disk number in the zip64 extra field");
                }

                diskStart = 0;
                if (BinaryPrimitives.ReadUInt32LittleEndian(z[p..]) != 0)
                {
                    throw new ArchiveUnsupportedException(ZipFormat.Name, "archives spanned over several files");
                }
            }
        }
        else
        {
            r.Zip64 = ZipFormat.Has(r.Extra, ZipFormat.ExtraZip64);
        }

        if (diskStart != 0)
        {
            throw new ArchiveUnsupportedException(ZipFormat.Name, "archives spanned over several files");
        }

        if (size > long.MaxValue || comp > long.MaxValue || offset > (ulong)(long.MaxValue - adjust))
        {
            throw ZipFormat.Bad(at, "sizes and an offset that fit in 63 bits");
        }

        r.Size = (long)size;
        r.CompressedSize = (long)comp;
        r.LocalOffset = (long)offset + adjust;
        if (r.LocalOffset >= dirStart)
        {
            throw ZipFormat.Bad(at, "a local header offset before the central directory");
        }

        r.Modified = ZipFormat.FromDos(dosDate, dosTime);
        ZipFormat.ReadTimes(r.Extra, ref r.Modified, ref r.Accessed, ref r.Changed);
        r.Name = ZipFormat.DecodeName(name, r.Flags, ZipFormat.Find(r.Extra, ZipFormat.ExtraUnicodePath), _options.LegacyNameEncoding);
        r.Comment = ZipFormat.DecodeName(comment, r.Flags, default, _options.LegacyNameEncoding);
        return r;
    }

    private static ulong NextU64(ReadOnlySpan<byte> data, ref int p, long at)
    {
        if (data.Length - p < 8)
        {
            throw ZipFormat.Bad(at, "a zip64 extra field holding every value the header marks as 0xFFFFFFFF");
        }

        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data[p..]);
        p += 8;
        return v;
    }

    // Orders entries by where their local headers are, rejects shared or overlapping ones, and gives each entry the offset
    // where its data must end.
    private void BuildEntries(List<Rec> raws, long dirStart)
    {
        int[] order = new int[raws.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => raws[a].LocalOffset.CompareTo(raws[b].LocalOffset));
        for (int i = 0; i < order.Length; i++)
        {
            Rec r = raws[order[i]];
            long next = i + 1 < order.Length ? raws[order[i + 1]].LocalOffset : dirStart;
            if (next == r.LocalOffset)
            {
                throw ZipFormat.Bad(r.LocalOffset, "entries with different local header offsets (two entries share one, so their data overlaps)");
            }

            r.Boundary = next;
            long minimum = r.LocalOffset + ZipFormat.LocalHeaderSize + r.RawName.Length;
            if (r.CompressedSize > next - minimum)
            {
                throw ZipFormat.Bad(r.LocalOffset, $"entry '{Printable(r.Name)}' to end before the next local header or the central directory (its compressed size of {r.CompressedSize} overlaps what follows)");
            }
        }

        _entries = new List<ZipEntry>(raws.Count);
        _byOffset = new Dictionary<long, ZipEntry>(raws.Count);
        foreach (Rec r in raws)
        {
            ZipEntry entry = Make(r, null);
            _entries.Add(entry);
            _byOffset[entry.LocalHeaderOffset] = entry;
        }
    }

    private static string Printable(string name) => name.Length > 100 ? name[..100] : name;

    private static bool IsUnixHost(ushort versionMadeBy) => (versionMadeBy >> 8) is 3 or 19;

    private static ZipEntry Make(Rec r, string? linkTarget)
    {
        string name = r.Name;
        bool slash = name.Length > 0 && (name[^1] is '/' or '\\');
        ArchiveEntryKind kind = slash ? ArchiveEntryKind.Directory : ArchiveEntryKind.File;
        UnixFileMode? mode = null;
        if (IsUnixHost(r.VersionMadeBy))
        {
            uint unix = r.ExternalAttributes >> 16;
            if (!slash)
            {
                kind = ZipFormat.KindFromUnixMode(unix, kind);
            }

            if ((unix & 0xFFF) != 0)
            {
                mode = (UnixFileMode)(int)(unix & 0xFFF);
            }
        }
        else if ((r.ExternalAttributes & 0x10) != 0)
        {
            kind = ArchiveEntryKind.Directory;
        }

        if (name.Length == 0)
        {
            throw ZipFormat.Bad(r.LocalOffset, "a non-empty entry name");
        }

        string trimmed = name.TrimEnd('/', '\\');
        if (trimmed.Length == 0)
        {
            trimmed = "/";
        }

        long size = kind == ArchiveEntryKind.File && r.SizeKnown ? r.Size : 0;
        return new ZipEntry
        {
            Name = trimmed,
            Kind = kind,
            Size = size,
            ModifiedTime = r.Modified,
            AccessTime = r.Accessed,
            ChangeTime = r.Changed,
            Mode = mode,
            LinkTarget = kind == ArchiveEntryKind.Symlink ? linkTarget ?? string.Empty : null,
            Method = (ZipMethod)r.Method,
            Flags = r.Flags,
            Crc32 = r.Crc,
            CompressedSize = r.CompressedSize,
            VersionMadeBy = r.VersionMadeBy,
            VersionNeeded = r.VersionNeeded,
            InternalAttributes = r.InternalAttributes,
            ExternalAttributes = r.ExternalAttributes,
            Comment = r.Comment,
            LocalHeaderOffset = r.LocalOffset,
            RawName = r.RawName,
            ExtraFields = r.Extra,
            UsesZip64 = r.Zip64,
            IsSizeKnown = r.SizeKnown,
            Boundary = r.Boundary,
            DeclaredSize = r.Size,
        };
    }

    // A symbolic link's target is the entry's data. It is read when the directory is loaded so that the entry is complete.
    private async ValueTask ResolveLinksAsync(bool async, CancellationToken ct)
    {
        for (int i = 0; i < _entries!.Count; i++)
        {
            ZipEntry e = _entries[i];
            if (e.Kind != ArchiveEntryKind.Symlink)
            {
                continue;
            }

            string target = string.Empty;
            long cap = (long)Limits.MaxPathLength * 4;
            if (e.DeclaredSize > cap)
            {
                throw new CompressionLimitException(ZipFormat.Name, nameof(ArchiveLimits.MaxPathLength), Limits.MaxPathLength, e.DeclaredSize);
            }

            try
            {
                using Stream s = await OpenFromDirectoryAsync(e, async, ct).ConfigureAwait(false);
                byte[] data = new byte[e.DeclaredSize];
                int got = 0;
                while (got < data.Length)
                {
                    int n = async ? await s.ReadAsync(data.AsMemory(got), ct).ConfigureAwait(false) : s.Read(data, got, data.Length - got);
                    if (n == 0)
                    {
                        break;
                    }

                    got += n;
                }

                target = PathRules.DecodeName(data.AsSpan(0, got));
            }
            catch (ArchiveUnsupportedException)
            {
                // An encrypted or unsupported link is listed with an empty target, which extraction refuses.
            }

            ZipEntry linked = Make(RecFrom(e), target);
            _entries[i] = linked;
            _byOffset![linked.LocalHeaderOffset] = linked;
        }
    }

    private static Rec RecFrom(ZipEntry e) => new()
    {
        VersionMadeBy = e.VersionMadeBy,
        VersionNeeded = e.VersionNeeded,
        Flags = e.Flags,
        Method = (ushort)e.Method,
        InternalAttributes = e.InternalAttributes,
        Crc = e.Crc32,
        ExternalAttributes = e.ExternalAttributes,
        CompressedSize = e.CompressedSize,
        Size = e.DeclaredSize,
        LocalOffset = e.LocalHeaderOffset,
        RawName = e.RawName.ToArray(),
        Extra = [.. e.ExtraFields],
        Name = e.Name,
        Comment = e.Comment,
        Modified = e.ModifiedTime,
        Accessed = e.AccessTime,
        Changed = e.ChangeTime,
        Zip64 = e.UsesZip64,
        SizeKnown = e.IsSizeKnown,
        Boundary = e.Boundary,
    };

    private static void CheckSupported(ZipEntry e)
    {
        if (e.IsEncrypted)
        {
            throw new ArchiveUnsupportedException(ZipFormat.Name, $"encrypted entries ('{Printable(e.Name)}')");
        }

        if (e.Method is not (ZipMethod.Store or ZipMethod.Deflate or ZipMethod.BZip2 or ZipMethod.Zstd or ZipMethod.Lzma or ZipMethod.Xz))
        {
            throw new ArchiveUnsupportedException(ZipFormat.Name, $"compression method {(int)e.Method} ({ZipFormat.MethodName((int)e.Method)}) of entry '{Printable(e.Name)}'");
        }
    }

    // Opens an entry through the directory: reads its local header, checks it against the directory entry, and returns the
    // decoding stream positioned at the data.
    private async ValueTask<Stream> OpenFromDirectoryAsync(ZipEntry e, bool async, CancellationToken ct)
    {
        CheckSupported(e);
        if (e.CompressedSize == 0 && e.DeclaredSize == 0 && e.Method == ZipMethod.Store)
        {
            return new MemoryReadStream(ReadOnlyMemory<byte>.Empty);
        }

        var buf = new ZipInputBuffer(_stream, _origin, e.LocalHeaderOffset);
        try
        {
            long at = e.LocalHeaderOffset;
            if (!await buf.EnsureAsync(ZipFormat.LocalHeaderSize, async, ct).ConfigureAwait(false))
            {
                throw ZipFormat.Bad(at, "a local file header (the archive ended before it)", truncated: true);
            }

            ReadOnlySpan<byte> h = buf.Span;
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != ZipFormat.LocalSignature)
            {
                throw ZipFormat.Bad(at, $"a local file header signature (PK\\3\\4) for entry '{Printable(e.Name)}', as the central directory says");
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(h[14..]);
            uint comp32 = BinaryPrimitives.ReadUInt32LittleEndian(h[18..]);
            uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(h[22..]);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[26..]);
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
            buf.Consume(ZipFormat.LocalHeaderSize);
            if (method != (ushort)e.Method || nameLen != e.RawName.Length || (flags & 0x41) != (e.Flags & 0x41))
            {
                throw ZipFormat.Bad(at, $"a local header that agrees with the central directory entry '{Printable(e.Name)}' (method, name length or encryption flag differ)");
            }

            byte[] name = new byte[nameLen];
            byte[] extra = new byte[extraLen];
            if (!await buf.ReadExactAsync(name, async, ct).ConfigureAwait(false) || !await buf.ReadExactAsync(extra, async, ct).ConfigureAwait(false))
            {
                throw ZipFormat.Bad(at, "the name and extra field of a local header (the archive ended inside them)", truncated: true);
            }

            if (!name.AsSpan().SequenceEqual(e.RawName.Span))
            {
                throw ZipFormat.Bad(at, $"a local header whose name is that of the central directory entry '{Printable(e.Name)}'");
            }

            // With bit 3 clear the local header repeats the sizes and CRC; a disagreement means the two structures describe different data.
            if ((flags & ZipFormat.FlagDescriptor) == 0)
            {
                ulong size = size32, comp = comp32;
                if (size32 == ZipFormat.Max32 || comp32 == ZipFormat.Max32)
                {
                    ReadOnlySpan<byte> z = ZipFormat.Find(ZipFormat.ParseExtra(extra, at + ZipFormat.LocalHeaderSize + nameLen), ZipFormat.ExtraZip64);
                    int p = 0;
                    if (size32 == ZipFormat.Max32)
                    {
                        size = NextU64(z, ref p, at);
                    }

                    if (comp32 == ZipFormat.Max32)
                    {
                        comp = NextU64(z, ref p, at);
                    }
                }

                if (size != (ulong)e.DeclaredSize || comp != (ulong)e.CompressedSize || crc != e.Crc32)
                {
                    throw ZipFormat.Bad(at, $"a local header whose sizes and CRC-32 match the central directory entry '{Printable(e.Name)}'");
                }
            }

            long dataStart = at + ZipFormat.LocalHeaderSize + nameLen + extraLen;
            if (dataStart > e.Boundary || e.CompressedSize > e.Boundary - dataStart)
            {
                throw ZipFormat.Bad(at, $"the data of entry '{Printable(e.Name)}' to end before the next entry or the central directory (the local header's extra field or the compressed size overlaps it)");
            }

            if (e.Method == ZipMethod.Store && e.CompressedSize != e.DeclaredSize)
            {
                throw ZipFormat.Bad(at, $"a stored entry whose compressed size equals its size ({e.DeclaredSize}), found {e.CompressedSize}");
            }

            return new ZipEntryStream(buf, true, e.Method, e.CompressedSize, e.DeclaredSize, e.Crc32, _options.VerifyChecksum, false, false, _meter, this, Limits);
        }
        catch
        {
            buf.Dispose();
            throw;
        }
    }

    // ---- forward-only reading ----

    private async ValueTask<ArchiveEntry?> ReadNextForwardAsync(bool async, CancellationToken ct)
    {
        _buf ??= new ZipInputBuffer(_stream, _origin, _seekable ? _stream.Position - _origin : 0);
        ZipInputBuffer buf = _buf;
        if (_entryStream is not null)
        {
            await _entryStream.DrainAsync(async, ct).ConfigureAwait(false);
            _entryStream.Invalidate();
            _entryStream = null;
        }
        else if (_currentRec is { } skipped)
        {
            // Never opened: skip the data without decoding it, if its length is known.
            _currentRec = null;
            if (skipped.SizeKnown && (skipped.Flags & ZipFormat.FlagDescriptor) == 0)
            {
                if (!await buf.SkipAsync(skipped.CompressedSize, async, ct).ConfigureAwait(false))
                {
                    throw ZipFormat.Bad(buf.Offset, "the entry's data (the archive ended inside it)", truncated: true);
                }

                _meter.AddCompressed(skipped.CompressedSize);
            }
            else
            {
                CheckForwardStreamable(skipped);
                using var drain = BuildForwardStream(skippedEntry: null, skipped);
                await drain.DrainAsync(async, ct).ConfigureAwait(false);
            }
        }

        _currentRec = null;
        if (_ended)
        {
            return null;
        }

        while (true)
        {
            long at = buf.Offset;
            if (!await buf.EnsureAsync(4, async, ct).ConfigureAwait(false))
            {
                throw ZipFormat.Bad(at, "a local file header or the central directory (the archive ended without an end record)", truncated: true);
            }

            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(buf.Span);
            if (_first && (sig == ZipFormat.SpanMarker || sig == ZipFormat.DescriptorSignature))
            {
                // APPNOTE 8.5.3 and 8.5.4: a marker some writers put at the start of an archive.
                buf.Consume(4);
                _first = false;
                continue;
            }

            _first = false;
            if (sig is ZipFormat.CentralSignature or ZipFormat.EndSignature or ZipFormat.Zip64EndSignature)
            {
                _ended = true;
                return null;
            }

            if (sig != ZipFormat.LocalSignature)
            {
                throw ZipFormat.Bad(at, "a local file header signature (PK\\3\\4), a central directory header or an end record");
            }

            return await ReadLocalHeaderAsync(buf, at, async, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<ArchiveEntry> ReadLocalHeaderAsync(ZipInputBuffer buf, long at, bool async, CancellationToken ct)
    {
        if (!await buf.EnsureAsync(ZipFormat.LocalHeaderSize, async, ct).ConfigureAwait(false))
        {
            throw ZipFormat.Bad(at, "a local file header (the archive ended inside it)", truncated: true);
        }

        ReadOnlySpan<byte> h = buf.Span;
        var r = new Rec
        {
            VersionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]),
            Method = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]),
            Crc = BinaryPrimitives.ReadUInt32LittleEndian(h[14..]),
            LocalOffset = at,
        };
        ushort dosTime = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
        ushort dosDate = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
        uint comp32 = BinaryPrimitives.ReadUInt32LittleEndian(h[18..]);
        uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(h[22..]);
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[26..]);
        int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        buf.Consume(ZipFormat.LocalHeaderSize);
        byte[] name = new byte[nameLen];
        byte[] extra = new byte[extraLen];
        if (!await buf.ReadExactAsync(name, async, ct).ConfigureAwait(false) || !await buf.ReadExactAsync(extra, async, ct).ConfigureAwait(false))
        {
            throw ZipFormat.Bad(at, "the name and extra field of a local header (the archive ended inside them)", truncated: true);
        }

        if (nameLen == 0)
        {
            throw ZipFormat.Bad(at + ZipFormat.LocalHeaderSize, "a non-empty entry name");
        }

        r.RawName = name;
        r.Extra = ZipFormat.ParseExtra(extra, at + ZipFormat.LocalHeaderSize + nameLen);
        r.Zip64 = ZipFormat.Has(r.Extra, ZipFormat.ExtraZip64);
        r.Name = ZipFormat.DecodeName(name, r.Flags, ZipFormat.Find(r.Extra, ZipFormat.ExtraUnicodePath), _options.LegacyNameEncoding);
        r.Modified = ZipFormat.FromDos(dosDate, dosTime);
        ZipFormat.ReadTimes(r.Extra, ref r.Modified, ref r.Accessed, ref r.Changed);
        _currentZip64Descriptor = r.Zip64;

        ulong size = size32, comp = comp32;
        if (size32 == ZipFormat.Max32 || comp32 == ZipFormat.Max32)
        {
            // APPNOTE 4.5.3: in a local header the zip64 field holds both sizes.
            ReadOnlySpan<byte> z = ZipFormat.Find(r.Extra, ZipFormat.ExtraZip64);
            int p = 0;
            size = NextU64(z, ref p, at);
            comp = NextU64(z, ref p, at);
        }

        if (size > long.MaxValue || comp > long.MaxValue)
        {
            throw ZipFormat.Bad(at, "sizes that fit in 63 bits");
        }

        if ((r.Flags & ZipFormat.FlagDescriptor) == 0)
        {
            r.Size = (long)size;
            r.CompressedSize = (long)comp;
        }
        else if (r.Method == (ushort)ZipMethod.Store && comp != 0)
        {
            // A stored entry cannot find its own end; a writer that sets bit 3 and still fills in the size is believed.
            r.Size = r.CompressedSize = (long)comp;
            r.Crc = 0;
        }
        else
        {
            r.SizeKnown = false;
            r.Crc = 0;
        }

        ZipEntry entry = Make(r, null);
        _currentRec = r;
        _entryStream = null;
        if (r.SizeKnown)
        {
            _knownTotal += entry.Size;
        }

        return entry;
    }

    private static void CheckForwardStreamable(Rec r)
    {
        if (r.Method == (ushort)ZipMethod.Store && !r.SizeKnown)
        {
            throw new ArchiveUnsupportedException(ZipFormat.Name, $"stored entry '{Printable(r.Name)}' whose size is only in a data descriptor (needs the central directory; open a seekable stream)");
        }
    }

    private ZipEntryStream BuildForwardStream(ZipEntry entry) => BuildForwardStream(entry, _currentRec ?? throw new InvalidOperationException("There is no current entry."));

    private ZipEntryStream BuildForwardStream(ZipEntry? skippedEntry, Rec r)
    {
        _ = skippedEntry;
        if ((r.Flags & 0x41) != 0 || r.Method == (ushort)ZipMethod.Aes)
        {
            throw new ArchiveUnsupportedException(ZipFormat.Name, $"encrypted entries ('{Printable(r.Name)}')");
        }

        if (r.Method is not ((ushort)ZipMethod.Store or (ushort)ZipMethod.Deflate or (ushort)ZipMethod.BZip2 or (ushort)ZipMethod.Zstd or (ushort)ZipMethod.Lzma or (ushort)ZipMethod.Xz))
        {
            throw new ArchiveUnsupportedException(ZipFormat.Name, $"compression method {r.Method} ({ZipFormat.MethodName(r.Method)}) of entry '{Printable(r.Name)}'");
        }

        CheckForwardStreamable(r);
        bool descriptor = (r.Flags & ZipFormat.FlagDescriptor) != 0;
        return new ZipEntryStream(
            _buf!, false, (ZipMethod)r.Method, r.SizeKnown ? r.CompressedSize : -1, r.SizeKnown ? r.Size : -1, r.Crc, _options.VerifyChecksum,
            descriptor, _currentZip64Descriptor, _meter, this, Limits);
    }
}
