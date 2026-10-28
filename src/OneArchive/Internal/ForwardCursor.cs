using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Internal;

/// <summary>
/// Position tracking over an archive stream for the formats whose members follow each other with no index (cpio, ar).
/// Positions are relative to where the stream was when the reader was created. A seekable source can move in both
/// directions; a forward-only one skips by reading.
/// </summary>
internal sealed class ForwardCursor
{
    private readonly string _format;
    private byte[]? _scratch;

    public ForwardCursor(Stream stream, string format)
    {
        Stream = stream;
        _format = format;
        Seekable = stream.CanSeek;
        if (Seekable)
        {
            Origin = stream.Position;
            Length = stream.Length - Origin;
        }
    }

    public Stream Stream { get; }

    public bool Seekable { get; }

    /// <summary>Gets the stream position at which the archive starts (seekable sources only).</summary>
    public long Origin { get; }

    /// <summary>Gets the number of bytes from <see cref="Origin"/> to the end (seekable sources only).</summary>
    public long Length { get; }

    /// <summary>Gets the current position relative to <see cref="Origin"/>.</summary>
    public long Pos { get; private set; }

    /// <summary>Records bytes that an entry stream read straight from the underlying stream.</summary>
    public void NoteConsumed(int n) => Pos += n;

    public async ValueTask<int> ReadFullAsync(bool async, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = async
                ? await Stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false)
                : Stream.Read(buffer.Span[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
            Pos += n;
        }

        return total;
    }

    /// <summary>Moves to an absolute position; a forward-only source can only move forward.</summary>
    public async ValueTask MoveToAsync(bool async, long target, string expected, CancellationToken ct)
    {
        if (Seekable)
        {
            if (target > Length)
            {
                throw new CompressionFormatException(_format, Length, expected, true);
            }

            if (target != Pos || Stream.Position != Origin + Pos)
            {
                Stream.Seek(Origin + target, SeekOrigin.Begin);
            }

            Pos = target;
            return;
        }

        if (target < Pos)
        {
            throw new InvalidOperationException("A forward-only stream cannot move backwards.");
        }

        _scratch ??= new byte[8192];
        while (Pos < target)
        {
            int want = (int)Math.Min(_scratch.Length, target - Pos);
            int n = await ReadFullAsync(async, _scratch.AsMemory(0, want), ct).ConfigureAwait(false);
            if (n < want)
            {
                throw new CompressionFormatException(_format, Pos, expected, true);
            }
        }
    }

    /// <summary>Reads to the end of the stream so that a decompressor underneath runs its own end checks.</summary>
    public async ValueTask DrainAsync(bool async, CancellationToken ct)
    {
        _scratch ??= new byte[8192];
        while (true)
        {
            int n = async
                ? await Stream.ReadAsync(_scratch, ct).ConfigureAwait(false)
                : Stream.Read(_scratch, 0, _scratch.Length);
            if (n == 0)
            {
                return;
            }

            Pos += n;
        }
    }
}
