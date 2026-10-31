using System.Buffers.Binary;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Cfb;

// Recovery mode (CompoundFileReaderOptions.Recovery): reading what a damaged compound file still holds, and reporting the rest.
//
// The strict loader runs first. Only when it refuses the file does this loader start over from the bytes. It keeps the strict
// loader's central guarantee, that every sector belongs to at most one chain: the first structure to claim a sector keeps it
// (the FAT and DIFAT, then the directory, the mini FAT, the mini stream, then streams in tree order), and a later chain that
// reaches a claimed sector ends there. So the bytes recovered never add up to more than the file holds, and loops end.
public sealed partial class CompoundFileReader
{
    private readonly List<CfbDamage> _damage = [];
    private bool _recovered;
    private long _recoveryBytes;
    private long[] _recLength = [];
    private long[] _recCount = [];
    private bool[] _entryDamaged = [];
    private int _perSector;

    /// <summary>
    /// Gets what recovery skipped, fixed or found damaged, in the order it was found. Always empty unless <see cref="CompoundFileReaderOptions.Recovery"/>
    /// is set, and empty when the file passes every strict check. The list is complete once the file is loaded, which this property does.
    /// </summary>
    /// <exception cref="CompressionFormatException">The file is malformed beyond what recovery repairs.</exception>
    public IReadOnlyList<CfbDamage> Damage
    {
        get
        {
            EnsureLoaded();
            return _damage;
        }
    }

    /// <summary>Gets a value indicating whether recovery had to skip or fix anything. See <see cref="Damage"/>.</summary>
    /// <exception cref="CompressionFormatException">The file is malformed beyond what recovery repairs.</exception>
    public bool IsRecovered => Damage.Count > 0;

    private void Note(CfbDamageKind kind, long offset, long length, int sid, string reason)
    {
        if (_damage.Count >= Limits.MaxEntryCount)
        {
            throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxEntryCount), Limits.MaxEntryCount, _damage.Count + 1L);
        }

        string? name = null;
        if (sid >= 0 && sid < _dir.Length)
        {
            name = sid == 0 ? string.Empty : _dir[sid].Name;
            _entryDamaged[sid] = true;
        }

        _damage.Add(new CfbDamage(kind, offset, Math.Max(0, length), sid, name, reason));
    }

    private void Charge(long bytes)
    {
        _recoveryBytes += bytes;
        if (_recoveryBytes > _options.MaxRecoveryBytes)
        {
            throw new CompressionLimitException(Name, nameof(CompoundFileReaderOptions.MaxRecoveryBytes), _options.MaxRecoveryBytes, _recoveryBytes);
        }
    }

    private bool IsClaimed(uint sector) => (_claimed[sector >> 6] & (1UL << (int)(sector & 63))) != 0;

    private bool TryClaim(uint sector)
    {
        ref ulong word = ref _claimed[sector >> 6];
        ulong bit = 1UL << (int)(sector & 63);
        if ((word & bit) != 0)
        {
            return false;
        }

        word |= bit;
        return true;
    }

    // How many bytes of a sector the file holds: a file cut short may end inside the last one.
    private int SectorBytes(uint sector) => (int)Math.Clamp(_source.Length - (((long)sector + 1) << _shift), 0, _sectorSize);

    // Reads a sector; bytes the file does not hold are set to pad (0xFF reads as FREESECT in an allocation table). Returns the bytes present.
    private int ReadSectorPadded(uint sector, Span<byte> destination, byte pad)
    {
        int present = SectorBytes(sector);
        if (present > 0)
        {
            _source.ReadAt(((long)sector + 1) << _shift, destination[..present]);
        }

        destination[present.._sectorSize].Fill(pad);
        return present;
    }

    private long FatAt(uint sector, long fallback)
    {
        uint slot = sector / (uint)_perSector;
        return slot < _fatSectors.Length && _fatSectors[slot] != FreeSect
            ? ((_fatSectors[slot] + 1L) << _shift) + ((sector % _perSector) * 4L)
            : fallback;
    }

    private long MiniFatAt(uint mini, long fallback)
    {
        uint slot = mini / (uint)_perSector;
        return slot < _miniFatSectors.Length ? ((_miniFatSectors[slot] + 1L) << _shift) + ((mini % _perSector) * 4L) : fallback;
    }

    private uint NextInFat(uint sector) => sector < _fat.Length ? _fat[sector] : FreeSect;

    // False for a sector the FAT marks free. A free sector may hold leftovers of an older layout; when the part of the FAT that would say is lost, the sector is given the benefit of the doubt.
    private bool MayBeInUse(uint sector) => NextInFat(sector) != FreeSect || FatAt(sector, -1) < 0;

    // [MS-CFB] 2.2 to 2.6 again, accepting what can be used. Errors that remain are the ones with nothing left to read: no header, no FAT, no directory.
    private void LoadRecovering()
    {
        _recovered = true;
        _damage.Clear();
        _dir = [];
        _order = [];
        _children = [];
        _parents = [];
        _fat = [];
        _fatSectors = [];
        _dirSectors = [];
        _miniFat = [];
        _miniFatSectors = [];
        _miniStream = [];
        _miniClaimed = [];
        _recoveryBytes = 0;

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
        int storedShift = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(30));
        if (major != 3 && major != 4)
        {
            if (storedShift != 9 && storedShift != 12)
            {
                throw Bad(26, $"major version 3 or 4 (found {major})");
            }

            int guess = storedShift == 9 ? 3 : 4;
            Note(CfbDamageKind.HeaderInconsistent, 26, 2, -1, $"major version 3 or 4 expected (found {major}); version {guess} taken from the sector shift");
            major = guess;
        }

        int shift = major == 3 ? 9 : 12;
        if (storedShift != shift)
        {
            Note(CfbDamageKind.HeaderInconsistent, 30, 2, -1, $"sector shift {shift} for major version {major} expected (found {storedShift}); {shift} used");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(28)) != 0xFFFE)
        {
            Note(CfbDamageKind.HeaderInconsistent, 28, 2, -1, "the little-endian byte order mark 0xFFFE expected");
        }

        int miniShift = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(32));
        if (miniShift != 6)
        {
            Note(CfbDamageKind.HeaderInconsistent, 32, 2, -1, $"mini sector shift 6 expected (found {miniShift}); 6 used");
        }

        uint cutoff = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(56));
        if (cutoff != 4096)
        {
            Note(CfbDamageKind.HeaderInconsistent, 56, 4, -1, $"a mini stream cutoff size of 4096 expected (found {cutoff}); 4096 used");
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
            MiniSectorShift = 6,
            HeaderClassId = new Guid(h.AsSpan(8, 16)),
            DirectorySectorCount = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(40)),
            FatSectorCount = numFat,
            FirstDirectorySector = firstDir,
            TransactionSignature = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(52)),
            MiniStreamCutoff = 4096,
            FirstMiniFatSector = firstMini,
            MiniFatSectorCount = numMini,
            FirstDifatSector = firstDif,
            DifatSectorCount = numDif,
            HeaderDifat = hdrDifat,
        };

        _v4 = major == 4;
        _shift = shift;
        _sectorSize = 1 << shift;
        _perSector = _sectorSize / 4;

        // A last sector the file holds only in part still counts: its bytes are used and the cut is reported where a chain reaches it.
        long sectors = Math.Max(0, ((length + _sectorSize - 1) >> shift) - 1);
        _sectorCount = (uint)Math.Min(sectors, MaxRegSect);
        _claimed = new ulong[(_sectorCount + 63) / 64];
        byte[] buf = new byte[_sectorSize];

        LoadAllocationTable(hdrDifat, firstDif, numDif, numFat, buf);
        LoadDirectoryRecovering(firstDir, buf);
        LoadMiniFatRecovering(firstMini, numMini, buf);
        LoadMiniStreamRecovering();
        BuildTreeRecovering();
        ValidateStreamsRecovering();
        _root = MakeEntry(0);
    }

    // [MS-CFB] 2.3 and 2.5. FAT sectors are located by the header's 109 DIFAT entries, then the DIFAT chain. A slot that cannot be used leaves its
    // part of the FAT free, so chains that cross it end there.
    private void LoadAllocationTable(uint[] hdrDifat, uint firstDif, uint numDif, uint numFat, byte[] buf)
    {
        var slots = new List<uint>();

        void Slot(uint s, long at, string where)
        {
            if (s == FreeSect)
            {
                slots.Add(FreeSect);
            }
            else if (s >= _sectorCount)
            {
                Note(CfbDamageKind.AllocationTableUnusable, at, 4, -1, $"{where} inside the file ({_sectorCount} sectors after the header; found 0x{s:X8}{Special(s)}); that part of the FAT is lost");
                slots.Add(FreeSect);
            }
            else if (!TryClaim(s))
            {
                Note(CfbDamageKind.AllocationTableUnusable, at, 4, -1, $"{where} that no other sector reference uses (sector {s} is listed twice); that part of the FAT is lost");
                slots.Add(FreeSect);
            }
            else
            {
                slots.Add(s);
            }
        }

        for (int i = 0; i < 109; i++)
        {
            Slot(hdrDifat[i], 76 + (i * 4), "a FAT sector location in the header's DIFAT to be");
        }

        uint link = firstDif;
        long linkAt = 68;
        uint walked = 0;
        while (link != EndOfChain && !(link == FreeSect && walked == 0 && numDif == 0))
        {
            if (link >= _sectorCount)
            {
                Note(CfbDamageKind.AllocationTableUnusable, linkAt, 4, -1, $"a DIFAT sector location inside the file ({_sectorCount} sectors after the header; found 0x{link:X8}{Special(link)}); the DIFAT chain ends here");
                break;
            }

            if (!TryClaim(link))
            {
                Note(CfbDamageKind.AllocationTableUnusable, linkAt, 4, -1, $"a DIFAT sector in sector {link} that no other chain uses (the DIFAT chain loops); it ends here");
                break;
            }

            walked++;
            if (SectorBytes(link) < _sectorSize)
            {
                Note(CfbDamageKind.Truncated, ((long)link + 1) << _shift, SectorBytes(link), -1, "a whole DIFAT sector (the file ends inside it)");
            }

            ReadSectorPadded(link, buf, 0xFF);
            long baseAt = ((long)link + 1) << _shift;
            for (int i = 0; i < _perSector - 1; i++)
            {
                Slot(BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4)), baseAt + (i * 4), "a FAT sector location in a DIFAT sector to be");
            }

            linkAt = baseAt + _sectorSize - 4;
            link = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(_sectorSize - 4));
        }

        if (walked != numDif)
        {
            Note(CfbDamageKind.HeaderInconsistent, 72, 4, -1, $"{numDif} DIFAT sectors declared; the chain from the first DIFAT sector holds {walked}");
        }

        while (slots.Count > 0 && slots[^1] == FreeSect)
        {
            slots.RemoveAt(slots.Count - 1);
        }

        int usable = 0;
        foreach (uint s in slots)
        {
            if (s != FreeSect)
            {
                usable++;
            }
        }

        if (usable == 0)
        {
            throw Bad(44, "at least one usable FAT sector location in the DIFAT");
        }

        if (usable != numFat)
        {
            Note(CfbDamageKind.HeaderInconsistent, 44, 4, -1, $"{numFat} FAT sectors declared; {usable} usable FAT sector locations found");
        }

        _fatSectors = slots.ToArray();
        _fat = new uint[(long)slots.Count * _perSector];
        for (int f = 0; f < _fatSectors.Length; f++)
        {
            if (_fatSectors[f] == FreeSect)
            {
                Array.Fill(_fat, FreeSect, f * _perSector, _perSector);
                continue;
            }

            if (ReadSectorPadded(_fatSectors[f], buf, 0xFF) < _sectorSize)
            {
                Note(CfbDamageKind.Truncated, ((long)_fatSectors[f] + 1) << _shift, SectorBytes(_fatSectors[f]), -1, "a whole FAT sector (the file ends inside it); the missing entries are free");
            }

            for (int i = 0; i < _perSector; i++)
            {
                _fat[((long)f * _perSector) + i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4));
            }
        }
    }

    // [MS-CFB] 2.6: the directory chain. When it does not start at a usable sector, the root entry is searched for; when it is cut short, sectors that
    // follow the last one and look like directory sectors are taken, since writers lay the directory out contiguously more often than not.
    private void LoadDirectoryRecovering(uint firstDir, byte[] buf)
    {
        uint start = firstDir;
        if (start >= _sectorCount || IsClaimed(start))
        {
            Note(CfbDamageKind.DirectoryUnusable, 48, 4, -1, $"the first directory sector to be an unused sector inside the file (found 0x{start:X8}{Special(start)}); searching for the root entry");
            start = FindRootSector();
        }

        long entriesPer = _sectorSize / DirEntrySize;
        var chain = new List<uint>();
        uint cur = start;
        long at = 48;
        bool cut = false;
        while (cur != EndOfChain)
        {
            if (cur >= _sectorCount)
            {
                Note(CfbDamageKind.DirectoryUnusable, at, 4, -1, $"the directory chain to stay inside the file ({_sectorCount} sectors after the header; found 0x{cur:X8}{Special(cur)}); it ends here");
                cut = true;
                break;
            }

            if (!TryClaim(cur))
            {
                Note(CfbDamageKind.DirectoryUnusable, at, 4, -1, $"the directory chain in sector {cur} that no other chain uses (sectors are used twice, or the chain loops); it ends here");
                cut = true;
                break;
            }

            chain.Add(cur);
            CheckDirectoryCount(chain.Count, entriesPer);
            at = FatAt(cur, at);
            cur = NextInFat(cur);
        }

        if (cut)
        {
            uint first = chain[^1] + 1;
            uint s = first;
            int added = 0;
            while (s < _sectorCount && !IsClaimed(s) && MayBeInUse(s))
            {
                Charge(_sectorSize);
                if (!LooksLikeDirectorySector(s, buf))
                {
                    break;
                }

                TryClaim(s);
                chain.Add(s);
                CheckDirectoryCount(chain.Count, entriesPer);
                added++;
                s++;
            }

            if (added > 0)
            {
                Note(CfbDamageKind.DirectoryUnusable, ((long)first + 1) << _shift, (long)added * _sectorSize, -1, $"{added} directory sectors that follow the break were found by scanning");
            }
        }

        long dirCount = chain.Count * entriesPer;
        _dirSectors = chain.ToArray();
        _dir = new DirEntry[dirCount];
        _entryDamaged = new bool[dirCount];
        for (int s = 0; s < _dirSectors.Length; s++)
        {
            if (ReadSectorPadded(_dirSectors[s], buf, 0) < _sectorSize)
            {
                Note(CfbDamageKind.Truncated, ((long)_dirSectors[s] + 1) << _shift, SectorBytes(_dirSectors[s]), -1, "a whole directory sector (the file ends inside it); the missing entries are unallocated");
            }

            for (int e = 0; e < entriesPer; e++)
            {
                int sid = (s * (int)entriesPer) + e;
                ParseDirEntryRecovering(buf.AsSpan(e * DirEntrySize, DirEntrySize), sid, ref _dir[sid]);
            }
        }
    }

    private void CheckDirectoryCount(int sectors, long entriesPer)
    {
        long dirCount = sectors * entriesPer;
        if (dirCount > _options.MaxDirectoryEntries)
        {
            throw new CompressionLimitException(Name, nameof(CompoundFileReaderOptions.MaxDirectoryEntries), _options.MaxDirectoryEntries, dirCount);
        }
    }

    private uint FindRootSector()
    {
        byte[] b = new byte[DirEntrySize];
        uint stale = FreeSect;
        for (uint s = 0; s < _sectorCount; s++)
        {
            if (IsClaimed(s) || SectorBytes(s) < DirEntrySize)
            {
                continue;
            }

            Charge(DirEntrySize);
            _source.ReadAt(((long)s + 1) << _shift, b);
            // [MS-CFB] 2.6.2: the root entry is named "Root Entry".
            if (b[0x42] == 5 && BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0x40)) == 22 && NameIsRootEntry(b))
            {
                // Writers that rewrite a directory can leave the old one behind, marked free: it is the second choice.
                if (!MayBeInUse(s))
                {
                    stale = stale == FreeSect ? s : stale;
                    continue;
                }

                Note(CfbDamageKind.DirectoryUnusable, ((long)s + 1) << _shift, 0, -1, $"the root entry was found in sector {s}");
                return s;
            }
        }

        if (stale != FreeSect)
        {
            Note(CfbDamageKind.DirectoryUnusable, ((long)stale + 1) << _shift, 0, -1, $"the root entry was found in sector {stale}, which the FAT marks free");
            return stale;
        }

        throw Bad(48, "a directory chain (the first directory sector is unusable and no root entry was found by scanning)");
    }

    private static bool NameIsRootEntry(ReadOnlySpan<byte> b)
    {
        const string Root = "Root Entry";
        for (int i = 0; i < Root.Length; i++)
        {
            if (char.ToUpperInvariant((char)BinaryPrimitives.ReadUInt16LittleEndian(b[(i * 2)..])) != char.ToUpperInvariant(Root[i]))
            {
                return false;
            }
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(b[20..]) == 0;
    }

    // A sector of 128-byte entries, each unallocated (type 0, no name) or a storage or stream with a plausible name.
    private bool LooksLikeDirectorySector(uint sector, byte[] buf)
    {
        if (SectorBytes(sector) < _sectorSize)
        {
            return false;
        }

        ReadSectorPadded(sector, buf, 0);
        bool any = false;
        for (int e = 0; e < _sectorSize / DirEntrySize; e++)
        {
            ReadOnlySpan<byte> b = buf.AsSpan(e * DirEntrySize, DirEntrySize);
            int cb = BinaryPrimitives.ReadUInt16LittleEndian(b[0x40..]);
            byte type = b[0x42];
            if (type == 0)
            {
                if (cb != 0)
                {
                    return false;
                }

                continue;
            }

            if ((type != 1 && type != 2) || (cb & 1) != 0 || cb < 4 || cb > 64 || BinaryPrimitives.ReadUInt16LittleEndian(b[(cb - 2)..]) != 0 || b[0x43] > 1)
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    // [MS-CFB] 2.6.1: one entry, keeping what is usable. Every fix is reported once per entry.
    private void ParseDirEntryRecovering(ReadOnlySpan<byte> b, int sid, ref DirEntry d)
    {
        long at = DirOffset(sid);
        byte type = b[0x42];
        d.Left = BinaryPrimitives.ReadUInt32LittleEndian(b[0x44..]);
        d.Right = BinaryPrimitives.ReadUInt32LittleEndian(b[0x48..]);
        d.Child = BinaryPrimitives.ReadUInt32LittleEndian(b[0x4C..]);
        d.Name = string.Empty;
        d.Color = 1;

        if (sid == 0)
        {
            if (type != 5 && type != 1)
            {
                // Without a root, the entries are attached to a root made up here (see BuildTreeRecovering).
                d.Type = 5;
                d.Left = d.Right = d.Child = NoStream;
                d.Start = EndOfChain;
                Note(CfbDamageKind.BadEntry, at + 0x42, 1, 0, $"the root storage (object type 5) as the first directory entry (found {type}); an empty root is used");
                return;
            }

            if (type != 5)
            {
                Note(CfbDamageKind.BadEntry, at + 0x42, 1, 0, "the root storage (object type 5) as the first directory entry (found 1); it is used as the root");
            }

            type = 5;
        }
        else if (type != 1 && type != 2)
        {
            d.Type = 0;
            if (type != 0)
            {
                Note(CfbDamageKind.BadEntry, at + 0x42, 1, -1, $"object type 0, 1 or 2 for directory entry {sid} (found {type}); the entry is ignored");
            }

            return;
        }

        d.Type = type;
        long firstAt = -1;
        string? reasons = null;

        void Flag(long where, string reason)
        {
            firstAt = firstAt < 0 ? where : firstAt;
            reasons = reasons is null ? reason : reasons + "; " + reason;
        }

        d.Color = b[0x43];
        if (d.Color > 1)
        {
            Flag(at + 0x43, $"color flag 0 or 1 (found {d.Color})");
            d.Color = 1;
        }

        if (sid != 0)
        {
            int cb = BinaryPrimitives.ReadUInt16LittleEndian(b[0x40..]);
            if ((cb & 1) != 0 || cb > 64 || cb < 2)
            {
                Flag(at + 0x40, $"an even name length of 2 to 64 bytes (found {cb})");
                cb = Math.Min(cb & ~1, 64);
            }

            int units = cb < 2 ? 0 : (cb / 2) - 1;
            var chars = new char[units];
            for (int i = 0; i < units; i++)
            {
                chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(b[(i * 2)..]);
                if (chars[i] == '\0')
                {
                    Flag(at + (i * 2), "a name without a null before its declared end");
                    units = i;
                    break;
                }
            }

            if (units == 0)
            {
                Flag(at + 0x40, "a name of at least one character");
                d.Name = "unnamed-" + sid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                d.Name = new string(chars, 0, units);
            }
        }

        d.Clsid = new Guid(b.Slice(0x50, 16));
        d.State = BinaryPrimitives.ReadUInt32LittleEndian(b[0x60..]);
        d.Created = BinaryPrimitives.ReadUInt64LittleEndian(b[0x64..]);
        d.Modified = BinaryPrimitives.ReadUInt64LittleEndian(b[0x6C..]);
        d.Start = BinaryPrimitives.ReadUInt32LittleEndian(b[0x74..]);
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(b[0x78..]);
        if (!_v4)
        {
            size = (uint)size;
        }

        if (size > long.MaxValue)
        {
            Flag(at + 0x78, "a stream size below 2^63");
            size = long.MaxValue;
        }

        d.Size = (long)size;
        if (firstAt >= 0)
        {
            Note(CfbDamageKind.BadEntry, firstAt, 0, sid, $"directory entry {sid} to be valid: {reasons}");
        }
    }

    // [MS-CFB] 2.4: the mini FAT is a chain of sectors; the part that cannot be read leaves its mini sectors free.
    private void LoadMiniFatRecovering(uint firstMini, uint numMini, byte[] buf)
    {
        var chain = new List<uint>();
        uint cur = firstMini;
        long at = 60;
        if (!(numMini == 0 && (cur == EndOfChain || cur == FreeSect)))
        {
            while (cur != EndOfChain)
            {
                if (cur >= _sectorCount)
                {
                    Note(CfbDamageKind.MiniStreamUnusable, at, 4, -1, $"the mini FAT chain to stay inside the file ({_sectorCount} sectors after the header; found 0x{cur:X8}{Special(cur)}); it ends here");
                    break;
                }

                if (!TryClaim(cur))
                {
                    Note(CfbDamageKind.MiniStreamUnusable, at, 4, -1, $"the mini FAT chain in sector {cur} that no other chain uses (the chain loops or shares a sector); it ends here");
                    break;
                }

                chain.Add(cur);
                at = FatAt(cur, at);
                cur = NextInFat(cur);
            }

            if (chain.Count != numMini)
            {
                Note(CfbDamageKind.HeaderInconsistent, 64, 4, -1, $"{numMini} mini FAT sectors declared; the chain from the first mini FAT sector holds {chain.Count}");
            }
        }

        _miniFatSectors = chain.ToArray();
        _miniFat = new uint[(long)chain.Count * _perSector];
        for (int m = 0; m < chain.Count; m++)
        {
            if (ReadSectorPadded(chain[m], buf, 0xFF) < _sectorSize)
            {
                Note(CfbDamageKind.Truncated, ((long)chain[m] + 1) << _shift, SectorBytes(chain[m]), -1, "a whole mini FAT sector (the file ends inside it); the missing entries are free");
            }

            for (int i = 0; i < _perSector; i++)
            {
                _miniFat[((long)m * _perSector) + i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4));
            }
        }
    }

    // [MS-CFB] 2.6.2: the root entry holds the mini stream. Its chain decides how many bytes exist; the declared size is only believed
    // when the chain agrees with it to within a sector.
    private void LoadMiniStreamRecovering()
    {
        long declared = _dir[0].Size;
        var chain = new List<uint>();
        long capacity = 0;
        uint cur = _dir[0].Start;
        long at = DirOffset(0) + 0x74;
        CfbDamageKind problemKind = CfbDamageKind.MiniStreamUnusable;
        long problemAt = at;
        string? problem = null;
        if (!(declared == 0 && (cur == EndOfChain || cur == FreeSect)) && !(cur == EndOfChain))
        {
            while (cur != EndOfChain)
            {
                if (cur >= _sectorCount)
                {
                    problemKind = CfbDamageKind.MiniStreamUnusable;
                    problemAt = at;
                    problem = $"the mini stream chain to stay inside the file ({_sectorCount} sectors after the header; found 0x{cur:X8}{Special(cur)})";
                    break;
                }

                if (!TryClaim(cur))
                {
                    problemAt = at;
                    problem = $"the mini stream chain in sector {cur} that no other chain uses (the chain loops or shares a sector)";
                    break;
                }

                chain.Add(cur);
                int present = SectorBytes(cur);
                capacity += present;
                if (present < _sectorSize)
                {
                    problemKind = CfbDamageKind.Truncated;
                    problemAt = ((long)cur + 1) << _shift;
                    problem = "a whole mini stream sector (the file ends inside it)";
                    break;
                }

                at = FatAt(cur, at);
                cur = NextInFat(cur);
            }
        }

        long effective = declared;
        if (declared > capacity)
        {
            if (problem is not null)
            {
                Note(problemKind, problemAt, 0, 0, $"{problem}; it holds {capacity} bytes of the {declared} declared");
            }
            else
            {
                Note(CfbDamageKind.MiniStreamUnusable, DirOffset(0) + 0x78, 8, 0, $"a mini stream chain of at least {declared} bytes (the chain holds {capacity})");
            }

            effective = capacity;
        }
        else if (declared < capacity - (_sectorSize - 1))
        {
            Note(CfbDamageKind.MiniStreamUnusable, DirOffset(0) + 0x78, 8, 0, $"a mini stream size matching its chain of {capacity} bytes (found {declared}); the chain's size is used");
            effective = capacity;
        }

        _miniStream = chain.ToArray();
        _miniStreamSize = effective;
        long miniSectors = (effective + MiniSectorSize - 1) / MiniSectorSize;
        _miniClaimed = new ulong[(miniSectors + 63) / 64];
    }

    // [MS-CFB] 2.6.4, kept as lenient as the tree allows: an invalid or repeated link drops its branch, the dropped entries are attached to the root,
    // and siblings out of order are sorted. Explicit stacks again, so no shape of tree exhausts the call stack.
    private void BuildTreeRecovering()
    {
        int count = _dir.Length;
        var visited = new ulong[(count + 63) / 64];
        visited[0] |= 1;
        _parents = new int[count];
        _children = [];
        var work = new Stack<int>();

        List<int> rootKids = SiblingsOfRecovering(0, visited);
        foreach (int k in rootKids)
        {
            if (_dir[k].Type == (byte)CfbObjectType.Storage)
            {
                work.Push(k);
            }
        }

        ExpandStorages(work, visited);

        bool adopted = false;
        for (int sid = 1; sid < count; sid++)
        {
            if ((visited[sid >> 6] & (1UL << (sid & 63))) != 0 || (_dir[sid].Type != (byte)CfbObjectType.Storage && _dir[sid].Type != (byte)CfbObjectType.Stream))
            {
                continue;
            }

            visited[sid >> 6] |= 1UL << (sid & 63);
            Note(CfbDamageKind.OrphanedEntry, DirOffset(sid), DirEntrySize, sid, $"directory entry {sid} to be reachable from the root; it is attached to the root");
            AcceptEntry(sid);
            rootKids.Add(sid);
            adopted = true;
            if (_dir[sid].Type == (byte)CfbObjectType.Storage)
            {
                work.Push(sid);
                ExpandStorages(work, visited);
            }
        }

        if (adopted)
        {
            SortSiblings(rootKids);
        }

        if (rootKids.Count > 0)
        {
            _children[0] = rootKids.ToArray();
        }

        // Parents before children, siblings in name order, as the strict reader lists them.
        var order = new List<int>();
        var frames = new Stack<(int[] Kids, int Index, int Depth)>();
        if (rootKids.Count > 0)
        {
            frames.Push((_children[0], 0, 1));
        }

        while (frames.Count > 0)
        {
            (int[] kids, int index, int depth) = frames.Pop();
            if (index >= kids.Length)
            {
                continue;
            }

            frames.Push((kids, index + 1, depth));
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
            if (_children.TryGetValue(sid, out int[]? sub))
            {
                frames.Push((sub, 0, depth + 1));
            }
        }

        _order = order.ToArray();
    }

    private void ExpandStorages(Stack<int> work, ulong[] visited)
    {
        while (work.Count > 0)
        {
            int storage = work.Pop();
            List<int> kids = SiblingsOfRecovering(storage, visited);
            if (kids.Count == 0)
            {
                continue;
            }

            _children[storage] = kids.ToArray();
            foreach (int k in kids)
            {
                if (_dir[k].Type == (byte)CfbObjectType.Storage)
                {
                    work.Push(k);
                }
            }
        }
    }

    private void AcceptEntry(int sid)
    {
        if (_dir[sid].Type == (byte)CfbObjectType.Stream && _dir[sid].Child != NoStream)
        {
            Note(CfbDamageKind.BadLink, DirOffset(sid) + 0x4C, 4, sid, "NOSTREAM as the child of a stream object; the child is ignored");
        }
    }

    // In-order walk of the sibling tree under a storage; a link that cannot be followed ends its branch.
    private List<int> SiblingsOfRecovering(int storage, ulong[] visited)
    {
        var result = new List<int>();
        if (_dir[storage].Type == (byte)CfbObjectType.Stream)
        {
            return result;
        }

        var stack = new Stack<int>();
        uint cur = _dir[storage].Child;
        int linkOwner = storage;
        long linkAt = DirOffset(storage) + 0x4C;
        while (true)
        {
            while (cur != NoStream)
            {
                int sid = CheckLinkRecovering(cur, linkAt, linkOwner, visited);
                if (sid < 0)
                {
                    break;
                }

                _parents[sid] = storage;
                stack.Push(sid);
                cur = _dir[sid].Left;
                linkOwner = sid;
                linkAt = DirOffset(sid) + 0x44;
            }

            if (stack.Count == 0)
            {
                break;
            }

            int node = stack.Pop();
            result.Add(node);
            cur = _dir[node].Right;
            linkOwner = node;
            linkAt = DirOffset(node) + 0x48;
        }

        for (int i = 1; i < result.Count; i++)
        {
            int c = CompareNames(_dir[result[i - 1]].Name, _dir[result[i]].Name);
            if (c >= 0)
            {
                Note(CfbDamageKind.BadLink, DirOffset(result[i]), DirEntrySize, result[i], c == 0
                    ? $"unique names among the entries of one storage ('{Printable(_dir[result[i]].Name)}' appears twice)"
                    : $"siblings in the order of [MS-CFB] 2.6.4 ('{Printable(_dir[result[i - 1]].Name)}' should not come before '{Printable(_dir[result[i]].Name)}'); they are sorted");
                SortSiblings(result);
                break;
            }
        }

        return result;
    }

    private void SortSiblings(List<int> sids) =>
        sids.Sort((x, y) =>
        {
            int c = CompareNames(_dir[x].Name, _dir[y].Name);
            return c != 0 ? c : x.CompareTo(y);
        });

    private int CheckLinkRecovering(uint id, long at, int owner, ulong[] visited)
    {
        if (id > 0xFFFFFFF9 || id >= _dir.Length)
        {
            Note(CfbDamageKind.BadLink, at, 4, owner, $"a directory entry ID below {_dir.Length} or NOSTREAM (found 0x{id:X8}); the branch is dropped");
            return -1;
        }

        int sid = (int)id;
        byte t = _dir[sid].Type;
        if (t == 0 || t == (byte)CfbObjectType.RootStorage)
        {
            Note(CfbDamageKind.BadLink, at, 4, owner, $"a link to a storage or stream entry (entry {sid} is {(t == 0 ? "unallocated" : "the root")}); the branch is dropped");
            return -1;
        }

        ref ulong word = ref visited[sid >> 6];
        ulong bit = 1UL << (sid & 63);
        if ((word & bit) != 0)
        {
            Note(CfbDamageKind.BadLink, at, 4, owner, $"entry {sid} to be reachable once (the directory tree loops or shares a subtree); the branch is dropped");
            return -1;
        }

        word |= bit;
        AcceptEntry(sid);
        return sid;
    }

    // Walks every reachable stream's chain once, in tree order, claiming its sectors. A stream keeps the bytes before the first problem.
    private void ValidateStreamsRecovering()
    {
        _recLength = new long[_dir.Length];
        _recCount = new long[_dir.Length];
        foreach (int sid in _order)
        {
            ref DirEntry d = ref _dir[sid];
            if (d.Type != (byte)CfbObjectType.Stream || d.Size == 0)
            {
                continue;
            }

            long declared = d.Size;
            bool mini = declared < _header.MiniStreamCutoff;
            long unit = mini ? MiniSectorSize : _sectorSize;
            long needed = (declared / unit) + (declared % unit != 0 ? 1 : 0);
            long usable = 0;
            long count = 0;
            long at = DirOffset(sid) + 0x74;
            long problemAt = at;
            string? problem = null;
            uint cur = d.Start;
            long miniSectors = (_miniStreamSize + MiniSectorSize - 1) / MiniSectorSize;
            while (true)
            {
                bool extra = count >= needed;
                if (cur == EndOfChain)
                {
                    if (!extra)
                    {
                        problemAt = at;
                        problem = $"the chain to continue to {needed} sectors (it ends after {count})";
                    }

                    break;
                }

                if (mini)
                {
                    if (cur >= miniSectors)
                    {
                        if (!extra)
                        {
                            problemAt = at;
                            problem = $"the chain to stay inside the mini stream ({miniSectors} mini sectors; found 0x{cur:X8}{Special(cur)})";
                        }

                        break;
                    }

                    ref ulong word = ref _miniClaimed[cur >> 6];
                    ulong bit = 1UL << (int)(cur & 63);
                    if ((word & bit) != 0)
                    {
                        if (!extra)
                        {
                            problemAt = at;
                            problem = $"the chain in mini sector {cur} that no other chain uses (the chain loops or shares a mini sector)";
                        }

                        break;
                    }

                    word |= bit;
                    if (!extra)
                    {
                        count++;
                        usable += Math.Min(MiniSectorSize, _miniStreamSize - ((long)cur * MiniSectorSize));
                    }

                    at = MiniFatAt(cur, at);
                    cur = cur < _miniFat.Length ? _miniFat[cur] : FreeSect;
                }
                else
                {
                    if (cur >= _sectorCount)
                    {
                        if (!extra)
                        {
                            problemAt = at;
                            problem = $"the chain to stay inside the file ({_sectorCount} sectors after the header; found 0x{cur:X8}{Special(cur)})";
                        }

                        break;
                    }

                    if (!TryClaim(cur))
                    {
                        if (!extra)
                        {
                            problemAt = at;
                            problem = $"the chain in sector {cur} that no other chain uses (the chain loops or shares a sector)";
                        }

                        break;
                    }

                    int present = SectorBytes(cur);
                    if (!extra)
                    {
                        count++;
                        usable += present;
                    }

                    if (present < _sectorSize)
                    {
                        problemAt = ((long)cur + 1) << _shift;
                        problem = "the file ends inside the last sector of the chain";
                        break;
                    }

                    at = FatAt(cur, at);
                    cur = NextInFat(cur);
                }
            }

            long length = Math.Min(declared, usable);
            _recLength[sid] = length;
            _recCount[sid] = count;
            if (length < declared)
            {
                Note(CfbDamageKind.DamagedStream, problemAt, 0, sid, $"{(mini ? "a mini " : "a ")}sector chain holding the {declared}-byte stream '{Printable(d.Name)}': {(problem is null ? "the chain is too short" : problem)}; {length} bytes can be read");
            }
        }
    }

    private CfbStream OpenRecovered(int sid)
    {
        ref DirEntry d = ref _dir[sid];
        if (d.Type != (byte)CfbObjectType.Stream || sid == 0 || _recLength[sid] == 0)
        {
            return new CfbStream(this, [], 0, false);
        }

        // The chain was checked when the file was loaded; its first _recCount sectors are replayed here.
        bool mini = d.Size < _header.MiniStreamCutoff;
        long count = _recCount[sid];
        var chain = new List<uint>((int)Math.Min(count, 1 << 20));
        uint cur = d.Start;
        for (long i = 0; i < count; i++)
        {
            chain.Add(cur);
            if (i + 1 < count)
            {
                cur = mini ? (cur < _miniFat.Length ? _miniFat[cur] : FreeSect) : NextInFat(cur);
            }
        }

        return new CfbStream(this, chain.ToArray(), _recLength[sid], mini);
    }
}
