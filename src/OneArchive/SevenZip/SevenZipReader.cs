using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>
/// Reads 7z archives (the format of 7zFormat.txt): the signature header and its CRCs, plain and encoded headers, packed
/// streams, folders with their coder graphs, substreams, empty files, directories and anti-items, names, times and
/// attributes. Entries are addressed through their folder: a solid folder is decoded from its start up to the entry.
/// </summary>
/// <remarks>
/// <para>The header is read from the end of the archive, so the source must be able to seek; the happy path copies a
/// source that cannot into memory first (bounded by <see cref="ArchiveLimits.MaxTotalSize"/>).</para>
/// <para>Supported coders: Copy, Deflate, BZip2 and, using the method ID that 7-Zip forks assign, Zstandard. A folder
/// that uses any other method (or a multi-stream coder such as BCJ2, or encryption) raises
/// <see cref="ArchiveUnsupportedException"/> naming the method ID when its content is opened; listing still works.</para>
/// <para>Errors: <see cref="CompressionFormatException"/> (format "7z", byte offset in the file or in the decoded
/// header, what was expected), <see cref="CompressionLimitException"/> and <see cref="ArchiveUnsupportedException"/>.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class SevenZipReader : ArchiveReader
{
    private static readonly byte[] s_signature = [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _origin;
    private readonly SevenZipReaderOptions _options;
    private readonly ExpansionMeter _meter;
    private bool _loaded;
    private long _fileLength;
    private SevenZipStreamsInfo _streams = new();
    private long[] _packOffsets = [];
    private List<SevenZipEntry> _items = [];
    private int _next;
    private SevenZipFolderCursor? _seq;
    private int _seqFolder = -1;
    private readonly List<(int Folder, SevenZipFolderCursor Cursor)> _cache = [];
    private SevenZipFolderCursor? _linkCursor;
    private int _linkFolder = -1;
    private readonly SevenZipKeyCache _keys = new();

    /// <summary>Creates a reader over <paramref name="stream"/>, whose current position is the start of the archive.</summary>
    /// <param name="stream">A readable, seekable stream of 7z data.</param>
    /// <param name="options">Reading options; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when the reader is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read or cannot seek.</exception>
    public SevenZipReader(Stream stream, SevenZipReaderOptions? options = null, bool leaveOpen = false)
        : base(ArchiveFormat.SevenZip, (options ?? SevenZipReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable: a 7z header is at the end of the file.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _origin = stream.Position;
        _options = options ?? SevenZipReaderOptions.Default;
        _meter = new ExpansionMeter(Limits, SevenZipFormat.Name);
    }

    /// <inheritdoc />
    protected override string FormatName => SevenZipFormat.Name;

    /// <inheritdoc />
    public override bool SupportsRandomAccess => true;

    /// <summary>Gets the archive format version from the signature header, as <c>major.minor</c>.</summary>
    public Version FormatVersion { get; private set; } = new(0, 0);

    /// <summary>Gets a value indicating whether the header was stored compressed.</summary>
    public bool IsHeaderEncoded { get; private set; }

    /// <summary>Gets the offset of the packed streams from the end of the signature header.</summary>
    public long PackPosition
    {
        get
        {
            EnsureLoaded();
            return _streams.PackPos;
        }
    }

    /// <summary>Gets the sizes of the archive's packed streams.</summary>
    public IReadOnlyList<long> PackSizes
    {
        get
        {
            EnsureLoaded();
            return _streams.PackSizes;
        }
    }

    /// <summary>Gets the folders, with their coder graphs.</summary>
    public IReadOnlyList<SevenZipFolder> Folders
    {
        get
        {
            EnsureLoaded();
            return _streams.Folders;
        }
    }

    /// <summary>Gets every file record of the header in order, anti-items included. Limits are applied by <see cref="GetNextEntry"/>, not here.</summary>
    public IReadOnlyList<SevenZipEntry> Items
    {
        get
        {
            EnsureLoaded();
            return _items;
        }
    }

    /// <summary>Advances to the next entry (anti-items are skipped).</summary>
    /// <returns>The entry, or <see langword="null"/> at the end of the archive.</returns>
    /// <exception cref="CompressionFormatException">The header is malformed or truncated.</exception>
    /// <exception cref="CompressionLimitException">A limit was exceeded.</exception>
    /// <exception cref="ArchiveUnsupportedException">The header uses an unsupported feature.</exception>
    public new SevenZipEntry? GetNextEntry() => (SevenZipEntry?)base.GetNextEntry();

    /// <summary>Advances to the next entry (anti-items are skipped).</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> at the end of the archive.</returns>
    public new async ValueTask<SevenZipEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (SevenZipEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        while (_next < _items.Count)
        {
            SevenZipEntry e = _items[_next++];
            if (!e.IsAnti)
            {
                return new ValueTask<ArchiveEntry?>(e);
            }
        }

        return new ValueTask<ArchiveEntry?>((ArchiveEntry?)null);
    }

    /// <inheritdoc />
    /// <exception cref="ArchiveUnsupportedException">The entry's folder uses an unsupported method.</exception>
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        SevenZipEntry entry = (SevenZipEntry?)CurrentEntry ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        if (entry.FolderIndex < 0 || entry.Size == 0)
        {
            return new MemoryReadStream(ReadOnlyMemory<byte>.Empty);
        }

        bool encrypted = RequirePassword(entry);
        if (_seq is null || _seqFolder != entry.FolderIndex)
        {
            _seq?.Dispose();
            _seq = NewCursor(entry.FolderIndex);
            _seqFolder = entry.FolderIndex;
        }

        return new SevenZipEntryStream(_seq, null, entry, _options.VerifyChecksum, encrypted);
    }

    /// <inheritdoc />
    public override Stream OpenEntryStream(ArchiveEntry entry)
    {
        ThrowIfDisposed();
        EnsureLoaded();
        if (entry is not SevenZipEntry e || e.Index < 0 || e.Index >= _items.Count || !ReferenceEquals(_items[e.Index], e))
        {
            throw new ArgumentException("The entry was not read by this SevenZipReader.", nameof(entry));
        }

        if (e.FolderIndex < 0 || e.Size == 0)
        {
            return new MemoryReadStream(ReadOnlyMemory<byte>.Empty);
        }

        // Random access: lease a decoder for the folder (a recently used one when there is one, so that reading a solid
        // folder's entries one after another does not decode it from the start each time) and hand it back on dispose.
        int folder = e.FolderIndex;
        bool encrypted = RequirePassword(e);
        SevenZipFolderCursor? cursor = null;
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
        return new SevenZipEntryStream(cursor, c => ReturnCursor(folder, c), e, _options.VerifyChecksum, encrypted);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _seq?.Dispose();
            _linkCursor?.Dispose();
            _keys.Dispose();
            foreach ((_, SevenZipFolderCursor c) in _cache)
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

    private static bool IsEncrypted(SevenZipFolder folder)
    {
        foreach (SevenZipCoder c in folder.Coders)
        {
            if (c.MethodId == SevenZipFormat.MethodAes)
            {
                return true;
            }
        }

        return false;
    }

    // Returns whether the entry's folder is encrypted; throws, naming the entry, when it is and there is no password.
    private bool RequirePassword(SevenZipEntry entry)
    {
        if (!IsEncrypted(_streams.Folders[entry.FolderIndex]))
        {
            return false;
        }

        if (_options.Password.IsEmpty)
        {
            throw new ArchivePasswordException(SevenZipFormat.Name, entry.Name, ArchivePasswordProblem.Required);
        }

        return true;
    }

    // At most two idle decoders are kept: each holds a dictionary's worth of memory.
    private void ReturnCursor(int folder, SevenZipFolderCursor cursor)
    {
        if (IsDisposed)
        {
            cursor.Dispose();
            return;
        }

        _cache.Add((folder, cursor));
        if (_cache.Count > 2)
        {
            _cache[0].Cursor.Dispose();
            _cache.RemoveAt(0);
        }
    }

    // ---- loading ----

    private SevenZipFolderCursor NewCursor(int folderIndex)
    {
        SevenZipFolder folder = _streams.Folders[folderIndex];
        long errorOffset = folder.PackedStreams.Count > 0 ? _packOffsets[folder.FirstPackStream] : 0;
        return new SevenZipFolderCursor(() => SevenZipFolderDecoder.Open(
            folder,
            k => new SevenZipPackStream(_stream, _packOffsets[folder.FirstPackStream + k], _streams.PackSizes[folder.FirstPackStream + k], errorOffset, _meter),
            errorOffset,
            Limits,
            _options,
            _keys), _meter);
    }

    private void EnsureLoaded()
    {
        ThrowIfDisposed();
        if (_loaded)
        {
            return;
        }

        Load();
        _loaded = true;
    }

    private static CompressionFormatException Bad(long offset, string expected, bool truncated = false) =>
        new(SevenZipFormat.Name, offset, expected, truncated);

    private void Load()
    {
        _fileLength = _stream.Length - _origin;
        Span<byte> sig = stackalloc byte[SevenZipFormat.SignatureHeaderSize];
        _stream.Position = _origin;
        int got = _stream.ReadAtLeast(sig, sig.Length, throwOnEndOfStream: false);
        if (got < sig.Length)
        {
            throw Bad(got, "a 32-byte signature header", true);
        }

        // 7zFormat.txt "SignatureHeader".
        if (!sig[..6].SequenceEqual(s_signature))
        {
            throw Bad(0, "the 7z signature '7z' BC AF 27 1C");
        }

        if (sig[6] != 0)
        {
            throw new ArchiveUnsupportedException(SevenZipFormat.Name, $"format version {sig[6]}.{sig[7]}");
        }

        FormatVersion = new Version(sig[6], sig[7]);
        uint startCrc = BinaryPrimitives.ReadUInt32LittleEndian(sig[8..]);
        if (Crc32.Compute(sig[12..32]) != startCrc)
        {
            throw Bad(8, "a start-header CRC that matches bytes 12 to 31");
        }

        long nextOffset = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(sig[12..]), long.MaxValue);
        ulong nextSizeRaw = BinaryPrimitives.ReadUInt64LittleEndian(sig[20..]);
        uint nextCrc = BinaryPrimitives.ReadUInt32LittleEndian(sig[28..]);
        if (nextSizeRaw == 0)
        {
            return; // An empty archive.
        }

        if (nextSizeRaw > (ulong)_options.MaxHeaderSize)
        {
            throw new CompressionLimitException(SevenZipFormat.Name, nameof(SevenZipReaderOptions.MaxHeaderSize), _options.MaxHeaderSize, (long)Math.Min(nextSizeRaw, long.MaxValue));
        }

        int nextSize = (int)nextSizeRaw;
        long headerPos = SevenZipFormat.SignatureHeaderSize + nextOffset;
        if (nextOffset < 0 || headerPos > _fileLength || nextSize > _fileLength - headerPos)
        {
            throw Bad(Math.Min(headerPos, _fileLength), $"a header of {nextSize} bytes at offset {headerPos}; the archive ends early", true);
        }

        byte[] header = new byte[nextSize];
        _stream.Position = _origin + headerPos;
        _stream.ReadExactly(header);
        if (Crc32.Compute(header) != nextCrc)
        {
            throw Bad(headerPos, "a header whose CRC matches the signature header");
        }

        var parser = new SevenZipHeaderParser(_options);
        long baseOffset = headerPos;
        bool decoded = false;
        for (int depth = 0; ; depth++)
        {
            var r = new SevenZipByteReader(header, 0, header.Length, baseOffset, decoded);
            if (header[0] != SevenZipFormat.EncodedHeader)
            {
                (_streams, List<SevenZipFileRecord> files) = parser.ParseHeader(r);
                FinishStreams(_streams);
                BuildItems(files);
                return;
            }

            if (depth >= 4)
            {
                throw r.Bad("a header that is encoded at most four times");
            }

            IsHeaderEncoded = true;
            r.ReadByte();
            SevenZipStreamsInfo info = parser.ParseStreamsInfo(r);
            if (info.Folders.Length != 1)
            {
                throw r.Bad("exactly one folder in an encoded header");
            }

            FinishStreams(info);
            SevenZipFolder folder = info.Folders[0];
            if (folder.UnpackSize > _options.MaxHeaderSize)
            {
                throw new CompressionLimitException(SevenZipFormat.Name, nameof(SevenZipReaderOptions.MaxHeaderSize), _options.MaxHeaderSize, folder.UnpackSize);
            }

            _streams = info; // The cursor reads packed streams through the current layout.
            _packOffsets = ComputePackOffsets(info);
            byte[] plain = new byte[folder.UnpackSize];
            bool encrypted = IsEncrypted(folder);
            if (encrypted && _options.Password.IsEmpty)
            {
                throw new ArchivePasswordException(SevenZipFormat.Name, string.Empty, ArchivePasswordProblem.Required);
            }

            try
            {
                using (SevenZipFolderCursor cursor = NewCursor(0))
                {
                    int filled = 0;
                    while (filled < plain.Length)
                    {
                        int n = cursor.Read(plain.AsSpan(filled));
                        if (n == 0)
                        {
                            throw Bad(_packOffsets[0], "the whole encoded header", true);
                        }

                        filled += n;
                    }
                }

                if (folder.Crc is { } crc && Crc32.Compute(plain) != crc)
                {
                    throw Bad(_packOffsets.Length > 0 ? _packOffsets[0] : 0, "an encoded header whose CRC matches the folder's");
                }
            }
            catch (CompressionFormatException ex) when (encrypted)
            {
                // 7z stores no password check: a wrong password decrypts to noise that fails the header's CRC or decoder.
                throw new ArchivePasswordException(SevenZipFormat.Name, string.Empty, ArchivePasswordProblem.Incorrect, ex, "7z cannot tell a wrong password from a damaged header");
            }

            if (plain.Length == 0)
            {
                throw Bad(baseOffset, "a non-empty header");
            }

            header = plain;
            baseOffset = 0;
            decoded = true;
        }
    }

    private long[] ComputePackOffsets(SevenZipStreamsInfo info)
    {
        var offsets = new long[info.PackSizes.Length];
        long pos = SevenZipFormat.SignatureHeaderSize;
        if (info.PackPos > _fileLength - pos)
        {
            throw Bad(pos, $"packed streams starting at offset {pos + info.PackPos}; the archive ends early", true);
        }

        pos += info.PackPos;
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = _origin + pos;
            long size = info.PackSizes[i];
            if (size > _fileLength - pos)
            {
                throw Bad(pos, $"a packed stream of {size} bytes; the archive ends early", true);
            }

            pos += size;
        }

        return offsets;
    }

    private void FinishStreams(SevenZipStreamsInfo info)
    {
        foreach (SevenZipFolder f in info.Folders)
        {
            if (f.UnpackSize > Limits.MaxTotalSize)
            {
                throw new CompressionLimitException(SevenZipFormat.Name, nameof(ArchiveLimits.MaxTotalSize), Limits.MaxTotalSize, f.UnpackSize);
            }
        }

        _ = ComputePackOffsets(info);
    }

    private void BuildItems(List<SevenZipFileRecord> files)
    {
        _packOffsets = ComputePackOffsets(_streams);
        var items = new List<SevenZipEntry>(files.Count);
        int folder = 0;
        int sub = 0;
        long offset = 0;
        for (int i = 0; i < files.Count; i++)
        {
            SevenZipFileRecord f = files[i];
            int folderIndex = -1;
            long size = 0;
            uint? crc = null;
            long folderOffset = 0;
            if (!f.EmptyStream)
            {
                while (folder < _streams.Folders.Length && sub >= _streams.Folders[folder].SubStreamSizes.Count)
                {
                    folder++;
                    sub = 0;
                    offset = 0;
                }

                if (folder >= _streams.Folders.Length)
                {
                    throw Bad(0, "a data stream for every file with data");
                }

                SevenZipFolder fo = _streams.Folders[folder];
                folderIndex = folder;
                size = fo.SubStreamSizes[sub];
                crc = fo.SubStreamCrcs[sub];
                folderOffset = offset;
                offset += size;
                sub++;
            }

            items.Add(MakeEntry(i, f, folderIndex, size, crc, folderOffset));
        }

        _items = items;
    }

    private SevenZipEntry MakeEntry(int index, SevenZipFileRecord f, int folderIndex, long size, uint? crc, long folderOffset)
    {
        uint? attr = f.Attributes;
        uint? unix = attr is { } a && (a & 0x8000) != 0 ? a >> 16 : null;
        ArchiveEntryKind kind = ArchiveEntryKind.File;
        if (f.EmptyStream && !f.EmptyFile)
        {
            kind = ArchiveEntryKind.Directory;
        }
        else if (attr is { } da && (da & 0x10) != 0)
        {
            kind = ArchiveEntryKind.Directory;
        }
        else if (unix is { } m)
        {
            kind = (m & 0xF000) switch
            {
                0xA000 => ArchiveEntryKind.Symlink,
                0x4000 => ArchiveEntryKind.Directory,
                0x2000 => ArchiveEntryKind.CharacterDevice,
                0x6000 => ArchiveEntryKind.BlockDevice,
                0x1000 => ArchiveEntryKind.Fifo,
                _ => ArchiveEntryKind.File,
            };
        }

        if (kind == ArchiveEntryKind.Directory)
        {
            // A directory carries no data; whatever the header says is ignored.
            size = 0;
            folderIndex = -1;
        }

        string name = f.Name;
        while (name.Length > 1 && name[^1] == '/')
        {
            name = name[..^1];
        }

        string? linkTarget = null;
        if (kind == ArchiveEntryKind.Symlink)
        {
            linkTarget = ReadLinkTarget(folderIndex, folderOffset, size);
        }

        return new SevenZipEntry
        {
            Name = name,
            Kind = kind,
            Size = kind == ArchiveEntryKind.File ? size : 0,
            ModifiedTime = f.MTime,
            AccessTime = f.ATime,
            CreationTime = f.CTime,
            Mode = unix is { } um ? (UnixFileMode)(um & 0xFFF) : null,
            LinkTarget = linkTarget,
            Index = index,
            Attributes = attr,
            IsEmptyStream = f.EmptyStream,
            IsAnti = f.Anti,
            Crc32 = crc,
            FolderIndex = folderIndex,
            FolderOffset = folderOffset,
        };
    }

    // A symbolic link stores its target as the file's content.
    private string? ReadLinkTarget(int folderIndex, long offset, long size)
    {
        if (folderIndex < 0 || size == 0)
        {
            return string.Empty;
        }

        if (size > (long)Limits.MaxPathLength * 4)
        {
            throw new CompressionLimitException(SevenZipFormat.Name, nameof(ArchiveLimits.MaxPathLength), Limits.MaxPathLength, size);
        }

        // An encrypted link is listed with an empty target when there is no password or it is wrong; extraction refuses it.
        if (IsEncrypted(_streams.Folders[folderIndex]))
        {
            if (_options.Password.IsEmpty)
            {
                return string.Empty;
            }

            try
            {
                return ReadLinkTargetCore(folderIndex, offset, size);
            }
            catch (CompressionFormatException)
            {
                _linkCursor?.Dispose();
                _linkCursor = null;
                return string.Empty;
            }
        }

        return ReadLinkTargetCore(folderIndex, offset, size);
    }

    private string? ReadLinkTargetCore(int folderIndex, long offset, long size)
    {
        if (_linkCursor is null || _linkFolder != folderIndex || _linkCursor.Position > offset)
        {
            _linkCursor?.Dispose();
            _linkCursor = NewCursor(folderIndex);
            _linkFolder = folderIndex;
        }

        _linkCursor.SkipTo(offset);
        byte[] buf = new byte[size];
        int filled = 0;
        while (filled < buf.Length)
        {
            int n = _linkCursor.Read(buf.AsSpan(filled));
            if (n == 0)
            {
                throw Bad(0, "the symbolic link's target", true);
            }

            filled += n;
        }

        return Internal.PathRules.DecodeName(buf);
    }
}

/// <summary>A forward-only read position in one folder's unpacked stream; going back re-decodes from the start.</summary>
internal sealed class SevenZipFolderCursor(Func<Stream> open, ExpansionMeter meter) : IDisposable
{
    private Stream? _stream;

    public long Position { get; private set; }

    public int Read(Span<byte> buffer)
    {
        try
        {
            _stream ??= open();
            int n = _stream.Read(buffer);
            Position += n;
            meter.AddProduced(n);
            return n;
        }
        catch
        {
            // A decoder that failed cannot go on; the next use starts the folder again from its beginning.
            _stream?.Dispose();
            _stream = null;
            Position = 0;
            throw;
        }
    }

    public void SkipTo(long target)
    {
        if (target < Position)
        {
            _stream?.Dispose();
            _stream = null;
            Position = 0;
        }

        if (target == Position)
        {
            return;
        }

        byte[] scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (Position < target)
            {
                int want = (int)Math.Min(scratch.Length, target - Position);
                if (Read(scratch.AsSpan(0, want)) == 0)
                {
                    throw new CompressionFormatException(SevenZipFormat.Name, 0, "the folder's unpacked data up to the entry", true);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}

/// <summary>The content of one entry: a window of its folder's unpacked stream, with the entry's CRC-32 checked at the end.</summary>
internal sealed class SevenZipEntryStream(SevenZipFolderCursor cursor, Action<SevenZipFolderCursor>? release, SevenZipEntry entry, bool verify, bool encrypted = false) : Stream
{
    private long _pos;
    private uint _crc;
    private bool _released;
    private bool _crcValid = verify && entry.Crc32 is not null;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => entry.Size;

    public override long Position
    {
        get => _pos;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!encrypted)
        {
            return ReadCore(buffer);
        }

        try
        {
            return ReadCore(buffer);
        }
        catch (CompressionFormatException ex)
        {
            // 7z stores no password check: a wrong password decrypts to noise that fails the CRC or the decoder.
            throw new ArchivePasswordException(SevenZipFormat.Name, entry.Name, ArchivePasswordProblem.Incorrect, ex, "7z cannot tell a wrong password from damaged data");
        }
    }

    private int ReadCore(Span<byte> buffer)
    {
        long left = entry.Size - _pos;
        if (left <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > left)
        {
            buffer = buffer[..(int)left];
        }

        cursor.SkipTo(entry.FolderOffset + _pos);
        int n = cursor.Read(buffer);
        if (n == 0)
        {
            throw new CompressionFormatException(SevenZipFormat.Name, 0, $"{left} more bytes of '{entry.Name}'; the folder ended", true);
        }

        if (_crcValid)
        {
            _crc = Crc32.Update(_crc, buffer[..n]);
        }

        _pos += n;
        if (_pos == entry.Size && _crcValid && _crc != entry.Crc32)
        {
            throw new CompressionFormatException(SevenZipFormat.Name, 0, $"CRC-32 {entry.Crc32:X8} for '{entry.Name}', computed {_crc:X8}");
        }

        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => entry.Size + offset,
        };
        if (target < 0 || target > entry.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (target != _pos)
        {
            _crcValid = false; // The checksum only means something for a straight read.
            _pos = target;
        }

        return _pos;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && release is not null && !_released)
        {
            _released = true;
            release(cursor);
        }

        base.Dispose(disposing);
    }
}
