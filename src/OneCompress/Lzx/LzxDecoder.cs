using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lzx;

/// <summary>
/// Decodes LZX as cabinets store it ([MS-PATCH] 2, without the reference data of LZX DELTA): the stream is cut into frames of 32768
/// uncompressed bytes, and the compressed bytes of a frame end on a 16-bit boundary. A cabinet keeps one frame in each data block.
/// </summary>
/// <remarks>
/// <para>Call <see cref="DecodeFrame"/> once per frame, in order, with the compressed bytes of that frame and a destination as long as the
/// frame is: 32768 bytes for every frame except possibly the last. Blocks, Huffman trees, the repeated offsets and the window carry over
/// from one frame to the next, so one decoder must see every frame of a stream and no other.</para>
/// <para>Window sizes are 2<sup>15</sup> to 2<sup>21</sup> bytes and must be known in advance (a cabinet stores them in the folder's
/// compression type). Memory is the window plus a few tables, allocated by the constructor.</para>
/// <para>Hostile input: every length and offset is checked against the window, the frame and the block before it is used. Errors are
/// <see cref="CompressionFormatException"/> with the byte offset in the compressed stream (the sum of the frames given so far plus the
/// position in this frame). After an error the decoder refuses further frames until <see cref="Reset"/>.</para>
/// </remarks>
[Experimental("ONECOMPRESS009", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzxDecoder
{
    /// <summary>The uncompressed size of every frame except possibly the last: 32768 bytes.</summary>
    public const int FrameSize = 32768;

    internal const string FormatName = "lzx";

    private const int LiteralSymbols = 256;
    private const int LengthSymbols = 249;
    private const int AlignedSymbols = 8;
    private const int PreTreeSymbols = 20;

    // [MS-PATCH] 2.3.2.1: "1 to 16 bits" of zero padding align an uncompressed block's data, so a header that already ends on a word
    // boundary is followed by a whole zero word.
    private const bool FullPaddingWordWhenAligned = true;

    private enum BlockType
    {
        None = 0,
        Verbatim = 1,
        Aligned = 2,
        Uncompressed = 3,
    }

    private readonly int _windowBits;
    private readonly int _windowSize;
    private readonly int _slotCount;
    private readonly int _mainSymbols;
    private readonly long _maxOutput;
    private readonly byte[] _window;
    private readonly byte[] _mainLengths;
    private readonly byte[] _lengthLengths = new byte[LengthSymbols];
    private readonly byte[] _alignedLengths = new byte[AlignedSymbols];
    private readonly byte[] _preLengths = new byte[PreTreeSymbols];
    private readonly LzxPrefixCode _mainCode;
    private readonly LzxPrefixCode _lengthCode = new(LengthSymbols);
    private readonly LzxPrefixCode _alignedCode = new(AlignedSymbols);
    private readonly LzxPrefixCode _preCode = new(PreTreeSymbols);

    private long _total;
    private long _inputOffset;
    private int _windowPos;
    private int _r0 = 1;
    private int _r1 = 1;
    private int _r2 = 1;
    private bool _headerRead;
    private bool _translate;
    private uint _translationSize;
    private BlockType _blockType;
    private int _blockRemaining;
    private bool _rawOdd;
    private bool _finished;
    private bool _faulted;

    /// <summary>Creates a decoder.</summary>
    /// <param name="windowBits">The window size as a power of two: 15 to 21 (32 KiB to 2 MiB), the range cabinets allow.</param>
    /// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowBits"/> is outside 15 to 21, or a limit is negative.</exception>
    public LzxDecoder(int windowBits, LzxDecompressionOptions? options = null)
    {
        options ??= LzxDecompressionOptions.Default;
        _slotCount = LzxPositionSlots.GetSlotCount(windowBits);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxOutputSize, nameof(options));
        _windowBits = windowBits;
        _windowSize = 1 << windowBits;
        _maxOutput = options.MaxOutputSize;
        _mainSymbols = LiteralSymbols + (8 * _slotCount);
        _mainLengths = new byte[_mainSymbols];
        _mainCode = new LzxPrefixCode(_mainSymbols);
        _window = new byte[_windowSize];
    }

    /// <summary>Gets the window size exponent.</summary>
    public int WindowBits => _windowBits;

    /// <summary>Gets the number of compressed bytes the frames decoded so far used.</summary>
    public long TotalConsumed => _inputOffset;

    /// <summary>Gets the number of uncompressed bytes produced so far.</summary>
    public long TotalOutput => _total;

    /// <summary>Forgets all state, including a fault, so that the decoder starts a new stream.</summary>
    public void Reset()
    {
        _total = 0;
        _inputOffset = 0;
        _windowPos = 0;
        _r0 = _r1 = _r2 = 1;
        _headerRead = false;
        _translate = false;
        _translationSize = 0;
        _blockType = BlockType.None;
        _blockRemaining = 0;
        _rawOdd = false;
        _finished = false;
        _faulted = false;
        Array.Clear(_mainLengths);
        Array.Clear(_lengthLengths);
        Array.Clear(_window);
    }

    /// <summary>Decodes one frame.</summary>
    /// <param name="source">The compressed bytes of the frame and, optionally, everything after it; the decoder reads what the frame needs.</param>
    /// <param name="destination">Receives the frame: 32768 bytes, or 1 to 32768 for the last frame of the stream.</param>
    /// <param name="consumed">The number of bytes of <paramref name="source"/> the frame used, rounded up to a 16-bit boundary; always set.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destination"/> is empty or longer than 32768 bytes.</exception>
    /// <exception cref="InvalidOperationException">An earlier frame failed (call <see cref="Reset"/>), or a frame shorter than 32768 bytes was already decoded.</exception>
    /// <exception cref="CompressionFormatException">The data is malformed or ends early (<see cref="CompressionFormatException.IsTruncated"/> is set).</exception>
    /// <exception cref="CompressionLimitException">The output would pass <see cref="LzxDecompressionOptions.MaxOutputSize"/>.</exception>
    public void DecodeFrame(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed)
    {
        consumed = 0;
        if (_faulted)
        {
            throw new InvalidOperationException("The decoder failed earlier; call Reset() before using it again.");
        }

        if (destination.Length is 0 or > FrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), "A frame is 1 to 32768 bytes.");
        }

        if (_finished)
        {
            throw new InvalidOperationException("A frame shorter than 32768 bytes ends the stream.");
        }

        if (destination.Length > _maxOutput - _total)
        {
            throw new CompressionLimitException(FormatName, nameof(LzxDecompressionOptions.MaxOutputSize), _maxOutput, _total + destination.Length);
        }

        try
        {
            consumed = DecodeFrameCore(source, destination);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private int DecodeFrameCore(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var br = new LzxBitReader(source, _inputOffset);
        if (!_headerRead)
        {
            // [MS-PATCH] 2.2.2: one bit says whether x86 call translation is on; if so, a 32-bit translation size follows (high word first).
            if (br.ReadBits(1) != 0)
            {
                uint high = br.ReadBits(16);
                uint low = br.ReadBits(16);
                _translate = true;
                _translationSize = (high << 16) | low;
            }

            _headerRead = true;
        }

        long frameStart = _total;
        int frameWindowPos = _windowPos;
        int produced = 0;
        while (produced < destination.Length)
        {
            if (_blockRemaining == 0)
            {
                StartBlock(ref br);
                continue;
            }

            int n = Math.Min(_blockRemaining, destination.Length - produced);
            if (_blockType == BlockType.Uncompressed)
            {
                CopyUncompressed(ref br, n);
            }
            else
            {
                DecodeTokens(ref br, n);
            }

            produced += n;
            _blockRemaining -= n;
        }

        // [MS-PATCH] 2.2.1: the bitstream is padded with zeros to a 16-bit boundary after each frame.
        br.AlignToWord(false);
        int used = br.FinishFrame();
        _inputOffset += used;
        _window.AsSpan(frameWindowPos, destination.Length).CopyTo(destination);
        if (_translate)
        {
            LzxCallTranslation.Undo(destination, frameStart, _translationSize);
        }

        if (destination.Length < FrameSize)
        {
            _finished = true;
        }

        return used;
    }

    // [MS-PATCH] 2.3.1: a 3-bit block type, a 24-bit uncompressed size, then the type's own header.
    private void StartBlock(ref LzxBitReader br)
    {
        uint type = br.ReadBits(3);
        int size = (int)br.ReadBits(24);
        switch (type)
        {
            case 1:
            case 2:
                _blockType = (BlockType)type;
                if (type == 2)
                {
                    // [MS-PATCH] 2.3.2.3: eight 3-bit code lengths for the aligned-offset tree.
                    for (int i = 0; i < AlignedSymbols; i++)
                    {
                        _alignedLengths[i] = (byte)br.ReadBits(3);
                    }

                    if (!_alignedCode.Build(_alignedLengths))
                    {
                        throw br.Invalid("a valid aligned-offset tree (code lengths that do not oversubscribe)");
                    }
                }

                // [MS-PATCH] 2.3.2.2, 2.5: the main tree is sent as two trees (literals, then match codes), then the length tree.
                ReadLengths(ref br, _mainLengths.AsSpan(0, LiteralSymbols), "main tree");
                ReadLengths(ref br, _mainLengths.AsSpan(LiteralSymbols), "main tree");
                ReadLengths(ref br, _lengthLengths, "length tree");
                if (!_mainCode.Build(_mainLengths))
                {
                    throw br.Invalid("a valid main tree (code lengths that do not oversubscribe)");
                }

                if (!_lengthCode.Build(_lengthLengths))
                {
                    throw br.Invalid("a valid length tree (code lengths that do not oversubscribe)");
                }

                break;
            case 3:
                _blockType = BlockType.Uncompressed;
                br.AlignToWord(FullPaddingWordWhenAligned);
                int pos = br.TakeBytePosition();
                if (br.Source.Length - pos < 12)
                {
                    throw br.Truncated();
                }

                ReadOnlySpan<byte> r = br.Source.Slice(pos, 12);
                _r0 = ClampOffset(BinaryPrimitives.ReadUInt32LittleEndian(r));
                _r1 = ClampOffset(BinaryPrimitives.ReadUInt32LittleEndian(r[4..]));
                _r2 = ClampOffset(BinaryPrimitives.ReadUInt32LittleEndian(r[8..]));
                br.SetBytePosition(pos + 12);
                _rawOdd = (size & 1) != 0;
                break;
            default:
                _blockType = BlockType.None;
                throw br.Invalid("a block type of 1 (verbatim), 2 (aligned offset) or 3 (uncompressed)");
        }

        _blockRemaining = size;
        if (size == 0)
        {
            _blockType = BlockType.None;
        }
    }

    // A repeated offset can only be used while it is within the window, so larger stored values are kept as "too large".
    private static int ClampOffset(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private void CopyUncompressed(ref LzxBitReader br, int n)
    {
        int pos = br.TakeBytePosition();
        if (br.Source.Length - pos < n)
        {
            throw br.Truncated();
        }

        int mask = _windowSize - 1;
        ReadOnlySpan<byte> data = br.Source.Slice(pos, n);
        int wp = _windowPos;
        int first = Math.Min(n, _windowSize - wp);
        data[..first].CopyTo(_window.AsSpan(wp));
        data[first..].CopyTo(_window);
        _windowPos = (wp + n) & mask;
        _total += n;
        pos += n;
        if (n == _blockRemaining && _rawOdd && pos < br.Source.Length)
        {
            // The data of an odd-sized block is followed by one zero byte that realigns the bitstream.
            pos++;
        }

        br.SetBytePosition(pos);
    }

    private void DecodeTokens(ref LzxBitReader br, int count)
    {
        byte[] window = _window;
        int mask = _windowSize - 1;
        int pos = _windowPos;
        long total = _total;
        bool aligned = _blockType == BlockType.Aligned;
        int done = 0;
        while (done < count)
        {
            int symbol = _mainCode.Decode(ref br, "main tree");
            if (symbol < LiteralSymbols)
            {
                window[pos] = (byte)symbol;
                pos = (pos + 1) & mask;
                done++;
                continue;
            }

            // [MS-PATCH] 2.6.5, 2.7: the symbol packs the position slot and the length header.
            int m = symbol - LiteralSymbols;
            int length = m & 7;
            int slot = m >> 3;
            length = length == 7 ? _lengthCode.Decode(ref br, "length tree") + 9 : length + 2;
            int offset;
            if (slot == 0)
            {
                offset = _r0;
            }
            else if (slot == 1)
            {
                offset = _r1;
                _r1 = _r0;
                _r0 = offset;
            }
            else if (slot == 2)
            {
                offset = _r2;
                _r2 = _r0;
                _r0 = offset;
            }
            else
            {
                int extra = LzxPositionSlots.FooterBits[slot];
                uint verbatim;
                if (aligned && extra >= 3)
                {
                    verbatim = br.ReadBits(extra - 3) << 3;
                    verbatim += (uint)_alignedCode.Decode(ref br, "aligned-offset tree");
                }
                else
                {
                    verbatim = br.ReadBits(extra);
                }

                offset = LzxPositionSlots.BasePositions[slot] + (int)verbatim - 2;
                _r2 = _r1;
                _r1 = _r0;
                _r0 = offset;
            }

            if (length > count - done)
            {
                throw br.Invalid("a match that ends inside its block and frame");
            }

            if (offset < 1 || offset > _windowSize || offset > total + done)
            {
                throw br.Invalid("a match offset between 1 and the bytes already decoded (at most the window size)");
            }

            int src = (pos - offset) & mask;
            if (offset >= length && src + length <= window.Length && pos + length <= window.Length)
            {
                window.AsSpan(src, length).CopyTo(window.AsSpan(pos));
                pos += length;
                if (pos == window.Length)
                {
                    pos = 0;
                }
            }
            else
            {
                for (int i = 0; i < length; i++)
                {
                    window[pos] = window[src];
                    pos = (pos + 1) & mask;
                    src = (src + 1) & mask;
                }
            }

            done += length;
        }

        _windowPos = pos;
        _total = total + count;
    }

    // [MS-PATCH] 2.5: lengths are sent as differences from the previous tree's lengths, mod 17, through a 20-symbol pretree.
    private void ReadLengths(ref LzxBitReader br, Span<byte> lengths, string what)
    {
        for (int i = 0; i < PreTreeSymbols; i++)
        {
            _preLengths[i] = (byte)br.ReadBits(4);
        }

        if (!_preCode.Build(_preLengths))
        {
            throw br.Invalid("a valid pretree for the " + what);
        }

        int x = 0;
        while (x < lengths.Length)
        {
            int code = _preCode.Decode(ref br, "pretree");
            int run;
            int value;
            if (code <= 16)
            {
                lengths[x] = (byte)((lengths[x] - code + 17) % 17);
                x++;
                continue;
            }

            if (code == 17)
            {
                run = 4 + (int)br.ReadBits(4);
                value = 0;
            }
            else if (code == 18)
            {
                run = 20 + (int)br.ReadBits(5);
                value = 0;
            }
            else
            {
                run = 4 + (int)br.ReadBits(1);
                int next = _preCode.Decode(ref br, "pretree");
                if (next > 16)
                {
                    throw br.Invalid("a code length difference of 0 to 16 after a repeat code");
                }

                value = (lengths[x] - next + 17) % 17;
            }

            if (run > lengths.Length - x)
            {
                throw br.Invalid("a run of code lengths that ends inside the " + what);
            }

            lengths.Slice(x, run).Fill((byte)value);
            x += run;
        }
    }
}
