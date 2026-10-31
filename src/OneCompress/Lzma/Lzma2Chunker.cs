using System.Buffers;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// Cuts an LZMA2 stream into chunks: it asks an <see cref="LzmaEncoderCore"/> for up to 2 MiB of input per chunk, within a compressed
/// size of 64 KiB, and writes each as an LZMA chunk, or as stored chunks when coding did not make the data smaller. It also decides the
/// resets: the first chunk resets the dictionary and sets the properties, and the first LZMA chunk after a stored chunk resets the state.
/// </summary>
/// <remarks>
/// <para>The format (the LZMA2 chunk layout documented in the xz file format specification's filter section and in public descriptions):
/// control 0 ends the stream; 1 and 2 are stored chunks with and without a dictionary reset, followed by a 16-bit size minus one; control
/// 0x80 and above is an LZMA chunk whose bits 5 and 6 say what is reset (0 nothing, 1 state, 2 state and new properties, 3 everything
/// including the dictionary), whose low 5 bits are the top of the 21-bit uncompressed size minus one, followed by two bytes of the rest, two
/// bytes of the compressed size minus one and, when properties are reset, the properties byte.</para>
/// <para>The chunks depend only on the input, the settings and where <see cref="WriteChunks"/> was told to drain, never on how the input
/// was split into <see cref="Append"/> calls.</para>
/// </remarks>
public sealed class Lzma2Chunker
{
    /// <summary>The most uncompressed bytes in one LZMA chunk: 2 MiB.</summary>
    public const int MaxChunkUncompressedSize = 1 << 21;

    /// <summary>The most compressed bytes in one LZMA chunk, and the most bytes in a stored chunk: 64 KiB.</summary>
    public const int MaxChunkCompressedSize = 1 << 16;

    private readonly LzmaEncoderCore _core;
    private bool _needDictionaryReset = true;
    private bool _needProperties = true;
    private bool _needStateReset;
    private bool _open;
    private int _chunkBytes;

    /// <summary>Creates a chunker.</summary>
    /// <param name="settings">The resolved settings. <c>Lc + Lp</c> must be at most 4, as LZMA2 requires.</param>
    /// <param name="sizeHint">The expected input size when known, or -1; it only sizes memory.</param>
    /// <param name="unitSize">The size of the independent units the stream is cut into by the caller (xz blocks), or 0; see <see cref="LzmaEncoderCore"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><c>Lc + Lp</c> is above 4, or the settings are not resolved.</exception>
    public Lzma2Chunker(LzmaEncoderSettings settings, long sizeHint = -1, long unitSize = 0)
    {
        if (settings.Lc + settings.Lp > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "LZMA2 requires lc + lp to be at most 4.");
        }

        _core = new LzmaEncoderCore(settings, sizeHint, unitSize);
        DictionaryProperty = GetDictionaryProperty(settings.DictionarySize);
    }

    /// <summary>Gets the byte that declares the dictionary size in the container (the xz filter properties, the 7z coder properties).</summary>
    public byte DictionaryProperty { get; }

    /// <summary>Gets the number of bytes appended but not yet written as chunks.</summary>
    public int Buffered => _core.Buffered;

    // True while a flush drains: more input follows, so see LzmaEncoderCore.InputContinues. A drain that is not a flush ends the input.
    internal bool InputContinues
    {
        get => _core.InputContinues;
        set => _core.InputContinues = value;
    }

    /// <summary>Gets the symbol encoder the chunks are produced with.</summary>
    public LzmaEncoderCore Core => _core;

    /// <summary>Gets the dictionary property that declares at least <paramref name="dictionarySize"/> bytes.</summary>
    /// <param name="dictionarySize">The size in bytes.</param>
    /// <returns>The smallest property byte, 0 to 40, whose size is at least <paramref name="dictionarySize"/>.</returns>
    public static byte GetDictionaryProperty(uint dictionarySize)
    {
        for (int i = 0; i < 40; i++)
        {
            LzmaProperties.TryGetLzma2DictionarySize(i, out uint size);
            if (size >= dictionarySize)
            {
                return (byte)i;
            }
        }

        return 40;
    }

    /// <summary>Starts a new LZMA2 stream: the next chunk resets the dictionary.</summary>
    public void Reset()
    {
        _core.Reset();
        _needDictionaryReset = true;
        _needProperties = true;
        _needStateReset = false;
        _open = false;
        _chunkBytes = 0;
    }

    /// <summary>Copies input into the window.</summary>
    /// <param name="data">The input.</param>
    /// <returns>The number of bytes taken; fewer than offered when the window is full and <see cref="WriteChunks"/> must run.</returns>
    public int Append(ReadOnlySpan<byte> data) => _core.Append(data);

    /// <summary>Encodes buffered input and writes the chunks that are complete.</summary>
    /// <param name="output">Receives the chunks.</param>
    /// <param name="drain">True to encode everything buffered and close the chunk in progress (the input has ended; <see cref="Lzma2Encoder.Flush"/> is the supported way to end chunks in the middle of a stream); false to keep the last <see cref="LzmaEncoderCore.LookaheadLength"/> bytes back and leave a chunk open until it is full.</param>
    /// <param name="maxChunks">The most chunks to write.</param>
    /// <returns>The number of input bytes that were encoded.</returns>
    public int WriteChunks(IBufferWriter<byte> output, bool drain, int maxChunks = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(output);
        int total = 0;
        int chunks = 0;
        while (chunks < maxChunks)
        {
            int n = Step(output, drain, out bool closed);
            total += n;
            if (closed)
            {
                chunks++;
            }
            else if (n == 0)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>Writes the end-of-stream control byte. All buffered input must have been chunked (call <see cref="WriteChunks"/> with <c>drain</c> first).</summary>
    /// <param name="output">Receives the byte.</param>
    /// <exception cref="InvalidOperationException">Input is still buffered.</exception>
    public void WriteEnd(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_core.Buffered != 0 || _open)
        {
            throw new InvalidOperationException("Input is still buffered; write its chunks first.");
        }

        output.GetSpan(1)[0] = 0;
        output.Advance(1);
    }

    // A chunk stays open across calls until it is full (2 MiB of input or no room left in 64 KiB of output) or the caller drains, so
    // chunk boundaries never depend on how the input arrived.
    private int Step(IBufferWriter<byte> output, bool drain, out bool closed)
    {
        closed = false;
        LzmaRangeEncoder rc = _core.RangeEncoder;
        int n = 0;
        bool limited = false;
        if (_core.Buffered > 0 && (drain || _core.Buffered >= LzmaEncoderCore.LookaheadLength))
        {
            if (!_open)
            {
                rc.Reset();
                if (_needStateReset || _needProperties || _needDictionaryReset)
                {
                    _core.ResetState();
                }

                _chunkBytes = 0;
                _open = true;
            }

            n = _core.Encode(drain, MaxChunkUncompressedSize - _chunkBytes, MaxChunkCompressedSize, out limited);
            _chunkBytes += n;
        }

        if (_open && (limited || drain))
        {
            CloseChunk(output);
            closed = true;
        }

        return n;
    }

    private void CloseChunk(IBufferWriter<byte> output)
    {
        LzmaRangeEncoder rc = _core.RangeEncoder;
        int n = _chunkBytes;
        _open = false;
        _chunkBytes = 0;
        bool stateReset = _needStateReset || _needProperties || _needDictionaryReset;
        rc.Flush();
        ReadOnlySpan<byte> coded = rc.WrittenSpan;
        int headerSize = _needProperties ? 6 : 5;
        int storedChunks = (n + MaxChunkCompressedSize - 1) / MaxChunkCompressedSize;
        if (coded.Length + headerSize >= n + (3 * storedChunks))
        {
            // Coding did not pay: store the bytes. The probabilities now describe symbols the decoder never saw, so the next LZMA
            // chunk starts from the initial state.
            ReadOnlySpan<byte> raw = _core.GetEncoded(n);
            while (raw.Length > 0)
            {
                int take = Math.Min(raw.Length, MaxChunkCompressedSize);
                Span<byte> span = output.GetSpan(take + 3);
                span[0] = (byte)(_needDictionaryReset ? 1 : 2);
                span[1] = (byte)((take - 1) >> 8);
                span[2] = (byte)(take - 1);
                raw[..take].CopyTo(span[3..]);
                output.Advance(take + 3);
                raw = raw[take..];
                _needDictionaryReset = false;
            }

            _needStateReset = true;
            return;
        }

        int reset = _needDictionaryReset ? 3 : _needProperties ? 2 : stateReset ? 1 : 0;
        Span<byte> chunk = output.GetSpan(coded.Length + 6);
        int u = n - 1;
        int c = coded.Length - 1;
        chunk[0] = (byte)(0x80 | (reset << 5) | (u >> 16));
        chunk[1] = (byte)(u >> 8);
        chunk[2] = (byte)u;
        chunk[3] = (byte)(c >> 8);
        chunk[4] = (byte)c;
        int at = 5;
        if (_needProperties)
        {
            chunk[at++] = _core.Properties.PropertiesByte;
        }

        coded.CopyTo(chunk[at..]);
        output.Advance(at + coded.Length);
        _needDictionaryReset = false;
        _needProperties = false;
        _needStateReset = false;
        return;
    }
}
