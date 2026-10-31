using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Cfb;

/// <summary>
/// Reads Microsoft Compound File Binary files ([MS-CFB]), the container behind legacy Office documents (.doc, .xls, .ppt), Outlook
/// messages (.msg) and installer packages: versions 3 and 4 (512- and 4096-byte sectors), the DIFAT, FAT and mini FAT, the mini stream and the
/// directory tree of storages and streams.
/// </summary>
/// <remarks>
/// <para>The source must be seekable, or be given as memory: the sector chains are followed by offset. <see cref="Archive"/> copies a source that
/// cannot seek into memory first. Storages are listed as directories and streams as files, parents before their children, siblings in the
/// tree's name order. Names are sanitized (see <see cref="SanitizeName(string)"/>); the stored name is in <see cref="CfbEntry.RawName"/>.</para>
/// <para>The whole structure is validated when the file is first used, before any stream is read: the header, the DIFAT/FAT/mini FAT chains,
/// the directory tree and every stream's sector chain. Sectors may belong to one chain only, so the sizes declared by streams can never add up
/// to more than the file holds. The root storage is available as <see cref="Root"/>; it is not listed.</para>
/// <para>Errors: <see cref="CompressionFormatException"/> names the byte offset and what was expected; a limit raises
/// <see cref="CompressionLimitException"/>. A reader is not thread-safe.</para>
/// </remarks>
public sealed class CompoundFileReader : ArchiveReader
{
    internal const string Name = "cfb";

    private const int HeaderLength = 512;
    private const uint MaxRegSect = 0xFFFFFFFA;
    private const uint DifSect = 0xFFFFFFFC;
    private const uint FatSect = 0xFFFFFFFD;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;
    private const uint NoStream = 0xFFFFFFFF;
    private const int DirEntrySize = 128;
    private const int MiniSectorSize = 64;
    private const long FileTimeToDateTimeTicks = 504911232000000000L;
    private const long MaxFileTime = 2650467743999999999L;

    private static readonly byte[] s_signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly Source _source;
    private readonly bool _leaveOpen;
    private readonly CompoundFileReaderOptions _options;
    private bool _loaded;
    private int _next;

    private CompoundFileHeader _header = new();
    private int _shift;
    private int _sectorSize;
    private uint _sectorCount;
    private bool _v4;
    private uint[] _fat = [];
    private uint[] _fatSectors = [];
    private uint[] _dirSectors = [];
    private uint[] _miniFat = [];
    private uint[] _miniFatSectors = [];
    private uint[] _miniStream = [];
    private long _miniStreamSize;
    private ulong[] _claimed = [];
    private ulong[] _miniClaimed = [];
    private DirEntry[] _dir = [];
    private int[] _order = [];
    private Dictionary<int, int[]> _children = [];
    private CfbEntry? _root;

    /// <summary>Creates a reader over a seekable stream positioned at the start of the compound file.</summary>
    /// <param name="stream">A readable, seekable stream.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the reader is disposed.</param>
    /// <exception cref="ArgumentException">The stream cannot read or seek.</exception>
    public CompoundFileReader(Stream stream, CompoundFileReaderOptions? options = null, bool leaveOpen = false)
        : base(ArchiveFormat.CompoundFile, (options ?? CompoundFileReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable: a compound file's sector chains are followed by offset.", nameof(stream));
        }

        _source = new Source(stream);
        _leaveOpen = leaveOpen;
        _options = options ?? CompoundFileReaderOptions.Default;
    }

    /// <summary>Creates a reader over a compound file held in memory. The memory must not change while the reader is in use.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    public CompoundFileReader(ReadOnlyMemory<byte> data, CompoundFileReaderOptions? options = null)
        : base(ArchiveFormat.CompoundFile, (options ?? CompoundFileReaderOptions.Default).Limits)
    {
        _source = new Source(data);
        _leaveOpen = true;
        _options = options ?? CompoundFileReaderOptions.Default;
    }

    /// <inheritdoc />
    protected override string FormatName => Name;

    /// <inheritdoc />
    public override bool SupportsRandomAccess => true;

    /// <summary>Gets the header.</summary>
    /// <exception cref="CompressionFormatException">The file is malformed.</exception>
    public CompoundFileHeader Header
    {
        get
        {
            EnsureLoaded();
            return _header;
        }
    }

    /// <summary>Gets the root storage (its name is empty, its path is the archive root). It carries the root's CLSID, state bits and times.</summary>
    /// <exception cref="CompressionFormatException">The file is malformed.</exception>
    public CfbEntry Root
    {
        get
        {
            EnsureLoaded();
            return _root!;
        }
    }

    /// <summary>Reads the next entry.</summary>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new CfbEntry? GetNextEntry() => (CfbEntry?)base.GetNextEntry();

    /// <summary>Reads the next entry.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new async ValueTask<CfbEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (CfbEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Gets the direct children of a storage, in name order.</summary>
    /// <param name="storage">The root or a storage returned by this reader.</param>
    /// <returns>The children; empty for a stream or an empty storage.</returns>
    /// <exception cref="ArgumentException">The entry did not come from this reader.</exception>
    public IReadOnlyList<CfbEntry> GetChildren(CfbEntry storage)
    {
        int sid = Resolve(storage);
        if (!_children.TryGetValue(sid, out int[]? kids))
        {
            return [];
        }

        var list = new List<CfbEntry>(kids.Length);
        foreach (int k in kids)
        {
            list.Add(MakeEntry(k));
        }

        return list;
    }

    /// <summary>Finds an entry by its stored names, comparing them as [MS-CFB] 2.6.4 does (case-insensitively, one UTF-16 code unit at a time).</summary>
    /// <param name="path">The stored (raw, unsanitized) names from the root, separated by <c>/</c>; empty for the root. A stored name that itself contains <c>/</c> cannot be addressed this way; use <see cref="GetChildren"/>.</param>
    /// <returns>The entry, or <see langword="null"/> if there is none.</returns>
    public CfbEntry? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        EnsureLoaded();
        int sid = 0;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!_children.TryGetValue(sid, out int[]? kids))
            {
                return null;
            }

            int lo = 0, hi = kids.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int c = CompareNames(part, _dir[kids[mid]].Name);
                if (c == 0)
                {
                    found = kids[mid];
                    break;
                }

                if (c < 0)
                {
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            if (found < 0)
            {
                return null;
            }

            sid = found;
        }

        return sid == 0 ? _root : MakeEntry(sid);
    }

    /// <summary>Opens a stream by its stored names (see <see cref="Find"/>).</summary>
    /// <param name="path">The stored names from the root, separated by <c>/</c>.</param>
    /// <returns>A read-only, seekable stream over the content.</returns>
    /// <exception cref="FileNotFoundException">There is no stream at the path.</exception>
    public Stream OpenStream(string path)
    {
        CfbEntry e = Find(path) ?? throw new FileNotFoundException($"cfb: there is no entry '{path}'.");
        if (e.ObjectType == CfbObjectType.Storage)
        {
            throw new FileNotFoundException($"cfb: '{path}' is a storage, not a stream.");
        }

        return OpenEntryStream(e);
    }

    /// <summary>
    /// Turns one stored name into a name that is safe as a path component on every platform. Each of these UTF-16 code units is written as
    /// <c>%</c> and four uppercase hex digits: controls (U+0000-U+001F, U+007F-U+009F), <c>/ \ : * ? " &lt; &gt; | %</c>, and unpaired surrogates. A name
    /// that is <c>.</c> or <c>..</c> has its first character escaped, and a name ending in <c>.</c> or a space has its last character escaped.
    /// The mapping is one-to-one, so two different stored names never collide.
    /// </summary>
    /// <param name="name">A stored name.</param>
    /// <returns>The sanitized component.</returns>
    public static string SanitizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool escape = c < 0x20 || (c >= 0x7F && c <= 0x9F) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '%';
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                sb.Append(c).Append(name[++i]);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                escape = true;
            }

            if (!escape && ((name is "." or ".." && i == 0) || (i == name.Length - 1 && (c == '.' || c == ' '))))
            {
                escape = true;
            }

            if (escape)
            {
                sb.Append('%').Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    protected override ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        return new ValueTask<ArchiveEntry?>(_next < _order.Length ? MakeEntry(_order[_next++]) : null);
    }

    /// <inheritdoc />
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        return OpenEntryStream(CurrentEntry ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first."));
    }

    /// <inheritdoc />
    public override Stream OpenEntryStream(ArchiveEntry entry)
    {
        ThrowIfDisposed();
        int sid = Resolve(entry);
        ref DirEntry d = ref _dir[sid];
        if (d.Type == (byte)CfbObjectType.Storage || sid == 0 || d.Size == 0)
        {
            return new CfbStream(this, [], 0, false);
        }

        bool mini = d.Size < _header.MiniStreamCutoff;
        long unit = mini ? MiniSectorSize : _sectorSize;
        long needed = (d.Size + unit - 1) / unit;
        var chain = new List<uint>((int)Math.Min(needed, 1 << 20));
        if (mini)
        {
            WalkMiniChain(d.Start, "mini stream", DirOffset(sid) + 0x74, chain, claim: false, needed);
        }
        else
        {
            WalkChain(d.Start, "stream", DirOffset(sid) + 0x74, chain, claim: false, needed);
        }

        return new CfbStream(this, chain.ToArray(), d.Size, mini);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed && !_leaveOpen)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---- Reading stream bytes (used by CfbStream) ----

    internal int ReadStream(uint[] chain, bool mini, long position, Span<byte> destination)
    {
        if (!mini)
        {
            int si = (int)(position >> _shift);
            int off = (int)(position & (_sectorSize - 1));
            int run = 1;
            while (si + run < chain.Length && chain[si + run] == chain[si] + (uint)run && (long)run * _sectorSize - off < destination.Length)
            {
                run++;
            }

            int n = (int)Math.Min(destination.Length, (long)run * _sectorSize - off);
            _source.ReadAt((((long)chain[si] + 1) << _shift) + off, destination[..n]);
            return n;
        }

        long mo = (long)chain[(int)(position / MiniSectorSize)] * MiniSectorSize + (position % MiniSectorSize);
        int piece = (int)Math.Min(destination.Length, MiniSectorSize - (position % MiniSectorSize));
        uint ms = _miniStream[(int)(mo >> _shift)];
        _source.ReadAt((((long)ms + 1) << _shift) + (mo & (_sectorSize - 1)), destination[..piece]);
        return piece;
    }

    // ---- Loading ----

    private static CompressionFormatException Bad(long offset, string expected, bool truncated = false) => new(Name, offset, expected, truncated);

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        Load();
        _loaded = true;
    }

    private int Resolve(ArchiveEntry entry)
    {
        EnsureLoaded();
        ArgumentNullException.ThrowIfNull(entry);
        if (entry is not CfbEntry e || !ReferenceEquals(e.Owner, this) || e.StreamId < 0 || e.StreamId >= _dir.Length)
        {
            throw new ArgumentException("The entry was not read by this CompoundFileReader.", nameof(entry));
        }

        return e.StreamId;
    }

    // [MS-CFB] 2.2: the header, then 2.5, 2.3, 2.6 in the order the structures depend on each other.
    private void Load()
    {
        long length = _source.Length;
        if (length < HeaderLength)
        {
            throw Bad(length, "a 512-byte compound file header", truncated: true);
        }

        byte[] h = new byte[HeaderLength];
        _source.ReadAt(0, h);
        if (!h.AsSpan(0, 8).SequenceEqual(s_signature))
        {
            throw Bad(0, "the compound file signature D0 CF 11 E0 A1 B1 1A E1");
        }

        int minor = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(24));
        int major = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(26));
        if (major != 3 && major != 4)
        {
            throw Bad(26, $"major version 3 or 4 (found {major})");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(28)) != 0xFFFE)
        {
            throw Bad(28, "the little-endian byte order mark 0xFFFE");
        }

        int shift = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(30));
        if (shift != (major == 3 ? 9 : 12))
        {
            throw Bad(30, $"sector shift {(major == 3 ? 9 : 12)} for major version {major} (found {shift})");
        }

        int miniShift = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(32));
        if (miniShift != 6)
        {
            throw Bad(32, $"mini sector shift 6 (found {miniShift})");
        }

        uint cutoff = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(56));
        if (cutoff != 4096)
        {
            throw Bad(56, $"a mini stream cutoff size of 4096 (found {cutoff})");
        }

        uint numFat = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(44));
        uint firstDir = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(48));
        uint firstMini = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(60));
        uint numMini = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(64));
        uint firstDif = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(68));
        uint numDif = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(72));
        var hdrDifat = new uint[109];
        for (int i = 0; i < 109; i++)
        {
            hdrDifat[i] = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(76 + (i * 4)));
        }

        _header = new CompoundFileHeader
        {
            MajorVersion = major,
            MinorVersion = minor,
            SectorShift = shift,
            MiniSectorShift = miniShift,
            HeaderClassId = new Guid(h.AsSpan(8, 16)),
            DirectorySectorCount = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(40)),
            FatSectorCount = numFat,
            FirstDirectorySector = firstDir,
            TransactionSignature = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(52)),
            MiniStreamCutoff = cutoff,
            FirstMiniFatSector = firstMini,
            MiniFatSectorCount = numMini,
            FirstDifatSector = firstDif,
            DifatSectorCount = numDif,
            HeaderDifat = hdrDifat,
        };

        _v4 = major == 4;
        _shift = shift;
        _sectorSize = 1 << shift;
        long sectors = Math.Max(0, (length >> shift) - 1);
        _sectorCount = (uint)Math.Min(sectors, MaxRegSect);
        _claimed = new ulong[(_sectorCount + 63) / 64];
        int perSector = _sectorSize / 4;

        // [MS-CFB] 2.3 and 2.5: the FAT sectors are located by the DIFAT (109 entries in the header, then the DIFAT chain).
        if (numFat == 0)
        {
            throw Bad(44, "at least one FAT sector");
        }

        if (numFat > _sectorCount)
        {
            throw Bad(44, $"no more FAT sectors than the file has sectors ({_sectorCount}; found {numFat})", truncated: true);
        }

        if (numDif > _sectorCount)
        {
            throw Bad(72, $"no more DIFAT sectors than the file has sectors ({_sectorCount}; found {numDif})", truncated: true);
        }

        _fatSectors = new uint[numFat];
        int have = 0;
        for (int i = 0; i < 109 && have < numFat; i++, have++)
        {
            _fatSectors[have] = CheckSector(hdrDifat[i], 76 + (i * 4), "a FAT sector location in the header's DIFAT");
        }

        byte[] buf = new byte[_sectorSize];
        uint link = firstDif;
        long linkAt = 68;
        for (uint k = 0; k < numDif; k++)
        {
            uint dif = CheckSector(link, linkAt, "a DIFAT sector location");
            Claim(dif, linkAt, "a DIFAT sector");
            ReadSector(dif, buf);
            for (int i = 0; i < perSector - 1 && have < numFat; i++, have++)
            {
                _fatSectors[have] = CheckSector(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4)), (((long)dif + 1) << shift) + (i * 4), "a FAT sector location in a DIFAT sector");
            }

            // The last field of a DIFAT sector links to the next one.
            linkAt = (((long)dif + 1) << shift) + _sectorSize - 4;
            link = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(_sectorSize - 4));
        }

        if (numDif > 0 && link != EndOfChain)
        {
            throw Bad(linkAt, $"the last DIFAT sector to end the chain with ENDOFCHAIN ({numDif} DIFAT sectors are declared; found 0x{link:X8})");
        }

        if (have < numFat)
        {
            throw Bad(72, $"{numFat} FAT sector locations (the header and {numDif} DIFAT sectors hold only {have})");
        }

        _fat = new uint[(long)numFat * perSector];
        for (int f = 0; f < numFat; f++)
        {
            Claim(_fatSectors[f], ((long)_fatSectors[f] + 1) << shift, "a FAT sector");
            ReadSector(_fatSectors[f], buf);
            for (int i = 0; i < perSector; i++)
            {
                _fat[((long)f * perSector) + i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4));
            }
        }

        // [MS-CFB] 2.6: the directory is a chain of 128-byte entries.
        var dirChain = new List<uint>();
        WalkChain(firstDir, "directory", 48, dirChain, claim: true, long.MaxValue);
        if (dirChain.Count == 0)
        {
            throw Bad(48, "a directory chain of at least one sector");
        }

        long entriesPer = _sectorSize / DirEntrySize;
        long dirCount = dirChain.Count * entriesPer;
        if (dirCount > _options.MaxDirectoryEntries)
        {
            throw new CompressionLimitException(Name, nameof(CompoundFileReaderOptions.MaxDirectoryEntries), _options.MaxDirectoryEntries, dirCount);
        }

        _dirSectors = dirChain.ToArray();
        _dir = new DirEntry[dirCount];
        for (int s = 0; s < _dirSectors.Length; s++)
        {
            ReadSector(_dirSectors[s], buf);
            for (int e = 0; e < entriesPer; e++)
            {
                int sid = (s * (int)entriesPer) + e;
                ParseDirEntry(buf.AsSpan(e * DirEntrySize, DirEntrySize), sid, ref _dir[sid]);
            }
        }

        if (_dir[0].Type != (byte)CfbObjectType.RootStorage)
        {
            throw Bad(DirOffset(0) + 0x42, "the root storage (object type 5) as the first directory entry");
        }

        // [MS-CFB] 2.4: the mini FAT.
        var miniChain = new List<uint>();
        if (numMini > 0 || firstMini != EndOfChain)
        {
            WalkChain(firstMini, "mini FAT", 60, miniChain, claim: true, long.MaxValue);
        }

        if (miniChain.Count != numMini)
        {
            throw Bad(64, $"{numMini} mini FAT sectors in the chain from the first mini FAT sector (found {miniChain.Count})");
        }

        _miniFatSectors = miniChain.ToArray();
        _miniFat = new uint[(long)miniChain.Count * perSector];
        for (int m = 0; m < miniChain.Count; m++)
        {
            ReadSector(miniChain[m], buf);
            for (int i = 0; i < perSector; i++)
            {
                _miniFat[((long)m * perSector) + i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4));
            }
        }

        _miniClaimed = new ulong[(_miniFat.Length + 63) / 64];

        // [MS-CFB] 2.6.2: the root entry holds the mini stream. In version 3 files only the low 32 bits of a size count (2.6.3).
        _miniStreamSize = _dir[0].Size;
        var ministream = new List<uint>();
        if (_miniStreamSize > 0)
        {
            WalkChain(_dir[0].Start, "mini stream", DirOffset(0) + 0x74, ministream, claim: true, long.MaxValue);
            long cap = (long)ministream.Count * _sectorSize;
            if (cap < _miniStreamSize)
            {
                throw Bad(DirOffset(0) + 0x78, $"a mini stream chain of at least {_miniStreamSize} bytes (the chain holds {cap})");
            }
        }

        _miniStream = ministream.ToArray();

        BuildTree();
        ValidateStreams();
        _root = MakeEntry(0);
    }

    private uint CheckSector(uint s, long at, string what)
    {
        if (s >= _sectorCount)
        {
            throw Bad(at, $"{what} inside the file (the file has {_sectorCount} sectors after the header; found 0x{s:X8})", truncated: s <= MaxRegSect);
        }

        return s;
    }

    private void Claim(uint sector, long at, string what)
    {
        ref ulong word = ref _claimed[sector >> 6];
        ulong bit = 1UL << (int)(sector & 63);
        if ((word & bit) != 0)
        {
            throw Bad(at, $"{what} in sector {sector} that no other chain uses (sectors are used twice, or a chain loops)");
        }

        word |= bit;
    }

    private void ReadSector(uint sector, Span<byte> destination) => _source.ReadAt(((long)sector + 1) << _shift, destination[.._sectorSize]);

    private long DirOffset(int sid)
    {
        int entriesPer = _sectorSize / DirEntrySize;
        return ((_dirSectors[sid / entriesPer] + 1L) << _shift) + ((sid % entriesPer) * (long)DirEntrySize);
    }

    private long FatEntryOffset(uint sector)
    {
        int perSector = _sectorSize / 4;
        return ((_fatSectors[(int)(sector / perSector)] + 1L) << _shift) + ((sector % perSector) * 4L);
    }

    // [MS-CFB] 2.1 and 2.3: follow a chain through the FAT. Each sector may be used once, which also stops loops.
    private long WalkChain(uint start, string what, long startAt, List<uint>? collect, bool claim, long stopAfter)
    {
        uint cur = start;
        long at = startAt;
        long count = 0;
        while (cur != EndOfChain && count < stopAfter)
        {
            if (cur >= _sectorCount)
            {
                throw Bad(at, $"the {what} chain to stay inside the file (the file has {_sectorCount} sectors after the header; found sector 0x{cur:X8}{Special(cur)})", truncated: cur <= MaxRegSect);
            }

            if (claim)
            {
                Claim(cur, at, $"the {what} chain");
            }

            collect?.Add(cur);
            count++;
            if (cur >= _fat.Length)
            {
                throw Bad(at, $"sector {cur} of the {what} chain to be covered by the FAT ({_fat.Length} entries)");
            }

            at = FatEntryOffset(cur);
            cur = _fat[cur];
        }

        return count;
    }

    private static string Special(uint s) => s switch
    {
        FreeSect => " (a free sector)",
        FatSect => " (a FAT sector)",
        DifSect => " (a DIFAT sector)",
        _ => string.Empty,
    };

    // [MS-CFB] 2.4: mini sector chains run through the mini FAT; each mini sector lies inside the mini stream.
    private long WalkMiniChain(uint start, string what, long startAt, List<uint>? collect, bool claim, long stopAfter)
    {
        uint cur = start;
        long at = startAt;
        long count = 0;
        long limit = (_miniStreamSize + MiniSectorSize - 1) / MiniSectorSize;
        while (cur != EndOfChain && count < stopAfter)
        {
            if (cur >= limit || cur >= _miniFat.Length)
            {
                throw Bad(at, $"the {what} chain to stay inside the mini stream ({limit} mini sectors; found 0x{cur:X8}{Special(cur)})");
            }

            if (claim)
            {
                ref ulong word = ref _miniClaimed[cur >> 6];
                ulong bit = 1UL << (int)(cur & 63);
                if ((word & bit) != 0)
                {
                    throw Bad(at, $"the {what} chain in mini sector {cur} that no other chain uses (mini sectors are used twice, or a chain loops)");
                }

                word |= bit;
            }

            collect?.Add(cur);
            count++;
            at = MiniFatEntryOffset(cur);
            cur = _miniFat[cur];
        }

        return count;
    }

    private long MiniFatEntryOffset(uint mini)
    {
        int perSector = _sectorSize / 4;
        return ((_miniFatSectors[(int)(mini / perSector)] + 1L) << _shift) + ((mini % perSector) * 4L);
    }

    // [MS-CFB] 2.6.1: one 128-byte entry.
    private void ParseDirEntry(ReadOnlySpan<byte> b, int sid, ref DirEntry d)
    {
        byte type = b[0x42];
        long at = DirOffset(sid);
        if (type != 0 && type != 1 && type != 2 && type != 5)
        {
            throw Bad(at + 0x42, $"object type 0, 1, 2 or 5 (found {type})");
        }

        d.Type = type;
        d.Left = BinaryPrimitives.ReadUInt32LittleEndian(b[0x44..]);
        d.Right = BinaryPrimitives.ReadUInt32LittleEndian(b[0x48..]);
        d.Child = BinaryPrimitives.ReadUInt32LittleEndian(b[0x4C..]);
        if (type == 0)
        {
            return;
        }

        if (b[0x43] > 1)
        {
            throw Bad(at + 0x43, $"color flag 0 or 1 (found {b[0x43]})");
        }

        int cb = BinaryPrimitives.ReadUInt16LittleEndian(b[0x40..]);
        if ((cb & 1) != 0 || cb > 64 || cb < 2)
        {
            throw Bad(at + 0x40, $"an even name length of 2 to 64 bytes including the terminating null (found {cb})");
        }

        int units = (cb / 2) - 1;
        var chars = new char[units];
        for (int i = 0; i < units; i++)
        {
            chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(b[(i * 2)..]);
            if (chars[i] == '\0')
            {
                throw Bad(at + (i * 2), "a name without a null before its declared end");
            }
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(b[(units * 2)..]) != 0)
        {
            throw Bad(at + (units * 2), "a name ending in a UTF-16 null character");
        }

        if (units == 0 && type != 5)
        {
            throw Bad(at + 0x40, "a name of at least one character");
        }

        if (type == 5 && sid != 0)
        {
            throw Bad(at + 0x42, "a root storage only as the first directory entry");
        }

        d.Name = new string(chars);
        d.Color = b[0x43];
        d.Clsid = new Guid(b.Slice(0x50, 16));
        d.State = BinaryPrimitives.ReadUInt32LittleEndian(b[0x60..]);
        d.Created = BinaryPrimitives.ReadUInt64LittleEndian(b[0x64..]);
        d.Modified = BinaryPrimitives.ReadUInt64LittleEndian(b[0x6C..]);
        d.Start = BinaryPrimitives.ReadUInt32LittleEndian(b[0x74..]);
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(b[0x78..]);
        if (!_v4)
        {
            // [MS-CFB] 2.6.1: older writers left the high 32 bits uninitialized in version 3 files; they are ignored.
            size = (uint)size;
        }

        if (size > long.MaxValue)
        {
            throw Bad(at + 0x78, "a stream size below 2^63");
        }

        d.Size = (long)size;
    }

    // [MS-CFB] 2.6.4: the siblings under one storage form a binary search tree ordered by name. The tree is walked with explicit
    // stacks, so its shape (or a cycle in it) cannot exhaust the call stack, and each entry may be reached once.
    private void BuildTree()
    {
        int count = _dir.Length;
        var visited = new ulong[(count + 63) / 64];
        _children = [];
        var order = new List<int>();
        var parents = new int[count];
        var frames = new Stack<(int Parent, int[] Kids, int Index, int Depth)>();
        int[] rootKids = SiblingsOf(0, visited, parents);
        if (rootKids.Length > 0)
        {
            _children[0] = rootKids;
            frames.Push((0, rootKids, 0, 1));
        }

        while (frames.Count > 0)
        {
            (int parent, int[] kids, int index, int depth) = frames.Pop();
            if (index >= kids.Length)
            {
                continue;
            }

            frames.Push((parent, kids, index + 1, depth));
            int sid = kids[index];
            if (depth > Limits.MaxPathDepth)
            {
                throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxPathDepth), Limits.MaxPathDepth, depth);
            }

            if (order.Count >= Limits.MaxEntryCount)
            {
                throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxEntryCount), Limits.MaxEntryCount, order.Count + 1L);
            }

            order.Add(sid);
            if (_dir[sid].Type == (byte)CfbObjectType.Stream && _dir[sid].Child != NoStream)
            {
                throw Bad(DirOffset(sid) + 0x4C, "NOSTREAM as the child of a stream object");
            }

            if (_dir[sid].Type == (byte)CfbObjectType.Storage)
            {
                int[] sub = SiblingsOf(sid, visited, parents);
                if (sub.Length > 0)
                {
                    _children[sid] = sub;
                    frames.Push((sid, sub, 0, depth + 1));
                }
            }
        }

        _order = order.ToArray();
        _parents = parents;
    }

    private int[] _parents = [];

    // In-order walk of the sibling tree under a storage, checking every link before it is followed.
    private int[] SiblingsOf(int storage, ulong[] visited, int[] parents)
    {
        uint child = _dir[storage].Child;
        if (_dir[storage].Type == (byte)CfbObjectType.Stream)
        {
            if (child != NoStream)
            {
                throw Bad(DirOffset(storage) + 0x4C, "NOSTREAM as the child of a stream object");
            }

            return [];
        }

        var result = new List<int>();
        var stack = new Stack<int>();
        uint cur = child;
        long linkAt = DirOffset(storage) + 0x4C;
        while (true)
        {
            while (cur != NoStream)
            {
                int sid = CheckLink(cur, linkAt, visited);
                parents[sid] = storage;
                stack.Push(sid);
                cur = _dir[sid].Left;
                linkAt = DirOffset(sid) + 0x44;
            }

            if (stack.Count == 0)
            {
                break;
            }

            int node = stack.Pop();
            result.Add(node);
            cur = _dir[node].Right;
            linkAt = DirOffset(node) + 0x48;
        }

        // [MS-CFB] 2.6.4: left is less than right; a shorter name is less than a longer one, then by upper-cased UTF-16 code unit.
        for (int i = 1; i < result.Count; i++)
        {
            int c = CompareNames(_dir[result[i - 1]].Name, _dir[result[i]].Name);
            if (c >= 0)
            {
                throw Bad(DirOffset(result[i]), c == 0
                    ? $"unique names among the entries of one storage ('{Printable(_dir[result[i]].Name)}' appears twice)"
                    : $"siblings in the order of [MS-CFB] 2.6.4 ('{Printable(_dir[result[i - 1]].Name)}' should not come before '{Printable(_dir[result[i]].Name)}')");
            }
        }

        return result.ToArray();
    }

    private int CheckLink(uint id, long at, ulong[] visited)
    {
        if (id > 0xFFFFFFF9 || id >= _dir.Length)
        {
            throw Bad(at, $"a directory entry ID below {_dir.Length} or NOSTREAM (found 0x{id:X8})");
        }

        int sid = (int)id;
        byte t = _dir[sid].Type;
        if (t == 0 || t == (byte)CfbObjectType.RootStorage)
        {
            throw Bad(at, $"a link to a storage or stream entry (entry {sid} is {(t == 0 ? "unallocated" : "the root")})");
        }

        ref ulong word = ref visited[sid >> 6];
        ulong bit = 1UL << (sid & 63);
        if ((word & bit) != 0)
        {
            throw Bad(at, $"entry {sid} to be reachable once (the directory tree loops or shares a subtree)");
        }

        word |= bit;
        return sid;
    }

    private static string Printable(string s)
    {
        var chars = (s.Length > 40 ? s[..40] : s).ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }

    // [MS-CFB] 2.6.4: names are compared by length first, then one UTF-16 code unit at a time after simple upper-casing.
    // Surrogates are never upper-cased (char.ToUpperInvariant leaves them alone).
    internal static int CompareNames(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return a.Length < b.Length ? -1 : 1;
        }

        for (int i = 0; i < a.Length; i++)
        {
            char x = char.ToUpperInvariant(a[i]);
            char y = char.ToUpperInvariant(b[i]);
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        return 0;
    }

    // Every reachable stream's chain is walked once: it must end, stay inside the file, share no sector, and hold the declared size.
    private void ValidateStreams()
    {
        foreach (int sid in _order)
        {
            ref DirEntry d = ref _dir[sid];
            if (d.Type != (byte)CfbObjectType.Stream || d.Size == 0)
            {
                continue;
            }

            bool mini = d.Size < _header.MiniStreamCutoff;
            long unit = mini ? MiniSectorSize : _sectorSize;
            long needed = (d.Size + unit - 1) / unit;
            long have = mini
                ? WalkMiniChain(d.Start, "stream", DirOffset(sid) + 0x74, null, claim: true, long.MaxValue)
                : WalkChain(d.Start, "stream", DirOffset(sid) + 0x74, null, claim: true, long.MaxValue);
            if (have < needed)
            {
                throw Bad(DirOffset(sid) + 0x78, $"a {(mini ? "mini " : string.Empty)}sector chain of at least {needed} sectors for the {d.Size}-byte stream '{Printable(d.Name)}' (the chain has {have})");
            }
        }
    }

    // ---- Entries ----

    private string PathOf(int sid)
    {
        var parts = new List<string>();
        int cur = sid;
        while (cur > 0)
        {
            parts.Add(SanitizeName(_dir[cur].Name));
            cur = _parents[cur];
        }

        parts.Reverse();
        return string.Join('/', parts);
    }

    private static DateTimeOffset? FromFileTime(ulong t) =>
        t == 0 || t > (ulong)MaxFileTime ? null : new DateTimeOffset((long)t + FileTimeToDateTimeTicks, TimeSpan.Zero);

    private CfbEntry MakeEntry(int sid)
    {
        ref DirEntry d = ref _dir[sid];
        bool storage = d.Type != (byte)CfbObjectType.Stream;
        return new CfbEntry
        {
            Name = sid == 0 ? string.Empty : PathOf(sid),
            RawName = sid == 0 ? string.Empty : d.Name,
            Kind = storage ? ArchiveEntryKind.Directory : ArchiveEntryKind.File,
            Size = storage ? 0 : d.Size,
            ModifiedTime = FromFileTime(d.Modified),
            CreationTime = FromFileTime(d.Created),
            StreamId = sid,
            ParentStreamId = sid == 0 ? -1 : _parents[sid],
            ObjectType = (CfbObjectType)d.Type,
            Color = (CfbColor)d.Color,
            LeftSiblingId = d.Left,
            RightSiblingId = d.Right,
            ChildId = d.Child,
            ClassId = d.Clsid,
            StateBits = d.State,
            CreationFileTime = d.Created,
            ModifiedFileTime = d.Modified,
            StartSector = d.Start,
            IsInMiniStream = !storage && d.Size > 0 && d.Size < _header.MiniStreamCutoff,
            Owner = this,
        };
    }

    private struct DirEntry
    {
        public string Name;
        public byte Type;
        public byte Color;
        public uint Left;
        public uint Right;
        public uint Child;
        public Guid Clsid;
        public uint State;
        public ulong Created;
        public ulong Modified;
        public uint Start;
        public long Size;
    }

    // Reads bytes at an offset from a stream or from memory; a short read means the file changed or was cut off.
    private sealed class Source : IDisposable
    {
        private readonly Stream? _stream;
        private readonly long _origin;
        private readonly ReadOnlyMemory<byte> _memory;

        public Source(Stream stream)
        {
            _stream = stream;
            _origin = stream.Position;
            Length = stream.Length - _origin;
        }

        public Source(ReadOnlyMemory<byte> memory)
        {
            _memory = memory;
            Length = memory.Length;
        }

        public long Length { get; }

        public void ReadAt(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset > Length - destination.Length)
            {
                throw Bad(offset, $"{destination.Length} bytes inside the file", truncated: true);
            }

            if (_stream is null)
            {
                _memory.Span.Slice((int)offset, destination.Length).CopyTo(destination);
                return;
            }

            _stream.Position = _origin + offset;
            int done = 0;
            while (done < destination.Length)
            {
                int n = _stream.Read(destination[done..]);
                if (n <= 0)
                {
                    throw Bad(offset + done, "more data (the stream ended early)", truncated: true);
                }

                done += n;
            }
        }

        public void Dispose() => _stream?.Dispose();
    }
}
