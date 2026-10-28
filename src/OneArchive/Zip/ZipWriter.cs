using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneArchive.Zip;

/// <summary>
/// Writes zip archives (PKWARE APPNOTE 6.3.10) with the methods store, deflate and zstd (93). The output depends only
/// on the entries and the options, not on the platform, the clock, or how the content streams chunk their reads.
/// </summary>
/// <remarks>
/// <para>Layout, in the order written: for each entry a local header (name, then the extra fields zip64, extended
/// timestamp, NTFS times and the caller's own), the data, and for entries written with a data descriptor the descriptor;
/// then the central directory, the zip64 end record and locator when needed, and the end record.
/// Names are UTF-8 with bit 11 set when they are not ASCII. "Version made by" is 63 on a Unix host (3), the external
/// attributes carry the Unix mode (with the DOS directory bit for directories), and the DOS date and time are the
/// entry's UTC time (1980-01-01 00:00 when there is none).</para>
/// <para>When the output can seek and <see cref="ZipWriterOptions.DataDescriptors"/> is <see cref="ZipDataDescriptorMode.Auto"/>, local
/// headers are patched with the CRC-32 and sizes after the data; otherwise (and always for
/// <see cref="ZipDataDescriptorMode.Always"/>) bit 3 is set and a descriptor with signature follows the data, with 8-byte sizes
/// when the local header has a zip64 field. Directories, links and empty files are stored and have no descriptor.
/// Documented in <c>docs/archive.md</c>.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZipWriter : ArchiveWriter
{
    private const ushort VersionMadeBy = (3 << 8) | 63;

    private sealed class CountingSink(Stream inner) : Stream
    {
        public long Count { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Count += buffer.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Count += buffer.Length;
        }
    }

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ZipWriterOptions _options;
    private readonly bool _seekable;
    private readonly long _origin;
    private readonly bool _descriptors;
    private readonly MemoryStream _directory = new();
    private readonly byte[] _commentBytes;
    private long _pos;
    private long _count;
    private bool _finished;
    private bool _failed;
    private bool _disposed;

    /// <summary>Creates a writer that writes to <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream. If it can seek, local headers are patched in place (unless descriptors are forced).</param>
    /// <param name="options">Writing options; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when the writer is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written, descriptors are forbidden and it cannot seek, or the comment is longer than 65535 bytes.</exception>
    public ZipWriter(Stream stream, ZipWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _options = options ?? new ZipWriterOptions();
        _seekable = stream.CanSeek;
        _origin = _seekable ? stream.Position : 0;
        _descriptors = _options.DataDescriptors switch
        {
            ZipDataDescriptorMode.Always => true,
            ZipDataDescriptorMode.Never => _seekable ? false : throw new ArgumentException("ZipDataDescriptorMode.Never needs an output stream that can seek.", nameof(options)),
            _ => !_seekable,
        };
        _commentBytes = _options.Comment is { Length: > 0 } c ? Encoding.UTF8.GetBytes(c) : [];
        if (_commentBytes.Length > 65535)
        {
            throw new ArgumentException("The archive comment is longer than 65535 bytes.", nameof(options));
        }
    }

    /// <inheritdoc />
    public override ArchiveFormat Format => ArchiveFormat.Zip;

    /// <inheritdoc />
    protected override async ValueTask WriteEntryAsync(ArchiveEntry entry, Stream? content, bool async, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The archive was already finished.");
        }

        if (_failed)
        {
            throw new InvalidOperationException("An earlier write failed; the archive cannot be continued.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Prepared p = Prepare(entry);
        try
        {
            await WriteCoreAsync(entry, p, content, async, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private sealed class Prepared
    {
        public byte[] Name = [];
        public byte[] Comment = [];
        public ushort Flags;
        public ZipMethod Method;
        public long Size;
        public byte[]? InMemory;     // content that is known up front (links)
        public uint ExternalAttributes;
        public bool Zip64Local;
        public bool Descriptor;
        public ArchiveEntryKind Kind;
    }

    private Prepared Prepare(ArchiveEntry entry)
    {
        if (entry.Kind is ArchiveEntryKind.HardLink or ArchiveEntryKind.CharacterDevice or ArchiveEntryKind.BlockDevice or ArchiveEntryKind.Fifo)
        {
            throw new ArchiveUnsupportedException(ZipFormat.Name, $"{entry.Kind} entries (zip cannot store them)");
        }

        string name = entry.Name;
        if (string.IsNullOrEmpty(name) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An entry name must be non-empty and contain no NUL.", nameof(entry));
        }

        if (entry.Kind == ArchiveEntryKind.Directory && !name.EndsWith('/'))
        {
            name += "/";
        }

        var p = new Prepared { Kind = entry.Kind, Name = Encoding.UTF8.GetBytes(name) };
        string? comment = (entry as ZipEntry)?.Comment;
        p.Comment = string.IsNullOrEmpty(comment) ? [] : Encoding.UTF8.GetBytes(comment);
        if (p.Name.Length > 65535 || p.Comment.Length > 65535)
        {
            throw new ArgumentException("An entry name or comment is longer than 65535 bytes.", nameof(entry));
        }

        if (!IsAscii(p.Name) || !IsAscii(p.Comment))
        {
            p.Flags |= ZipFormat.FlagUtf8;
        }

        uint type;
        UnixFileMode defaultMode;
        switch (entry.Kind)
        {
            case ArchiveEntryKind.Directory:
                type = 0x4000;
                defaultMode = (UnixFileMode)0b111_101_101;
                p.Method = ZipMethod.Store;
                break;
            case ArchiveEntryKind.Symlink:
                if (string.IsNullOrEmpty(entry.LinkTarget))
                {
                    throw new ArgumentException("A symbolic link entry needs a LinkTarget.", nameof(entry));
                }

                type = 0xA000;
                defaultMode = (UnixFileMode)0x1FF;
                p.Method = ZipMethod.Store;
                p.InMemory = Encoding.UTF8.GetBytes(entry.LinkTarget);
                p.Size = p.InMemory.Length;
                break;
            default:
                if (entry.Size < 0)
                {
                    throw new ArgumentException("The entry's size is negative.", nameof(entry));
                }

                type = 0x8000;
                defaultMode = (UnixFileMode)0b110_100_100;
                p.Size = entry.Size;
                p.Method = entry.Size == 0 ? ZipMethod.Store : _options.Method;
                if (p.Method is not (ZipMethod.Store or ZipMethod.Deflate or ZipMethod.Zstd))
                {
                    throw new ArchiveUnsupportedException(ZipFormat.Name, $"writing the {p.Method} method (store, deflate and zstd can be written)");
                }

                break;
        }

        uint perms = (uint)((entry.Mode ?? defaultMode) & (UnixFileMode)0xFFF);
        p.ExternalAttributes = ((type | perms) << 16) | (entry.Kind == ArchiveEntryKind.Directory ? 0x10u : 0u);
        p.Zip64Local = _options.Zip64 == ZipZip64Mode.Always || p.Size >= ZipFormat.LocalZip64Threshold;
        if (p.Zip64Local && _options.Zip64 == ZipZip64Mode.Never)
        {
            throw new ArgumentException($"Entry '{name}' is too large for a zip without zip64 (ZipZip64Mode.Never).", nameof(entry));
        }

        // Entries whose data is not known up front need the CRC and sizes later: a descriptor, or a patch of the header.
        p.Descriptor = _descriptors && p.InMemory is null && p.Size > 0;
        return p;
    }

    private static bool IsAscii(byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            if (b >= 0x80)
            {
                return false;
            }
        }

        return true;
    }

    private static Stream OpenCompress(ZipMethod method, Stream sink, int? level, long size)
    {
        // The CRC-32 in the zip header covers the data, so a zstd frame carries no checksum of its own.
        Stream stream = method == ZipMethod.Zstd
            ? new ZstdStream(sink, new ZstdCompressionOptions { Level = level ?? 3, Checksum = false, ContentSize = size }, leaveOpen: true)
            : OpenDeflate();
        return stream;
    }

    private static Stream OpenDeflate() =>
        throw new ArchiveUnsupportedException(ZipFormat.Name, "writing Deflate entries (the Deflate encoder is not available in this build)");

    private async ValueTask WriteBytesAsync(ReadOnlyMemory<byte> data, bool async, CancellationToken ct)
    {
        if (async)
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        else
        {
            _stream.Write(data.Span);
        }

        _pos += data.Length;
    }

    private static void BuildTimes(ArchiveEntry entry, out byte[] local, out byte[] central, out byte[] ntfs)
    {
        local = central = ntfs = [];
        DateTimeOffset? m = entry.ModifiedTime, a = entry.AccessTime, c = entry.ChangeTime;
        bool fits(DateTimeOffset? t) => t is { } v && v.ToUnixTimeSeconds() is >= int.MinValue and <= int.MaxValue;
        bool fine(DateTimeOffset? t) => t is { } v && v.Ticks % TimeSpan.TicksPerSecond == 0;
        bool needNtfs = (m is not null && (!fine(m) || !fits(m))) || (a is not null && (!fine(a) || !fits(a))) || (c is not null && (!fine(c) || !fits(c)));
        if (m is not null && fits(m))
        {
            // APPNOTE-adjacent Info-ZIP field 0x5455: flags, then the 32-bit times that the flags announce.
            int flags = 1 | (fits(a) ? 2 : 0) | (fits(c) ? 4 : 0);
            var l = new List<byte> { (byte)flags };
            AddInt32(l, m.Value);
            if (fits(a))
            {
                AddInt32(l, a!.Value);
            }

            if (fits(c))
            {
                AddInt32(l, c!.Value);
            }

            local = Field(ZipFormat.ExtraExtendedTime, l.ToArray());
            var cl = new List<byte> { 1 };
            AddInt32(cl, m.Value);
            central = Field(ZipFormat.ExtraExtendedTime, cl.ToArray());
        }

        if (needNtfs)
        {
            byte[] data = new byte[4 + 4 + 24];
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 24);
            DateTimeOffset mm = m ?? a ?? c ?? DateTimeOffset.UnixEpoch;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), ZipFormat.ToFileTime(mm));
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), ZipFormat.ToFileTime(a ?? mm));
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24), ZipFormat.ToFileTime(c ?? mm));
            ntfs = Field(ZipFormat.ExtraNtfs, data);
        }

        static void AddInt32(List<byte> list, DateTimeOffset t)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, (int)t.ToUnixTimeSeconds());
            list.AddRange(b.ToArray());
        }
    }

    private static byte[] Field(ushort id, byte[] data)
    {
        byte[] f = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(f, id);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), (ushort)data.Length);
        data.CopyTo(f, 4);
        return f;
    }

    private static byte[] UserExtras(ArchiveEntry entry)
    {
        if (entry is not ZipEntry z)
        {
            return [];
        }

        var ms = new MemoryStream();
        foreach (ZipExtraField f in z.ExtraFields)
        {
            if (f.Id is ZipFormat.ExtraZip64 or ZipFormat.ExtraExtendedTime or ZipFormat.ExtraNtfs or ZipFormat.ExtraUnicodePath)
            {
                continue;
            }

            ms.Write(Field(f.Id, f.Data.ToArray()));
        }

        return ms.ToArray();
    }

    private async ValueTask WriteCoreAsync(ArchiveEntry entry, Prepared p, Stream? content, bool async, CancellationToken ct)
    {
        if (p.Kind == ArchiveEntryKind.File && p.Size > 0 && content is null)
        {
            throw new ArgumentException("A file entry with a size needs content.", nameof(content));
        }

        BuildTimes(entry, out byte[] utLocal, out byte[] utCentral, out byte[] ntfs);
        byte[] user = UserExtras(entry);
        long headerOffset = _pos;
        (ushort date, ushort time) = ZipFormat.ToDos(entry.ModifiedTime);

        // Content known up front (links, directories, empty files) gets its final header at once.
        uint crc = 0;
        if (p.InMemory is not null)
        {
            crc = Crc32.Compute(p.InMemory);
        }

        bool known = p.InMemory is not null || p.Size == 0;
        ushort flags = p.Flags;
        if (p.Descriptor)
        {
            flags |= ZipFormat.FlagDescriptor;
        }

        // A stored entry's length is its declared size, so even with a descriptor the local header can carry it: a
        // forward-only reader cannot find the end of stored data any other way. Other methods leave it zero (bit 3).
        bool sizesInHeader = known || (p.Descriptor && p.Method == ZipMethod.Store);
        byte[] zip64Local = p.Zip64Local ? Field(ZipFormat.ExtraZip64, new byte[16]) : [];
        if (sizesInHeader && p.Zip64Local)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(zip64Local.AsSpan(4), (ulong)p.Size);
            BinaryPrimitives.WriteUInt64LittleEndian(zip64Local.AsSpan(12), (ulong)p.Size);
        }

        int extraLength = zip64Local.Length + utLocal.Length + ntfs.Length + user.Length;
        if (extraLength > 65535)
        {
            throw new ArgumentException("The entry's extra fields are longer than 65535 bytes.", nameof(entry));
        }

        ushort needed = p.Method switch { ZipMethod.Zstd => 63, ZipMethod.Deflate => 20, _ => 10 };
        if (p.Kind == ArchiveEntryKind.Directory || p.Descriptor)
        {
            needed = Math.Max(needed, (ushort)20);
        }

        if (p.Zip64Local)
        {
            needed = Math.Max(needed, (ushort)45);
        }

        // Local file header (APPNOTE 4.3.7).
        byte[] header = new byte[ZipFormat.LocalHeaderSize + p.Name.Length + extraLength];
        Span<byte> h = header;
        BinaryPrimitives.WriteUInt32LittleEndian(h, ZipFormat.LocalSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], needed);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], (ushort)p.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], time);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], date);
        if (known || !p.Descriptor)
        {
            // Final values when known; a patch fills them in otherwise.
            BinaryPrimitives.WriteUInt32LittleEndian(h[14..], crc);
        }

        uint size32 = p.Zip64Local ? ZipFormat.Max32 : (sizesInHeader ? (uint)p.Size : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[18..], size32);
        BinaryPrimitives.WriteUInt32LittleEndian(h[22..], size32);
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)p.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)extraLength);
        int at = ZipFormat.LocalHeaderSize;
        p.Name.CopyTo(h[at..]);
        at += p.Name.Length;
        zip64Local.CopyTo(h[at..]);
        at += zip64Local.Length;
        utLocal.CopyTo(h[at..]);
        at += utLocal.Length;
        ntfs.CopyTo(h[at..]);
        at += ntfs.Length;
        user.CopyTo(h[at..]);
        await WriteBytesAsync(header, async, ct).ConfigureAwait(false);

        long compressed = 0;
        if (p.InMemory is not null)
        {
            await WriteBytesAsync(p.InMemory, async, ct).ConfigureAwait(false);
            compressed = p.InMemory.Length;
        }
        else if (p.Size > 0)
        {
            var sink = new CountingSink(_stream);
            Stream target = p.Method == ZipMethod.Store ? sink : OpenCompress(p.Method, sink, _options.Level, p.Size);
            var running = new Crc32();
            long total = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    int n = async
                        ? await content!.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)
                        : content!.Read(buffer, 0, buffer.Length);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                    if (total > p.Size)
                    {
                        throw new ArgumentException($"The content of '{entry.Name}' is longer than the entry's size of {p.Size}.", nameof(content));
                    }

                    running.Append(buffer.AsSpan(0, n));
                    if (async)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        target.Write(buffer, 0, n);
                    }
                }

                if (total != p.Size)
                {
                    throw new ArgumentException($"The content of '{entry.Name}' is {total} bytes but the entry's size is {p.Size}.", nameof(content));
                }

                if (!ReferenceEquals(target, sink))
                {
                    if (async)
                    {
                        await target.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        target.Dispose();
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            crc = running.GetCurrentValue();
            compressed = sink.Count;
            _pos += compressed;
        }

        if (compressed >= ZipFormat.Max32 && !p.Zip64Local)
        {
            throw new ArgumentException($"Entry '{entry.Name}' compressed to {compressed} bytes, which needs zip64; set ZipWriterOptions.Zip64 to Always.", nameof(entry));
        }

        if (p.Descriptor)
        {
            byte[] d = new byte[p.Zip64Local ? 24 : 16];
            BinaryPrimitives.WriteUInt32LittleEndian(d, ZipFormat.DescriptorSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), crc);
            if (p.Zip64Local)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(8), (ulong)compressed);
                BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(16), (ulong)p.Size);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), (uint)compressed);
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)p.Size);
            }

            await WriteBytesAsync(d, async, ct).ConfigureAwait(false);
        }
        else if (!known)
        {
            await PatchHeaderAsync(headerOffset, p, crc, compressed, async, ct).ConfigureAwait(false);
        }

        AddDirectoryRecord(entry, p, flags, needed, time, date, crc, compressed, headerOffset, utCentral, ntfs, user);
    }

    // Writes the CRC-32 and sizes into a local header that is already on the stream, then returns to the end.
    private async ValueTask PatchHeaderAsync(long headerOffset, Prepared p, uint crc, long compressed, bool async, CancellationToken ct)
    {
        byte[] patch;
        long patchAt;
        if (p.Zip64Local)
        {
            // The 32-bit fields stay 0xFFFFFFFF; the values go in the zip64 field, after its 4-byte header.
            patch = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(patch, (ulong)p.Size);
            BinaryPrimitives.WriteUInt64LittleEndian(patch.AsSpan(8), (ulong)compressed);
            patchAt = headerOffset + ZipFormat.LocalHeaderSize + p.Name.Length + 4;
            byte[] crcOnly = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcOnly, crc);
            _stream.Seek(_origin + headerOffset + 14, SeekOrigin.Begin);
            await WriteRawAsync(crcOnly, async, ct).ConfigureAwait(false);
        }
        else
        {
            patch = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(patch, crc);
            BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(4), (uint)compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(8), (uint)p.Size);
            patchAt = headerOffset + 14;
        }

        _stream.Seek(_origin + patchAt, SeekOrigin.Begin);
        await WriteRawAsync(patch, async, ct).ConfigureAwait(false);
        _stream.Seek(_origin + _pos, SeekOrigin.Begin);
    }

    private async ValueTask WriteRawAsync(byte[] data, bool async, CancellationToken ct)
    {
        if (async)
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        else
        {
            _stream.Write(data);
        }
    }

    // Central directory file header (APPNOTE 4.3.12), kept in memory until Finish.
    private void AddDirectoryRecord(
        ArchiveEntry entry, Prepared p, ushort flags, ushort needed, ushort time, ushort date, uint crc, long compressed, long headerOffset,
        byte[] utCentral, byte[] ntfs, byte[] user)
    {
        bool bigSize = p.Size >= ZipFormat.Max32, bigComp = compressed >= ZipFormat.Max32, bigOffset = headerOffset >= ZipFormat.Max32;
        if ((bigSize || bigComp || bigOffset) && _options.Zip64 == ZipZip64Mode.Never)
        {
            throw new ArgumentException("The archive needs zip64 (ZipZip64Mode.Never).", nameof(entry));
        }

        int zipLen = (bigSize ? 8 : 0) + (bigComp ? 8 : 0) + (bigOffset ? 8 : 0);
        byte[] zip64 = [];
        if (zipLen > 0)
        {
            byte[] data = new byte[zipLen];
            int q = 0;
            if (bigSize)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(q), (ulong)p.Size);
                q += 8;
            }

            if (bigComp)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(q), (ulong)compressed);
                q += 8;
            }

            if (bigOffset)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(q), (ulong)headerOffset);
            }

            zip64 = Field(ZipFormat.ExtraZip64, data);
            needed = Math.Max(needed, (ushort)45);
        }

        int extraLength = zip64.Length + utCentral.Length + ntfs.Length + user.Length;
        if (extraLength > 65535)
        {
            throw new ArgumentException("The entry's extra fields are longer than 65535 bytes.", nameof(entry));
        }

        byte[] r = new byte[ZipFormat.CentralHeaderSize + p.Name.Length + extraLength + p.Comment.Length];
        Span<byte> h = r;
        BinaryPrimitives.WriteUInt32LittleEndian(h, ZipFormat.CentralSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], VersionMadeBy);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], needed);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], (ushort)p.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], time);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], date);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], bigComp ? ZipFormat.Max32 : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], bigSize ? ZipFormat.Max32 : (uint)p.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)p.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[30..], (ushort)extraLength);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)p.Comment.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], (ushort)((entry as ZipEntry)?.InternalAttributes ?? 0));
        BinaryPrimitives.WriteUInt32LittleEndian(h[38..], p.ExternalAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], bigOffset ? ZipFormat.Max32 : (uint)headerOffset);
        int at = ZipFormat.CentralHeaderSize;
        p.Name.CopyTo(h[at..]);
        at += p.Name.Length;
        zip64.CopyTo(h[at..]);
        at += zip64.Length;
        utCentral.CopyTo(h[at..]);
        at += utCentral.Length;
        ntfs.CopyTo(h[at..]);
        at += ntfs.Length;
        user.CopyTo(h[at..]);
        at += user.Length;
        p.Comment.CopyTo(h[at..]);
        _directory.Write(r);
        _count++;
    }

    /// <inheritdoc />
    protected override async ValueTask FinishAsync(bool async, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            return;
        }

        if (_failed)
        {
            throw new InvalidOperationException("An earlier write failed; the archive cannot be finished.");
        }

        try
        {
            long dirOffset = _pos;
            byte[] dir = _directory.ToArray();
            await WriteBytesAsync(dir, async, cancellationToken).ConfigureAwait(false);
            long dirSize = dir.Length;
            bool need64 = _options.Zip64 == ZipZip64Mode.Always || _count >= ZipFormat.Max16 || dirSize >= ZipFormat.Max32 || dirOffset >= ZipFormat.Max32;
            if (need64 && _options.Zip64 == ZipZip64Mode.Never)
            {
                throw new ArgumentException("The archive needs zip64 (ZipZip64Mode.Never).");
            }

            if (need64)
            {
                long recordOffset = _pos;
                byte[] z = new byte[ZipFormat.Zip64EndRecordSize + ZipFormat.Zip64LocatorSize];
                Span<byte> s = z;
                BinaryPrimitives.WriteUInt32LittleEndian(s, ZipFormat.Zip64EndSignature);
                BinaryPrimitives.WriteUInt64LittleEndian(s[4..], ZipFormat.Zip64EndRecordSize - 12);
                BinaryPrimitives.WriteUInt16LittleEndian(s[12..], VersionMadeBy);
                BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 45);
                BinaryPrimitives.WriteUInt64LittleEndian(s[24..], (ulong)_count);
                BinaryPrimitives.WriteUInt64LittleEndian(s[32..], (ulong)_count);
                BinaryPrimitives.WriteUInt64LittleEndian(s[40..], (ulong)dirSize);
                BinaryPrimitives.WriteUInt64LittleEndian(s[48..], (ulong)dirOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(s[56..], ZipFormat.Zip64LocatorSignature);
                BinaryPrimitives.WriteUInt64LittleEndian(s[64..], (ulong)recordOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 1);
                await WriteBytesAsync(z, async, cancellationToken).ConfigureAwait(false);
            }

            byte[] e = new byte[ZipFormat.EndRecordSize + _commentBytes.Length];
            Span<byte> t = e;
            BinaryPrimitives.WriteUInt32LittleEndian(t, ZipFormat.EndSignature);
            ushort count16 = _count >= ZipFormat.Max16 ? ZipFormat.Max16 : (ushort)_count;
            BinaryPrimitives.WriteUInt16LittleEndian(t[8..], count16);
            BinaryPrimitives.WriteUInt16LittleEndian(t[10..], count16);
            BinaryPrimitives.WriteUInt32LittleEndian(t[12..], dirSize >= ZipFormat.Max32 ? ZipFormat.Max32 : (uint)dirSize);
            BinaryPrimitives.WriteUInt32LittleEndian(t[16..], dirOffset >= ZipFormat.Max32 ? ZipFormat.Max32 : (uint)dirOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(t[20..], (ushort)_commentBytes.Length);
            _commentBytes.CopyTo(t[ZipFormat.EndRecordSize..]);
            await WriteBytesAsync(e, async, cancellationToken).ConfigureAwait(false);
            _finished = true;
            if (async)
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _stream.Flush();
            }
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (disposing && !_finished && !_failed)
            {
                Finish();
            }
        }
        finally
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_finished && !_failed)
            {
                await FinishAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
