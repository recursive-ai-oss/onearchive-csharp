using System.Buffers.Binary;
using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneArchive.Rar;

/// <summary>
/// Reads RAR 5 archives (RARLAB's "RAR 5.0 archive format" technote): lists every entry with its metadata and extracts
/// entries stored without compression (method 0), across the volumes of a multi-volume set.
/// </summary>
/// <remarks>
/// <para><b>What works.</b> File, directory, symbolic-link, hard-link and file-copy entries with names, sizes, attributes,
/// times (down to 100 ns), Unix owner and group, CRC-32 and BLAKE2sp checksums, the archive comment, the locator and
/// metadata records, and solid, locked and recovery-record flags. Stored data is checked against its CRC-32 or BLAKE2sp
/// when it is read to its end.</para>
/// <para><b>What does not, yet.</b> Entries compressed with methods 1 to 5 list fully but raise
/// <see cref="RarUnsupportedException"/> (kind <see cref="RarUnsupportedKind.CompressedEntry"/>) when opened. RAR 4 archives
/// and archives with encrypted headers raise the same exception when the reader starts; an entry whose data is encrypted raises it when
/// opened. The shape of <see cref="RarEntry"/> already carries what a decoder needs (method, version, dictionary size, solid flag).</para>
/// <para><b>Reading order.</b> The headers are read in file order, one after the other, so the source need not seek. The quick-open
/// data and the locator are never used for listing: what is listed is what is extracted. A self-extracting module in front of the
/// signature is skipped up to <see cref="RarReaderOptions.MaxSfxSize"/>.</para>
/// <para><b>Volumes.</b> Give the reader the first volume and a <see cref="RarReaderOptions.Volumes"/> function; it asks for each
/// following volume when it reaches the end of the previous one, and a file that spans volumes reads as one stream. When a
/// needed volume is missing, <see cref="CompressionFormatException"/> (truncated) is raised at the point it is needed.
/// <see cref="Archive"/> supplies the function from the file name when it opens a path.</para>
/// <para><b>Errors.</b> <see cref="CompressionFormatException"/> names the byte offset (in the volume, counted from the first byte of
/// the file, including any self-extracting module) and what was expected: a wrong header CRC-32, a header size over the technote's
/// 2 MiB, a vint longer than 64 bits, an extra-area record that runs past its area, a data area past the end of the volume.
/// Limits raise <see cref="CompressionLimitException"/>.</para>
/// </remarks>
public sealed class RarReader : ArchiveReader
{
    internal const string Name = "rar";

    // Technote, "RAR 5.0 signature": 52 61 72 21 1A 07 01 00. The RAR 4 signature is 52 61 72 21 1A 07 00.
    private static ReadOnlySpan<byte> Rar5Signature => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    private static ReadOnlySpan<byte> SignaturePrefix => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];

    // Technote, "General archive block format": the header size field is at most 3 bytes, so a header is at most 2 MiB.
    private const int MaxHeaderSize = 2 * 1024 * 1024;

    private const int MaxCommentSize = 1 << 20;

    private readonly Stream _firstStream;
    private readonly bool _leaveOpen;
    private readonly RarReaderOptions _options;
    private readonly byte[] _small = new byte[8];

    private Stream? _wrapper;        // a prefixed wrapper over the current volume's stream, when a signature search read ahead
    private Stream? _volumeStream;   // the current volume's stream as given
    private ForwardCursor _cursor = null!;
    private long _sfxOffset;
    private int _volume;
    private bool _started;
    private bool _ended;
    private RarArchiveHeader? _header;
    private RarBlock? _pending;
    private int _generation;

    // State of the entry most recently returned.
    private RarEntry? _entry;
    private bool _entryDone = true;
    private bool _streamOpened;
    private long _dataLeft;
    private bool _moreParts;
    private long _entryRead;
    private bool _useCrc;
    private bool _useHash;
    private uint _storedCrc;
    private bool _hasStoredCrc;
    private byte[]? _storedHash;
    private uint _crcPart;
    private uint _crcWhole;
    private Blake2sp? _hashPart;
    private Blake2sp? _hashWhole;

    /// <summary>Creates a reader over the first (or only) volume of a RAR 5 archive.</summary>
    /// <param name="stream">A readable stream positioned at the start of the archive. It need not seek.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the reader is disposed.</param>
    /// <exception cref="ArgumentException">The stream cannot be read.</exception>
    public RarReader(Stream stream, RarReaderOptions? options = null, bool leaveOpen = false)
        : base(ArchiveFormat.Rar, (options ?? RarReaderOptions.Default).Limits)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _firstStream = stream;
        _volumeStream = stream;
        _leaveOpen = leaveOpen;
        _options = options ?? RarReaderOptions.Default;
    }

    /// <inheritdoc />
    protected override string FormatName => Name;

    /// <summary>Gets the archive header, reading it first if needed.</summary>
    /// <exception cref="CompressionFormatException">The archive is malformed.</exception>
    /// <exception cref="RarUnsupportedException">The archive is RAR 4 or has encrypted headers.</exception>
    public RarArchiveHeader Header
    {
        get
        {
            ThrowIfDisposed();
            Internal.Sync.Result(EnsureStartedAsync(false, default));
            return _header!;
        }
    }

    /// <summary>Reads the next entry.</summary>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new RarEntry? GetNextEntry() => (RarEntry?)base.GetNextEntry();

    /// <summary>Reads the next entry.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> after the last one.</returns>
    public new async ValueTask<RarEntry?> GetNextEntryAsync(CancellationToken cancellationToken = default) =>
        (RarEntry?)await base.GetNextEntryAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Opens the data of the current entry.</summary>
    /// <returns>A read-only stream that ends at the entry's size, checking the stored checksum at its end. Empty for entries without data.</returns>
    /// <exception cref="InvalidOperationException">There is no current entry, or its stream was already opened.</exception>
    /// <exception cref="RarUnsupportedException">The entry is compressed or encrypted.</exception>
    public override Stream OpenEntryStream()
    {
        ThrowIfDisposed();
        RarEntry entry = (RarEntry?)CurrentEntry ?? throw new InvalidOperationException("There is no current entry; call GetNextEntry first.");
        if (entry.Kind != ArchiveEntryKind.File)
        {
            return new MemoryReadStream(ReadOnlyMemory<byte>.Empty);
        }

        if (entry.IsEncrypted)
        {
            throw new RarUnsupportedException(RarUnsupportedKind.EncryptedEntry, $"encrypted entries are not supported yet ('{RarText.Printable(entry.Name)}')", entry.Name, entry.Method, entry.DictionarySize);
        }

        if (!entry.IsStored)
        {
            throw new RarUnsupportedException(
                RarUnsupportedKind.CompressedEntry,
                $"RAR compressed entries are not supported yet ('{RarText.Printable(entry.Name)}' uses method {entry.Method}, dictionary size {entry.DictionarySize} bytes)",
                entry.Name,
                entry.Method,
                entry.DictionarySize);
        }

        if (_streamOpened)
        {
            throw new InvalidOperationException("The stream of the current entry was already opened.");
        }

        _streamOpened = true;
        return new RarEntryStream(this, _generation, entry.Size);
    }

    /// <inheritdoc />
    protected override async ValueTask<ArchiveEntry?> ReadNextEntryAsync(bool async, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(async, cancellationToken).ConfigureAwait(false);
        await SkipEntryAsync(async, cancellationToken).ConfigureAwait(false);
        _generation++;
        _entry = null;
        _streamOpened = false;
        while (!_ended)
        {
            RarBlock b = _pending ?? await ReadRequiredBlockAsync(async, "the next header or the end-of-archive header", cancellationToken).ConfigureAwait(false);
            _pending = null;
            switch (b.Type)
            {
                case 2:
                    if (b.ContinuedFromPrevious)
                    {
                        throw new CompressionFormatException(Name, b.Offset, "a file header that begins a file (this one continues a file from the previous volume; open the first volume of the set)");
                    }

                    return StartEntry(b);
                case 5:
                    await FinishVolumeAsync(b, async, cancellationToken).ConfigureAwait(false);
                    break;
                case 1:
                    throw new CompressionFormatException(Name, b.Offset, "a file, service or end-of-archive header (found a second main archive header)");
                case 4:
                    throw new CompressionFormatException(Name, b.Offset, "no archive encryption header after the main archive header");
                default:
                    // Service headers (comment, quick open, ACL, streams, recovery record) and unknown types: not entries.
                    await SkipDataAsync(b, async, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return null;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CloseCurrentVolume();
        }

        base.Dispose(disposing);
    }

    // Archive.Open calls this with the first bytes so that a RAR 4 file is refused when it is opened, not at the first entry.
    internal static void ThrowIfRar4(ReadOnlySpan<byte> prefix)
    {
        if (prefix.StartsWith(SignaturePrefix) && prefix.Length >= 7 && prefix[6] == 0)
        {
            throw new RarUnsupportedException(RarUnsupportedKind.Rar4Archive, "RAR 4 archives are not supported yet (only the RAR 5 format can be read)");
        }
    }

    // ---- starting -----------------------------------------------------------------------------------------------

    private async ValueTask EnsureStartedAsync(bool async, CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await OpenVolumeAsync(_firstStream, 0, async, ct).ConfigureAwait(false);
    }

    // Positions the cursor on the signature of a volume, then reads the main archive header (and, in the first volume, the comment).
    private async ValueTask OpenVolumeAsync(Stream stream, int volume, bool async, CancellationToken ct)
    {
        _volumeStream = stream;
        _wrapper = null;
        _volume = volume;
        _sfxOffset = 0;
        Stream positioned = await FindSignatureAsync(stream, volume == 0 ? _options.MaxSfxSize : 0, async, ct).ConfigureAwait(false);
        _cursor = new ForwardCursor(positioned, Name);
        if (!ReferenceEquals(positioned, stream))
        {
            _wrapper = positioned;
        }

        byte[] sig = new byte[8];
        int got = await _cursor.ReadFullAsync(async, sig, ct).ConfigureAwait(false);
        if (got < 8 || !sig.AsSpan().SequenceEqual(Rar5Signature))
        {
            throw new CompressionFormatException(Name, _sfxOffset, "the RAR 5 signature (52 61 72 21 1A 07 01 00)", got < 8);
        }

        RarBlock main = await ReadRequiredBlockAsync(async, "the main archive header", ct).ConfigureAwait(false);
        if (main.Type == 4)
        {
            throw new RarUnsupportedException(RarUnsupportedKind.EncryptedHeaders, "encrypted headers are not supported yet (the archive was made with a header password)");
        }

        if (main.Type != 1)
        {
            throw new CompressionFormatException(Name, main.Offset, $"the main archive header (type 1), found type {main.Type}");
        }

        RarArchiveHeader header = ParseMainHeader(main, volume);
        if (volume == 0)
        {
            if (header.IsVolume && header.VolumeNumber != 0)
            {
                throw new ArchiveUnsupportedException(Name, $"reading from volume {header.VolumeNumber + 1} of a multi-volume set (start with the first volume)");
            }

            // Technote, "Archive comment header": the comment service header follows the main header, stored.
            RarBlock? next = await ReadBlockAsync(async, ct).ConfigureAwait(false);
            string? comment = null;
            if (next is { Type: 3 } svc && ServiceName(svc) == "CMT")
            {
                comment = await ReadCommentAsync(svc, async, ct).ConfigureAwait(false);
                next = null;
            }

            _header = Copy(header, comment);
            _pending = next;
        }
        else
        {
            _header = header;
        }
    }

    private static RarArchiveHeader Copy(RarArchiveHeader h, string? comment) => new()
    {
        SignatureOffset = h.SignatureOffset,
        ArchiveFlags = h.ArchiveFlags,
        VolumeNumber = h.VolumeNumber,
        QuickOpenOffset = h.QuickOpenOffset,
        RecoveryRecordOffset = h.RecoveryRecordOffset,
        OriginalName = h.OriginalName,
        OriginalCreationTime = h.OriginalCreationTime,
        Comment = comment,
    };

    // Technote, "RAR 5.0 signature": the signature is searched for from the start, up to the largest module size the technote names.
    private async ValueTask<Stream> FindSignatureAsync(Stream stream, int maxSfx, bool async, CancellationToken ct)
    {
        long origin = stream.CanSeek ? stream.Position : 0;
        int limit = maxSfx + 8;
        byte[] buffer = new byte[Math.Min(limit, 64 * 1024)];
        int total = 0;
        int found = -1;
        while (true)
        {
            if (total == buffer.Length)
            {
                if (total >= limit)
                {
                    break;
                }

                Array.Resize(ref buffer, Math.Min(limit, buffer.Length * 2));
            }

            int n = async
                ? await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)
                : stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
            found = IndexOfSignature(buffer.AsSpan(0, total), out bool isRar4);
            if (found >= 0)
            {
                if (isRar4)
                {
                    throw new RarUnsupportedException(RarUnsupportedKind.Rar4Archive, "RAR 4 archives are not supported yet (only the RAR 5 format can be read)");
                }

                break;
            }
        }

        if (found < 0)
        {
            throw new CompressionFormatException(Name, 0, "the RAR 5 signature (52 61 72 21 1A 07 01 00) at the start of the file or within a self-extracting module", total < limit);
        }

        _sfxOffset = found;
        if (stream.CanSeek)
        {
            stream.Seek(origin + found, SeekOrigin.Begin);
            return stream;
        }

        byte[] rest = buffer.AsSpan(found, total - found).ToArray();
        return new PrefixedStream(rest, rest.Length, stream, leaveOpen: true);
    }

    private static int IndexOfSignature(ReadOnlySpan<byte> data, out bool isRar4)
    {
        isRar4 = false;
        int from = 0;
        while (from < data.Length)
        {
            int i = data[from..].IndexOf(SignaturePrefix);
            if (i < 0)
            {
                return -1;
            }

            i += from;
            if (i + 7 > data.Length)
            {
                return -1;
            }

            if (data[i + 6] == 0)
            {
                isRar4 = true;
                return i;
            }

            if (data[i + 6] == 1 && i + 8 <= data.Length && data[i + 7] == 0)
            {
                return i;
            }

            from = i + 1;
        }

        return -1;
    }

    // ---- headers ------------------------------------------------------------------------------------------------

    private async ValueTask<RarBlock> ReadRequiredBlockAsync(bool async, string expected, CancellationToken ct) =>
        await ReadBlockAsync(async, ct).ConfigureAwait(false)
        ?? throw new CompressionFormatException(Name, _sfxOffset + _cursor.Pos, expected, true);

    // Technote, "General archive block format": CRC32 (4), header size (vint, at most 3 bytes), then that many bytes.
    private async ValueTask<RarBlock?> ReadBlockAsync(bool async, CancellationToken ct)
    {
        long start = _cursor.Pos;
        long at = _sfxOffset + start;
        int got = await _cursor.ReadFullAsync(async, _small.AsMemory(0, 5), ct).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < 5)
        {
            throw new CompressionFormatException(Name, at, "a header (CRC-32 and size)", true);
        }

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(_small);
        byte[] size = new byte[3];
        size[0] = _small[4];
        int sizeLen = 1;
        while ((size[sizeLen - 1] & 0x80) != 0)
        {
            if (sizeLen == 3)
            {
                throw new CompressionFormatException(Name, at + 4, "a header size field of at most 3 bytes");
            }

            int more = await _cursor.ReadFullAsync(async, _small.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (more < 1)
            {
                throw new CompressionFormatException(Name, at + 4 + sizeLen, "the rest of the header size field", true);
            }

            size[sizeLen++] = _small[0];
        }

        int headerSize = 0;
        for (int i = 0; i < sizeLen; i++)
        {
            headerSize |= (size[i] & 0x7F) << (7 * i);
        }

        if (headerSize < 2 || headerSize > MaxHeaderSize)
        {
            throw new CompressionFormatException(Name, at + 4, $"a header size between 2 and {MaxHeaderSize} bytes (found {headerSize})");
        }

        long bodyStart = start + 4 + sizeLen;
        if (_cursor.Seekable && bodyStart + headerSize > _cursor.Length)
        {
            throw new CompressionFormatException(Name, at, $"a header of {headerSize} bytes, but only {Math.Max(0, _cursor.Length - bodyStart)} bytes are left", true);
        }

        byte[] body = new byte[headerSize];
        got = await _cursor.ReadFullAsync(async, body, ct).ConfigureAwait(false);
        if (got < headerSize)
        {
            throw new CompressionFormatException(Name, at + 4 + sizeLen + got, $"a header of {headerSize} bytes", true);
        }

        uint crc = Crc32.Update(Crc32.Compute(size.AsSpan(0, sizeLen)), body);
        if (crc != storedCrc)
        {
            throw new CompressionFormatException(Name, at, $"header CRC-32 {storedCrc:X8}, computed {crc:X8}");
        }

        var block = new RarBlock { Offset = at, Body = body, BodyOffset = _sfxOffset + bodyStart };
        var r = new RarFieldReader(body, block.BodyOffset);
        block.Type = r.ReadSmall("header type", int.MaxValue);
        block.Flags = r.ReadVint("header flags");
        int extraSize = 0;
        if (block.HasExtra)
        {
            extraSize = r.ReadSmall("extra area size", body.Length);
        }

        if (block.HasData)
        {
            block.DataSize = r.ReadVint("data area size");
        }

        block.FieldsStart = r.Position;
        if (extraSize > body.Length - r.Position)
        {
            throw new CompressionFormatException(Name, block.BodyOffset, $"an extra area of {extraSize} bytes inside a header with {body.Length - r.Position} bytes left");
        }

        block.ExtraStart = body.Length - extraSize;
        block.DataOffset = _cursor.Pos;
        if (block.DataSize > long.MaxValue)
        {
            throw new CompressionFormatException(Name, block.BodyOffset, "a data area size below 2^63");
        }

        return block;
    }

    private RarArchiveHeader ParseMainHeader(RarBlock b, int expectedVolume)
    {
        var f = new RarFieldReader(b.Body.AsSpan(0, b.ExtraStart), b.BodyOffset, b.FieldsStart);
        int flags = f.ReadSmall("archive flags", int.MaxValue);
        long volumeNumber = 0;
        if ((flags & 0x2) != 0)
        {
            volumeNumber = (long)Math.Min(f.ReadVint("volume number"), long.MaxValue);
        }

        long? quickOpen = null;
        long? recovery = null;
        string? originalName = null;
        DateTimeOffset? originalTime = null;
        var x = new RarFieldReader(b.Body.AsSpan(b.ExtraStart), b.BodyOffset + b.ExtraStart);
        while (x.Remaining > 0)
        {
            int size = x.ReadSmall("extra record size", x.Remaining);
            long recOffset = x.OffsetHere;
            if (size == 0)
            {
                throw new CompressionFormatException(Name, recOffset, "an extra record with at least a type");
            }

            var rec = new RarFieldReader(x.Take(size, "extra record"), recOffset);
            switch (rec.ReadVint("extra record type"))
            {
                case 1: // Locator record
                    int locFlags = rec.ReadSmall("locator flags");
                    if ((locFlags & 0x1) != 0)
                    {
                        long v = (long)Math.Min(rec.ReadVint("quick open offset"), long.MaxValue);
                        quickOpen = v == 0 ? null : v;
                    }

                    if ((locFlags & 0x2) != 0)
                    {
                        long v = (long)Math.Min(rec.ReadVint("recovery record offset"), long.MaxValue);
                        recovery = v == 0 ? null : v;
                    }

                    break;
                case 2: // Metadata record
                    int metaFlags = rec.ReadSmall("metadata flags");
                    if ((metaFlags & 0x1) != 0)
                    {
                        int nameLength = rec.ReadSmall("archive name length", rec.Remaining);
                        ReadOnlySpan<byte> raw = rec.Take(nameLength, "archive name");
                        int zero = raw.IndexOf((byte)0);

                        // A first byte of zero means the reserved space was too small to hold the name.
                        originalName = zero == 0 ? null : RarText.Utf8(zero < 0 ? raw : raw[..zero]);
                    }

                    if ((metaFlags & 0x2) != 0)
                    {
                        if ((metaFlags & 0x4) == 0)
                        {
                            originalTime = RarText.FromFileTime(rec.ReadUInt64("archive time"));
                        }
                        else if ((metaFlags & 0x8) == 0)
                        {
                            originalTime = RarText.FromUnix(rec.ReadUInt32("archive time"), 0);
                        }
                        else
                        {
                            ulong ns = rec.ReadUInt64("archive time");
                            originalTime = RarText.FromUnix(ns / 1_000_000_000UL, (uint)(ns % 1_000_000_000UL));
                        }
                    }

                    break;
            }
        }

        if (expectedVolume > 0 && ((flags & 0x1) == 0 || volumeNumber != expectedVolume))
        {
            throw new CompressionFormatException(Name, b.BodyOffset, $"volume {expectedVolume + 1} of the set (found {((flags & 0x1) == 0 ? "an archive that is not a volume" : "volume " + (volumeNumber + 1))})");
        }

        return new RarArchiveHeader
        {
            SignatureOffset = _sfxOffset,
            ArchiveFlags = flags,
            VolumeNumber = volumeNumber,
            QuickOpenOffset = quickOpen,
            RecoveryRecordOffset = recovery,
            OriginalName = originalName,
            OriginalCreationTime = originalTime,
        };
    }

    private static string ServiceName(RarBlock b) => ParseFileFields(b).Name;

    private async ValueTask<string?> ReadCommentAsync(RarBlock svc, bool async, CancellationToken ct)
    {
        FileFields fields = ParseFileFields(svc);
        long size = (long)svc.DataSize;
        if (fields.Method != 0 || fields.EncryptionPresent || size > MaxCommentSize)
        {
            await SkipDataAsync(svc, async, ct).ConfigureAwait(false);
            return null;
        }

        byte[] data = new byte[size];
        int got = await _cursor.ReadFullAsync(async, data, ct).ConfigureAwait(false);
        if (got < size)
        {
            throw new CompressionFormatException(Name, _sfxOffset + svc.DataOffset + got, "the archive comment", true);
        }

        return RarText.Utf8(data);
    }

    private async ValueTask SkipDataAsync(RarBlock b, bool async, CancellationToken ct)
    {
        if (b.HasData && b.DataSize > 0)
        {
            await _cursor.MoveToAsync(async, b.DataOffset + (long)b.DataSize, "the data area of a header", ct).ConfigureAwait(false);
        }
    }

    // ---- file headers -------------------------------------------------------------------------------------------

    private sealed class FileFields
    {
        public ulong FileFlags;
        public ulong UnpackedSize;
        public ulong Attributes;
        public uint? MTime32;
        public uint? Crc;
        public ulong CompressionInfo;
        public int HostOs;
        public string Name = string.Empty;
        public byte[]? NameBytes;
        public DateTimeOffset? MTime;
        public DateTimeOffset? ATime;
        public DateTimeOffset? CTime;
        public byte[]? Hash;
        public bool EncryptionPresent;
        public RarRedirectionType Redirection;
        public bool RedirectionIsDirectory;
        public string? RedirectionTarget;
        public long? Version;
        public string? UserName;
        public string? GroupName;
        public long? Uid;
        public long? Gid;

        public int Method => (int)((CompressionInfo >> 7) & 7);
    }

    // Technote, "File header and service header", and its extra record types.
    private static FileFields ParseFileFields(RarBlock b)
    {
        var f = new RarFieldReader(b.Body.AsSpan(0, b.ExtraStart), b.BodyOffset, b.FieldsStart);
        var ff = new FileFields
        {
            FileFlags = f.ReadVint("file flags"),
            UnpackedSize = f.ReadVint("unpacked size"),
            Attributes = f.ReadVint("attributes"),
        };
        if ((ff.FileFlags & 0x2) != 0)
        {
            ff.MTime32 = f.ReadUInt32("mtime");
        }

        if ((ff.FileFlags & 0x4) != 0)
        {
            ff.Crc = f.ReadUInt32("data CRC-32");
        }

        ff.CompressionInfo = f.ReadVint("compression information");
        ff.HostOs = f.ReadSmall("host OS", 255);
        int nameLength = f.ReadSmall("name length", f.Remaining);
        ff.NameBytes = f.Take(nameLength, "name").ToArray();
        ff.Name = RarText.Utf8(ff.NameBytes);

        if (ff.MTime32 is { } m)
        {
            ff.MTime = RarText.FromUnix(m, 0);
        }

        var x = new RarFieldReader(b.Body.AsSpan(b.ExtraStart), b.BodyOffset + b.ExtraStart);
        while (x.Remaining > 0)
        {
            int size = x.ReadSmall("extra record size", x.Remaining);
            long recOffset = x.OffsetHere;
            if (size == 0)
            {
                throw new CompressionFormatException(Name, recOffset, "an extra record with at least a type");
            }

            var rec = new RarFieldReader(x.Take(size, "extra record"), recOffset);
            switch (rec.ReadVint("extra record type"))
            {
                case 0x01: // File encryption
                    ff.EncryptionPresent = true;
                    break;
                case 0x02: // File hash
                    if (rec.ReadVint("hash type") == 0)
                    {
                        ff.Hash = rec.Take(32, "BLAKE2sp hash").ToArray();
                    }

                    break;
                case 0x03: // File time
                    ParseTimeRecord(ref rec, ff);
                    break;
                case 0x04: // File version
                    rec.ReadVint("file version flags");
                    ff.Version = (long)Math.Min(rec.ReadVint("file version number"), long.MaxValue);
                    break;
                case 0x05: // File system redirection
                    ulong type = rec.ReadVint("redirection type");
                    ulong flags = rec.ReadVint("redirection flags");
                    int targetLength = rec.ReadSmall("link target length", rec.Remaining);
                    string target = RarText.Utf8(rec.Take(targetLength, "link target"));
                    if (type is >= 1 and <= 5)
                    {
                        ff.Redirection = (RarRedirectionType)type;
                        ff.RedirectionIsDirectory = (flags & 1) != 0;
                        ff.RedirectionTarget = type is 2 or 3 ? target.Replace('\\', '/') : target;
                    }

                    break;
                case 0x06: // Unix owner
                    int ownerFlags = rec.ReadSmall("owner flags");
                    if ((ownerFlags & 0x1) != 0)
                    {
                        ff.UserName = RarText.Utf8(rec.Take(rec.ReadSmall("user name length", rec.Remaining), "user name"));
                    }

                    if ((ownerFlags & 0x2) != 0)
                    {
                        ff.GroupName = RarText.Utf8(rec.Take(rec.ReadSmall("group name length", rec.Remaining), "group name"));
                    }

                    if ((ownerFlags & 0x4) != 0)
                    {
                        ff.Uid = (long)Math.Min(rec.ReadVint("user id"), long.MaxValue);
                    }

                    if ((ownerFlags & 0x8) != 0)
                    {
                        ff.Gid = (long)Math.Min(rec.ReadVint("group id"), long.MaxValue);
                    }

                    break;
            }
        }

        return ff;
    }

    private static void ParseTimeRecord(ref RarFieldReader rec, FileFields ff)
    {
        int flags = rec.ReadSmall("file time flags");
        bool unix = (flags & 0x1) != 0;
        bool nanos = unix && (flags & 0x10) != 0;
        ulong m = 0, c = 0, a = 0;
        if ((flags & 0x2) != 0)
        {
            m = unix ? rec.ReadUInt32("mtime") : rec.ReadUInt64("mtime");
        }

        if ((flags & 0x4) != 0)
        {
            c = unix ? rec.ReadUInt32("ctime") : rec.ReadUInt64("ctime");
        }

        if ((flags & 0x8) != 0)
        {
            a = unix ? rec.ReadUInt32("atime") : rec.ReadUInt64("atime");
        }

        uint mn = 0, cn = 0, an = 0;
        if (nanos)
        {
            if ((flags & 0x2) != 0)
            {
                mn = rec.ReadUInt32("mtime nanoseconds");
            }

            if ((flags & 0x4) != 0)
            {
                cn = rec.ReadUInt32("ctime nanoseconds");
            }

            if ((flags & 0x8) != 0)
            {
                an = rec.ReadUInt32("atime nanoseconds");
            }
        }

        if ((flags & 0x2) != 0)
        {
            ff.MTime = unix ? RarText.FromUnix(m, mn) : RarText.FromFileTime(m);
        }

        if ((flags & 0x4) != 0)
        {
            ff.CTime = unix ? RarText.FromUnix(c, cn) : RarText.FromFileTime(c);
        }

        if ((flags & 0x8) != 0)
        {
            ff.ATime = unix ? RarText.FromUnix(a, an) : RarText.FromFileTime(a);
        }
    }

    private static long DictionarySize(ulong info)
    {
        // Technote, "Compression information": 128 KB << N, and for algorithm version 1 a 1/32 fraction step on top.
        int version = (int)(info & 0x3F);
        int n = (int)((info >> 10) & 0x1F);
        long baseSize = (128L * 1024) << n;
        if (version >= 1)
        {
            int fraction = (int)((info >> 15) & 0x1F);
            baseSize += baseSize / 32 * fraction;
        }

        return baseSize;
    }

    private RarEntry StartEntry(RarBlock b)
    {
        FileFields ff = ParseFileFields(b);
        if (ff.UnpackedSize > long.MaxValue)
        {
            throw new CompressionFormatException(Name, b.BodyOffset, $"an unpacked size below 2^63 (found {ff.UnpackedSize})");
        }

        bool isDirectory = (ff.FileFlags & 0x1) != 0;
        bool unknownSize = (ff.FileFlags & 0x8) != 0;
        long dataSize = (long)b.DataSize;
        ArchiveEntryKind kind = isDirectory ? ArchiveEntryKind.Directory : ArchiveEntryKind.File;
        string? target = null;
        switch (ff.Redirection)
        {
            case RarRedirectionType.UnixSymlink:
            case RarRedirectionType.WindowsSymlink:
            case RarRedirectionType.WindowsJunction:
                kind = ArchiveEntryKind.Symlink;
                target = ff.RedirectionTarget;
                break;
            case RarRedirectionType.HardLink:
            case RarRedirectionType.FileCopy:
                // A file copy is stored without data and names the file to copy; as a hard link it extracts to the same content.
                kind = ArchiveEntryKind.HardLink;
                target = ff.RedirectionTarget;
                break;
        }

        long size = kind == ArchiveEntryKind.File ? (unknownSize ? dataSize : (long)ff.UnpackedSize) : 0;
        int method = ff.Method;
        bool continues = b.ContinuesInNext;
        if (kind == ArchiveEntryKind.File && method == 0 && !continues && !unknownSize && !ff.EncryptionPresent && dataSize != size)
        {
            throw new CompressionFormatException(Name, b.BodyOffset, $"a stored entry whose data area ({dataSize} bytes) equals its unpacked size ({size} bytes)");
        }

        bool unix = ff.HostOs == (int)RarHostOs.Unix;
        var entry = new RarEntry
        {
            Name = ff.Name,
            Kind = kind,
            Size = size,
            ModifiedTime = ff.MTime,
            AccessTime = ff.ATime,
            Mode = unix ? (UnixFileMode)(ff.Attributes & 0xFFF) : null,
            Uid = ff.Uid ?? 0,
            Gid = ff.Gid ?? 0,
            UserName = ff.UserName,
            GroupName = ff.GroupName,
            LinkTarget = target,
            HostOs = (RarHostOs)ff.HostOs,
            Attributes = ff.Attributes,
            Method = method,
            CompressionVersion = (int)(ff.CompressionInfo & 0x3F),
            IsSolid = (ff.CompressionInfo & 0x40) != 0,
            DictionarySize = DictionarySize(ff.CompressionInfo),
            PackedSize = dataSize,
            SizeIsUnknown = unknownSize,
            Crc32 = continues ? null : ff.Crc,
            Blake2sp = continues || ff.Hash is null ? null : new ReadOnlyMemory<byte>(ff.Hash),
            CreationTime = ff.CTime,
            Redirection = ff.Redirection,
            RedirectionTargetIsDirectory = ff.RedirectionIsDirectory,
            FileVersion = ff.Version,
            IsEncrypted = ff.EncryptionPresent,
            FirstVolume = _volume,
            ContinuesInNextVolume = continues,
            HeaderOffset = b.Offset,
            DataOffset = _sfxOffset + b.DataOffset,
        };

        _entry = entry;
        _entryDone = kind != ArchiveEntryKind.File;
        _entryRead = 0;
        _dataLeft = dataSize;
        _moreParts = continues;
        _useCrc = ff.Crc is not null;
        _useHash = ff.Hash is not null;
        SetPart(ff);
        _crcWhole = 0;
        _hashWhole = _useHash && _options.VerifyChecksum ? new Blake2sp() : null;
        if (kind != ArchiveEntryKind.File)
        {
            // Links and directories carry no content; a data area they might have is skipped with the entry.
            _moreParts = false;
        }

        return entry;
    }

    private void SetPart(FileFields ff)
    {
        _hasStoredCrc = ff.Crc is not null;
        _storedCrc = ff.Crc ?? 0;
        _storedHash = ff.Hash;
        _crcPart = 0;
        _hashPart = _moreParts && _useHash && _options.VerifyChecksum ? new Blake2sp() : null;
    }

    // ---- entry data and volumes ---------------------------------------------------------------------------------

    private async ValueTask SkipEntryAsync(bool async, CancellationToken ct)
    {
        if (_entry is null)
        {
            // Nothing returned yet, or the previous call ended the archive.
            return;
        }

        RarEntry entry = _entry;
        if (entry.Kind != ArchiveEntryKind.File)
        {
            if (_dataLeft > 0)
            {
                await _cursor.MoveToAsync(async, _cursor.Pos + _dataLeft, "the data area of a header", ct).ConfigureAwait(false);
                _dataLeft = 0;
            }

            return;
        }

        while (!_entryDone)
        {
            if (_dataLeft > 0)
            {
                await _cursor.MoveToAsync(async, _cursor.Pos + _dataLeft, $"the data of '{RarText.Printable(entry.Name)}'", ct).ConfigureAwait(false);
                _dataLeft = 0;
            }

            if (!_moreParts)
            {
                _entryDone = true;
                break;
            }

            await NextPartAsync(entry, async, ct).ConfigureAwait(false);
        }
    }

    internal async ValueTask<int> ReadDataAsync(int generation, Memory<byte> buffer, bool async, CancellationToken ct)
    {
        if (generation != _generation || IsDisposed)
        {
            throw new InvalidOperationException("The entry stream is no longer valid: the reader moved to another entry or was disposed.");
        }

        RarEntry entry = _entry!;
        while (true)
        {
            if (_entryDone)
            {
                return 0;
            }

            if (_dataLeft == 0)
            {
                await FinishPartAsync(entry, async, ct).ConfigureAwait(false);
                continue;
            }

            if (buffer.Length == 0)
            {
                return 0;
            }

            int want = (int)Math.Min(buffer.Length, _dataLeft);
            long at = _sfxOffset + _cursor.Pos;
            int n = await _cursor.ReadFullAsync(async, buffer[..want], ct).ConfigureAwait(false);
            if (n < want)
            {
                throw new CompressionFormatException(Name, at + n, $"the data of '{RarText.Printable(entry.Name)}' ({want - n} more bytes)", true);
            }

            _dataLeft -= n;
            _entryRead += n;
            if (!entry.SizeIsUnknown && _entryRead > entry.Size)
            {
                throw new CompressionFormatException(Name, at, $"no more than the {entry.Size} bytes '{RarText.Printable(entry.Name)}' declares");
            }

            if (_options.VerifyChecksum)
            {
                ReadOnlySpan<byte> chunk = buffer.Span[..n];
                if (_useCrc)
                {
                    _crcWhole = Crc32.Update(_crcWhole, chunk);
                    if (_moreParts)
                    {
                        _crcPart = Crc32.Update(_crcPart, chunk);
                    }
                }

                _hashWhole?.Update(chunk);
                _hashPart?.Update(chunk);
            }

            return n;
        }
    }

    // The data area of the current part is used up: check what the header stored for it, then end the entry or go on to the next part.
    private async ValueTask FinishPartAsync(RarEntry entry, bool async, CancellationToken ct)
    {
        string name = RarText.Printable(entry.Name);
        long at = _sfxOffset + _cursor.Pos;
        if (_options.VerifyChecksum)
        {
            // Technote, "File header" (Data CRC32) and "File hash record": for a part that is not the last the value covers that
            // volume's packed data; for the last part, and for unsplit files, it covers the whole unpacked data.
            uint crc = _moreParts ? _crcPart : _crcWhole;
            if (_useCrc && _hasStoredCrc && crc != _storedCrc)
            {
                throw new CompressionFormatException(Name, at, $"CRC-32 {_storedCrc:X8} for '{name}', computed {crc:X8}");
            }

            Blake2sp? hash = _moreParts ? _hashPart : _hashWhole;
            if (hash is not null && _storedHash is not null)
            {
                Span<byte> digest = stackalloc byte[Blake2sp.DigestLength];
                hash.Final(digest);
                if (!digest.SequenceEqual(_storedHash))
                {
                    throw new CompressionFormatException(Name, at, $"BLAKE2sp hash {Convert.ToHexString(_storedHash)} for '{name}', computed {Convert.ToHexString(digest)}");
                }
            }
        }

        if (!_moreParts)
        {
            if (!entry.SizeIsUnknown && _entryRead != entry.Size)
            {
                throw new CompressionFormatException(Name, at, $"{entry.Size} bytes of data for '{name}', found {_entryRead}");
            }

            _entryDone = true;
            return;
        }

        await NextPartAsync(entry, async, ct).ConfigureAwait(false);
    }

    // The entry goes on in the next volume: find its continuation header there and take over its part state.
    private async ValueTask NextPartAsync(RarEntry entry, bool async, CancellationToken ct)
    {
        string name = RarText.Printable(entry.Name);
        while (true)
        {
            RarBlock b = await ReadRequiredBlockAsync(async, $"the continuation of '{name}'", ct).ConfigureAwait(false);
            if (b.Type == 5)
            {
                if ((ReadEndFlags(b) & 0x1) == 0)
                {
                    throw new CompressionFormatException(Name, b.Offset, $"the continuation of '{name}', but the archive ends here", true);
                }

                await OpenNextVolumeAsync(async, ct).ConfigureAwait(false);
                continue;
            }

            if (b.Type != 2)
            {
                await SkipDataAsync(b, async, ct).ConfigureAwait(false);
                continue;
            }

            FileFields ff = ParseFileFields(b);
            if (!b.ContinuedFromPrevious || ff.Name != entry.Name)
            {
                throw new CompressionFormatException(Name, b.Offset, $"a file header continuing '{name}' (found '{RarText.Printable(ff.Name)}'{(b.ContinuedFromPrevious ? string.Empty : ", which does not continue a file")})");
            }

            _dataLeft = (long)b.DataSize;
            _moreParts = b.ContinuesInNext;
            SetPart(ff);
            return;
        }
    }

    private static int ReadEndFlags(RarBlock b)
    {
        var f = new RarFieldReader(b.Body.AsSpan(0, b.ExtraStart), b.BodyOffset, b.FieldsStart);
        return f.ReadSmall("end of archive flags");
    }

    // An end-of-archive header outside any file: the archive ends, or the set goes on in the next volume.
    private async ValueTask FinishVolumeAsync(RarBlock end, bool async, CancellationToken ct)
    {
        if ((ReadEndFlags(end) & 0x1) == 0)
        {
            _ended = true;
            return;
        }

        await OpenNextVolumeAsync(async, ct).ConfigureAwait(false);
    }

    private async ValueTask OpenNextVolumeAsync(bool async, CancellationToken ct)
    {
        int next = _volume + 1;
        long at = _sfxOffset + _cursor.Pos;
        Stream? stream = _options.Volumes?.Invoke(next);
        if (stream is null)
        {
            throw new CompressionFormatException(Name, at, $"volume {next + 1} of the set (it was not supplied{(_options.Volumes is null ? "; set RarReaderOptions.Volumes to read a multi-volume archive" : string.Empty)})", true);
        }

        CloseCurrentVolume();
        if (!stream.CanRead)
        {
            stream.Dispose();
            throw new ArgumentException($"The stream for volume {next + 1} must be readable.");
        }

        try
        {
            await OpenVolumeAsync(stream, next, async, ct).ConfigureAwait(false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void CloseCurrentVolume()
    {
        _wrapper?.Dispose();
        _wrapper = null;
        if (_volumeStream is { } s && (_volume > 0 || !_leaveOpen))
        {
            s.Dispose();
        }

        _volumeStream = null;
    }
}

/// <summary>The content of one stored RAR entry, read through its reader so that it can cross volumes.</summary>
internal sealed class RarEntryStream(RarReader reader, int generation, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        // The synchronous path runs the shared code with the async flag off; it completes synchronously. A span cannot cross an
        // await, so go through a pooled array.
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1, buffer.Length));
        try
        {
            int n = Sync.Result(reader.ReadDataAsync(generation, rented.AsMemory(0, buffer.Length), false, default));
            rented.AsSpan(0, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await reader.ReadDataAsync(generation, buffer, true, cancellationToken).ConfigureAwait(false);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
