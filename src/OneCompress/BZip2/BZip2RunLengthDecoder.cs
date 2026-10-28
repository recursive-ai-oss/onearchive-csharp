using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// Undoes bzip2's first stage, a run-length encoding applied to the input before the block sort: after four equal bytes in a row
/// comes a count byte, 0 to 255, of further copies. The count is not itself part of a run; the byte after it starts a fresh one.
/// The state lives in the struct, so a block can be fed and drained in pieces. Runs never continue across blocks: call
/// <see cref="Reset"/> at the start of each block.
/// </summary>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public struct BZip2RunLengthDecoder
{
    private int _run;        // length of the current run of equal bytes, 0 to 4
    private byte _previous;
    private int _repeat;     // copies of _previous still to be written
    
    /// <summary>Gets a value indicating whether copies from a count byte are waiting to be written.</summary>
    public readonly bool HasPendingOutput => _repeat > 0;

    /// <summary>Gets a value indicating whether four equal bytes were just decoded, so that the next input byte is a count. A block must not end in this state.</summary>
    public readonly bool IsAwaitingCount => _run == 4 && _repeat == 0;

    /// <summary>Forgets any run: call it at the start of a block.</summary>
    public void Reset()
    {
        _run = 0;
        _previous = 0;
        _repeat = 0;
    }

    /// <summary>Decodes as much of <paramref name="source"/> as fits into <paramref name="destination"/>.</summary>
    /// <param name="source">Bytes as they come out of the inverse Burrows-Wheeler transform.</param>
    /// <param name="destination">Receives the original bytes.</param>
    /// <param name="consumed">The number of source bytes used.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see cref="OperationStatus.NeedMoreData"/> when all of <paramref name="source"/> is used and nothing is pending;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting. A block that ends right after four equal bytes has no count
    /// byte, which the bzip2 tool rejects: see <see cref="IsAwaitingCount"/>.</returns>
    public OperationStatus Decode(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        int r = 0;
        int w = 0;
        while (true)
        {
            if (_repeat > 0)
            {
                int n = Math.Min(_repeat, destination.Length - w);
                destination.Slice(w, n).Fill(_previous);
                w += n;
                _repeat -= n;
                if (_repeat > 0)
                {
                    consumed = r;
                    written = w;
                    return OperationStatus.DestinationTooSmall;
                }
            }

            if (r == source.Length)
            {
                consumed = r;
                written = w;
                return OperationStatus.NeedMoreData;
            }

            if (_run == 4)
            {
                // The count byte: it writes nothing itself.
                _repeat = source[r++];
                _run = 0;
                continue;
            }

            if (w == destination.Length)
            {
                consumed = r;
                written = w;
                return OperationStatus.DestinationTooSmall;
            }

            byte b = source[r++];
            if (_run > 0 && b == _previous)
            {
                _run++;
            }
            else
            {
                _previous = b;
                _run = 1;
            }

            destination[w++] = b;
        }
    }
}
