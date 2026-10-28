using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// A streaming brotli decoder (RFC 7932) that works over spans: it decodes as much as the input and the output space allow and
/// reports what it needs next.
/// </summary>
/// <remarks>
/// <para>This type has the same name as <c>System.IO.Compression.BrotliDecoder</c>, which is a different type; the namespaces differ, so
/// code that imports both namespaces must qualify or alias the name. The name follows the other OneCompress codecs
/// (<c>ZstdDecoder</c>, <c>BZip2Decoder</c>) and the shape follows the BCL's.</para>
/// <para>A decoder decodes one brotli stream. Call <see cref="Decompress"/> with the bytes not yet consumed and the free space of the
/// output; it returns <see cref="OperationStatus.Done"/> when the stream is complete and every byte of it has been written,
/// <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting and <see cref="OperationStatus.NeedMoreData"/> when all
/// input has been used. It never returns <see cref="OperationStatus.InvalidData"/>: malformed input throws, so a corrupt stream cannot be taken
/// for a short one. Any way of cutting input and output into calls gives the same bytes.</para>
/// <para>The decoder keeps the sliding window (up to 16 MiB, grown as the stream needs it) and, when a call ends in the middle of a
/// field of the format, the few bytes of that field; the buffers are reused by <see cref="Reset"/>. An instance is not thread-safe.
/// After an exception the decoder refuses further calls until <see cref="Reset"/>.</para>
/// </remarks>
[Experimental("ONECOMPRESS008", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed partial class BrotliDecoder
{
    internal const string FormatName = "brotli";

    // How much new input is added at a time to bytes left over from an earlier call, when a field of the format did not fit.
    private const int MinTopUp = 4096;

    private readonly long _maxWindowSize;
    private readonly long _maxOutputSize;

    private bool _failed;

    // Input that an earlier call took but could not use yet: the bytes of an unfinished field, starting at the byte that holds the next
    // unread bit. _bitOffset bits of the first byte of the next input are already used.
    private byte[] _pending = [];
    private int _pendingLen;
    private int _bitOffset;
    private long _pendingAbs;
    private long _totalClaimed;
    private bool _trailingLost;

    // The output side of the current call.
    private int _written;

    // The offset in the whole input of byte 0 of the span being decoded, for error messages.
    private long _absBase;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Limits and settings; <see langword="null"/> for the defaults.</param>
    public BrotliDecoder(BrotliDecompressionOptions? options = null)
    {
        options ??= BrotliDecompressionOptions.Default;
        _maxWindowSize = options.MaxWindowSize;
        _maxOutputSize = options.MaxOutputSize;
        ResetState();
    }

    /// <summary>
    /// True when the decoder is between streams: before any input, or after the last byte of a stream. False while a stream is in progress.
    /// </summary>
    public bool IsAtFrameBoundary => _phase == Phase.Done || (_phase == Phase.StreamHeader && _totalClaimed == 0);

    /// <summary>True when the stream has been decoded to its end; the decoder then ignores further input until <see cref="Reset"/>.</summary>
    public bool IsComplete => _phase == Phase.Done;

    /// <summary>
    /// True when the end of the stream was found inside bytes that an earlier call had already taken (in order to finish a field), so that bytes
    /// after the stream's end were consumed and cannot be reported. Only possible when input arrives in pieces.
    /// </summary>
    internal bool TrailingDataConsumed => _trailingLost;

    /// <summary>The number of bytes of the stream's output produced so far.</summary>
    public long TotalOutput => _totalOut;

    /// <summary>Throws if the stream is not complete, for a caller that knows no more input will come.</summary>
    /// <exception cref="CompressionFormatException">The decoder stopped inside a stream, or has not been given one. <see cref="CompressionFormatException.IsTruncated"/> is set.</exception>
    public void ThrowIfIncomplete()
    {
        if (_phase != Phase.Done)
        {
            throw new CompressionFormatException(FormatName, _totalClaimed, "more compressed data: the stream ends before its last meta-block", isTruncated: true);
        }
    }

    /// <summary>Returns the decoder to its initial state, keeping its buffers, so that it can decode another stream.</summary>
    public void Reset()
    {
        _failed = false;
        ResetState();
    }

    private void ResetState()
    {
        _pendingLen = 0;
        _bitOffset = 0;
        _pendingAbs = 0;
        _totalClaimed = 0;
        _trailingLost = false;
        _written = 0;
        _absBase = 0;
        ResetStream();
    }

    /// <summary>Decodes as much as the input and the free output space allow.</summary>
    /// <param name="source">The bytes of the stream that earlier calls did not consume, followed by any new ones.</param>
    /// <param name="destination">The space for decoded bytes.</param>
    /// <param name="consumed">The number of bytes of <paramref name="source"/> taken. Always set. A call that returns
    /// <see cref="OperationStatus.NeedMoreData"/> has taken all of them.</param>
    /// <param name="written">The number of bytes written to <paramref name="destination"/>. Always set.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when the stream is complete (bytes after it are not consumed); <see cref="OperationStatus.DestinationTooSmall"/>
    /// when more output is waiting; <see cref="OperationStatus.NeedMoreData"/> when the stream is not complete and all input was used.
    /// </returns>
    /// <exception cref="CompressionFormatException">The data is not a valid brotli stream. <see cref="CompressionFormatException.Offset"/>
    /// counts bytes from the first byte given to the decoder since the last <see cref="Reset"/>.</exception>
    /// <exception cref="CompressionLimitException">The stream declares a window or an output size beyond the limits in the options.</exception>
    /// <exception cref="InvalidOperationException">An earlier call threw and <see cref="Reset"/> was not called.</exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        consumed = 0;
        written = 0;
        if (_failed)
        {
            throw new InvalidOperationException("The decoder failed on earlier input; call Reset before decoding again.");
        }

        if (_phase == Phase.Done)
        {
            return OperationStatus.Done;
        }

        _written = 0;
        int srcOff = 0;
        try
        {
            OperationStatus status = DecompressCore(source, destination, ref srcOff);
            FlushOutput(destination);
            written = _written;
            consumed = srcOff;
            _totalClaimed += srcOff;
            return status;
        }
        catch
        {
            _failed = true;
            FlushOutput(destination);
            written = _written;
            consumed = srcOff;
            throw;
        }
    }

    private OperationStatus DecompressCore(ReadOnlySpan<byte> source, Span<byte> dest, ref int srcOff)
    {
        while (true)
        {
            if (_pendingLen > 0)
            {
                int oldLen = _pendingLen;
                int add = Math.Min(source.Length - srcOff, Math.Max(MinTopUp, oldLen));
                if (_pending.Length < oldLen + add)
                {
                    Array.Resize(ref _pending, Math.Max(oldLen + add, _pending.Length * 2));
                }

                source.Slice(srcOff, add).CopyTo(_pending.AsSpan(oldLen));
                Step step = Run(_pending.AsSpan(0, oldLen + add), _bitOffset, dest, _pendingAbs, out int bytePos, out int bitOff);
                if (bytePos >= oldLen || step == Step.Done)
                {
                    // The unfinished field is complete: from here on the source itself is decoded.
                    int fromSource = Math.Max(0, bytePos - oldLen);
                    if (step == Step.Done && bytePos < oldLen)
                    {
                        _trailingLost = true;
                    }

                    srcOff += fromSource;
                    _pendingLen = 0;
                    _bitOffset = bitOff;
                    if (step == Step.Starved)
                    {
                        continue;
                    }

                    return Finish(step);
                }

                _pendingAbs += bytePos;
                _bitOffset = bitOff;
                if (step == Step.Starved)
                {
                    // Still inside the field: keep everything from the next unread byte, and add more input if there is any.
                    _pending.AsSpan(bytePos, oldLen + add - bytePos).CopyTo(_pending);
                    _pendingLen = oldLen + add - bytePos;
                    srcOff += add;
                    if (srcOff >= source.Length)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    continue;
                }

                // Output ran out while the leftover bytes were still being read: the bytes added from the source stay with the caller.
                _pending.AsSpan(bytePos, oldLen - bytePos).CopyTo(_pending);
                _pendingLen = oldLen - bytePos;
                return Finish(step);
            }
            else
            {
                ReadOnlySpan<byte> input = source.Slice(srcOff);
                Step step = Run(input, _bitOffset, dest, _totalClaimed + srcOff, out int bytePos, out int bitOff);
                _bitOffset = bitOff;
                if (step == Step.Starved)
                {
                    int tail = input.Length - bytePos;
                    if (_pending.Length < tail)
                    {
                        Array.Resize(ref _pending, Math.Max(tail, 256));
                    }

                    input.Slice(bytePos).CopyTo(_pending);
                    _pendingLen = tail;
                    _pendingAbs = _totalClaimed + srcOff + bytePos;
                    srcOff = source.Length;
                    return OperationStatus.NeedMoreData;
                }

                srcOff += bytePos;
                return Finish(step);
            }
        }
    }

    private static OperationStatus Finish(Step step) => step == Step.Done ? OperationStatus.Done : OperationStatus.DestinationTooSmall;

    private enum Step : byte
    {
        Ok,
        Starved,
        Full,
        Done,
    }

    // Runs the decoder over input until it needs more input, runs out of output space or finishes the stream. The reader ends
    // at a position that is a clean restart point; bytePos and bitOff say where.
    private Step Run(ReadOnlySpan<byte> input, int bitOffset, Span<byte> dest, long absBase, out int bytePos, out int bitOff)
    {
        _absBase = absBase;
        var br = new BrotliBitReader(input, bitOffset);
        Step result = RunPhases(ref br, dest);
        bytePos = br.BytePosition;
        bitOff = br.BitOffset;
        return result;
    }

    private Step RunPhases(ref BrotliBitReader br, Span<byte> dest)
    {
        while (true)
        {
            switch (_phase)
            {
                case Phase.Done:
                    return Step.Done;

                case Phase.Raw:
                {
                    Step s = CopyRaw(ref br, dest);
                    if (s != Step.Ok)
                    {
                        return s;
                    }

                    break;
                }

                case Phase.Skip:
                {
                    int available = br.Source.Length - br.Position;
                    int n = Math.Min(available, _metaRemaining);
                    br.Advance(n);
                    _metaRemaining -= n;
                    if (_metaRemaining > 0)
                    {
                        return Step.Starved;
                    }

                    _phase = _isLast ? Phase.Done : Phase.MetaStart;
                    break;
                }

                case Phase.Commands:
                {
                    Step s = RunCommands(ref br, dest);
                    if (s != Step.Ok)
                    {
                        return s;
                    }

                    break;
                }

                default:
                {
                    // The header units each read their fields completely or not at all.
                    var checkpoint = br.Save();
                    int arenaUsed = _arenaUsed;
                    if (!RunHeaderUnit(ref br))
                    {
                        br.Restore(checkpoint);
                        _arenaUsed = arenaUsed;
                        return Step.Starved;
                    }

                    break;
                }
            }
        }
    }

    // Creates the format exception for a problem at the reader's position.
    private CompressionFormatException Error(in BrotliBitReader br, string expected) => new(FormatName, _absBase + br.BytePosition, expected);

    // Reports invalid data - or, if the reader has run past the end of the input, reports that the field is not complete yet, because
    // the values read from the missing bits mean nothing.
    private bool Bad(in BrotliBitReader br, string expected)
    {
        if (br.Overrun)
        {
            return false;
        }

        throw Error(br, expected);
    }
}
