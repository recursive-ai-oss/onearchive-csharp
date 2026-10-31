using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// Decodes an LZMA stream: either the <c>.lzma</c> "alone" format (a 13-byte header with the properties and the uncompressed size,
/// followed by the range-coded data), or, through <see cref="CreateRaw"/>, a bare range-coded stream whose properties came from a
/// container such as 7z or zip.
/// </summary>
/// <remarks>
/// <para>The stream ends at its end marker, or, when the header gives the uncompressed size, once that many bytes were produced (an end
/// marker right after them is accepted and consumed as well). <see cref="Decompress"/> returns <see cref="OperationStatus.Done"/> at that point; the decoder
/// then starts over with the next header on the next call.</para>
/// <para>See <c>docs/lzma-decoder.md</c> for the limits and the exact checks.</para>
/// </remarks>
public sealed class LzmaDecoder : IStepDecoder
{
    private const string Format = "lzma";
    private const int HeaderSize = 13;

    private enum Phase
    {
        Header,
        Begin,
        RangeInit,
        Data,
        Marker,
        Finished,
    }

    private readonly LzmaDecompressionOptions _options;
    private readonly LzmaDecoderCore _core = new();
    private readonly FieldStage _stage = new();
    private readonly bool _raw;
    private readonly LzmaProperties _rawProperties;
    private readonly long _rawSize;
    private Phase _phase;
    private bool _faulted;
    private long _inOffset;
    private long _totalOut;
    private long _remaining;
    private bool _sizeKnown;

    /// <summary>Creates a decoder for the <c>.lzma</c> format.</summary>
    /// <param name="options">Limits; <see langword="null"/> for <see cref="LzmaDecompressionOptions.Default"/>.</param>
    public LzmaDecoder(LzmaDecompressionOptions? options = null)
    {
        _options = options ?? LzmaDecompressionOptions.Default;
        _phase = Phase.Header;
    }

    private LzmaDecoder(LzmaProperties properties, long uncompressedSize, LzmaDecompressionOptions? options)
    {
        _options = options ?? LzmaDecompressionOptions.Default;
        _raw = true;
        _rawProperties = properties;
        _rawSize = uncompressedSize;
        _phase = Phase.Begin;
    }

    /// <summary>Creates a decoder for a bare LZMA stream whose properties and size are known from elsewhere.</summary>
    /// <param name="properties">The lc/lp/pb values and the dictionary size.</param>
    /// <param name="uncompressedSize">The uncompressed size, or -1 when unknown (the stream then needs an end marker).</param>
    /// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="CompressionLimitException">The dictionary size or the uncompressed size is over the limit in <paramref name="options"/>.</exception>
    public static LzmaDecoder CreateRaw(LzmaProperties properties, long uncompressedSize = -1, LzmaDecompressionOptions? options = null)
    {
        options ??= LzmaDecompressionOptions.Default;
        CheckLimits(options, properties.DictionarySize, uncompressedSize < 0 ? ulong.MaxValue : (ulong)uncompressedSize);
        return new LzmaDecoder(properties, uncompressedSize, options);
    }

    /// <summary>Gets a value indicating whether the decoder is between streams: nothing of a stream is partly read.</summary>
    public bool IsAtFrameBoundary => (_phase is Phase.Header or Phase.Begin) && _stage.Length == 0 && !_faulted;

    /// <summary>Gets the number of input bytes consumed since construction or the last <see cref="Reset"/>.</summary>
    public long TotalConsumed => _inOffset;

    /// <summary>Gets the number of bytes written since construction or the last <see cref="Reset"/>.</summary>
    public long TotalWritten => _totalOut;

    /// <summary>Forgets everything about the stream being read and clears a failure. Buffers are kept.</summary>
    public void Reset()
    {
        _phase = _raw ? Phase.Begin : Phase.Header;
        _faulted = false;
        _stage.Clear();
        _inOffset = 0;
        _totalOut = 0;
    }

    /// <summary>Throws when the decoder is inside a stream, which means the input ended too early.</summary>
    /// <exception cref="CompressionFormatException">The input ended inside a stream.</exception>
    public void ThrowIfIncomplete()
    {
        if (!IsAtFrameBoundary)
        {
            throw new CompressionFormatException(Format, _inOffset, "more compressed data", isTruncated: true);
        }
    }

    /// <summary>Decodes as much as the buffers allow.</summary>
    /// <param name="source">The compressed bytes not yet consumed, followed by any new ones.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="consumed">Receives the number of bytes taken from <paramref name="source"/>, also when the call throws.</param>
    /// <param name="written">Receives the number of bytes written, also when the call throws.</param>
    /// <returns><see cref="OperationStatus.Done"/> when a stream is complete, <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting, <see cref="OperationStatus.NeedMoreData"/> when all input was used.</returns>
    /// <exception cref="CompressionFormatException">The data is invalid.</exception>
    /// <exception cref="CompressionLimitException">The dictionary size or the output passes a limit.</exception>
    /// <exception cref="InvalidOperationException">A previous call threw and <see cref="Reset"/> was not called.</exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        if (_faulted)
        {
            consumed = written = 0;
            throw new InvalidOperationException("The decoder failed earlier; call Reset before reusing it.");
        }

        try
        {
            return OutputLimiter.Run(this, ref _totalOut, _options.MaxOutputSize, Format, source, destination, out consumed, out written);
        }
        catch
        {
            _faulted = true;
            consumed = written = 0;
            throw;
        }
    }

    OperationStatus IStepDecoder.Step(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        int pos = 0;
        written = 0;
        try
        {
            return Run(source, destination, ref pos, ref written);
        }
        finally
        {
            consumed = pos;
            _inOffset += pos;
        }
    }

    private static void CheckLimits(LzmaDecompressionOptions options, uint dictionarySize, ulong size)
    {
        if (dictionarySize > options.MaxDictionarySize)
        {
            throw new CompressionLimitException(Format, nameof(LzmaDecompressionOptions.MaxDictionarySize), options.MaxDictionarySize, dictionarySize);
        }

        if (size != ulong.MaxValue && size > (ulong)options.MaxOutputSize)
        {
            throw new CompressionLimitException(Format, nameof(LzmaDecompressionOptions.MaxOutputSize), options.MaxOutputSize, size > long.MaxValue ? long.MaxValue : (long)size);
        }
    }

    private void Begin(LzmaProperties properties, ulong size)
    {
        _core.Configure(properties.Lc, properties.Lp, properties.Pb);
        _core.SetDictionary(properties.DictionarySize);
        _sizeKnown = size != ulong.MaxValue;
        _remaining = _sizeKnown ? (long)size : long.MaxValue;
        _core.ResetDictionary(_sizeKnown ? _remaining : 0);
        _core.ResetState();
        _phase = Phase.RangeInit;
    }

    private OperationStatus Run(ReadOnlySpan<byte> source, Span<byte> destination, ref int pos, ref int written)
    {
        while (true)
        {
            switch (_phase)
            {
                case Phase.Header:
                {
                    if (!_stage.Fill(source, ref pos, HeaderSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    ReadOnlySpan<byte> h = _stage.Bytes;
                    long at = _inOffset + pos - HeaderSize;
                    if (!LzmaProperties.TryParsePropertiesByte(h[0], out int lc, out int lp, out int pb))
                    {
                        throw new CompressionFormatException(Format, at, "a properties byte below 225 ((pb * 5 + lp) * 9 + lc)");
                    }

                    var props = new LzmaProperties(lc, lp, pb, BinaryPrimitives.ReadUInt32LittleEndian(h[1..]));
                    ulong size = BinaryPrimitives.ReadUInt64LittleEndian(h[5..]);
                    CheckLimits(_options, props.DictionarySize, size);
                    _stage.Clear();
                    Begin(props, size);
                    break;
                }

                case Phase.Begin:
                    CheckLimits(_options, _rawProperties.DictionarySize, _rawSize < 0 ? ulong.MaxValue : (ulong)_rawSize);
                    Begin(_rawProperties, _rawSize < 0 ? ulong.MaxValue : (ulong)_rawSize);
                    break;

                case Phase.RangeInit:
                {
                    if (!_stage.Fill(source, ref pos, LzmaRangeDecoder.InitBytes))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    // LZMA specification, "Range Decoder": the first byte is always zero.
                    if (!_core.StartRangeDecoder(_stage.Bytes))
                    {
                        throw new CompressionFormatException(Format, _inOffset + pos - LzmaRangeDecoder.InitBytes, "a zero byte at the start of the range-coded data");
                    }

                    _stage.Clear();
                    _phase = Phase.Data;
                    break;
                }

                case Phase.Data:
                {
                    LzmaStatus status = _core.Decode(source[pos..], out int c, destination[written..], out int w, _remaining);
                    pos += c;
                    written += w;
                    if (_sizeKnown)
                    {
                        _remaining -= w;
                    }

                    switch (status)
                    {
                        case LzmaStatus.NeedInput:
                            return OperationStatus.NeedMoreData;
                        case LzmaStatus.OutputFull:
                            if (_sizeKnown && _remaining == 0)
                            {
                                if (_core.PendingMatchLength > 0)
                                {
                                    throw new CompressionFormatException(Format, _inOffset + pos, "a match that ends at the declared uncompressed size");
                                }

                                // LZMA specification, "The main loop of decoder": at the declared size the stream ends if the
                                // range decoder is finished, and otherwise only an end marker may follow.
                                _phase = _core.IsRangeDecoderFinished ? Phase.Finished : Phase.Marker;
                                break;
                            }

                            return OperationStatus.DestinationTooSmall;
                        case LzmaStatus.EndMarker:
                            if (_sizeKnown && _remaining > 0)
                            {
                                throw new CompressionFormatException(Format, _inOffset + pos, "the declared uncompressed size before the end marker");
                            }

                            CheckFinished(pos);
                            _phase = Phase.Finished;
                            break;
                        default:
                            throw new CompressionFormatException(Format, _inOffset + pos, "a match distance inside the dictionary");
                    }

                    break;
                }

                case Phase.Marker:
                {
                    LzmaStatus status = _core.DecodeEndMarker(source[pos..], out int c);
                    pos += c;
                    if (status == LzmaStatus.NeedInput)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    if (status != LzmaStatus.EndMarker)
                    {
                        throw new CompressionFormatException(Format, _inOffset + pos, "the end marker or a finished range decoder at the declared uncompressed size");
                    }

                    CheckFinished(pos);
                    _phase = Phase.Finished;
                    break;
                }

                default: // Finished
                    _phase = _raw ? Phase.Begin : Phase.Header;
                    return OperationStatus.Done;
            }
        }
    }

    // LZMA specification, "Range Decoder": after the last decision the code value must be 0.
    private void CheckFinished(int pos)
    {
        if (!_core.IsRangeDecoderFinished)
        {
            throw new CompressionFormatException(Format, _inOffset + pos, "the range decoder to end with a code value of 0");
        }
    }
}
