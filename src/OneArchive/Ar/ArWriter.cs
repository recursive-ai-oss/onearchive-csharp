using System.Buffers;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneArchive.Ar;

/// <summary>
/// Writes ar archives in the GNU, BSD or common variant.
/// </summary>
/// <remarks>
/// <para>Only regular files can be stored; other kinds raise <see cref="ArchiveUnsupportedException"/>. Defaults for missing fields: mode
/// <c>100644</c>, uid and gid 0, time 0 (nothing reads the clock). Header fields are left-aligned and space-padded; data is padded with
/// <c>\n</c> to an even length. A name may not contain NUL or (GNU) a line feed.</para>
/// <para>The GNU variant puts the <c>//</c> long-name table before the members that use it, so it holds the members until
/// <c>Finish</c>: in memory up to <see cref="ArWriterOptions.MaxMemoryBuffer"/>, then in a temporary file. The BSD and common variants
/// stream. <see cref="SetSymbolTable"/> stores a caller-built <c>/</c> symbol table (GNU variant only); the writer never builds one.</para>
/// <para>Limits of the fields (size 10 digits, uid and gid 6, time 12, mode 8 octal) raise <see cref="ArchiveUnsupportedException"/> naming the entry.</para>
/// </remarks>
public sealed class ArWriter : ArchiveWriter
{
    private const long MaxSize = 9_999_999_999;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ArWriterOptions _options;
    private readonly MemoryStream _names = new();
    private Stream? _spool;
    private byte[]? _symbols;
    private bool _magic;
    private bool _wroteMember;
    private bool _finished;
    private bool _faulted;
    private bool _disposed;

    /// <summary>Creates a writer.</summary>
    /// <param name="stream">A writable stream.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the writer is disposed.</param>
    public ArWriter(Stream stream, ArWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _options = options ?? ArWriterOptions.Default;
        if (!Enum.IsDefined(_options.Format))
        {
            throw new ArgumentException("Unknown ar format.", nameof(options));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        if (_options.Format == ArFormat.Gnu)
        {
            _spool = new MemoryStream();
        }
    }

    /// <inheritdoc />
    public override ArchiveFormat Format => ArchiveFormat.Ar;

    /// <summary>Stores a symbol table as the first member (named <c>/</c>). The data is written as given.</summary>
    /// <param name="table">The table's bytes, built by the caller (the format is the object-file format's concern).</param>
    /// <exception cref="NotSupportedException">The variant is not GNU.</exception>
    /// <exception cref="InvalidOperationException">An entry was already written, or the table was already set.</exception>
    public void SetSymbolTable(ReadOnlyMemory<byte> table)
    {
        if (_options.Format != ArFormat.Gnu)
        {
            throw new NotSupportedException("Only the GNU variant can store a symbol table.");
        }

        if (_wroteMember || _symbols is not null || _finished)
        {
            throw new InvalidOperationException("The symbol table must be set once, before any entry.");
        }

        _symbols = table.ToArray();
    }

    private static string Field(string value, int width) => value.PadRight(width, ' ');

    private static byte[] BuildHeader(byte[] name, long mtime, long uid, long gid, long mode, long size)
    {
        string rest = Field(mtime.ToString(CultureInfo.InvariantCulture), 12) + Field(uid.ToString(CultureInfo.InvariantCulture), 6)
            + Field(gid.ToString(CultureInfo.InvariantCulture), 6) + Field(Convert.ToString(mode, 8), 8) + Field(size.ToString(CultureInfo.InvariantCulture), 10) + "`\n";
        byte[] header = new byte[60];
        header.AsSpan(0, 16).Fill((byte)' ');
        name.CopyTo(header, 0);
        Encoding.ASCII.GetBytes(rest, header.AsSpan(16));
        return header;
    }

    /// <inheritdoc />
    protected override async ValueTask WriteEntryAsync(ArchiveEntry entry, Stream? content, bool async, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The archive is already finished.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind != ArchiveEntryKind.File)
        {
            throw new ArchiveUnsupportedException(ArReader.Name, $"{entry.Kind} entries (entry '{entry.Name}'); ar stores regular files only");
        }

        string name = entry.Name;
        if (string.IsNullOrEmpty(name) || name.Contains('\0', StringComparison.Ordinal) || name.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("The entry name must be non-empty and contain no NUL or line feed.", nameof(entry));
        }

        long size = entry.Size;
        if (size < 0)
        {
            throw new ArgumentException("The entry size is negative.", nameof(entry));
        }

        if (size > 0 && content is null)
        {
            throw new ArgumentException("A file entry with a size needs content.", nameof(entry));
        }

        long mtime = entry.ModifiedTime is { } t ? t.ToUnixTimeSeconds() : 0;
        long mode = 0x8000 | (entry.Mode is { } m ? (long)(int)m & 0xFFF : 0x1A4);
        if (size > MaxSize || mtime < 0 || mtime > 999_999_999_999 || entry.Uid is < 0 or > 999_999 || entry.Gid is < 0 or > 999_999)
        {
            throw new ArchiveUnsupportedException(ArReader.Name, $"a size, time, uid or gid that does not fit the 60-byte header (entry '{entry.Name}')");
        }

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] nameField;
        byte[]? bsdName = null;
        switch (_options.Format)
        {
            case ArFormat.Bsd:
                if (nameBytes.Length <= 16 && !name.Contains(' ', StringComparison.Ordinal) && !name.EndsWith('/') && !name.StartsWith("#1/", StringComparison.Ordinal))
                {
                    nameField = nameBytes;
                }
                else
                {
                    // BSD stores the name at the start of the data, unpadded (as bsdtar does), and counts it in the size.
                    bsdName = nameBytes;
                    nameField = Encoding.ASCII.GetBytes("#1/" + nameBytes.Length.ToString(CultureInfo.InvariantCulture));
                    size += nameBytes.Length;
                    if (size > MaxSize)
                    {
                        throw new ArchiveUnsupportedException(ArReader.Name, $"a size that does not fit the 60-byte header (entry '{entry.Name}')");
                    }
                }

                break;
            case ArFormat.Common:
                if (nameBytes.Length > 15)
                {
                    throw new ArchiveUnsupportedException(ArReader.Name, $"a name over 15 bytes in the common variant (entry '{entry.Name}'); use the GNU or BSD variant");
                }

                nameField = [.. nameBytes, (byte)'/'];
                break;
            default:
                if (nameBytes.Length <= 15 && nameBytes[0] != (byte)'/')
                {
                    nameField = [.. nameBytes, (byte)'/'];
                }
                else
                {
                    // GNU long names: "/offset" into the // table, whose entries end with "/\n".
                    nameField = Encoding.ASCII.GetBytes("/" + _names.Length.ToString(CultureInfo.InvariantCulture));
                    _names.Write(nameBytes);
                    _names.Write("/\n"u8);
                }

                break;
        }

        byte[] header = BuildHeader(nameField, mtime, entry.Uid, entry.Gid, mode, size);
        long contentSize = bsdName is null ? size : size - bsdName.Length;
        try
        {
            if (_options.Format != ArFormat.Gnu)
            {
                await EnsureMagicAsync(async, cancellationToken).ConfigureAwait(false);
            }

            _wroteMember = true;
            await WriteAsync(header, async, cancellationToken).ConfigureAwait(false);
            if (bsdName is not null)
            {
                await WriteAsync(bsdName, async, cancellationToken).ConfigureAwait(false);
            }

            if (contentSize > 0)
            {
                await CopyAsync(content!, contentSize, async, cancellationToken).ConfigureAwait(false);
            }

            if ((size & 1) != 0)
            {
                await WriteAsync("\n"u8.ToArray(), async, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private async ValueTask EnsureMagicAsync(bool async, CancellationToken ct)
    {
        if (!_magic)
        {
            _magic = true;
            await WriteToAsync(_stream, "!<arch>\n"u8.ToArray(), async, ct).ConfigureAwait(false);
        }
    }

    private static async ValueTask WriteToAsync(Stream s, ReadOnlyMemory<byte> data, bool async, CancellationToken ct)
    {
        if (async)
        {
            await s.WriteAsync(data, ct).ConfigureAwait(false);
        }
        else
        {
            s.Write(data.Span);
        }
    }

    // Members go to the spool in the GNU variant and straight to the output otherwise.
    private async ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool async, CancellationToken ct)
    {
        if (_spool is null)
        {
            await WriteToAsync(_stream, data, async, ct).ConfigureAwait(false);
            return;
        }

        if (_spool is MemoryStream ms && ms.Length + data.Length > _options.MaxMemoryBuffer)
        {
            FileStream file;
            try
            {
                file = new FileStream(Path.Combine(Path.GetTempPath(), "onearchive-ar-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
            {
                throw new ArchiveUnsupportedException(ArReader.Name, "writing more than ArWriterOptions.MaxMemoryBuffer bytes in the GNU variant on a platform without temporary files", e);
            }

            ms.Position = 0;
            await ms.CopyToAsync(file, ct).ConfigureAwait(false);
            _spool = file;
            await ms.DisposeAsync().ConfigureAwait(false);
        }

        await WriteToAsync(_spool, data, async, ct).ConfigureAwait(false);
    }

    private async ValueTask CopyAsync(Stream content, long size, bool async, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long left = size;
            while (left > 0)
            {
                int want = (int)Math.Min(buffer.Length, left);
                int n = async
                    ? await content.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false)
                    : content.Read(buffer, 0, want);
                if (n == 0)
                {
                    throw new ArgumentException($"The content ended {left} bytes before the entry's declared size of {size}.", nameof(content));
                }

                await WriteAsync(buffer.AsMemory(0, n), async, ct).ConfigureAwait(false);
                left -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask WriteSpecialAsync(string name, byte[] data, bool async, CancellationToken ct)
    {
        await WriteToAsync(_stream, BuildHeader(Encoding.ASCII.GetBytes(name), 0, 0, 0, 0, data.Length), async, ct).ConfigureAwait(false);
        await WriteToAsync(_stream, data, async, ct).ConfigureAwait(false);
        if ((data.Length & 1) != 0)
        {
            await WriteToAsync(_stream, "\n"u8.ToArray(), async, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask FinishAsync(bool async, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            await EnsureMagicAsync(async, cancellationToken).ConfigureAwait(false);
            if (_options.Format == ArFormat.Gnu)
            {
                if (_symbols is not null)
                {
                    await WriteSpecialAsync("/", _symbols, async, cancellationToken).ConfigureAwait(false);
                }

                if (_names.Length > 0)
                {
                    await WriteSpecialAsync("//", _names.ToArray(), async, cancellationToken).ConfigureAwait(false);
                }

                Stream spool = _spool!;
                spool.Position = 0;
                if (async)
                {
                    await spool.CopyToAsync(_stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    spool.CopyTo(_stream);
                }

                await spool.DisposeAsync().ConfigureAwait(false);
                _spool = null;
            }

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
            _faulted = true;
            throw;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }

        try
        {
            if (!_finished && !_faulted)
            {
                Finish();
            }
        }
        finally
        {
            _disposed = true;
            _spool?.Dispose();
            if (!_leaveOpen)
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
            if (!_finished && !_faulted)
            {
                await FinishAsync(true, default).ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
            if (_spool is not null)
            {
                await _spool.DisposeAsync().ConfigureAwait(false);
            }

            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
