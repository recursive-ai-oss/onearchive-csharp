using System.Buffers;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Deflate64;

/// <summary>
/// Compresses to one raw Deflate64 stream (zip method 9): RFC 1951 blocks with the Deflate64 symbols (docs/deflate64-derivation.md),
/// a 64 KiB history, matches of 3 to 65538 bytes. The output depends only on the input bytes, the level and where flushes fall;
/// not on how the caller splits its calls. Levels and block choice are in docs/deflate64-encoder.md.
/// </summary>
/// <remarks>
/// Memory: about 1 MiB at levels 1 to 4 (a 192 KiB window, a hash table, a 64 KiB chain array, token arrays and the output buffer) and about 2.5 MiB at levels 5 to 9 (binary-tree finder and price lists instead of the chain), allocated on
/// construction and reused. The encoder never produces a distance of 65536 (the farthest it uses is 65535).
/// </remarks>
public sealed partial class Deflate64Encoder
{
    private const int HistorySize = Deflate64Tables.WindowSize;
    private const int ChunkSize = 1 << 17;
    private const int BufferSize = HistorySize + ChunkSize;
    private const int MaxDistance = HistorySize - 1;
    private const int TokenCapacity = 1 << 15;
    private const int HashBits = 16;
    private const int CheckInterval = 512;
    private const int Null = -(1 << 30);

    private readonly int _level;
    private readonly LevelParameters _parameters;
    private readonly byte[] _window = new byte[BufferSize];
    private int _fill;
    private int _position;
    private bool _finished;
    private readonly DeflateBlockWriter _writer = new(1 << 18);
    private readonly DeflateHuffmanBuilder _builder = new();

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The level; <see langword="null"/> for <see cref="Deflate64Options.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0 to 9.</exception>
    public Deflate64Encoder(Deflate64Options? options = null)
    {
        options ??= Deflate64Options.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(options.Level, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Level, 9, nameof(options));
        _level = options.Level;
        _parameters = LevelParameters.For(_level);
        if (_parameters.Passes > 0)
        {
            _optimal = new OptimalState();
            _tokenLength = new int[TokenCapacity];
            _tokenValue = new int[TokenCapacity];
            ResetFinder();
        }
        else if (_level > 0)
        {
            _head = new int[1 << HashBits];
            _previous = new ushort[HistorySize];
            _tokenLength = new int[TokenCapacity];
            _tokenValue = new int[TokenCapacity];
            ResetFinder();
        }
    }

    /// <summary>Gets the level this encoder was made with.</summary>
    public int Level => _level;

    /// <summary>Gets the most bytes <see cref="Compress"/> can produce for input of <paramref name="inputLength"/> bytes (a sync flush adds 5 bytes each).</summary>
    /// <param name="inputLength">The input size.</param>
    /// <returns>The bound.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative or too large.</exception>
    public static long GetMaxCompressedLength(long inputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputLength, long.MaxValue / 2);
        return inputLength + (inputLength >> 12) + 64;
    }

    /// <summary>Starts a new stream, keeping the buffers. Everything pending is dropped.</summary>
    public void Reset()
    {
        _fill = 0;
        _position = 0;
        _finished = false;
        _writer.Reset();
        _blockStart = 0;
        _blockBytes = 0;
        _tokenCount = 0;
        if (_level > 0)
        {
            ResetFinder();
        }
    }

    /// <summary>Compresses input, finishing the stream when <paramref name="isFinalBlock"/> is true.</summary>
    /// <param name="source">New input. Pass the bytes <paramref name="consumed"/> left over by an earlier call again.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken. Less than the source's length only when <see cref="OperationStatus.DestinationTooSmall"/> is returned.</param>
    /// <param name="written">The number of bytes written to <paramref name="destination"/>.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> is the last of the stream.</param>
    /// <returns>
    /// <see cref="OperationStatus.NeedMoreData"/> when all input was taken and the stream is not finished;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting (call again with more room and the unconsumed input);
    /// <see cref="OperationStatus.Done"/> when the stream is finished and fully written.
    /// </returns>
    /// <exception cref="InvalidOperationException">Input was given after the stream finished; call <see cref="Reset"/> first.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock = false) =>
        Run(source, destination, out consumed, out written, isFinalBlock ? Mode.Finish : Mode.None);

    /// <summary>
    /// Forces out everything given so far and ends it on a byte boundary with an empty stored block (a sync flush), so a reader that has
    /// all the bytes written so far can produce all the input so far. Later matches may still reach back across the flush.
    /// </summary>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see cref="OperationStatus.Done"/> or <see cref="OperationStatus.DestinationTooSmall"/> (call again).</returns>
    public OperationStatus Flush(Span<byte> destination, out int written) =>
        Run(default, destination, out _, out written, Mode.Sync);

    private enum Mode
    {
        None,
        Sync,
        Finish,
    }

    private bool _flushPending;

    private OperationStatus Run(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, Mode mode)
    {
        consumed = 0;
        written = 0;
        while (true)
        {
            written += _writer.CopyPending(destination[written..]);
            if (_writer.PendingBytes > 0)
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (_finished)
            {
                if (consumed < source.Length)
                {
                    throw new InvalidOperationException("The stream is finished; call Reset() before compressing more.");
                }

                return OperationStatus.Done;
            }

            if (consumed < source.Length)
            {
                int take = Math.Min(source.Length - consumed, BufferSize - _fill);
                source.Slice(consumed, take).CopyTo(_window.AsSpan(_fill));
                _fill += take;
                consumed += take;
                if (_fill == BufferSize)
                {
                    ParseBuffered(final: false);
                    continue;
                }

                if (consumed < source.Length)
                {
                    continue;
                }
            }

            switch (mode)
            {
                case Mode.None:
                    return OperationStatus.NeedMoreData;
                case Mode.Sync:
                    if (_flushPending)
                    {
                        _flushPending = false;
                        return OperationStatus.Done;
                    }

                    ParseBuffered(final: false);
                    _writer.WriteEmptyStoredBlock();
                    _flushPending = true;
                    continue;
                default:
                    ParseBuffered(final: true);
                    _finished = true;
                    continue;
            }
        }
    }

    // Parses everything buffered beyond _position, writes the blocks, and slides the buffer so that the last HistorySize bytes stay.
    private void ParseBuffered(bool final)
    {
        if (_level == 0)
        {
            int count = _fill - _position;
            if (count > 0 || final)
            {
                _writer.WriteStoredBlocks(_window.AsSpan(_position, count), final);
            }
        }
        else
        {
            if (_fill > _position)
            {
                Parse(_fill);
            }

            CloseBlock(final);
        }

        _position = _fill;
        if (!final && _fill == BufferSize)
        {
            int delta = _fill - HistorySize;
            Buffer.BlockCopy(_window, delta, _window, 0, HistorySize);
            _fill = HistorySize;
            _position = HistorySize;
            if (_level > 0)
            {
                SlideFinder(delta);
            }
        }
        else if (final)
        {
            _writer.AlignToByte();
        }
    }

    private void WriteEmptyFinal()
    {
        // A fixed block holding only the end-of-block symbol: 03 00 on a byte boundary.
        _writer.WriteBits(3, 3);
        _writer.WriteBits(0, 7);
    }
}
