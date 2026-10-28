using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Cab;

/// <summary>
/// Reads Microsoft cabinet files ([MS-CAB]): the header, the folder and file tables and the data blocks, with stored, MSZIP and LZX
/// folders. A cabinet is read as a unit; a file that is continued from or into another cabinet of a set is listed but cannot be
/// extracted from this cabinet alone.
/// </summary>
/// <remarks>
/// <para>The source must be seekable: the file table points at the data blocks. <see cref="Archive"/> copies a source that cannot seek into
/// memory first. Names use <c>/</c> between components (cabinets store <c>\</c>), and the DOS date and time are read as UTC like zip's.</para>
/// <para>Files are decoded folder by folder. Reading the files of a folder in order costs one pass over it; asking for an earlier
/// file after a later one decodes the folder from its start again, because MSZIP and LZX carry history from block to block.</para>
/// <para>Errors: <see cref="CompressionFormatException"/> names the byte offset; a limit raises <see cref="CompressionLimitException"/>
/// (the folder's LZX window against <see cref="ArchiveLimits.MaxWindowSize"/>, the expansion ratio, the entry limits);
/// Quantum folders and files that span cabinets raise <see cref="ArchiveUnsupportedException"/>.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class CabReader : ArchiveReader
{
    internal const string Name = "cab";
    internal const int FixedHeaderLength = 36;
    internal const int MaxBlockUncompressed = 32768;

    private const int MaxHeaderReserve = 60000;
    private const long MaxFolderSize = 0x7FFF8000;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _origin;
    private readonly CabReaderOptions _options;
    private readonly ExpansionMeter _meter;
    private readonly List<(int Folder, CabFolderCursor Cursor)> _cache = [];
    private bool _loaded;
    private long _length;
    private CabHeader _header = new();
    private CabFolder[] _folders = [];
    private List<CabEntry> _items = [];
    private int _next;
    private CabFolderCursor? _seq;
    private int _seqFolder = -1;

    /// <summary>Creates a reader over a seekable stream positioned at the start of the cabinet.</summary>
    /// <param name="stream">A readable, seekable stream.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the reader is disposed.</param>
    /// <exception cref="ArgumentException">The stream cannot read or seek.</exception>
    public CabReader(Stream stream, CabReaderOptions? options = null, bool leaveOpen = false)
        : base(ArchiveFormat.Cab, (options ?? CabReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable: a cabinet's file table points at its data blocks.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _origin = stream.Position;
        _options = options ?? CabReaderOptions.Default;
        _meter = new ExpansionMeter(Limits, Name);
    }

    /// <inheritdoc />
    protected override string FormatName => Name;

    /// <inheritdoc />
    public override bool SupportsRandomAccess => true;

    /// <summary>Gets the cabinet header.</summary>
    /// <exception cref="CompressionFormatException">The header is malformed.</exception>
    public CabHeader Header
    {
        get
        {
            EnsureLoaded();
            return _header;
        }
    }

    /// <summary>Gets the folders of the cabinet.</summary>
    /// <exception cref="CompressionFormatException">The header is malformed.</exception>
    public IReadOnlyList<CabFolder> Folders
    {
        get
        {
            EnsureLoaded();
            return _folders;
        }
    }

    /// <summary>Gets every file entry in table order, including those that continue into other cabinets.</summary>
    /// <exception cref="CompressionFormatException">The header or file table is malformed.</exception>
    public IReadOnlyList<CabEntry> Items
    {
        get
        {
            EnsureLoaded();
            return _items;
        }
    }

    /// <summary>Reads the next entry.</summary>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new CabEntry? GetNextEntry() => (CabEntry?)base.GetNextEntry();

    /// <summary>Reads the next entry.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new async ValueTask<CabEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (CabEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        return new ValueTask<ArchiveEntry?>(_next < _items.Count ? _items[_next++] : null);
    }

    /// <inheritdoc />
    /// <exception cref="ArchiveUnsupportedException">The file continues in another cabinet, or its folder uses Quantum or an unknown method.</exception>
    /// <exception cref="CompressionLimitException">The folder's LZX window is larger than <see cref="ArchiveLimits.MaxWindowSize"/>.</exception>
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        CabEntry entry = (CabEntry?)CurrentEntry ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        if (!CanRead(entry))
        {
            return new MemoryReadStream(ReadOnlyMemory<byte>.Empty);
        }

        if (_seq is null || _seqFolder != entry.FolderIndex)
        {
            _seq?.Dispose();
            _seq = NewCursor(entry.FolderIndex);
            _seqFolder = entry.FolderIndex;
        }

        return new CabEntryStream(_seq, null, entry);
    }

    /// <inheritdoc />
    /// <exception cref="ArchiveUnsupportedException">The file continues in another cabinet, or its folder uses Quantum or an unknown method.</exception>
    public override Stream OpenEntryStream(ArchiveEntry entry)
    {
        ThrowIfDisposed();
        EnsureLoaded();
        if (entry is not CabEntry e || e.Index < 0 || e.Index >= _items.Count || !ReferenceEquals(_items[e.Index], e))
        {
            throw new ArgumentException("The entry was not read by this CabReader.", nameof(entry));
        }

        if (!CanRead(e))
        {
            return new MemoryReadStream(ReadOnlyMemory<byte>.Empty);
        }

        int folder = e.FolderIndex;
        CabFolderCursor? cursor = null;
        for (int i = 0; i < _cache.Count; i++)
        {
            if (_cache[i].Folder == folder)
            {
                cursor = _cache[i].Cursor;
                _cache.RemoveAt(i);
                break;
            }
        }

        cursor ??= NewCursor(folder);
        return new CabEntryStream(cursor, c => ReturnCursor(folder, c), e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _seq?.Dispose();
            foreach ((_, CabFolderCursor c) in _cache)
            {
                c.Dispose();
            }

            _cache.Clear();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    // Empty files need no data; everything else must be complete in this cabinet and use a method we can decode.
    private bool CanRead(CabEntry entry)
    {
        EnsureLoaded();
        if (entry.Continuation != CabContinuation.None)
        {
            string where = (entry.Continuation & CabContinuation.FromPrevious) != 0
                ? $"starts in the previous cabinet{(_header.PreviousCabinet is { } p ? " '" + p + "'" : string.Empty)}"
                : $"goes on in the next cabinet{(_header.NextCabinet is { } n ? " '" + n + "'" : string.Empty)}";
            throw new ArchiveUnsupportedException(Name, $"file '{entry.Name}' {where}; files that span cabinets cannot be read from one cabinet");
        }

        if (entry.Size == 0)
        {
            return false;
        }

        CabFolder folder = _folders[entry.FolderIndex];
        switch (folder.Compression)
        {
            case CabCompression.None:
            case CabCompression.MsZip:
                break;
            case CabCompression.Lzx:
                if ((1L << folder.WindowBits) > Limits.MaxWindowSize)
                {
                    throw new CompressionLimitException(Name, nameof(ArchiveLimits.MaxWindowSize), Limits.MaxWindowSize, 1L << folder.WindowBits);
                }

                break;
            case CabCompression.Quantum:
                throw new ArchiveUnsupportedException(Name, "Quantum compression (folder " + folder.Index + ")");
            default:
                throw new ArchiveUnsupportedException(Name, $"compression type 0x{folder.RawCompressionType:X4} (folder {folder.Index})");
        }

        return true;
    }

    private CabFolderCursor NewCursor(int folder) => new(_stream, _origin, _length, _header, _folders[folder], _options.VerifyChecksum, _meter);

    // At most two idle decoders are kept: an LZX decoder holds its window.
    private void ReturnCursor(int folder, CabFolderCursor cursor)
    {
        if (IsDisposed)
        {
            cursor.Dispose();
            return;
        }

        _cache.Add((folder, cursor));
        while (_cache.Count > 2)
        {
            _cache[0].Cursor.Dispose();
            _cache.RemoveAt(0);
        }
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

    // ---- Parsing ([MS-CAB] 2.1 to 2.3) ----

    private static CompressionFormatException Bad(long offset, string expected, bool truncated = false) => new(Name, offset, expected, truncated);

    private void Load()
    {
        _length = _stream.Length - _origin;
        var t = new CabTableReader(_stream, _origin, _length);
        Span<byte> h = stackalloc byte[FixedHeaderLength];
        if (_length < FixedHeaderLength)
        {
            throw Bad(_length, "a 36-byte cabinet header (the signature 'MSCF')", truncated: true);
        }

        t.ReadExact(0, h);
        if (!h[..4].SequenceEqual("MSCF"u8))
        {
            throw Bad(0, "the cabinet signature 'MSCF'");
        }

        long cabinetSize = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
        long filesOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        int minor = h[24];
        int major = h[25];
        int folderCount = BinaryPrimitives.ReadUInt16LittleEndian(h[26..]);
        int fileCount = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        var flags = (CabHeaderFeatures)BinaryPrimitives.ReadUInt16LittleEndian(h[30..]);
        int setId = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
        int cabinetIndex = BinaryPrimitives.ReadUInt16LittleEndian(h[34..]);
        if (major != 1)
        {
            throw Bad(25, $"cabinet format version 1.x (found {major}.{minor})");
        }

        long pos = FixedHeaderLength;
        int headerReserve = 0;
        int folderReserve = 0;
        int dataReserve = 0;
        byte[] headerReserveBytes = [];
        if ((flags & CabHeaderFeatures.ReservePresent) != 0)
        {
            Span<byte> r = stackalloc byte[4];
            t.ReadExact(pos, r);
            headerReserve = BinaryPrimitives.ReadUInt16LittleEndian(r);
            folderReserve = r[2];
            dataReserve = r[3];
            pos += 4;
            if (headerReserve > MaxHeaderReserve)
            {
                throw Bad(pos - 4, $"a header reserve of at most {MaxHeaderReserve} bytes (found {headerReserve})");
            }

            headerReserveBytes = new byte[headerReserve];
            t.ReadExact(pos, headerReserveBytes);
            pos += headerReserve;
        }

        string? prevCab = null, prevDisk = null, nextCab = null, nextDisk = null;
        if ((flags & CabHeaderFeatures.PreviousCabinet) != 0)
        {
            prevCab = t.ReadZString(ref pos, 255, "the previous cabinet's name");
            prevDisk = t.ReadZString(ref pos, 255, "the previous disk's name");
        }

        if ((flags & CabHeaderFeatures.NextCabinet) != 0)
        {
            nextCab = t.ReadZString(ref pos, 255, "the next cabinet's name");
            nextDisk = t.ReadZString(ref pos, 255, "the next disk's name");
        }

        if (filesOffset < pos || filesOffset > _length)
        {
            throw Bad(16, $"a file table offset between {pos} and the end of the cabinet ({_length}); found {filesOffset}");
        }

        // CFFOLDER: coffCabStart (4), cCFData (2), typeCompress (2), then the folder reserve.
        var folders = new CabFolder[folderCount];
        int folderLength = 8 + folderReserve;
        byte[] fb = new byte[folderLength];
        for (int i = 0; i < folderCount; i++)
        {
            t.ReadExact(pos, fb);
            long start = BinaryPrimitives.ReadUInt32LittleEndian(fb);
            int blocks = BinaryPrimitives.ReadUInt16LittleEndian(fb.AsSpan(4));
            int type = BinaryPrimitives.ReadUInt16LittleEndian(fb.AsSpan(6));
            if (blocks > 0 && start >= _length)
            {
                throw Bad(pos, $"a data offset inside the cabinet for folder {i} (found {start}, size {_length})");
            }

            var method = (CabCompression)(type & 0x000F);
            int window = 0;
            if (method == CabCompression.Lzx)
            {
                window = (type >> 8) & 0x1F;
                if (window < 15 || window > 21)
                {
                    // 17 to 25 is the range of LZX DELTA; a cabinet's own LZX uses 15 to 21.
                    throw new ArchiveUnsupportedException(Name, $"an LZX window of 2^{window} bytes (folder {i}); cabinets use 2^15 to 2^21");
                }
            }

            folders[i] = new CabFolder
            {
                Index = i,
                DataOffset = start,
                BlockCount = blocks,
                Compression = method,
                RawCompressionType = type,
                WindowBits = window,
                Reserve = folderReserve > 0 ? fb.AsMemory(8).ToArray() : default,
            };
            pos += folderLength;
        }

        if (folderCount > 0 && pos > filesOffset)
        {
            throw Bad(16, $"a file table after the folder table (offset {filesOffset}, folders end at {pos})");
        }

        _header = new CabHeader
        {
            CabinetSize = cabinetSize,
            FilesOffset = filesOffset,
            FormatVersion = new Version(major, minor),
            FolderCount = folderCount,
            FileCount = fileCount,
            Flags = flags,
            SetId = setId,
            CabinetIndex = cabinetIndex,
            HeaderReserve = headerReserveBytes,
            FolderReserveSize = folderReserve,
            DataReserveSize = dataReserve,
            PreviousCabinet = prevCab,
            PreviousDisk = prevDisk,
            NextCabinet = nextCab,
            NextDisk = nextDisk,
        };
        _folders = folders;

        // CFFILE: cbFile (4), uoffFolderStart (4), iFolder (2), date (2), time (2), attribs (2), then the NUL-terminated name.
        var items = new List<CabEntry>(Math.Min(fileCount, 4096));
        pos = filesOffset;
        Span<byte> fh = stackalloc byte[16];
        for (int i = 0; i < fileCount; i++)
        {
            long at = pos;
            t.ReadExact(pos, fh);
            pos += 16;
            long size = BinaryPrimitives.ReadUInt32LittleEndian(fh);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(fh[4..]);
            int raw = BinaryPrimitives.ReadUInt16LittleEndian(fh[8..]);
            ushort date = BinaryPrimitives.ReadUInt16LittleEndian(fh[10..]);
            ushort time = BinaryPrimitives.ReadUInt16LittleEndian(fh[12..]);
            var attrs = (CabAttributes)BinaryPrimitives.ReadUInt16LittleEndian(fh[14..]);
            byte[] rawName = t.ReadZBytes(ref pos, 256, $"the name of file {i}");

            CabContinuation continuation = CabContinuation.None;
            int folderIndex = raw;
            if (raw >= 0xFFFD)
            {
                continuation = raw switch
                {
                    0xFFFD => CabContinuation.FromPrevious,
                    0xFFFE => CabContinuation.ToNext,
                    _ => CabContinuation.FromPreviousToNext,
                };
                folderIndex = raw == 0xFFFE ? folderCount - 1 : 0;
            }

            if (folderIndex >= folderCount || folderIndex < 0)
            {
                throw Bad(at + 8, $"a folder index below {folderCount} (found {raw})");
            }

            if (continuation == CabContinuation.None && (offset + size > MaxFolderSize || offset + size > (long)folders[folderIndex].BlockCount * MaxBlockUncompressed))
            {
                throw Bad(at, $"a file that lies within its folder's {folders[folderIndex].BlockCount} data blocks (offset {offset}, size {size})");
            }

            string name = DecodeName(rawName, attrs);
            items.Add(new CabEntry
            {
                Name = name,
                Kind = ArchiveEntryKind.File,
                Size = size,
                ModifiedTime = ZipFormat.FromDos(date, time),
                Mode = ModeOf(attrs),
                Index = i,
                FolderIndex = folderIndex,
                RawFolderIndex = raw,
                FolderOffset = offset,
                Attributes = attrs,
                Continuation = continuation,
                RawName = rawName,
                RawDosDateTime = ((uint)date << 16) | time,
            });
        }

        _items = items;
    }

    private static UnixFileMode ModeOf(CabAttributes attrs)
    {
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if ((attrs & CabAttributes.ReadOnly) == 0)
        {
            mode |= UnixFileMode.UserWrite;
        }

        if ((attrs & CabAttributes.Execute) != 0)
        {
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        }

        return mode;
    }

    // [MS-CAB] 2.3: with _A_NAME_IS_UTF the name is UTF-8; otherwise it follows the locale, which we read as ISO 8859-1 unless told.
    private string DecodeName(byte[] raw, CabAttributes attrs)
    {
        Encoding encoding = (attrs & CabAttributes.NameIsUtf) != 0 ? Encoding.UTF8 : _options.LegacyNameEncoding ?? Encoding.Latin1;
        return encoding.GetString(raw).Replace('\\', '/');
    }
}

/// <summary>Random access to the header and table region of the cabinet through a small buffer.</summary>
internal sealed class CabTableReader(Stream stream, long origin, long length)
{
    private readonly byte[] _buffer = new byte[4096];
    private long _bufferStart = -1;
    private int _bufferLength;

    public void ReadExact(long offset, Span<byte> destination)
    {
        if (offset + destination.Length > length)
        {
            throw new CompressionFormatException(CabReader.Name, length, $"{destination.Length} bytes at offset {offset}; the cabinet ends early", true);
        }

        int done = 0;
        while (done < destination.Length)
        {
            long at = offset + done;
            if (_bufferStart < 0 || at < _bufferStart || at >= _bufferStart + _bufferLength)
            {
                Fill(at);
            }

            int from = (int)(at - _bufferStart);
            int n = Math.Min(destination.Length - done, _bufferLength - from);
            _buffer.AsSpan(from, n).CopyTo(destination[done..]);
            done += n;
        }
    }

    public byte[] ReadZBytes(ref long pos, int maxLength, string what)
    {
        byte[] scratch = new byte[maxLength + 1];
        long start = pos;
        for (int i = 0; i <= maxLength; i++)
        {
            Span<byte> one = scratch.AsSpan(i, 1);
            ReadExact(start + i, one);
            if (one[0] == 0)
            {
                pos = start + i + 1;
                return scratch.AsSpan(0, i).ToArray();
            }
        }

        throw new CompressionFormatException(CabReader.Name, start, $"{what}: a NUL-terminated string of at most {maxLength} bytes");
    }

    public string ReadZString(ref long pos, int maxLength, string what) => System.Text.Encoding.Latin1.GetString(ReadZBytes(ref pos, maxLength, what));

    private void Fill(long at)
    {
        stream.Seek(origin + at, SeekOrigin.Begin);
        int want = (int)Math.Min(_buffer.Length, length - at);
        int n = 0;
        while (n < want)
        {
            int r = stream.Read(_buffer, n, want - n);
            if (r == 0)
            {
                break;
            }

            n += r;
        }

        if (n == 0)
        {
            throw new CompressionFormatException(CabReader.Name, at, "more cabinet data", true);
        }

        _bufferStart = at;
        _bufferLength = n;
    }
}
