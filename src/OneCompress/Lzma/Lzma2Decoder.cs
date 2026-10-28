using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// Decodes a raw LZMA2 stream: a sequence of chunks, each stored or LZMA-coded, ending with a zero control byte. This is the data
/// inside an xz block, a 7z coder or a zip entry; the dictionary size comes from a one-byte property that the container stores.
/// </summary>
/// <remarks>
/// <para>Chunk format (public descriptions of LZMA2 and the xz format's filter section): control byte 0 ends the stream; 1 is a stored chunk that
/// also resets the dictionary; 2 is a stored chunk; 0x80 and above start an LZMA chunk, whose bits 5-6 say what is reset first
/// (0 nothing, 1 the state, 2 the state and a new lc/lp/pb byte follows, 3 also the dictionary) and whose low 5 bits are the top bits of the
/// uncompressed size minus one. The first chunk must reset the dictionary, and the first LZMA chunk after a dictionary reset
/// must bring its properties. lc + lp may not exceed 4.</para>
/// <para>A chunk's compressed bytes are decoded in one piece: when the caller's span holds the whole chunk it is decoded from
/// there, otherwise the chunk (at most 64 KiB) is collected in an internal buffer first.</para>
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Lzma2Decoder : IStepDecoder
{
    private const string Format = "lzma2";
    private const int MaxCompressedChunk = 1 << 16;

    private enum Phase
    {
        Control,
        Header,
        Stored,
        Coded,
    }

    private readonly LzmaDecompressionOptions _options;
    private readonly LzmaDecoderCore _core = new();
    private readonly FieldStage _stage = new();
    private byte[]? _chunkBuffer;
    private int _bufferStart;
    private int _bufferEnd;
    private Phase _phase;
    private bool _faulted;
    private bool _started;
    private long _inOffset;
    private long _totalOut;
    private uint _dictionarySize;
    private long _expectedOutput;
    private bool _needDictionaryReset;
    private bool _needProperties;
    private bool _rangeStarted;
    private bool _staged;
    private int _control;
    private int _chunkOutLeft;
    private int _chunkInLeft;

    /// <summary>Creates a decoder for a stream whose dictionary size is given by the LZMA2 property byte.</summary>
    /// <param name="dictionaryProperty">The property byte, 0 to 40 (a dictionary of 4 KiB to 4 GiB - 1).</param>
    /// <param name="options">Limits; <see langword="null"/> for <see cref="LzmaDecompressionOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dictionaryProperty"/> is above 40.</exception>
    /// <exception cref="CompressionLimitException">The dictionary size is above <see cref="LzmaDecompressionOptions.MaxDictionarySize"/>.</exception>
    public Lzma2Decoder(byte dictionaryProperty, LzmaDecompressionOptions? options = null)
    {
        _options = options ?? LzmaDecompressionOptions.Default;
        Configure(dictionaryProperty, 0);
    }

    /// <summary>Gets a value indicating whether the decoder is between streams: no chunk of a stream has been started.</summary>
    public bool IsAtFrameBoundary => !_started && _phase == Phase.Control && !_faulted;

    /// <summary>Gets the number of input bytes consumed since construction or the last <see cref="Reset()"/>.</summary>
    public long TotalConsumed => _inOffset;

    /// <summary>Gets the number of bytes written since construction or the last <see cref="Reset()"/>.</summary>
    public long TotalWritten => _totalOut;

    /// <summary>Forgets the stream being read and clears a failure, keeping the dictionary size and the buffers.</summary>
    public void Reset()
    {
        _faulted = false;
        _inOffset = 0;
        _totalOut = 0;
        StartStream();
    }

    /// <summary>Starts over with a new dictionary size property, as the next block of an xz file needs.</summary>
    /// <param name="dictionaryProperty">The property byte, 0 to 40.</param>
    /// <param name="expectedOutput">The uncompressed size when known, or 0; it only sizes the first window allocation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dictionaryProperty"/> is above 40.</exception>
    /// <exception cref="CompressionLimitException">The dictionary size is above the limit.</exception>
    public void Reset(byte dictionaryProperty, long expectedOutput = 0)
    {
        _faulted = false;
        _inOffset = 0;
        _totalOut = 0;
        Configure(dictionaryProperty, expectedOutput);
    }

    /// <summary>Throws when the decoder is inside a stream, which means the input ended too early.</summary>
    /// <exception cref="CompressionFormatException">The input ended before the end-of-stream control byte.</exception>
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
    /// <returns><see cref="OperationStatus.Done"/> after the end-of-stream control byte, <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting, <see cref="OperationStatus.NeedMoreData"/> when all input was used.</returns>
    /// <exception cref="CompressionFormatException">The data is invalid.</exception>
    /// <exception cref="CompressionLimitException">The output passes <see cref="LzmaDecompressionOptions.MaxOutputSize"/>.</exception>
    /// <exception cref="InvalidOperationException">A previous call threw and <see cref="Reset()"/> was not called.</exception>
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

    private void Configure(byte property, long expectedOutput)
    {
        if (!LzmaProperties.TryGetLzma2DictionarySize(property, out uint size))
        {
            throw new ArgumentOutOfRangeException(nameof(property), "The LZMA2 dictionary property must be 0 to 40.");
        }

        if (size > _options.MaxDictionarySize)
        {
            throw new CompressionLimitException(Format, nameof(LzmaDecompressionOptions.MaxDictionarySize), _options.MaxDictionarySize, size);
        }

        _dictionarySize = size;
        _expectedOutput = expectedOutput;
        _core.SetDictionary(size);
        StartStream();
    }

    private void StartStream()
    {
        _phase = Phase.Control;
        _started = false;
        _needDictionaryReset = true;
        _needProperties = true;
        _stage.Clear();
        _bufferStart = _bufferEnd = 0;
        _core.SetDictionary(_dictionarySize);
    }

    private OperationStatus Run(ReadOnlySpan<byte> source, Span<byte> destination, ref int pos, ref int written)
    {
        while (true)
        {
            switch (_phase)
            {
                case Phase.Control:
                {
                    if (pos >= source.Length)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _control = source[pos++];
                    if (_control == 0)
                    {
                        _started = false;
                        _needDictionaryReset = true;
                        _needProperties = true;
                        return OperationStatus.Done;
                    }

                    // Control bytes 3 to 0x7F are not defined.
                    if (_control is > 2 and < 0x80)
                    {
                        throw new CompressionFormatException(Format, _inOffset + pos - 1, "a control byte of 0, 1, 2 or at least 0x80");
                    }

                    _started = true;
                    _stage.Clear();
                    _stage.Append((byte)_control);
                    _phase = Phase.Header;
                    break;
                }

                case Phase.Header:
                {
                    int need = _control < 0x80 ? 3 : (_control >= 0xC0 ? 6 : 5);
                    if (!_stage.Fill(source, ref pos, need))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    StartChunk(_stage.Bytes, _inOffset + pos - need);
                    break;
                }

                case Phase.Stored:
                {
                    int take = Math.Min(Math.Min(_chunkOutLeft, source.Length - pos), destination.Length - written);
                    if (take > 0)
                    {
                        _core.Append(source.Slice(pos, take), destination[written..]);
                        pos += take;
                        written += take;
                        _chunkOutLeft -= take;
                    }

                    if (_chunkOutLeft == 0)
                    {
                        _phase = Phase.Control;
                        break;
                    }

                    return pos >= source.Length ? OperationStatus.NeedMoreData : OperationStatus.DestinationTooSmall;
                }

                default:
                {
                    OperationStatus status = RunCoded(source, destination, ref pos, ref written, out bool chunkDone);
                    if (!chunkDone)
                    {
                        return status;
                    }

                    _phase = Phase.Control;
                    break;
                }
            }
        }
    }

    private void StartChunk(ReadOnlySpan<byte> h, long at)
    {
        bool dictionaryReset = _control == 1 || _control >= 0xE0;
        if (dictionaryReset)
        {
            _core.ResetDictionary(_expectedOutput);
            _needDictionaryReset = false;
            _needProperties = true;
        }
        else if (_needDictionaryReset)
        {
            throw new CompressionFormatException(Format, at, "a first chunk that resets the dictionary (control byte 1 or at least 0xE0)");
        }

        _stage.Clear();
        if (_control < 0x80)
        {
            _chunkOutLeft = ((h[1] << 8) | h[2]) + 1;
            _phase = Phase.Stored;
            return;
        }

        _chunkOutLeft = (((_control & 0x1F) << 16) | (h[1] << 8) | h[2]) + 1;
        _chunkInLeft = ((h[3] << 8) | h[4]) + 1;
        int kind = (_control >> 5) & 3;
        if (kind >= 2)
        {
            if (!LzmaProperties.TryParsePropertiesByte(h[5], out int lc, out int lp, out int pb) || lc + lp > 4)
            {
                throw new CompressionFormatException(Format, at + 5, "an lc/lp/pb byte with lc + lp at most 4");
            }

            _core.Configure(lc, lp, pb);
            _needProperties = false;
        }
        else if (_needProperties)
        {
            throw new CompressionFormatException(Format, at, "an LZMA chunk with new properties (control byte at least 0xC0) after a dictionary reset");
        }

        if (kind >= 1)
        {
            _core.ResetState();
        }

        if (_chunkInLeft < LzmaRangeDecoder.InitBytes)
        {
            throw new CompressionFormatException(Format, at + 3, "a compressed chunk size of at least 5");
        }

        _rangeStarted = false;
        _staged = false;
        _bufferStart = _bufferEnd = 0;
        _phase = Phase.Coded;
    }

    private OperationStatus RunCoded(ReadOnlySpan<byte> source, Span<byte> destination, ref int pos, ref int written, out bool chunkDone)
    {
        chunkDone = false;
        if (!_staged && source.Length - pos < _chunkInLeft)
        {
            _staged = true;
            _bufferStart = _bufferEnd = 0;
        }

        bool staged = _staged;
        ReadOnlySpan<byte> data;
        if (staged)
        {
            _chunkBuffer ??= new byte[MaxCompressedChunk];
            int have = _bufferEnd - _bufferStart;
            int take = Math.Min(_chunkInLeft - have, source.Length - pos);
            source.Slice(pos, take).CopyTo(_chunkBuffer.AsSpan(_bufferEnd));
            _bufferEnd += take;
            pos += take;
            if (_bufferEnd - _bufferStart < _chunkInLeft)
            {
                return OperationStatus.NeedMoreData;
            }

            data = _chunkBuffer.AsSpan(_bufferStart, _chunkInLeft);
        }
        else
        {
            data = source.Slice(pos, _chunkInLeft);
        }

        int used = 0;
        long at = _inOffset + pos;
        if (!_rangeStarted)
        {
            if (!_core.StartRangeDecoder(data))
            {
                throw new CompressionFormatException(Format, at, "a zero byte at the start of the chunk's range-coded data");
            }

            _rangeStarted = true;
            used = LzmaRangeDecoder.InitBytes;
        }

        LzmaStatus status = _core.Decode(data[used..], out int c, destination[written..], out int w, _chunkOutLeft);
        used += c;
        written += w;
        _chunkOutLeft -= w;
        _chunkInLeft -= used;
        if (staged)
        {
            _bufferStart += used;
        }
        else
        {
            pos += used;
            at = _inOffset + pos;
        }

        switch (status)
        {
            case LzmaStatus.NeedInput:
                throw new CompressionFormatException(Format, at, "the chunk's compressed data to hold all its symbols");
            case LzmaStatus.OutputFull when _chunkOutLeft == 0:
                if (_core.PendingMatchLength > 0)
                {
                    throw new CompressionFormatException(Format, at, "a match that ends at the chunk's uncompressed size");
                }

                if (_chunkInLeft != 0 || !_core.IsRangeDecoderFinished)
                {
                    throw new CompressionFormatException(Format, at, "the chunk's compressed size to end exactly at its last symbol");
                }

                _bufferStart = _bufferEnd = 0;
                chunkDone = true;
                return OperationStatus.Done;
            case LzmaStatus.OutputFull:
                return OperationStatus.DestinationTooSmall;
            case LzmaStatus.EndMarker:
                throw new CompressionFormatException(Format, at, "no end marker inside an LZMA2 chunk");
            default:
                throw new CompressionFormatException(Format, at, "a match distance inside the dictionary");
        }
    }
}
