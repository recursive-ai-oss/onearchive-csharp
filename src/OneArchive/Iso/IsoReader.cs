using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Iso;

/// <summary>
/// Reads ISO 9660 images (ECMA-119) with the Joliet and Rock Ridge extensions: the volume descriptors, the directory hierarchy of
/// the primary or the Joliet descriptor, Rock Ridge names, modes, owners, times, symbolic links and device numbers, relocated
/// directories, multi-extent files and SUSP continuation areas.
/// </summary>
/// <remarks>
/// <para>The source must be seekable, because directories and files are found by block number; <see cref="Archive"/> copies a
/// source that cannot seek into memory first. The whole hierarchy is read when the first entry is asked for, under the entry,
/// depth and size limits, and entries come in directory order with each directory before its contents.</para>
/// <para>Which hierarchy is listed is <see cref="IsoReaderOptions.Tree"/>; by default the primary hierarchy with its Rock Ridge
/// names when it has them, otherwise Joliet, otherwise the plain ISO 9660 identifiers. The directory <c>RR_MOVED</c> that holds
/// relocated deep directories is hidden and the directories are listed at their original place.</para>
/// <para>Extended attribute records are skipped, interleaved files are read as contiguous, and zisofs-compressed files are listed
/// but raise <see cref="ArchiveUnsupportedException"/> when opened. A boot catalog is reported in
/// <see cref="IsoVolumeInfo.BootCatalogBlock"/> and nothing more. Block sizes other than 2048 bytes are not supported.</para>
/// <para>Errors: <see cref="CompressionFormatException"/> names the byte offset (directory loops, extents past the end of the
/// image, overlapping or repeated continuation areas, a missing descriptor terminator, records that do not fit their sector);
/// a limit raises <see cref="CompressionLimitException"/>; unsupported features raise <see cref="ArchiveUnsupportedException"/>.</para>
/// </remarks>
public sealed class IsoReader : ArchiveReader
{
    internal const string Name = IsoFormat.Name;
    private const int MaxDescriptors = 256;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _origin;
    private readonly IsoReaderOptions _options;
    private bool _loaded;
    private long _total;
    private long _continuationBytes;
    private IsoVolumeInfo _volume = new();
    private List<IsoEntry> _items = [];
    private int _next;

    /// <summary>Creates a reader over a seekable stream positioned at the start of the image.</summary>
    /// <param name="stream">A readable, seekable stream.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the reader is disposed.</param>
    /// <exception cref="ArgumentException">The stream cannot read or seek.</exception>
    public IsoReader(Stream stream, IsoReaderOptions? options = null, bool leaveOpen = false)
        : base(ArchiveFormat.Iso, (options ?? IsoReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable: directories and files are found by block number.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _origin = stream.Position;
        _options = options ?? IsoReaderOptions.Default;
    }

    /// <inheritdoc />
    protected override string FormatName => Name;

    /// <inheritdoc />
    public override bool SupportsRandomAccess => true;

    /// <summary>Gets what the volume descriptors say.</summary>
    /// <exception cref="CompressionFormatException">The descriptors are malformed.</exception>
    public IsoVolumeInfo Volume
    {
        get
        {
            EnsureLoaded();
            return _volume;
        }
    }

    /// <summary>Gets every entry in listing order.</summary>
    /// <exception cref="CompressionFormatException">The image is malformed.</exception>
    /// <exception cref="CompressionLimitException">A limit was exceeded.</exception>
    public IReadOnlyList<IsoEntry> Items
    {
        get
        {
            EnsureLoaded();
            return _items;
        }
    }

    /// <summary>Reads the next entry.</summary>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new IsoEntry? GetNextEntry() => (IsoEntry?)base.GetNextEntry();

    /// <summary>Reads the next entry.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new async ValueTask<IsoEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (IsoEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        return new ValueTask<ArchiveEntry?>(_next < _items.Count ? _items[_next++] : null);
    }

    /// <inheritdoc />
    /// <exception cref="ArchiveUnsupportedException">The file is zisofs-compressed.</exception>
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        IsoEntry entry = (IsoEntry?)CurrentEntry ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        return Open(entry);
    }

    /// <inheritdoc />
    /// <exception cref="ArchiveUnsupportedException">The file is zisofs-compressed.</exception>
    public override Stream OpenEntryStream(ArchiveEntry entry)
    {
        ThrowIfDisposed();
        EnsureLoaded();
        if (entry is not IsoEntry e || !_items.Contains(e))
        {
            throw new ArgumentException("The entry was not read by this IsoReader.", nameof(entry));
        }

        return Open(e);
    }

    private Stream Open(IsoEntry entry)
    {
        if (entry.Kind != ArchiveEntryKind.File || entry.Size == 0)
        {
            return new MemoryReadStream(ReadOnlyMemory<byte>.Empty);
        }

        if (entry.IsZisofs)
        {
            throw new ArchiveUnsupportedException(Name, $"zisofs-compressed files (file '{entry.Name}')");
        }

        return new IsoEntryStream(_stream, _origin, entry.Extents);
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

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        Load();
        _loaded = true;
    }

    // ---- low-level reads ----

    private void ReadAt(long offset, Span<byte> destination, string what)
    {
        if (offset < 0 || offset + destination.Length > _total)
        {
            throw IsoFormat.Bad(Math.Min(Math.Max(offset, 0), _total), $"{what} inside the image ({destination.Length} bytes at offset {offset}; the image is {_total} bytes)", truncated: true);
        }

        _stream.Seek(_origin + offset, SeekOrigin.Begin);
        int done = 0;
        while (done < destination.Length)
        {
            int n = _stream.Read(destination[done..]);
            if (n == 0)
            {
                throw IsoFormat.Bad(offset + done, what, truncated: true);
            }

            done += n;
        }
    }

    // ---- volume descriptors (ECMA-119 clause 8) ----

    private sealed class Descriptor
    {
        public long Offset;
        public byte[] Data = [];
    }

    private void Load()
    {
        _total = _stream.Length - _origin;
        Descriptor? primary = null;
        Descriptor? joliet = null;
        int jolietLevel = 0;
        long? boot = null;
        bool terminated = false;
        long pos = IsoFormat.FirstDescriptorSector * (long)IsoFormat.SectorSize;
        for (int n = 0; n < MaxDescriptors; n++, pos += IsoFormat.SectorSize)
        {
            if (pos + IsoFormat.SectorSize > _total)
            {
                throw IsoFormat.Bad(_total, "a volume descriptor set terminator (type 255) before the end of the image", truncated: true);
            }

            byte[] d = new byte[IsoFormat.SectorSize];
            ReadAt(pos, d, "a volume descriptor");
            if (!d.AsSpan(1, 5).SequenceEqual("CD001"u8))
            {
                throw IsoFormat.Bad(pos + 1, "the standard identifier 'CD001' in a volume descriptor");
            }

            int type = d[0];
            int version = d[6];
            if (type == 255)
            {
                // Clause 8.3: the terminator ends the set.
                if (version != 1)
                {
                    throw IsoFormat.Bad(pos + 6, "version 1 in the volume descriptor set terminator");
                }

                terminated = true;
                break;
            }

            switch (type)
            {
                case 0:
                    // Clause 8.2 and the El Torito specification: a boot record names its system in bytes 7 to 38.
                    if (boot is null && d.AsSpan(7, 23).SequenceEqual("EL TORITO SPECIFICATION"u8))
                    {
                        boot = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(71));
                    }

                    break;
                case 1:
                    if (version != 1)
                    {
                        throw IsoFormat.Bad(pos + 6, "version 1 in the primary volume descriptor");
                    }

                    primary ??= new Descriptor { Offset = pos, Data = d };
                    break;
                case 2:
                    // Clause 8.5 and the Joliet specification: the escape sequences %/@, %/C or %/E select UCS-2 level 1 to 3.
                    if (version == 1 && joliet is null && d[88] == (byte)'%' && d[89] == (byte)'/')
                    {
                        int level = d[90] switch { (byte)'@' => 1, (byte)'C' => 2, (byte)'E' => 3, _ => 0 };
                        if (level != 0)
                        {
                            joliet = new Descriptor { Offset = pos, Data = d };
                            jolietLevel = level;
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        if (!terminated)
        {
            throw IsoFormat.Bad(pos, $"a volume descriptor set terminator (type 255) within {MaxDescriptors} descriptors");
        }

        if (primary is null)
        {
            throw IsoFormat.Bad(IsoFormat.FirstDescriptorSector * (long)IsoFormat.SectorSize, "a primary volume descriptor (type 1)");
        }

        ReadOnlySpan<byte> p = primary.Data;
        int blockSize = BinaryPrimitives.ReadUInt16LittleEndian(p[128..]);
        if (blockSize != IsoFormat.SectorSize || BinaryPrimitives.ReadUInt16BigEndian(p[130..]) != blockSize)
        {
            throw new ArchiveUnsupportedException(Name, $"a logical block size of {blockSize} bytes (only 2048 is supported)");
        }

        long spaceBlocks = IsoFormat.ReadBoth32(p[80..], primary.Offset + 80, "the volume space size");
        (long rootExtent, long rootLength) = ReadRootRecord(primary);

        // Which hierarchy do we list? Rock Ridge needs the SP entry in the root's own record.
        SystemUseInfo? rootSys = ProbeRoot(rootExtent, rootLength);
        bool hasRockRidge = rootSys is { HasSp: true } && (rootSys.HasPx || (rootSys.ExtensionIds?.Exists(static id => id.StartsWith("RRIP", StringComparison.Ordinal) || id.StartsWith("IEEE", StringComparison.Ordinal)) ?? false));
        bool useRr = hasRockRidge && _options.UseRockRidge;
        IsoTree tree = _options.Tree;
        if (tree == IsoTree.Auto)
        {
            tree = useRr || joliet is null ? IsoTree.Primary : IsoTree.Joliet;
        }
        else if (tree == IsoTree.Joliet && joliet is null)
        {
            throw new ArchiveUnsupportedException(Name, "a Joliet hierarchy (the image has no Joliet volume descriptor)");
        }

        _volume = new IsoVolumeInfo
        {
            VolumeId = IsoFormat.ReadText(p.Slice(40, 32)),
            SystemId = IsoFormat.ReadText(p.Slice(8, 32)),
            VolumeSetId = IsoFormat.ReadText(p.Slice(190, 128)),
            PublisherId = IsoFormat.ReadText(p.Slice(318, 128)),
            PreparerId = IsoFormat.ReadText(p.Slice(446, 128)),
            ApplicationId = IsoFormat.ReadText(p.Slice(574, 128)),
            CreationTime = IsoFormat.ParseDate17(p.Slice(813, 17)),
            ModificationTime = IsoFormat.ParseDate17(p.Slice(830, 17)),
            VolumeSpaceBlocks = spaceBlocks,
            HasJoliet = joliet is not null,
            JolietLevel = jolietLevel,
            HasRockRidge = hasRockRidge,
            Extensions = rootSys?.ExtensionIds ?? [],
            BootCatalogBlock = boot,
        };

        if (tree == IsoTree.Joliet)
        {
            (long jExtent, long jLength) = ReadRootRecord(joliet!);
            Walk(IsoTree.Joliet, jExtent, jLength, rr: false, skip: 0);
        }
        else
        {
            Walk(IsoTree.Primary, rootExtent, rootLength, useRr, rootSys?.SkipLength ?? 0);
        }
    }

    // Clause 8.4.18: the root directory record sits at byte 156 of the descriptor and is 34 bytes long.
    private (long Extent, long Length) ReadRootRecord(Descriptor d)
    {
        ReadOnlySpan<byte> r = d.Data.AsSpan(156, 34);
        long at = d.Offset + 156;
        if (r[0] != 34 || r[32] != 1 || r[33] != 0)
        {
            throw IsoFormat.Bad(at, "a 34-byte root directory record with the identifier 0x00");
        }

        if ((r[25] & (byte)IsoRecordAttributes.Directory) == 0)
        {
            throw IsoFormat.Bad(at + 25, "the directory flag in the root directory record");
        }

        long extent = IsoFormat.ReadBoth32(r[2..], at + 2, "the root directory's extent");
        long length = IsoFormat.ReadBoth32(r[10..], at + 10, "the root directory's length");
        CheckDirectoryExtent(extent, length, at);
        return (extent, length);
    }

    private void CheckDirectoryExtent(long extent, long length, long recordOffset)
    {
        if (length < IsoFormat.RecordFixedLength + 1)
        {
            throw IsoFormat.Bad(recordOffset, $"a directory of at least one record (length {length})");
        }

        if (length > _options.MaxDirectorySize)
        {
            throw new CompressionLimitException(Name, nameof(IsoReaderOptions.MaxDirectorySize), _options.MaxDirectorySize, length);
        }

        if ((extent * IsoFormat.SectorSize) + length > _total)
        {
            throw IsoFormat.Bad(recordOffset, $"a directory extent inside the image (block {extent}, {length} bytes; the image is {_total} bytes)", truncated: _total > 0);
        }
    }

    // ---- directories (clause 9.1) ----

    private sealed class Rec
    {
        public long Offset;
        public IsoRecordAttributes Flags;
        public byte[] Ident = [];
        public List<IsoExtent> Extents = [];
        public long Length;
        public DateTimeOffset? Time;
        public SystemUseInfo? Sys;
        public long FirstExtent;
        public long FirstLength;
    }

    private sealed class Frame(List<Rec> recs, string namePrefix, string isoPrefix, int depth)
    {
        public List<Rec> Recs = recs;
        public int Next;
        public string NamePrefix = namePrefix;
        public string IsoPrefix = isoPrefix;
        public int Depth = depth;
    }

    private byte[] ReadDirectoryBytes(long extent, long length, long recordOffset)
    {
        CheckDirectoryExtent(extent, length, recordOffset);
        byte[] buffer = new byte[length];
        ReadAt(extent * IsoFormat.SectorSize, buffer, "a directory");
        return buffer;
    }

    // Reads the root's own record to learn whether the SUSP is in use, before the real walk.
    private SystemUseInfo? ProbeRoot(long extent, long length)
    {
        byte[] buffer = ReadDirectoryBytes(extent, length, extent * IsoFormat.SectorSize);
        return ParseDirectory(buffer, extent, rr: false, skip: 0, probeDot: true).Dot?.Sys;
    }

    private (Rec? Dot, List<Rec> Children) ParseDirectory(byte[] buffer, long extent, bool rr, int skip, bool probeDot)
    {
        long baseOffset = extent * IsoFormat.SectorSize;
        var children = new List<Rec>();
        Rec? dot = null;
        Rec? pending = null;
        int pos = 0;
        int index = 0;
        while (pos < buffer.Length)
        {
            int len = buffer[pos];
            if (len == 0)
            {
                // Clause 9.1: a record never crosses a sector; the rest of the sector is zeros.
                pos = ((pos / IsoFormat.SectorSize) + 1) * IsoFormat.SectorSize;
                continue;
            }

            long at = baseOffset + pos;
            if (len < IsoFormat.RecordFixedLength + 1 || pos + len > buffer.Length || (pos % IsoFormat.SectorSize) + len > IsoFormat.SectorSize)
            {
                throw IsoFormat.Bad(at, $"a directory record of at least 34 bytes that ends inside its sector and its directory (length {len})");
            }

            ReadOnlySpan<byte> r = buffer.AsSpan(pos, len);
            int idLen = r[32];
            if (IsoFormat.RecordFixedLength + idLen > len || idLen == 0)
            {
                throw IsoFormat.Bad(at + 32, $"a file identifier of 1 to {len - IsoFormat.RecordFixedLength} bytes (length {idLen})");
            }

            long recExtent = IsoFormat.ReadBoth32(r[2..], at + 2, "the extent location");
            long recLength = IsoFormat.ReadBoth32(r[10..], at + 10, "the data length");
            var flags = (IsoRecordAttributes)r[25];
            int extAttr = r[1];
            byte[] ident = r.Slice(33, idLen).ToArray();
            int sysStart = IsoFormat.RecordFixedLength + idLen + ((idLen & 1) == 0 ? 1 : 0);
            index++;
            pos += len;

            SystemUseInfo? sys = null;
            bool rootDot = probeDot && index == 1;
            if ((rr || rootDot) && sysStart < len)
            {
                sys = SystemUseParser.Parse(r[sysStart..], rootDot ? 0 : skip, at + sysStart, _options.MaxSystemUseSize, ReadContinuation);
            }

            var rec = new Rec
            {
                Offset = at,
                Flags = flags,
                Ident = ident,
                Length = recLength,
                Time = IsoFormat.ParseDate7(r.Slice(18, 7)),
                Sys = sys,
                FirstExtent = recExtent + extAttr,
                FirstLength = recLength,
            };
            if (recLength > 0 || (flags & IsoRecordAttributes.Directory) != 0)
            {
                rec.Extents.Add(new IsoExtent(recExtent + extAttr, recLength));
            }

            if (idLen == 1 && ident[0] <= 1 && (flags & IsoRecordAttributes.Directory) != 0 && pending is null)
            {
                // "." and ".." (clause 9.1.11), always the first two records.
                if (ident[0] == 0)
                {
                    dot = rec;
                }

                continue;
            }

            if (pending is not null)
            {
                // Clause 9.1.6 and 9.2: the next record of the same name continues a multi-extent file.
                if (!ident.AsSpan().SequenceEqual(pending.Ident))
                {
                    throw IsoFormat.Bad(at, "the continuation of a multi-extent file under the same file identifier");
                }

                pending.Extents.AddRange(rec.Extents);
                pending.Length += rec.Length;
                if ((flags & IsoRecordAttributes.MultiExtent) == 0)
                {
                    pending = null;
                }

                continue;
            }

            children.Add(rec);
            if ((flags & IsoRecordAttributes.MultiExtent) != 0)
            {
                pending = rec;
            }
        }

        if (pending is not null)
        {
            throw IsoFormat.Bad(baseOffset + buffer.Length, "the last extent of a multi-extent file (the directory ends first)");
        }

        return (dot, children);
    }

    // SUSP 5.1: a continuation area lies in the image, is read once per use, and counts against the budget for the whole image.
    private byte[] ReadContinuation(long block, long offset, long length, long recordOffset)
    {
        long start = (block * IsoFormat.SectorSize) + offset;
        if (offset >= IsoFormat.SectorSize || start + length > _total)
        {
            throw IsoFormat.Bad(recordOffset, $"a continuation area inside the image (block {block}, offset {offset}, length {length})");
        }

        _continuationBytes += length;
        if (_continuationBytes > _options.MaxContinuationBytes)
        {
            throw new CompressionLimitException(Name, nameof(IsoReaderOptions.MaxContinuationBytes), _options.MaxContinuationBytes, _continuationBytes);
        }

        byte[] area = new byte[length];
        ReadAt(start, area, "a continuation area");
        return area;
    }

    // ---- the walk ----

    private void Walk(IsoTree tree, long rootExtent, long rootLength, bool rr, int skip)
    {
        var items = new List<IsoEntry>();
        var visited = new HashSet<long> { rootExtent };
        var stack = new Stack<Frame>();
        List<Rec> rootChildren = ParseDirectory(ReadDirectoryBytes(rootExtent, rootLength, rootExtent * IsoFormat.SectorSize), rootExtent, rr, skip, probeDot: false).Children;
        stack.Push(new Frame(rootChildren, string.Empty, string.Empty, 0));
        while (stack.Count > 0)
        {
            Frame frame = stack.Peek();
            if (frame.Next >= frame.Recs.Count)
            {
                stack.Pop();
                continue;
            }

            Rec rec = frame.Recs[frame.Next++];
            SystemUseInfo? sys = rec.Sys;
            if (rr && sys is { Relocated: true })
            {
                // RRIP 4.1.5.3: the real position of a moved directory; it is listed where its "CL" placeholder is.
                continue;
            }

            bool isDirectory = (rec.Flags & IsoRecordAttributes.Directory) != 0 || (rr && sys is { ChildLink: not null });
            string identText = tree == IsoTree.Joliet ? DecodeJoliet(rec) : Encoding.Latin1.GetString(rec.Ident);
            if (identText.Length == 0 || identText.Contains('/', StringComparison.Ordinal) || identText.Contains('\0', StringComparison.Ordinal))
            {
                throw IsoFormat.Bad(rec.Offset + 33, "a file identifier without '/' or NUL");
            }

            string isoComp = IsoFormat.StripVersion(identText, isDirectory);
            string nameComp = isoComp;
            if (rr && sys?.Name is { Length: > 0 } nm)
            {
                nameComp = PathRules.DecodeName(nm);
                if (nameComp.Contains('/', StringComparison.Ordinal) || nameComp.Contains('\0', StringComparison.Ordinal))
                {
                    throw IsoFormat.Bad(rec.Offset, "a Rock Ridge name (NM) without '/' or NUL");
                }
            }

            string name = frame.NamePrefix + nameComp;
            string isoPath = frame.IsoPrefix + isoComp;
            if (items.Count >= Limits.MaxEntryCount)
            {
                throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxEntryCount), Limits.MaxEntryCount, items.Count + 1);
            }

            if (name.Length > Limits.MaxPathLength)
            {
                throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxPathLength), Limits.MaxPathLength, name.Length);
            }

            List<Rec>? childRecs = null;
            Rec? childDot = null;
            bool movedDirectory = rr && sys is { ChildLink: not null };
            IsoExtent[] extents;
            if (isDirectory)
            {
                long extent = rec.FirstExtent;
                long length = rec.FirstLength;
                if (rr && sys is { ChildLink: { } moved })
                {
                    // The placeholder's own extent is meaningless; the directory is where "CL" says, and its length is in its "." record.
                    extent = moved;
                    byte[] first = new byte[IsoFormat.RecordFixedLength + 1];
                    ReadAt(extent * IsoFormat.SectorSize, first, "the relocated directory's first record");
                    length = IsoFormat.ReadBoth32(first.AsSpan(10), (extent * IsoFormat.SectorSize) + 10, "the relocated directory's length");
                }

                if (frame.Depth + 1 > Limits.MaxPathDepth)
                {
                    throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxPathDepth), Limits.MaxPathDepth, frame.Depth + 1);
                }

                if (!visited.Add(extent))
                {
                    throw IsoFormat.Bad(rec.Offset, $"a directory that is reached once (block {extent} is a directory loop or is listed twice)");
                }

                byte[] bytes = ReadDirectoryBytes(extent, length, rec.Offset);
                (childDot, childRecs) = ParseDirectory(bytes, extent, rr, skip, probeDot: false);
                extents = [new IsoExtent(extent, length)];
                if (rr && sys is { ChildLink: not null } && childDot?.Sys is { } movedSys)
                {
                    sys = movedSys;
                }
                else if (rr && sys is not { HasPx: true } && childDot?.Sys is { HasPx: true } dotSys)
                {
                    sys = dotSys;
                }

                if (rr && frame.Depth == 0 && childRecs.Count > 0 && childRecs.TrueForAll(static c => c.Sys is { Relocated: true }))
                {
                    // The holding directory for relocated directories (RRIP 4.1.5.3) is not part of the user's tree.
                    continue;
                }
            }
            else
            {
                extents = [.. rec.Extents];
                CheckFileExtents(extents, rec.Offset);
            }

            IsoEntry entry = BuildEntry(tree, rec, sys, name, isoPath, identText, isDirectory, extents, movedDirectory);
            items.Add(entry);
            if (isDirectory)
            {
                stack.Push(new Frame(childRecs!, name + "/", isoPath + "/", frame.Depth + 1));
            }
        }

        _items = items;
    }

    private static string DecodeJoliet(Rec rec)
    {
        if ((rec.Ident.Length & 1) != 0)
        {
            throw IsoFormat.Bad(rec.Offset + 32, "a Joliet file identifier of an even number of bytes (UCS-2)");
        }

        return Encoding.BigEndianUnicode.GetString(rec.Ident);
    }

    private void CheckFileExtents(IsoExtent[] extents, long recordOffset)
    {
        foreach (IsoExtent x in extents)
        {
            if (x.Length > 0 && ((x.Location * IsoFormat.SectorSize) + x.Length > _total))
            {
                throw IsoFormat.Bad(recordOffset, $"a file extent inside the image (block {x.Location}, {x.Length} bytes; the image is {_total} bytes)", truncated: true);
            }
        }
    }

    private static IsoEntry BuildEntry(IsoTree tree, Rec rec, SystemUseInfo? sys, string name, string isoPath, string identifier, bool isDirectory, IsoExtent[] extents, bool movedDirectory)
    {
        ArchiveEntryKind kind = ArchiveEntryKind.File;
        UnixFileMode? mode = null;
        string? link = null;
        long major = 0;
        long minor = 0;
        if (isDirectory)
        {
            kind = ArchiveEntryKind.Directory;
        }

        if (sys is { HasPx: true })
        {
            mode = (UnixFileMode)(sys.Mode & 0xFFF);
            if (!isDirectory)
            {
                switch (sys.Mode & 0xF000)
                {
                    case 0xA000:
                        kind = ArchiveEntryKind.Symlink;
                        link = sys.LinkTarget ?? string.Empty;
                        break;
                    case 0x2000:
                        kind = ArchiveEntryKind.CharacterDevice;
                        break;
                    case 0x6000:
                        kind = ArchiveEntryKind.BlockDevice;
                        break;
                    case 0x1000:
                    case 0xC000:
                        kind = ArchiveEntryKind.Fifo;
                        break;
                    default:
                        break;
                }
            }
        }

        if (kind is ArchiveEntryKind.CharacterDevice or ArchiveEntryKind.BlockDevice && sys?.Device is { } dev)
        {
            // The Linux encoding of a 64-bit device number (makedev(3)).
            major = (long)(((dev >> 32) & 0xFFFFF000UL) | ((dev >> 8) & 0xFFFUL));
            minor = (long)(((dev >> 12) & 0xFFFFFF00UL) | (dev & 0xFFUL));
        }

        long size = 0;
        if (kind == ArchiveEntryKind.File)
        {
            foreach (IsoExtent x in extents)
            {
                size += x.Length;
            }
        }
        else if (kind != ArchiveEntryKind.Directory)
        {
            extents = [];
        }

        IsoRockRidge? rr = sys is { HasRockRidgeEntries: true }
            ? new IsoRockRidge
            {
                RawMode = sys.Mode,
                Links = sys.Links,
                SerialNumber = sys.Serial,
                DeviceNumber = sys.Device ?? 0,
                CreationTime = sys.Creation,
                BackupTime = sys.Backup,
                ExpirationTime = sys.Expiration,
                EffectiveTime = sys.Effective,
                IsRelocated = sys.Relocated,
                IsChildLink = movedDirectory,
                ZisofsSize = sys.ZisofsSize,
            }
            : null;
        return new IsoEntry
        {
            Name = name,
            Kind = kind,
            Size = size,
            ModifiedTime = sys?.Modify ?? rec.Time,
            AccessTime = sys?.Access,
            ChangeTime = sys?.Attributes,
            Mode = mode,
            Uid = sys?.Uid ?? 0,
            Gid = sys?.Gid ?? 0,
            LinkTarget = link,
            DeviceMajor = major,
            DeviceMinor = minor,
            Tree = tree,
            IsoPath = isoPath,
            Identifier = identifier,
            Extents = extents,
            Flags = rec.Flags,
            RecordOffset = rec.Offset,
            RockRidge = rr,
            RecordingTime = rec.Time,
        };
    }
}
