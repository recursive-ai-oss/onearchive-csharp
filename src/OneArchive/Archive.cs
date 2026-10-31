using RecursiveAI.OneArchive.Internal;
using RecursiveAI.OneArchive.Ar;
using RecursiveAI.OneArchive.Cpio;
using RecursiveAI.OneArchive.Cab;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneArchive.Iso;
using RecursiveAI.OneArchive.Rar;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Tar;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive;

/// <summary>
/// The happy path: open, list, read and extract an archive in one call, whatever its format, and create one from files.
/// The format is detected from the data's magic bytes (and, for compressed archives, from the decompressed data), falling
/// back to the file name's extension. Everything here is built on the per-format readers and writers
/// (<see cref="TarReader"/>, <see cref="TarWriter"/>), which stay available for work the happy path does not cover.
/// </summary>
/// <remarks>
/// <para>An archive opened from a seekable source supports <see cref="Entries"/> and <see cref="OpenEntry(ArchiveEntry)"/>.
/// A tar read from a file is addressed directly; a compressed archive is decompressed again from the start to reach an
/// entry. A source that cannot seek can be read once, in order, with <see cref="OpenReader"/> or <see cref="Extract(string, ArchiveExtractOptions?, CancellationToken)"/>.</para>
/// <para>An <see cref="Archive"/> is not thread-safe.</para>
/// </remarks>
public sealed class Archive : IDisposable, IAsyncDisposable
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly bool _seekable;
    private readonly long _origin;
    private readonly ArchiveOpenOptions _options;
    private readonly string? _singleName;
    private readonly string? _sourceName;
    private readonly DateTimeOffset? _singleTime;
    private ArchiveReader? _pending;
    private ArchiveReader? _indexReader;
    private List<ArchiveEntry>? _entries;
    private bool _disposed;

    private Archive(Stream source, bool leaveOpen, long origin, ArchiveOpenOptions options, ArchiveFormat format, ArchiveCompression compression, ArchiveReader first, string? singleName, DateTimeOffset? singleTime, string? sourceName)
    {
        _sourceName = sourceName;
        _singleName = singleName;
        _singleTime = singleTime;
        _source = source;
        _leaveOpen = leaveOpen;
        _options = options;
        _seekable = source.CanSeek;
        _origin = origin;
        Format = format;
        Compression = compression;
        _pending = first;
    }

    /// <summary>Gets the container format.</summary>
    public ArchiveFormat Format { get; }

    /// <summary>Gets the compression layer around the container.</summary>
    public ArchiveCompression Compression { get; }

    /// <summary>Gets a value indicating whether the source can seek, so that <see cref="Entries"/> and <see cref="OpenEntry(ArchiveEntry)"/> work and the archive can be read more than once.</summary>
    public bool IsRandomAccess => _seekable;

    // ---- opening ----

    /// <summary>Opens an archive file.</summary>
    /// <param name="path">The archive's path.</param>
    /// <param name="options">Limits and overrides; <see langword="null"/> for the defaults.</param>
    /// <returns>The opened archive; dispose it to close the file.</returns>
    /// <exception cref="CompressionFormatException">The data is not a recognisable archive.</exception>
    /// <exception cref="ArchiveUnsupportedException">The archive uses a compression layer that is not available yet.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static Archive Open(string path, ArchiveOpenOptions? options = null) =>
        Internal.Sync.Result(OpenCoreAsync(path, null, options, false, default));

    /// <summary>Opens an archive file.</summary>
    /// <param name="path">The archive's path.</param>
    /// <param name="options">Limits and overrides; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">A token that cancels the open.</param>
    /// <returns>The opened archive.</returns>
    public static ValueTask<Archive> OpenAsync(string path, ArchiveOpenOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenCoreAsync(path, null, options, true, cancellationToken);

    /// <summary>Opens an archive from a stream.</summary>
    /// <param name="stream">A readable stream positioned at the start of the archive. Seekable streams enable random access.</param>
    /// <param name="options">Limits and overrides; <see cref="ArchiveOpenOptions.LeaveOpen"/> says whether the stream stays open.</param>
    /// <returns>The opened archive.</returns>
    /// <exception cref="ArgumentException">The stream cannot be read.</exception>
    /// <exception cref="CompressionFormatException">The data is not a recognisable archive.</exception>
    public static Archive Open(Stream stream, ArchiveOpenOptions? options = null) =>
        Internal.Sync.Result(OpenCoreAsync(null, stream, options, false, default));

    /// <summary>Opens an archive from a stream.</summary>
    /// <param name="stream">A readable stream positioned at the start of the archive.</param>
    /// <param name="options">Limits and overrides.</param>
    /// <param name="cancellationToken">A token that cancels the open.</param>
    /// <returns>The opened archive.</returns>
    public static ValueTask<Archive> OpenAsync(Stream stream, ArchiveOpenOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenCoreAsync(null, stream, options, true, cancellationToken);

    private static async ValueTask<Archive> OpenCoreAsync(string? path, Stream? stream, ArchiveOpenOptions? options, bool async, CancellationToken ct)
    {
        options ??= new ArchiveOpenOptions();
        bool leaveOpen;
        string? name = options.FileName;
        if (path is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = (async ? FileOptions.Asynchronous : FileOptions.None) | FileOptions.SequentialScan,
            });
            leaveOpen = false;
            name = path;
        }
        else
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanRead)
            {
                throw new ArgumentException("The stream must be readable.", nameof(stream));
            }

            leaveOpen = options.LeaveOpen;
        }

        try
        {
            return await DetectAndOpenAsync(stream, leaveOpen, name, options, async, ct).ConfigureAwait(false);
        }
        catch
        {
            if (!leaveOpen)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    private static async ValueTask<int> ReadUpToAsync(Stream stream, byte[] buffer, bool async, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = async
                ? await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)
                : stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static async ValueTask<Archive> DetectAndOpenAsync(Stream source, bool leaveOpen, string? name, ArchiveOpenOptions options, bool async, CancellationToken ct)
    {
        long origin = source.CanSeek ? source.Position : 0;
        byte[] prefix = new byte[ArchiveDetector.ExtendedPrefixLength];
        int got = await ReadUpToAsync(source, prefix, async, ct).ConfigureAwait(false);
        Stream raw;
        if (source.CanSeek)
        {
            source.Seek(origin, SeekOrigin.Begin);
            raw = source;
        }
        else
        {
            raw = new PrefixedStream(prefix, got, source, leaveOpen: true);
        }

        ArchiveFormat nameFormat = name is null ? ArchiveFormat.Unknown : ArchiveDetector.FromFileName(name).Format;
        ArchiveCompression compression = options.Compression ?? ArchiveDetector.DetectCompression(prefix.AsSpan(0, got));
        if (options.Compression is null && compression == ArchiveCompression.None && name is not null
            && ArchiveDetector.FromFileName(name).Compression == ArchiveCompression.Brotli
            && ArchiveDetector.DetectFormat(prefix.AsSpan(0, got)) == ArchiveFormat.Unknown)
        {
            // Brotli has no magic (RFC 7932 section 9.1), so the name decides, and only when the bytes are not already a container.
            compression = ArchiveCompression.Brotli;
        }

        ArchiveFormat format = options.Format ?? ArchiveFormat.Unknown;
        Stream top = raw;
        Stream? plainStream = null;
        ExpansionMeter? meter = null;
        if (compression != ArchiveCompression.None)
        {
            meter = new ExpansionMeter(options.Limits, "archive");
            Stream counted = new CountingStream(raw, meter, leaveOpen: true);
            Stream plain = new ProducedStream(CompressionFilters.OpenDecompress(compression, counted, options.Limits), meter);
            plainStream = plain;
            byte[] inner = new byte[ArchiveDetector.PrefixLength];
            int innerGot;
            try
            {
                innerGot = await ReadUpToAsync(plain, inner, async, ct).ConfigureAwait(false);
            }
            catch
            {
                plain.Dispose();
                throw;
            }

            top = new PrefixedStream(inner, innerGot, plain, leaveOpen: false);
            if (format == ArchiveFormat.Unknown)
            {
                format = ArchiveDetector.DetectFormat(inner.AsSpan(0, innerGot));
                if (format == ArchiveFormat.Unknown && innerGot == inner.Length && !inner.AsSpan().ContainsAnyExcept((byte)0))
                {
                    // A compressed stream that starts with a zero block is a tar with no entries, unless the name says it is
                    // some other compressed file (a disk image starts with zeros too).
                    if (nameFormat == ArchiveFormat.Tar || name is null)
                    {
                        format = ArchiveFormat.Tar;
                    }
                }
            }
        }
        else if (format == ArchiveFormat.Unknown)
        {
            format = ArchiveDetector.DetectFormat(prefix.AsSpan(0, got));
        }

        if (format == ArchiveFormat.Unknown)
        {
            format = nameFormat;
        }

        if (format == ArchiveFormat.Rar && compression == ArchiveCompression.None)
        {
            RarReader.ThrowIfRar4(prefix.AsSpan(0, got));
        }

        string? singleName = null;
        DateTimeOffset? singleTime = null;
        if (format == ArchiveFormat.Unknown && compression != ArchiveCompression.None)
        {
            // A compressed file that holds no container is an archive of one file.
            format = ArchiveFormat.SingleStream;
            (singleName, singleTime) = SingleStreamName(compression, plainStream, name);
        }

        if (format == ArchiveFormat.Unknown)
        {
            if (top != raw)
            {
                top.Dispose();
            }

            throw new CompressionFormatException("archive", 0, "a recognised archive format (zip, 7z, tar, or a compressed single file) in the data or in the file name's extension");
        }

        ArchiveReader reader = CreateReader(format, top, ownsTop: top != raw, options, meter, singleName, singleTime, name);
        return new Archive(source, leaveOpen, origin, options, format, compression, reader, singleName, singleTime, name);
    }

    // The entry name of a single compressed file: the gzip header's FNAME if it has one (the decompressor has already read
    // the start of the data, so the header is known), otherwise the file name without the compression extension.
    private static (string Name, DateTimeOffset? Time) SingleStreamName(ArchiveCompression compression, Stream? plain, string? archiveName)
    {
        string? name = null;
        DateTimeOffset? time = null;
        if (compression == ArchiveCompression.Gzip && plain is ProducedStream { Inner: { } gz }
            && CodecRegistry.Require(CodecId.Deflate, "gzip", "gzip headers").TryGetGZipHeader(gz, out string? stored, out uint modified))
        {
            if (stored is { Length: > 0 })
            {
                name = stored;
            }

            if (modified != 0)
            {
                time = DateTimeOffset.FromUnixTimeSeconds(modified);
            }
        }

        if (name is null)
        {
            string file = archiveName is null ? string.Empty : Path.GetFileName(archiveName);
            foreach (string suffix in new[] { ".gz", ".bz2", ".zst", ".zstd", ".lz4", ".xz", ".lz", ".br" })
            {
                if (file.Length > suffix.Length && file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    file = file[..^suffix.Length];
                    break;
                }
            }

            name = file.Length > 0 ? file : "data";
        }

        return (name, time);
    }

    private static ArchiveReader CreateReader(ArchiveFormat format, Stream stream, bool ownsTop, ArchiveOpenOptions options, ExpansionMeter? meter, string? singleName, DateTimeOffset? singleTime, string? sourceName)
    {
        switch (format)
        {
            case ArchiveFormat.Zip:
                ZipReaderOptions zip = options.Zip is { } z
                    ? new ZipReaderOptions { Limits = options.Limits, VerifyChecksum = z.VerifyChecksum, ForceStreaming = z.ForceStreaming, LegacyNameEncoding = z.LegacyNameEncoding, MaxCentralDirectorySize = z.MaxCentralDirectorySize, Recovery = z.Recovery, MaxRecoveryBytes = z.MaxRecoveryBytes, Password = options.Password.IsEmpty ? z.Password : options.Password }
                    : new ZipReaderOptions { Limits = options.Limits, Password = options.Password };
                return new ZipReader(stream, zip, leaveOpen: !ownsTop);
            case ArchiveFormat.SevenZip:
                SevenZipReaderOptions sz = options.SevenZip is { } s7
                    ? new SevenZipReaderOptions { Limits = options.Limits, VerifyChecksum = s7.VerifyChecksum, MaxHeaderSize = s7.MaxHeaderSize, MaxDictionarySize = s7.MaxDictionarySize, MaxCodersPerFolder = s7.MaxCodersPerFolder, MaxKeyDerivationPower = s7.MaxKeyDerivationPower, Password = options.Password.IsEmpty ? s7.Password : options.Password }
                    : new SevenZipReaderOptions { Limits = options.Limits, Password = options.Password };
                if (!stream.CanSeek)
                {
                    // The header is at the end of a 7z file: a source that cannot seek is copied into memory first.
                    Stream spooled = SpoolToMemory(stream, options.Limits);
                    if (ownsTop)
                    {
                        stream.Dispose();
                    }

                    return new SevenZipReader(spooled, sz, leaveOpen: false);
                }

                return new SevenZipReader(stream, sz, leaveOpen: !ownsTop);
            case ArchiveFormat.Cab:
                CabReaderOptions cab = options.Cab is { } cb
                    ? new CabReaderOptions { Limits = options.Limits, VerifyChecksum = cb.VerifyChecksum, LegacyNameEncoding = cb.LegacyNameEncoding }
                    : new CabReaderOptions { Limits = options.Limits };
                if (!stream.CanSeek)
                {
                    // The file table points at the data blocks: a source that cannot seek is copied into memory first.
                    Stream spooled = SpoolToMemory(stream, options.Limits, CabReader.Name);
                    if (ownsTop)
                    {
                        stream.Dispose();
                    }

                    return new CabReader(spooled, cab, leaveOpen: false);
                }

                return new CabReader(stream, cab, leaveOpen: !ownsTop);
            case ArchiveFormat.CompoundFile:
                var cfb = new CompoundFileReaderOptions { Limits = options.Limits, MaxDirectoryEntries = options.CompoundFile?.MaxDirectoryEntries ?? CompoundFileReaderOptions.DefaultMaxDirectoryEntries };
                if (!stream.CanSeek)
                {
                    // Sector chains are followed by offset: a source that cannot seek is copied into memory first.
                    Stream spooled = SpoolToMemory(stream, options.Limits, CompoundFileReader.Name);
                    if (ownsTop)
                    {
                        stream.Dispose();
                    }

                    return new CompoundFileReader(spooled, cfb, leaveOpen: false);
                }

                return new CompoundFileReader(stream, cfb, leaveOpen: !ownsTop);
            case ArchiveFormat.Rar:
                RarReaderOptions rar = options.Rar is { } rr
                    ? new RarReaderOptions { Limits = options.Limits, VerifyChecksum = rr.VerifyChecksum, Volumes = rr.Volumes, MaxSfxSize = rr.MaxSfxSize }
                    : new RarReaderOptions { Limits = options.Limits };
                if (rar.Volumes is null && sourceName is not null && File.Exists(sourceName) && meter is null)
                {
                    rar = new RarReaderOptions { Limits = rar.Limits, VerifyChecksum = rar.VerifyChecksum, MaxSfxSize = rar.MaxSfxSize, Volumes = RarVolumes.ForFile(sourceName) };
                }

                return new RarReader(stream, rar, leaveOpen: !ownsTop);

            case ArchiveFormat.Iso:
                IsoReaderOptions iso = options.Iso is { } io
                    ? new IsoReaderOptions { Limits = options.Limits, Tree = io.Tree, UseRockRidge = io.UseRockRidge, MaxDirectorySize = io.MaxDirectorySize, MaxSystemUseSize = io.MaxSystemUseSize, MaxContinuationBytes = io.MaxContinuationBytes }
                    : new IsoReaderOptions { Limits = options.Limits };
                if (!stream.CanSeek)
                {
                    // Directories and files are found by block number: a source that cannot seek is copied into memory first.
                    Stream spooled = SpoolToMemory(stream, options.Limits, IsoReader.Name);
                    if (ownsTop)
                    {
                        stream.Dispose();
                    }

                    return new IsoReader(spooled, iso, leaveOpen: false);
                }

                return new IsoReader(stream, iso, leaveOpen: !ownsTop);
            case ArchiveFormat.SingleStream:
                return new SingleStreamReader(stream, singleName ?? "data", singleTime, options.Limits, leaveOpen: !ownsTop);
            case ArchiveFormat.Tar:
                TarReaderOptions tar = options.Tar is { } t
                    ? new TarReaderOptions
                    {
                        Limits = options.Limits,
                        MaxExtendedHeaderSize = t.MaxExtendedHeaderSize,
                        MaxSparseRegions = t.MaxSparseRegions,
                        IgnoreZeroBlocks = t.IgnoreZeroBlocks,
                        RequireEndOfArchive = t.RequireEndOfArchive,
                    }
                    : new TarReaderOptions { Limits = options.Limits };
                return new TarReader(stream, tar, leaveOpen: !ownsTop, meter);
            case ArchiveFormat.Cpio:
                return new CpioReader(stream, options.Cpio is { } c ? new CpioReaderOptions { Limits = options.Limits, VerifyChecksum = c.VerifyChecksum, RequireTrailer = c.RequireTrailer, ResolveLinkStubs = c.ResolveLinkStubs } : new CpioReaderOptions { Limits = options.Limits }, leaveOpen: !ownsTop, drainAtEnd: meter is not null);
            case ArchiveFormat.Ar:
                return new ArReader(stream, options.Ar is { } a ? new ArReaderOptions { Limits = options.Limits, MaxNameTableSize = a.MaxNameTableSize, IncludeSpecialMembers = a.IncludeSpecialMembers } : new ArReaderOptions { Limits = options.Limits }, leaveOpen: !ownsTop, drainAtEnd: meter is not null);
            default:
                throw new ArchiveUnsupportedException("archive", $"the {format} format");
        }
    }

    private static MemoryStream SpoolToMemory(Stream source, ArchiveLimits limits, string format = "7z")
    {
        var memory = new MemoryStream();
        byte[] buffer = new byte[81920];
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (memory.Length + n > limits.MaxTotalSize)
            {
                throw new CompressionLimitException(format, nameof(ArchiveLimits.MaxTotalSize), limits.MaxTotalSize, memory.Length + n);
            }

            memory.Write(buffer, 0, n);
        }

        memory.Position = 0;
        return memory;
    }

    // A reader positioned at the start of the archive. The first call hands out the reader built while detecting.
    private ArchiveReader NewReader()
    {
        if (_pending is { } first)
        {
            _pending = null;
            return first;
        }

        if (!_seekable)
        {
            throw new InvalidOperationException("The source cannot seek, so the archive can only be read once.");
        }

        _source.Seek(_origin, SeekOrigin.Begin);
        ExpansionMeter? meter = null;
        Stream top = _source;
        bool owns = false;
        if (Compression != ArchiveCompression.None)
        {
            meter = new ExpansionMeter(_options.Limits, "archive");
            Stream counted = new CountingStream(_source, meter, leaveOpen: true);
            top = new ProducedStream(CompressionFilters.OpenDecompress(Compression, counted, _options.Limits), meter);
            owns = true;
        }

        return CreateReader(Format, top, owns, _options, meter, _singleName, _singleTime, _sourceName);
    }

    // ---- reading ----

    /// <summary>
    /// Opens a sequential reader over the archive's entries. A seekable source can be read again after the previous
    /// reader is disposed; any other source gives its reader once.
    /// </summary>
    /// <returns>A reader positioned before the first entry.</returns>
    /// <exception cref="InvalidOperationException">The source cannot seek and the archive was already read.</exception>
    public ArchiveReader OpenReader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NewReader();
    }

    /// <summary>
    /// Gets every entry of the archive, in archive order. The first call reads the whole archive (and decompresses it,
    /// for a compressed one); the limits apply.
    /// </summary>
    /// <exception cref="NotSupportedException">The source cannot seek (<see cref="IsRandomAccess"/> is false).</exception>
    /// <exception cref="CompressionFormatException">The archive is malformed.</exception>
    public IReadOnlyList<ArchiveEntry> Entries
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_seekable)
            {
                throw new NotSupportedException("The source cannot seek; read the archive in order with OpenReader or Extract.");
            }

            if (_entries is null)
            {
                var list = new List<ArchiveEntry>();
                ArchiveReader reader = NewReader();
                try
                {
                    while (reader.GetNextEntry() is { } entry)
                    {
                        list.Add(entry);
                    }
                }
                catch
                {
                    reader.Dispose();
                    throw;
                }

                if (reader.SupportsRandomAccess && Compression == ArchiveCompression.None)
                {
                    _indexReader = reader;
                }
                else
                {
                    reader.Dispose();
                }

                _entries = list;
            }

            return _entries;
        }
    }

    /// <summary>Finds an entry by its exact name; the last one wins when a name occurs twice, as when extracting.</summary>
    /// <param name="name">The entry's name as stored.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    /// <exception cref="NotSupportedException">The source cannot seek.</exception>
    public ArchiveEntry? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var entries = Entries;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Name == name)
            {
                return entries[i];
            }
        }

        return null;
    }

    /// <summary>Opens the content of an entry listed in <see cref="Entries"/>.</summary>
    /// <param name="entry">An entry of <see cref="Entries"/>.</param>
    /// <returns>A read-only stream; seekable for an uncompressed tar. Dispose it when done.</returns>
    /// <exception cref="NotSupportedException">The source cannot seek.</exception>
    /// <exception cref="ArgumentException">The entry is not one of this archive's entries.</exception>
    public Stream OpenEntry(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var entries = Entries;
        if (_indexReader is { } direct)
        {
            return direct.OpenEntryStream(entry);
        }

        int index = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (ReferenceEquals(entries[i], entry))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            throw new ArgumentException("The entry does not belong to this archive.", nameof(entry));
        }

        // A compressed archive cannot be addressed: decompress again up to the entry.
        ArchiveReader reader = NewReader();
        try
        {
            for (int i = 0; i <= index; i++)
            {
                reader.GetNextEntry();
            }

            return new ReaderOwnedStream(reader.OpenEntryStream(), reader);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    // ---- extraction ----

    /// <summary>Extracts the whole archive into a directory, applying the safety rules and limits of <paramref name="options"/>.</summary>
    /// <param name="targetDirectory">The directory to extract into; created if missing.</param>
    /// <param name="options">Safety, limit and metadata options; <see langword="null"/> for the safe defaults.</param>
    /// <param name="cancellationToken">A token that cancels the extraction.</param>
    /// <returns>What was extracted.</returns>
    /// <exception cref="ArchiveSafetyException">An entry breaks a safety rule; nothing is written for it.</exception>
    /// <exception cref="CompressionLimitException">A limit was exceeded.</exception>
    /// <exception cref="CompressionFormatException">The archive is malformed or truncated.</exception>
    public ArchiveExtractResult Extract(string targetDirectory, ArchiveExtractOptions? options = null, CancellationToken cancellationToken = default) =>
        Internal.Sync.Result(ExtractCoreAsync(targetDirectory, options, false, cancellationToken));

    /// <summary>Extracts the whole archive into a directory, applying the safety rules and limits of <paramref name="options"/>.</summary>
    /// <param name="targetDirectory">The directory to extract into; created if missing.</param>
    /// <param name="options">Safety, limit and metadata options; <see langword="null"/> for the safe defaults.</param>
    /// <param name="cancellationToken">A token that cancels the extraction.</param>
    /// <returns>What was extracted.</returns>
    public ValueTask<ArchiveExtractResult> ExtractAsync(string targetDirectory, ArchiveExtractOptions? options = null, CancellationToken cancellationToken = default) =>
        ExtractCoreAsync(targetDirectory, options, true, cancellationToken);

    private async ValueTask<ArchiveExtractResult> ExtractCoreAsync(string targetDirectory, ArchiveExtractOptions? options, bool async, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(targetDirectory);
        options ??= new ArchiveExtractOptions { Limits = _options.Limits };
        ArchiveReader reader = NewReader();
        try
        {
            var extractor = new ArchiveExtractor(targetDirectory, options, reader.Format.ToString().ToLowerInvariant());
            ArchiveExtractResult result = await extractor.RunAsync(reader, async, ct).ConfigureAwait(false);

            // Read the compressed stream to its end so that its own checks (frame checksum, truncation) run.
            if (reader is TarReader tar)
            {
                await tar.CompleteAsync(async, ct).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            if (async)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>Extracts an archive file into a directory.</summary>
    /// <param name="archivePath">The archive's path.</param>
    /// <param name="targetDirectory">The directory to extract into; created if missing.</param>
    /// <param name="options">Safety, limit and metadata options; <see langword="null"/> for the safe defaults.</param>
    /// <param name="cancellationToken">A token that cancels the extraction.</param>
    /// <returns>What was extracted.</returns>
    public static ArchiveExtractResult Extract(string archivePath, string targetDirectory, ArchiveExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        using Archive archive = Open(archivePath, options);
        return archive.Extract(targetDirectory, options, cancellationToken);
    }

    /// <summary>Extracts an archive from a stream into a directory.</summary>
    /// <param name="archive">The archive's data.</param>
    /// <param name="targetDirectory">The directory to extract into; created if missing.</param>
    /// <param name="options">Safety, limit and metadata options; <see langword="null"/> for the safe defaults.</param>
    /// <param name="cancellationToken">A token that cancels the extraction.</param>
    /// <returns>What was extracted.</returns>
    public static ArchiveExtractResult Extract(Stream archive, string targetDirectory, ArchiveExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        using Archive opened = Open(archive, options);
        return opened.Extract(targetDirectory, options, cancellationToken);
    }

    /// <summary>Extracts an archive file into a directory.</summary>
    /// <param name="archivePath">The archive's path.</param>
    /// <param name="targetDirectory">The directory to extract into; created if missing.</param>
    /// <param name="options">Safety, limit and metadata options; <see langword="null"/> for the safe defaults.</param>
    /// <param name="cancellationToken">A token that cancels the extraction.</param>
    /// <returns>What was extracted.</returns>
    public static async ValueTask<ArchiveExtractResult> ExtractAsync(string archivePath, string targetDirectory, ArchiveExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        Archive archive = await OpenAsync(archivePath, options, cancellationToken).ConfigureAwait(false);
        await using (archive.ConfigureAwait(false))
        {
            return await archive.ExtractAsync(targetDirectory, options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Extracts an archive from a stream into a directory.</summary>
    /// <param name="archive">The archive's data.</param>
    /// <param name="targetDirectory">The directory to extract into; created if missing.</param>
    /// <param name="options">Safety, limit and metadata options; <see langword="null"/> for the safe defaults.</param>
    /// <param name="cancellationToken">A token that cancels the extraction.</param>
    /// <returns>What was extracted.</returns>
    public static async ValueTask<ArchiveExtractResult> ExtractAsync(Stream archive, string targetDirectory, ArchiveExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        Archive opened = await OpenAsync(archive, options, cancellationToken).ConfigureAwait(false);
        await using (opened.ConfigureAwait(false))
        {
            return await opened.ExtractAsync(targetDirectory, options, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- creating ----

    /// <summary>Starts creating an archive file. Add content with <see cref="ArchiveBuilder.Add(string, string?)"/>, then call <see cref="ArchiveBuilder.Save"/>.</summary>
    /// <param name="path">The archive's path. The extension chooses the format and compression (<c>.tar</c>, <c>.tar.zst</c>, <c>.tar.lz4</c>) unless <paramref name="options"/> says otherwise.</param>
    /// <param name="options">Format, compression and reproducibility options.</param>
    /// <returns>A builder.</returns>
    /// <exception cref="ArgumentException">The extension names no format that can be written and no format is set.</exception>
    /// <exception cref="ArchiveUnsupportedException">The chosen compression is not available yet.</exception>
    public static ArchiveBuilder Create(string path, ArchiveCreateOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new ArchiveBuilder(path, null, options ?? new ArchiveCreateOptions());
    }

    /// <summary>Starts creating an archive written to a stream.</summary>
    /// <param name="stream">A writable stream; closed by <see cref="ArchiveBuilder.Save"/> unless <see cref="ArchiveCreateOptions.LeaveOpen"/> is set.</param>
    /// <param name="options">Format (default tar), compression (default none) and reproducibility options.</param>
    /// <returns>A builder.</returns>
    public static ArchiveBuilder Create(Stream stream, ArchiveCreateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ArchiveBuilder(null, stream, options ?? new ArchiveCreateOptions());
    }

    // ---- disposal ----

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending?.Dispose();
        _indexReader?.Dispose();
        if (!_leaveOpen)
        {
            _source.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pending is not null)
        {
            await _pending.DisposeAsync().ConfigureAwait(false);
        }

        if (_indexReader is not null)
        {
            await _indexReader.DisposeAsync().ConfigureAwait(false);
        }

        if (!_leaveOpen)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
