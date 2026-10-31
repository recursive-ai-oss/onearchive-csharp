using System.Buffers;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// A brotli compressor over spans (RFC 7932). It writes one stream: the window header, meta-blocks, and the final meta-block when the last
/// call is made.
/// </summary>
/// <remarks>
/// <para>Input is collected into a block of the quality's size (128 KiB to 4 MiB); a block is parsed and coded when it is full, on
/// <see cref="Flush"/>, or when the stream ends, so a call may consume much and write nothing, and one call may produce a block's output.
/// What the destination cannot take stays inside the encoder until the next call. The output depends only on the input bytes, the
/// options and where flushes were asked for, never on how the input is cut into calls.</para>
/// <para>The encoder holds the window plus one block (up to about 20 MiB at the largest window and 4 MiB blocks) and, from quality 2 up,
/// four bytes per byte of that for the match finder. Choices the format leaves open are written down in docs/brotli-encoder.md.</para>
/// </remarks>
public sealed class BrotliEncoder
{
    private readonly BrotliEncoderCore _core;
    private bool _flushPending;

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The quality and window; <see langword="null"/> for <see cref="BrotliOptions.Default"/>.</param>
    public BrotliEncoder(BrotliOptions? options = null)
    {
        options ??= BrotliOptions.Default;
        Quality = options.Quality;
        WindowBits = options.WindowBits;
        _core = new BrotliEncoderCore(Quality, WindowBits);
    }

    /// <summary>Gets the quality, 0 to 11.</summary>
    public int Quality { get; }

    /// <summary>Gets the window size in bits, 10 to 24.</summary>
    public int WindowBits { get; }

    /// <summary>Returns an upper bound for the size of the compressed form of <paramref name="inputLength"/> bytes, written in one call.</summary>
    /// <param name="inputLength">The input size.</param>
    /// <returns>The bound: the input plus 6 bytes per meta-block at its smallest size (128 KiB) and the stream header and end.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative.</exception>
    public static long GetMaxCompressedLength(long inputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        return inputLength + (6 * ((inputLength >> 17) + 1)) + 8;
    }

    /// <summary>Starts a new stream, dropping anything not yet written.</summary>
    public void Reset()
    {
        _core.Reset();
        _flushPending = false;
    }

    /// <summary>Compresses a part of the data.</summary>
    /// <param name="source">The next input bytes.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken.</param>
    /// <param name="written">The number of bytes written to <paramref name="destination"/>.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> is the last input: the stream is then ended once the input is taken.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when the stream has ended and all of it has been written (only after a call with <paramref name="isFinalBlock"/>);
    /// <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting: call again with more room (and the rest of the input);
    /// <see cref="OperationStatus.NeedMoreData"/> when all input was taken and the stream is not final.
    /// </returns>
    /// <exception cref="InvalidOperationException">The stream has ended (call <see cref="Reset"/>) and <paramref name="source"/> is not empty.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock = false)
    {
        consumed = 0;
        written = 0;
        if (_core.IsFinished && !source.IsEmpty)
        {
            throw new InvalidOperationException("The stream has ended; call Reset() to start another.");
        }

        // Take the input a block at a time, so that what is waiting never grows far beyond what the destination takes.
        while (true)
        {
            written += _core.Output.Drain(destination.Slice(written));
            if (_core.Output.PendingBytes > 0)
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (_core.IsFinished)
            {
                return OperationStatus.Done;
            }

            if (consumed < source.Length)
            {
                int take = Math.Min(source.Length - consumed, 1 << 20);
                _core.Append(source.Slice(consumed, take));
                consumed += take;
                continue;
            }

            if (!isFinalBlock)
            {
                return OperationStatus.NeedMoreData;
            }

            _core.Finish();
        }
    }

    /// <summary>
    /// Codes the data collected so far as a meta-block, followed by an empty metadata meta-block that ends on a byte boundary, so that a
    /// decoder can produce everything written so far. Flushing often costs ratio: it ends the block early.
    /// </summary>
    /// <param name="destination">Receives the waiting bytes.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see cref="OperationStatus.Done"/> when nothing is waiting any more, otherwise <see cref="OperationStatus.DestinationTooSmall"/>: call again with more room.</returns>
    public OperationStatus Flush(Span<byte> destination, out int written)
    {
        if (!_flushPending && !_core.IsFinished)
        {
            _core.Flush();
            _flushPending = true;
        }

        written = _core.Output.Drain(destination);
        if (_core.Output.PendingBytes > 0)
        {
            return OperationStatus.DestinationTooSmall;
        }

        _flushPending = false;
        return OperationStatus.Done;
    }
}
