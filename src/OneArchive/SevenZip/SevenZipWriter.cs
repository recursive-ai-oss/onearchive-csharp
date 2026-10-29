using RecursiveAI.OneArchive.Internal;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>
/// Writes 7z archives (<c>7zFormat.txt</c>, method IDs from <c>Methods.txt</c>). The output depends only on the entries and
/// the options: entries keep the caller's order, no clock is read, and nothing is multithreaded.
/// </summary>
/// <remarks>
/// <para>Layout: the 32-byte start header, the packed streams of each folder in order, the header (or an LZMA-encoded
/// header preceded by its packed stream), and the start header patched at the end. The output stream must seek; if it
/// cannot, the whole archive is built in memory and copied out by <see cref="ArchiveWriter.Finish()"/>.</para>
/// <para>Files with content go into folders (solid: several files in one compression stream, up to the limits in
/// <see cref="SevenZipWriterOptions"/>); directories and empty files only appear in the header. Symbolic links are
/// files whose content is the UTF-8 target and whose attributes carry the Unix type. Documented in <c>docs/7z-writer.md</c>.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class SevenZipWriter : ArchiveWriter
{
    private const int StartHeaderSize = 32;
    private const int HeadSize = 4096;
    private const long FileTimeOffset = 504911232000000000L; // 1601-01-01 in DateTime ticks

    private static readonly UnicodeEncoding s_names = new(false, false, true);

    private readonly Stream _target;
    private readonly bool _leaveOpen;
    private readonly SevenZipWriterOptions _options;
    private readonly SevenZipMethod _method;
    private readonly Stream _out;
    private readonly long _origin;
    private readonly bool _buffered;
    private readonly List<FileRecord> _files = [];
    private readonly List<FolderRecord> _folders = [];
    private readonly bool _encrypt;
    private byte[]? _key;
    private FolderRecord? _cur;
    private bool _disposed;
    private bool _finished;
    private bool _failed;

    private sealed class FileRecord
    {
        public required byte[] Name;
        public bool EmptyStream;
        public bool EmptyFile;
        public long? CTime;
        public long? ATime;
        public long? MTime;
        public uint? Attributes;
    }

    private sealed class FolderRecord
    {
        public SevenZipFilter Filter;
        public byte[] CompressorProperties = [];
        public Stream Top = null!;
        public CountingSink Sink = null!;
        public SevenZipAesEncryptStream? Aes;
        public byte[] AesProperties = [];
        public long AesUnpack;
        public long Unpack;
        public long Pack;
        public List<long> Sizes = [];
        public List<uint> Crcs = [];
    }

    /// <summary>Creates a writer that writes to <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream, preferably seekable (otherwise the archive is held in memory until it is finished).</param>
    /// <param name="options">Writing options; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when the writer is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public SevenZipWriter(Stream stream, SevenZipWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _options = options ?? new SevenZipWriterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Level, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.Level, 9);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.DeltaDistance, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.DeltaDistance, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.SolidBlockSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.SolidFileCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.KeyDerivationPower, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.KeyDerivationPower, 24);
        _encrypt = !_options.Password.IsEmpty;
        _method = _options.Level == 0 ? SevenZipMethod.Copy : _options.Method;
        _target = stream;
        _leaveOpen = leaveOpen;
        _buffered = !stream.CanSeek;
        _out = _buffered ? new MemoryStream() : stream;
        _origin = _buffered ? 0 : stream.Position;
        _out.Write(new byte[StartHeaderSize]);
    }

    /// <inheritdoc />
    public override ArchiveFormat Format => ArchiveFormat.SevenZip;

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
        FileRecord record = Prepare(entry, out byte[]? linkBytes);
        try
        {
            long size = linkBytes?.Length ?? (entry.Kind == ArchiveEntryKind.File ? entry.Size : 0);
            if (size > 0)
            {
                Stream source = linkBytes is not null ? new MemoryStream(linkBytes, false) : content ?? throw new ArgumentException("A file entry with a size needs content.", nameof(content));
                try
                {
                    await WriteContentAsync(entry, source, size, async, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (linkBytes is not null)
                    {
                        source.Dispose();
                    }
                }
            }
            else
            {
                record.EmptyStream = true;
                record.EmptyFile = entry.Kind != ArchiveEntryKind.Directory;
            }

            _files.Add(record);
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private static FileRecord Prepare(ArchiveEntry entry, out byte[]? linkBytes)
    {
        linkBytes = null;
        if (entry.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory or ArchiveEntryKind.Symlink))
        {
            throw new ArchiveUnsupportedException("7z", $"{entry.Kind} entries (7z stores files, directories and symbolic links)");
        }

        string name = entry.Name;
        if (string.IsNullOrEmpty(name) || name.Contains('\0'))
        {
            throw new ArgumentException("An entry name must be non-empty and contain no NUL.", nameof(entry));
        }

        name = name.TrimEnd('/');
        if (name.Length == 0)
        {
            throw new ArgumentException("An entry name must contain more than slashes.", nameof(entry));
        }

        byte[] nameBytes;
        try
        {
            nameBytes = s_names.GetBytes(name);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("The entry name is not valid UTF-16 text.", nameof(entry));
        }

        if (entry.Kind == ArchiveEntryKind.File && entry.Size < 0)
        {
            throw new ArgumentException("The entry's size is negative.", nameof(entry));
        }

        if (entry.Kind == ArchiveEntryKind.Symlink)
        {
            if (string.IsNullOrEmpty(entry.LinkTarget))
            {
                throw new ArgumentException("A symbolic link entry needs a LinkTarget.", nameof(entry));
            }

            linkBytes = Encoding.UTF8.GetBytes(entry.LinkTarget);
        }

        uint attrib = entry.Kind == ArchiveEntryKind.Directory ? 0x10u : 0x20u;
        if (entry.Mode is { } mode)
        {
            uint type = entry.Kind switch { ArchiveEntryKind.Directory => 0x4000u, ArchiveEntryKind.Symlink => 0xA000u, _ => 0x8000u };
            attrib |= 0x8000 | ((type | ((uint)mode & 0xFFFu)) << 16);
        }
        else if (entry.Kind == ArchiveEntryKind.Symlink)
        {
            attrib |= 0x8000 | ((0xA000u | 0x1FFu) << 16);
        }

        return new FileRecord
        {
            Name = nameBytes,
            CTime = FileTime(entry.ChangeTime),
            ATime = FileTime(entry.AccessTime),
            MTime = FileTime(entry.ModifiedTime),
            Attributes = attrib,
        };
    }

    private static long? FileTime(DateTimeOffset? t)
    {
        if (t is not { } v)
        {
            return null;
        }

        long ticks = v.UtcTicks - FileTimeOffset;
        return ticks < 0 ? throw new ArgumentException("7z cannot store times before 1601-01-01.") : ticks;
    }

    private async ValueTask WriteContentAsync(ArchiveEntry entry, Stream source, long size, bool async, CancellationToken ct)
    {
        byte[] buffer = new byte[(int)Math.Min(size, 1 << 16)];
        int headLength = (int)Math.Min(size, HeadSize);
        byte[] head = new byte[headLength];
        await ReadExactAsync(source, head, entry, size, async, ct).ConfigureAwait(false);

        SevenZipFilter filter = ChooseFilter(head);
        bool close = _cur is not null &&
            (!_options.Solid ||
             _cur.Filter != filter ||
             _cur.Sizes.Count >= (_options.SolidFileCount == 0 ? int.MaxValue : _options.SolidFileCount) ||
             _cur.Unpack + size > _options.SolidBlockSize);
        if (close)
        {
            await CloseFolderAsync(async, ct).ConfigureAwait(false);
        }

        FolderRecord folder = _cur ?? OpenFolder(filter, _options.Solid ? -1 : size);
        _cur = folder;

        var crc = default(Crc32);
        crc.Append(head);
        await WriteToFolderAsync(folder, head, async, ct).ConfigureAwait(false);
        long remaining = size - headLength;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int want = (int)Math.Min(remaining, buffer.Length);
            int n = async
                ? await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false)
                : source.Read(buffer, 0, want);
            if (n <= 0)
            {
                throw new ArgumentException($"The content of '{entry.Name}' ended before the entry's size of {size} bytes.", nameof(entry));
            }

            crc.Append(buffer.AsSpan(0, n));
            await WriteToFolderAsync(folder, buffer.AsMemory(0, n), async, ct).ConfigureAwait(false);
            remaining -= n;
        }

        if (entry.Kind == ArchiveEntryKind.File)
        {
            byte[] probe = new byte[1];
            int extra = async ? await source.ReadAsync(probe, ct).ConfigureAwait(false) : source.Read(probe, 0, 1);
            if (extra > 0)
            {
                throw new ArgumentException($"The content of '{entry.Name}' is longer than the entry's size of {size}.", nameof(entry));
            }
        }

        folder.Unpack += size;
        folder.Sizes.Add(size);
        folder.Crcs.Add(crc.GetCurrentValue());
    }

    private static async ValueTask ReadExactAsync(Stream source, byte[] destination, ArchiveEntry entry, long size, bool async, CancellationToken ct)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int n = async
                ? await source.ReadAsync(destination.AsMemory(done), ct).ConfigureAwait(false)
                : source.Read(destination, done, destination.Length - done);
            if (n <= 0)
            {
                throw new ArgumentException($"The content of '{entry.Name}' ended before the entry's size of {size} bytes.", nameof(entry));
            }

            done += n;
        }
    }

    private static async ValueTask WriteToFolderAsync(FolderRecord folder, ReadOnlyMemory<byte> data, bool async, CancellationToken ct)
    {
        if (async)
        {
            await folder.Top.WriteAsync(data, ct).ConfigureAwait(false);
        }
        else
        {
            folder.Top.Write(data.Span);
        }
    }

    private SevenZipFilter ChooseFilter(ReadOnlySpan<byte> head)
    {
        if (_method == SevenZipMethod.Copy && _options.Filter == SevenZipFilter.Auto)
        {
            return SevenZipFilter.None;
        }

        return _options.Filter == SevenZipFilter.Auto ? Detect(head) : _options.Filter;
    }

    // Executables get the converter of their instruction set; the machine field is read from the ELF or PE header.
    private static SevenZipFilter Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 20 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
        {
            ushort machine = head[5] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(head[18..]) : BinaryPrimitives.ReadUInt16LittleEndian(head[18..]);
            if (head[5] == 1)
            {
                return machine switch
                {
                    3 or 62 => SevenZipFilter.X86,
                    40 => SevenZipFilter.Arm,
                    183 => SevenZipFilter.Arm64,
                    243 => SevenZipFilter.RiscV,
                    50 => SevenZipFilter.Ia64,
                    _ => SevenZipFilter.None,
                };
            }

            if (head[5] == 2)
            {
                return machine switch
                {
                    20 or 21 => SevenZipFilter.PowerPc,
                    2 or 43 => SevenZipFilter.Sparc,
                    _ => SevenZipFilter.None,
                };
            }

            return SevenZipFilter.None;
        }

        if (head.Length >= 0x40 && head[0] == (byte)'M' && head[1] == (byte)'Z')
        {
            uint pe = BinaryPrimitives.ReadUInt32LittleEndian(head[0x3C..]);
            if (pe <= (uint)head.Length - 6 && head.Slice((int)pe, 4).SequenceEqual("PE\0\0"u8))
            {
                return BinaryPrimitives.ReadUInt16LittleEndian(head[((int)pe + 4)..]) switch
                {
                    0x014C or 0x8664 => SevenZipFilter.X86,
                    0x01C0 => SevenZipFilter.Arm,
                    0x01C2 or 0x01C4 => SevenZipFilter.ArmThumb,
                    0xAA64 => SevenZipFilter.Arm64,
                    _ => SevenZipFilter.None,
                };
            }
        }

        return SevenZipFilter.None;
    }

    private FolderRecord OpenFolder(SevenZipFilter filter, long knownLength)
    {
        var sink = new CountingSink(_out);
        var folder = new FolderRecord { Filter = filter, Sink = sink };

        // With a password the coders write into the AES stream, which pads and writes to the counted sink.
        Stream codecSink = sink;
        if (_encrypt)
        {
            byte[] iv = NewIv();
            folder.Aes = new SevenZipAesEncryptStream(sink, Key(), iv);
            folder.AesProperties = SevenZipAes.BuildProperties(_options.KeyDerivationPower, iv);
            codecSink = folder.Aes;
        }

        CodecId? id = _method switch
        {
            SevenZipMethod.Lzma2 or SevenZipMethod.Lzma => CodecId.Lzma,
            SevenZipMethod.Deflate => CodecId.Deflate,
            SevenZipMethod.Deflate64 => CodecId.Deflate64,
            SevenZipMethod.Zstd => CodecId.Zstd,
            SevenZipMethod.Ppmd => CodecId.Ppmd,
            _ => null,
        };
        Stream top = id is { } known
            ? CodecRegistry.Require(known, SevenZipFormat.Name, $"writing the {_method} method")
                .OpenSevenZipCompress(_method, codecSink, _options, knownLength, props => folder.CompressorProperties = props, DeferThreshold(knownLength))
            : codecSink;

        if (filter != SevenZipFilter.None)
        {
            top = CodecRegistry.Require(CodecId.Filters, SevenZipFormat.Name, $"writing the {filter} filter")
                .OpenSevenZipFilter(top, filter, _options.DeltaDistance);
        }

        folder.Top = top;
        return folder;
    }

    // A non-solid folder's size is the file's; a solid folder's is unknown until it closes, so its first 16 MiB are held back:
    // a folder that ends inside them gets tables sized to its content instead of to a dictionary of up to 64 MiB, which in a
    // 32-bit WebAssembly heap is the difference between finishing and running out of memory.
    private static int DeferThreshold(long knownLength) => knownLength >= 0 ? 0 : 16 << 20;

    private async ValueTask CloseFolderAsync(bool async, CancellationToken ct)
    {
        if (_cur is not { } folder)
        {
            return;
        }

        _cur = null;
        if (async)
        {
            await folder.Top.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            folder.Top.Dispose();
        }

        if (folder.Aes is { } aes)
        {
            aes.Complete();
            folder.AesUnpack = aes.InputCount;
            aes.Dispose();
        }

        folder.Pack = folder.Sink.Count;
        _folders.Add(folder);
        _ = ct;
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
            await CloseFolderAsync(async, cancellationToken).ConfigureAwait(false);
            long nextOffset = 0;
            long nextSize = 0;
            uint nextCrc = 0;
            if (_files.Count > 0)
            {
                byte[] header = BuildHeader();
                long dataEnd = _out.Position - _origin - StartHeaderSize;
                if (_options.EncodeHeader || (_encrypt && _options.EncryptHeader))
                {
                    byte[] packed = CodecRegistry.Require(CodecId.Lzma, SevenZipFormat.Name, "compressing the header (set EncodeHeader to false to leave it plain)").SevenZipCompressHeader(header, out byte[] props);
                    long packPos = dataEnd;
                    long packedSize = packed.Length;
                    byte[] aesProps = [];
                    if (_encrypt && _options.EncryptHeader)
                    {
                        byte[] iv = NewIv();
                        aesProps = SevenZipAes.BuildProperties(_options.KeyDerivationPower, iv);
                        using var counted = new CountingSink(_out);
                        using var aes = new SevenZipAesEncryptStream(counted, Key(), iv);
                        aes.Write(packed);
                        aes.Complete();
                        packedSize = counted.Count;
                    }
                    else
                    {
                        _out.Write(packed);
                    }

                    header = BuildEncodedHeaderRecord(packPos, packedSize, header.Length, Crc32.Compute(header), props, aesProps, packed.Length);
                    dataEnd += packedSize;
                }

                nextOffset = dataEnd;
                nextSize = header.Length;
                nextCrc = Crc32.Compute(header);
                _out.Write(header);
            }

            byte[] start = new byte[StartHeaderSize];
            start[0] = 0x37;
            start[1] = 0x7A;
            start[2] = 0xBC;
            start[3] = 0xAF;
            start[4] = 0x27;
            start[5] = 0x1C;
            start[7] = 4; // format version 0.4
            BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(12), (ulong)nextOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(start.AsSpan(20), (ulong)nextSize);
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(28), nextCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(8), Crc32.Compute(start.AsSpan(12, 20)));
            long end = _out.Position;
            _out.Position = _origin;
            _out.Write(start);
            _out.Position = end;
            if (_buffered)
            {
                var ms = (MemoryStream)_out;
                if (async)
                {
                    await _target.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _target.Write(ms.GetBuffer(), 0, (int)ms.Length);
                }
            }

            _finished = true;
            if (async)
            {
                await _target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _target.Flush();
            }
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    // The header is small and its size is known, so the encoder is told the length and writes no end marker.
    // The key does not depend on the IV (7z AES has no salt by default), so one derivation serves every folder and the header.
    private byte[] Key() => _key ??= SevenZipAes.DeriveKey(_options.Password, [], _options.KeyDerivationPower);

    private byte[] NewIv()
    {
        byte[] iv = new byte[SevenZipAes.BlockLength];
        if (_options.RandomBytes is { } fill)
        {
            fill(iv);
        }
        else
        {
            RandomNumberGenerator.Fill(iv);
        }

        return iv;
    }

    private static byte[] BuildEncodedHeaderRecord(long packPos, long packSize, long unpackSize, uint crc, byte[] props, byte[] aesProps, long aesUnpack)
    {
        var w = new HeaderBuffer();
        w.Byte(0x17); // EncodedHeader
        w.Byte(0x06); // PackInfo
        w.Number((ulong)packPos);
        w.Number(1);
        w.Byte(0x09);
        w.Number((ulong)packSize);
        w.Byte(0x00);
        w.Byte(0x07); // UnpackInfo
        w.Byte(0x0B);
        w.Number(1);
        w.Byte(0);
        bool encrypted = aesProps.Length > 0;
        w.Number(encrypted ? 2UL : 1UL); // coders: the AES decryptor first, then LZMA reading its output
        if (encrypted)
        {
            WriteAesCoder(w, aesProps);
        }

        w.Byte(0x23);
        w.Bytes([0x03, 0x01, 0x01]);
        w.Number(5);
        w.Bytes(props);
        if (encrypted)
        {
            w.Number(1); // LZMA's input is fed by the AES output
            w.Number(0);
        }

        w.Byte(0x0C);
        if (encrypted)
        {
            w.Number((ulong)aesUnpack);
        }

        w.Number((ulong)unpackSize);
        w.Byte(0x0A);
        w.Byte(1);
        w.UInt32(crc);
        w.Byte(0x00);
        w.Byte(0x00);
        return w.ToArray();
    }

    // 7zFormat.txt "Coders Info": flag byte (4-byte ID, has properties), the method ID 06 F1 07 01, the properties.
    private static void WriteAesCoder(HeaderBuffer w, byte[] props)
    {
        w.Byte(0x24);
        w.Bytes([0x06, 0xF1, 0x07, 0x01]);
        w.Number((ulong)props.Length);
        w.Bytes(props);
    }

    private byte[] BuildHeader()
    {
        var w = new HeaderBuffer();
        w.Byte(0x01); // Header
        if (_folders.Count > 0)
        {
            w.Byte(0x04); // MainStreamsInfo
            w.Byte(0x06); // PackInfo
            w.Number(0);
            w.Number((ulong)_folders.Count);
            w.Byte(0x09);
            foreach (FolderRecord f in _folders)
            {
                w.Number((ulong)f.Pack);
            }

            w.Byte(0x00);
            w.Byte(0x07); // UnpackInfo
            w.Byte(0x0B);
            w.Number((ulong)_folders.Count);
            w.Byte(0);
            foreach (FolderRecord f in _folders)
            {
                WriteFolder(w, f);
            }

            w.Byte(0x0C);
            foreach (FolderRecord f in _folders)
            {
                if (f.Aes is not null)
                {
                    w.Number((ulong)f.AesUnpack);
                }

                w.Number((ulong)f.Unpack);
                if (f.Filter != SevenZipFilter.None)
                {
                    w.Number((ulong)f.Unpack);
                }
            }

            w.Byte(0x00);
            w.Byte(0x08); // SubStreamsInfo
            w.Byte(0x0D);
            foreach (FolderRecord f in _folders)
            {
                w.Number((ulong)f.Sizes.Count);
            }

            w.Byte(0x09);
            foreach (FolderRecord f in _folders)
            {
                for (int i = 0; i < f.Sizes.Count - 1; i++)
                {
                    w.Number((ulong)f.Sizes[i]);
                }
            }

            w.Byte(0x0A);
            w.Byte(1);
            foreach (FolderRecord f in _folders)
            {
                foreach (uint c in f.Crcs)
                {
                    w.UInt32(c);
                }
            }

            w.Byte(0x00);
            w.Byte(0x00);
        }

        w.Byte(0x05); // FilesInfo
        w.Number((ulong)_files.Count);
        int empty = 0;
        int emptyFiles = 0;
        foreach (FileRecord f in _files)
        {
            if (f.EmptyStream)
            {
                empty++;
                if (f.EmptyFile)
                {
                    emptyFiles++;
                }
            }
        }

        if (empty > 0)
        {
            WriteBits(w, 0x0E, _files.Select(f => f.EmptyStream));
            if (emptyFiles > 0)
            {
                WriteBits(w, 0x0F, _files.Where(f => f.EmptyStream).Select(f => f.EmptyFile));
            }
        }

        int nameBytes = 1 + _files.Sum(f => f.Name.Length + 2);
        w.Byte(0x11);
        w.Number((ulong)nameBytes);
        w.Byte(0);
        foreach (FileRecord f in _files)
        {
            w.Bytes(f.Name);
            w.Bytes([0, 0]);
        }

        WriteTimes(w, 0x12, f => f.CTime);
        WriteTimes(w, 0x13, f => f.ATime);
        WriteTimes(w, 0x14, f => f.MTime);
        List<uint?> attrs = _files.Select(f => f.Attributes).ToList();
        if (attrs.Any(a => a.HasValue))
        {
            WriteDefined(w, 0x15, attrs.Select(a => a.HasValue), attrs.Where(a => a.HasValue).Count() * 4, hb => { foreach (uint? a in attrs) { if (a is { } v) { hb.UInt32(v); } } });
        }

        w.Byte(0x00);
        w.Byte(0x00);
        return w.ToArray();

        void WriteTimes(HeaderBuffer hb, byte id, Func<FileRecord, long?> pick)
        {
            List<long?> values = _files.Select(pick).ToList();
            if (values.Any(v => v.HasValue))
            {
                WriteDefined(hb, id, values.Select(v => v.HasValue), values.Count(v => v.HasValue) * 8, b => { foreach (long? v in values) { if (v is { } t) { b.UInt64((ulong)t); } } });
            }
        }
    }

    // A property whose values may be missing for some files: allDefined byte or a bit vector, external = 0, then the values.
    private static void WriteDefined(HeaderBuffer w, byte id, IEnumerable<bool> defined, int valueBytes, Action<HeaderBuffer> values)
    {
        bool[] d = defined.ToArray();
        bool all = d.All(x => x);
        int vector = all ? 0 : (d.Length + 7) / 8;
        w.Byte(id);
        w.Number((ulong)(2 + vector + valueBytes));
        w.Byte(all ? (byte)1 : (byte)0);
        if (!all)
        {
            w.Bytes(Pack(d));
        }

        w.Byte(0);
        values(w);
    }

    private static void WriteBits(HeaderBuffer w, byte id, IEnumerable<bool> bits)
    {
        byte[] packed = Pack(bits.ToArray());
        w.Byte(id);
        w.Number((ulong)packed.Length);
        w.Bytes(packed);
    }

    private static byte[] Pack(bool[] bits)
    {
        byte[] r = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                r[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }

        return r;
    }

    // Coders in the order 7-Zip lists them: the compression method first, then the filter whose input is its output.
    private void WriteFolder(HeaderBuffer w, FolderRecord f)
    {
        bool filtered = f.Filter != SevenZipFilter.None;
        bool aes = f.Aes is not null;
        w.Number((ulong)(1 + (filtered ? 1 : 0) + (aes ? 1 : 0)));
        if (aes)
        {
            WriteAesCoder(w, f.AesProperties);
        }

        switch (_method)
        {
            case SevenZipMethod.Lzma2:
                w.Byte(0x21);
                w.Byte(0x21);
                w.Number(1);
                w.Bytes(f.CompressorProperties);
                break;
            case SevenZipMethod.Lzma:
                w.Byte(0x23);
                w.Bytes([0x03, 0x01, 0x01]);
                w.Number(5);
                w.Bytes(f.CompressorProperties);
                break;
            case SevenZipMethod.Deflate:
                w.Byte(0x03);
                w.Bytes([0x04, 0x01, 0x08]);
                break;
            case SevenZipMethod.Deflate64:
                w.Byte(0x03);
                w.Bytes([0x04, 0x01, 0x09]);
                break;
            case SevenZipMethod.Zstd:
                w.Byte(0x03);
                w.Bytes([0xF7, 0x11, 0x01]);
                break;
            case SevenZipMethod.Ppmd:
                w.Byte(0x23);
                w.Bytes([0x03, 0x04, 0x01]);
                w.Number(5);
                w.Bytes(f.CompressorProperties);
                break;
            default:
                w.Byte(0x01);
                w.Byte(0x00);
                break;
        }

        if (filtered)
        {
            switch (f.Filter)
            {
                case SevenZipFilter.Delta:
                    w.Byte(0x21);
                    w.Byte(0x03);
                    w.Number(1);
                    w.Byte((byte)(_options.DeltaDistance - 1));
                    break;
                case SevenZipFilter.X86:
                    w.Byte(0x04);
                    w.Bytes([0x03, 0x03, 0x01, 0x03]);
                    break;
                case SevenZipFilter.PowerPc:
                    w.Byte(0x04);
                    w.Bytes([0x03, 0x03, 0x02, 0x05]);
                    break;
                case SevenZipFilter.Ia64:
                    w.Byte(0x04);
                    w.Bytes([0x03, 0x03, 0x04, 0x01]);
                    break;
                case SevenZipFilter.Arm:
                    w.Byte(0x04);
                    w.Bytes([0x03, 0x03, 0x05, 0x01]);
                    break;
                case SevenZipFilter.ArmThumb:
                    w.Byte(0x04);
                    w.Bytes([0x03, 0x03, 0x07, 0x01]);
                    break;
                case SevenZipFilter.Sparc:
                    w.Byte(0x04);
                    w.Bytes([0x03, 0x03, 0x08, 0x05]);
                    break;
                case SevenZipFilter.Arm64:
                    w.Byte(0x01);
                    w.Byte(0x0A);
                    break;
                default:
                    w.Byte(0x01);
                    w.Byte(0x0B);
                    break;
            }

        }

        // Bond pairs: each coder's input is fed by the output of the coder before it (the AES decryptor, then the
        // compressor, then the filter, when present).
        for (int i = 1; i < 1 + (filtered ? 1 : 0) + (aes ? 1 : 0); i++)
        {
            w.Number((ulong)i);
            w.Number((ulong)(i - 1));
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
            if (disposing)
            {
                _cur?.Top.Dispose();
                if (_buffered)
                {
                    _out.Dispose();
                }

                if (!_leaveOpen)
                {
                    _target.Dispose();
                }
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
            if (_cur is { } c)
            {
                await c.Top.DisposeAsync().ConfigureAwait(false);
            }

            if (_buffered)
            {
                await _out.DisposeAsync().ConfigureAwait(false);
            }

            if (!_leaveOpen)
            {
                await _target.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class HeaderBuffer
    {
        private readonly List<byte> _bytes = [];

        public void Byte(byte b) => _bytes.Add(b);

        public void Bytes(ReadOnlySpan<byte> b) => _bytes.AddRange(b.ToArray());

        public void UInt32(uint v)
        {
            Span<byte> s = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(s, v);
            Bytes(s);
        }

        public void UInt64(ulong v)
        {
            Span<byte> s = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(s, v);
            Bytes(s);
        }

        // 7zFormat.txt "REAL_UINT64": the count of leading 1 bits in the first byte is the number of extra bytes.
        public void Number(ulong value)
        {
            byte first = 0;
            byte mask = 0x80;
            int i;
            for (i = 0; i < 8; i++)
            {
                if (value < (1UL << (7 * (i + 1))))
                {
                    first |= (byte)(value >> (8 * i));
                    break;
                }

                first |= mask;
                mask >>= 1;
            }

            _bytes.Add(first);
            for (int k = 0; k < i; k++)
            {
                _bytes.Add((byte)(value >> (8 * k)));
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
