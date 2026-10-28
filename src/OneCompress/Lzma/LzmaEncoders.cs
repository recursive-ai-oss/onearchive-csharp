using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// Compresses data to a raw LZMA2 stream (the form stored inside xz, 7z and zip): chunks that end with a zero control byte, without
/// any header. The container needs <see cref="DictionaryProperty"/>.
/// </summary>
/// <remarks>
/// <para>Shaped like <c>BrotliEncoder</c>: call <see cref="Compress"/> with the input and a destination until it returns
/// <see cref="OperationStatus.Done"/>; pass <c>isFinalBlock</c> with the last input. <see cref="Flush"/> ends the current chunk so a
/// decoder can read everything given so far; it costs ratio. An instance holds the window and match finder (several times the dictionary
/// size) and is reused with <see cref="Reset"/>. It is not thread-safe.</para>
/// <para>Output depends only on the input, the options and where flushes fall.</para>
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Lzma2Encoder : IQueuedEncoder
{
    private readonly Lzma2Chunker _chunker;
    private readonly OutputQueue _queue = new();
    private bool _finished;

    OutputQueue IQueuedEncoder.Queue => _queue;

    bool IQueuedEncoder.IsDone => _finished;

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The options; <see langword="null"/> for the defaults. <c>Lc + Lp</c> must be at most 4.</param>
    /// <param name="inputLength">The total input length when known, or -1. It only sizes memory.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public Lzma2Encoder(LzmaOptions? options = null, long inputLength = -1)
    {
        _chunker = new Lzma2Chunker((options ?? LzmaOptions.Default).Resolve(), inputLength);
    }

    /// <summary>Gets the dictionary size byte for the container.</summary>
    public byte DictionaryProperty => _chunker.DictionaryProperty;

    /// <summary>Gets the chunker, for callers that compose the raw parts themselves.</summary>
    public Lzma2Chunker Chunker => _chunker;

    /// <summary>Gets a value indicating whether the final block was given and all output (including the end byte) has been produced.</summary>
    public bool IsFinished => _finished;

    /// <summary>Compresses input.</summary>
    /// <param name="source">The next input.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken. Input not taken must be passed again.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> is the last input.</param>
    /// <returns><see cref="OperationStatus.Done"/> when the stream is complete; <see cref="OperationStatus.DestinationTooSmall"/> when more output is waiting; <see cref="OperationStatus.NeedMoreData"/> when all input was taken and the stream is not final.</returns>
    /// <exception cref="InvalidOperationException">Input was passed after the final block.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock) =>
        EncodeLoop.Compress(this, source, destination, out consumed, out written, isFinalBlock);

    /// <summary>Encodes everything given so far into complete chunks, without ending the stream.</summary>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see cref="OperationStatus.Done"/> when all output was written; <see cref="OperationStatus.DestinationTooSmall"/> when more is waiting (call again).</returns>
    public OperationStatus Flush(Span<byte> destination, out int written) => EncodeLoop.Flush(this, destination, out written);

    /// <summary>Starts a new stream, keeping the allocated window and tables.</summary>
    public void Reset()
    {
        _chunker.Reset();
        _queue.Clear();
        _finished = false;
    }

    int IQueuedEncoder.Accept(ReadOnlySpan<byte> source) => _chunker.Append(source);

    bool IQueuedEncoder.Pump(bool drain) => _chunker.WriteChunks(_queue, drain, maxChunks: 8) > 0;

    void IQueuedEncoder.Finish()
    {
        _chunker.WriteEnd(_queue);
        _finished = true;
    }
}

/// <summary>
/// Compresses data to the <c>.lzma</c> format ("LZMA alone"): a 13-byte header with the properties, the dictionary size and the
/// uncompressed size, followed by one range-coded stream.
/// </summary>
/// <remarks>
/// If <c>inputLength</c> is given the header declares it and the end marker is written only when <see cref="LzmaOptions.WriteEndMarker"/>
/// is set; the caller must then supply exactly that many bytes. Without it the header says "unknown" and an end marker always follows.
/// Shaped like <see cref="Lzma2Encoder"/>.
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzmaEncoder : IQueuedEncoder
{
    private const int PumpBytes = 1 << 20;

    private readonly LzmaEncoderCore _core;
    private readonly OutputQueue _queue = new();
    private bool _finished;

    OutputQueue IQueuedEncoder.Queue => _queue;

    bool IQueuedEncoder.IsDone => _finished;
    private readonly long _declaredLength;
    private readonly bool _endMarker;
    private bool _headerWritten;
    private long _total;

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The options; <see langword="null"/> for the defaults.</param>
    /// <param name="inputLength">The total input length when known, or -1.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range, or <paramref name="inputLength"/> is below -1.</exception>
    public LzmaEncoder(LzmaOptions? options = null, long inputLength = -1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputLength, -1);
        LzmaEncoderSettings settings = (options ?? LzmaOptions.Default).Resolve();
        _core = new LzmaEncoderCore(settings, inputLength);
        _declaredLength = inputLength;
        _endMarker = inputLength < 0 || settings.WriteEndMarker;
    }

    /// <summary>Gets the stream properties written to the header.</summary>
    public LzmaProperties Properties => _core.Properties;

    /// <summary>Gets a value indicating whether the final block was given and all output has been produced.</summary>
    public bool IsFinished => _finished;

    /// <summary>Compresses input.</summary>
    /// <param name="source">The next input.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken. Input not taken must be passed again.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> is the last input.</param>
    /// <returns><see cref="OperationStatus.Done"/> when the stream is complete; <see cref="OperationStatus.DestinationTooSmall"/> when more output is waiting; <see cref="OperationStatus.NeedMoreData"/> when all input was taken and the stream is not final.</returns>
    /// <exception cref="InvalidOperationException">The total input differs from <c>inputLength</c>, or input was passed after the final block.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock) =>
        EncodeLoop.Compress(this, source, destination, out consumed, out written, isFinalBlock);

    /// <summary>Starts a new stream, keeping the allocated window and tables.</summary>
    public void Reset()
    {
        _core.Reset();
        _queue.Clear();
        _finished = false;
        _headerWritten = false;
        _total = 0;
    }

    private void WriteHeader()
    {
        if (_headerWritten)
        {
            return;
        }

        _headerWritten = true;
        Span<byte> h = _queue.GetSpan(13);
        h[0] = _core.Properties.PropertiesByte;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h[1..], _core.Properties.DictionarySize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(h[5..], _declaredLength < 0 ? ulong.MaxValue : (ulong)_declaredLength);
        _queue.Advance(13);
    }

    int IQueuedEncoder.Accept(ReadOnlySpan<byte> source)
    {
        WriteHeader();
        if (_declaredLength >= 0 && _core.Position + _core.Buffered + source.Length > _declaredLength)
        {
            throw new InvalidOperationException("More input was given than the declared input length.");
        }

        return _core.Append(source);
    }

    bool IQueuedEncoder.Pump(bool drain)
    {
        WriteHeader();
        int n = _core.Encode(drain, PumpBytes);
        _total += n;
        LzmaRangeEncoder rc = _core.RangeEncoder;
        if (rc.WrittenLength > 0)
        {
            _queue.Write(rc.WrittenSpan);
            rc.ClearWritten();
        }

        return n > 0;
    }

    void IQueuedEncoder.Finish()
    {
        if (_declaredLength >= 0 && _total != _declaredLength)
        {
            throw new InvalidOperationException("Less input was given than the declared input length.");
        }

        if (_endMarker)
        {
            _core.EncodeEndMarker();
        }

        LzmaRangeEncoder rc = _core.RangeEncoder;
        rc.Flush();
        _queue.Write(rc.WrittenSpan);
        rc.ClearWritten();
        _finished = true;
    }
}
