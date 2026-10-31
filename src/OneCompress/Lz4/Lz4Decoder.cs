using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// Decompresses LZ4 frames (LZ4 Frame Format) across arbitrary splits of input and output: standard frames, skippable
/// frames and the legacy frame format. A decoder holds the buffers it needs and reuses them, so decoding many frames with
/// one instance allocates nothing once its buffers have reached the size the frames need.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/> follows the shape of <c>BrotliDecoder</c>, with one difference: malformed
/// input raises <see cref="CompressionFormatException"/> instead of returning a status, so a corrupt stream can never be
/// mistaken for a short one. After an exception the decoder refuses further input until <see cref="Reset"/> is called.
/// </para>
/// <para>
/// The decoder stops at the end of each frame and returns <see cref="OperationStatus.Done"/>; calling it again continues
/// with the next frame, so concatenated frames are decoded by looping. Skippable frames count as frames and are reported
/// to <see cref="Lz4DecompressionOptions.SkippableFrameHandler"/>. A legacy frame has no end mark: it ends at the next frame's
/// magic number, at a zero block size, or where the input ends, so for a legacy frame <c>Done</c> comes only with the next
/// frame's magic number.
/// </para>
/// <para>
/// Compressed blocks are decoded only after the whole block (and its checksum) has arrived and been verified, so corrupt
/// compressed data is never handed out. Uncompressed blocks are handed out as they arrive and checked at their end.
/// </para>
/// <para>An instance is not thread-safe. Dictionaries (<see cref="Lz4Dictionary"/>) are, and may be shared.</para>
/// </remarks>
public sealed class Lz4Decoder
{
    private enum State
    {
        FrameStart,
        SkippableBody,
        BlockHeader,
        RawBlock,
        BlockChecksum,
        CompressedBlock,
        ContentChecksum,
        LegacyBlockHeader,
    }

    private const int HistorySize = 64 * 1024;

    private readonly Lz4DecompressionOptions _options;
    private readonly byte[] _small = new byte[Lz4Format.FrameHeaderMax];

    private State _state;
    private bool _failed;
    private long _inputOffset;  // input bytes consumed by earlier calls
    private long _totalOutput;  // output produced since the last reset, for MaxOutputSize
    private int _smallLen;

    // The current frame.
    private bool _legacy;
    private bool _independent;
    private bool _checkBlock;
    private bool _hasBlockChecksum;
    private bool _checkContent;
    private bool _hasContentChecksum;
    private int _blockMax;
    private long _contentSize;       // declared, or -1
    private long _contentRemaining;  // declared minus produced, or -1 when not declared
    private long _frameOutput;
    private Lz4Dictionary? _dictionary;
    private int _frameBase;          // position in a contiguous destination where the frame's output starts
    private bool _contiguous;
    private XxHash32 _contentHash;
    private XxHash32 _blockHash;

    // The current block.
    private int _blockSize;
    private int _blockRemaining;
    private int _blockFill;
    private byte[] _blockBuffer = [];

    // Output awaiting delivery, and the history that matches copy from, for frames that cannot decode straight into the caller's buffer.
    private byte[] _window = [];
    private int _windowPosition;
    private int _flushed;
    private bool _windowReady;
    private Lz4Dictionary? _windowDictionary;  // the dictionary currently installed at the start of the window

    // A skippable frame's payload, when a handler wants it.
    private byte[] _skipBuffer = [];
    private int _skipVariant;
    private long _skipRemaining;
    private int _skipFill;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">The limits and dictionaries to use; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit in <paramref name="options"/> is out of range.</exception>
    public Lz4Decoder(Lz4DecompressionOptions? options = null)
    {
        _options = options ?? Lz4DecompressionOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxBlockSize, 1024, "MaxBlockSize");
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxOutputSize, "MaxOutputSize");
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxSkippableFrameSize, "MaxSkippableFrameSize");
    }

    /// <summary>Gets a value indicating whether the decoder is between frames with nothing buffered, so input may end here.</summary>
    public bool IsAtFrameBoundary =>
        ((_state == State.FrameStart || _state == State.LegacyBlockHeader) && _smallLen == 0) && _flushed == _windowPosition;

    /// <summary>Gets the number of input bytes consumed since construction or the last <see cref="Reset"/>.</summary>
    public long TotalConsumed => _inputOffset;

    /// <summary>Gets the number of output bytes produced since construction or the last <see cref="Reset"/>.</summary>
    public long TotalProduced => _totalOutput;

    /// <summary>Forgets any partly decoded frame and clears the failed state, so the decoder can start on new data.</summary>
    public void Reset()
    {
        _state = State.FrameStart;
        _failed = false;
        _inputOffset = 0;
        _totalOutput = 0;
        _smallLen = 0;
        _windowPosition = 0;
        _flushed = 0;
        _windowReady = false;
        _blockFill = 0;
        _skipFill = 0;
        _skipRemaining = 0;
        _dictionary = null;
        _contiguous = false;
    }

    /// <summary>Throws if the input ended inside a frame; call it when no more input will arrive.</summary>
    /// <exception cref="CompressionFormatException">The decoder is in the middle of a frame (<see cref="CompressionFormatException.IsTruncated"/> is true).</exception>
    public void ThrowIfIncomplete()
    {
        if (IsAtFrameBoundary)
        {
            return;
        }

        string expected = _state switch
        {
            State.FrameStart => "the rest of a frame header",
            State.SkippableBody => $"{_skipRemaining} more bytes of a skippable frame",
            State.BlockHeader => "a block header",
            State.RawBlock => $"{_blockRemaining} more bytes of an uncompressed block",
            State.BlockChecksum => "the 4-byte block checksum",
            State.CompressedBlock => $"{_blockSize - _blockFill} more bytes of a compressed block",
            State.ContentChecksum => "the 4-byte content checksum",
            State.LegacyBlockHeader => "a 4-byte legacy block size",
            _ => "more data",
        };
        if (_flushed != _windowPosition)
        {
            expected = "the decoded output to be collected";
        }

        Lz4Format.ThrowTruncated(_inputOffset, expected);
    }

    /// <summary>
    /// Reads the decoded size of the data without decoding it, by walking the frame headers and block sizes.
    /// </summary>
    /// <param name="source">One or more complete frames.</param>
    /// <param name="size">The total decoded size, when the method returns true.</param>
    /// <returns>True when every frame states its content size; false when some frame is a legacy frame or leaves the size out.</returns>
    /// <exception cref="CompressionFormatException">The frames are truncated or their headers are invalid.</exception>
    public static bool TryGetDecompressedSize(ReadOnlySpan<byte> source, out long size)
    {
        size = 0;
        if (source.IsEmpty)
        {
            Lz4Format.ThrowTruncated(0, "an LZ4 frame");
        }

        long total = 0;
        long pos = 0;
        bool known = true;
        while (pos < source.Length)
        {
            ReadOnlySpan<byte> rest = source.Slice((int)pos);
            if (rest.Length < 4)
            {
                Lz4Format.ThrowTruncated(pos, "a frame magic number");
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(rest);
            if (Lz4Format.IsSkippable(magic))
            {
                if (rest.Length < 8)
                {
                    Lz4Format.ThrowTruncated(pos, "a skippable frame size");
                }

                pos += 8L + BinaryPrimitives.ReadUInt32LittleEndian(rest.Slice(4));
                if (pos > source.Length)
                {
                    Lz4Format.ThrowTruncated(source.Length, "the rest of a skippable frame");
                }

                continue;
            }

            if (magic == Lz4Format.LegacyMagic)
            {
                return false;
            }

            if (Lz4FrameHeader.TryParse(rest, out Lz4FrameHeader header, pos) != OperationStatus.Done)
            {
                Lz4Format.ThrowTruncated(source.Length, "the rest of a frame header");
            }

            pos += header.Length;
            if (header.ContentSize is long cs)
            {
                total += cs;
            }
            else
            {
                known = false;
            }

            while (true)
            {
                if (source.Length - pos < 4)
                {
                    Lz4Format.ThrowTruncated(source.Length, "a block header");
                }

                uint block = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice((int)pos));
                pos += 4;
                if (block == 0)
                {
                    break;
                }

                pos += (block & 0x7FFFFFFF) + (header.HasBlockChecksum ? 4 : 0);
                if (pos > source.Length)
                {
                    Lz4Format.ThrowTruncated(source.Length, "the rest of a block");
                }
            }

            if (header.HasContentChecksum)
            {
                pos += 4;
                if (pos > source.Length)
                {
                    Lz4Format.ThrowTruncated(source.Length, "the 4-byte content checksum");
                }
            }
        }

        size = total;
        return known;
    }

    /// <summary>
    /// Decompresses as much as the buffers allow.
    /// </summary>
    /// <param name="source">Compressed bytes. Pass the bytes not consumed by earlier calls, followed by any new ones.</param>
    /// <param name="destination">Space for decompressed bytes.</param>
    /// <param name="consumed">The number of bytes of <paramref name="source"/> used.</param>
    /// <param name="written">The number of bytes written to <paramref name="destination"/>.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when a frame has ended and all of its output has been written (call again for the
    /// next frame); <see cref="OperationStatus.DestinationTooSmall"/> when <paramref name="destination"/> is full and more
    /// output is waiting; <see cref="OperationStatus.NeedMoreData"/> when all of <paramref name="source"/> was used and the
    /// frame is incomplete. It never returns <see cref="OperationStatus.InvalidData"/>.
    /// </returns>
    /// <exception cref="CompressionFormatException">The data is corrupt, or a checksum or the declared content size does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit of the options (<c>MaxBlockSize</c>, <c>MaxOutputSize</c>, <c>MaxSkippableFrameSize</c>), before the memory is allocated.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    /// <exception cref="InvalidOperationException">An earlier call threw and <see cref="Reset"/> has not been called.</exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        int ip = 0;
        int dpos = 0;
        consumed = 0;
        written = 0;
        if (_failed)
        {
            throw new InvalidOperationException("The decoder failed earlier; call Reset before reusing it.");
        }

        try
        {
            return Run(source, destination, ref dpos, ref ip, contiguous: false);
        }
        catch
        {
            _failed = true;
            throw;
        }
        finally
        {
            _inputOffset += ip;
            consumed = ip;
            written = dpos;
        }
    }

    /// <summary>
    /// Decompresses a complete input into a buffer that holds the complete output, reporting whether it was large enough.
    /// The decoder is reset first. Where it can, the decoder writes blocks straight into <paramref name="destination"/>
    /// without an intermediate copy.
    /// </summary>
    /// <param name="source">One or more complete frames, ending at a frame boundary.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written; meaningful only when the method returns true.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small, after which the decoder must be reset.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or a checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit of the decoder's options.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    public bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        Reset();
        bytesWritten = 0;
        if (source.IsEmpty)
        {
            Lz4Format.ThrowTruncated(0, "an LZ4 frame");
        }

        int ip = 0;
        int dpos = 0;
        try
        {
            while (true)
            {
                // The whole input is passed on every step, so offsets in errors are positions in it.
                OperationStatus status = Run(source, destination, ref dpos, ref ip, contiguous: true);
                switch (status)
                {
                    case OperationStatus.DestinationTooSmall:
                        _failed = true;
                        return false;
                    case OperationStatus.NeedMoreData:
                        _inputOffset = ip;
                        ThrowIfIncomplete();
                        bytesWritten = dpos;
                        return true;
                    default:
                        if (ip >= source.Length)
                        {
                            bytesWritten = dpos;
                            return true;
                        }

                        break;
                }
            }
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    // Copies bytes of the small buffer's worth from the source until n are held; true once they are. The bytes are then
    // in `bytes`, either a slice of the source (nothing was buffered) or the small buffer.
    private bool Fill(ReadOnlySpan<byte> src, ref int ip, int n, out ReadOnlySpan<byte> bytes)
    {
        if (_smallLen == 0 && src.Length - ip >= n)
        {
            bytes = src.Slice(ip, n);
            ip += n;
            return true;
        }

        int take = Math.Min(n - _smallLen, src.Length - ip);
        src.Slice(ip, take).CopyTo(_small.AsSpan(_smallLen));
        _smallLen += take;
        ip += take;
        if (_smallLen < n)
        {
            bytes = default;
            return false;
        }

        _smallLen = 0;
        bytes = _small.AsSpan(0, n);
        return true;
    }

    // How many bytes the header at the start of h needs in total, judged from the bytes seen so far.
    private static int HeaderNeed(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4)
        {
            return 4;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (magic == Lz4Format.FrameMagic)
        {
            if (h.Length < 6)
            {
                return 6;
            }

            int flg = h[4];
            return 4 + 2 + ((flg & 0x08) != 0 ? 8 : 0) + ((flg & 0x01) != 0 ? 4 : 0) + 1;
        }

        return Lz4Format.IsSkippable(magic) ? 8 : 4;
    }

    private OperationStatus Run(ReadOnlySpan<byte> src, Span<byte> dst, ref int dpos, ref int ip, bool contiguous)
    {
        _contiguous = contiguous;
        while (true)
        {
            // Output decoded earlier but not yet delivered always goes first.
            if (_flushed < _windowPosition)
            {
                int n = Math.Min(_windowPosition - _flushed, dst.Length - dpos);
                _window.AsSpan(_flushed, n).CopyTo(dst.Slice(dpos));
                _flushed += n;
                dpos += n;
                if (_flushed < _windowPosition)
                {
                    return OperationStatus.DestinationTooSmall;
                }
            }

            switch (_state)
            {
                case State.FrameStart:
                    {
                        ReadOnlySpan<byte> h;
                        bool inPlace = _smallLen == 0 && src.Length - ip >= Lz4Format.FrameHeaderMax;
                        if (inPlace)
                        {
                            h = src.Slice(ip);
                        }
                        else
                        {
                            while (true)
                            {
                                int need = HeaderNeed(_small.AsSpan(0, _smallLen));
                                if (_smallLen >= need)
                                {
                                    break;
                                }

                                int take = Math.Min(need - _smallLen, src.Length - ip);
                                if (take == 0)
                                {
                                    return OperationStatus.NeedMoreData;
                                }

                                src.Slice(ip, take).CopyTo(_small.AsSpan(_smallLen));
                                _smallLen += take;
                                ip += take;
                            }

                            h = _small.AsSpan(0, _smallLen);
                        }

                        long start = _inputOffset + ip - (inPlace ? 0 : _smallLen);
                        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(h);
                        int used;
                        if (magic == Lz4Format.FrameMagic)
                        {
                            Lz4FrameHeader.TryParse(h, out Lz4FrameHeader header, start);
                            BeginFrame(header, start, dpos);
                            used = header.Length;
                        }
                        else if (Lz4Format.IsSkippable(magic))
                        {
                            BeginSkippable(magic, BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(4)));
                            used = 8;
                        }
                        else if (magic == Lz4Format.LegacyMagic)
                        {
                            BeginLegacy();
                            used = 4;
                        }
                        else
                        {
                            Lz4Format.ThrowInvalid(start, "an LZ4 frame magic number (0x184D2204, a skippable 0x184D2A5X or the legacy 0x184C2102)");
                            return OperationStatus.InvalidData;
                        }

                        if (inPlace)
                        {
                            ip += used;
                        }
                        else
                        {
                            _smallLen = 0;
                        }

                        break;
                    }

                case State.SkippableBody:
                    {
                        Lz4SkippableFrameHandler? handler = _options.SkippableFrameHandler;
                        int n = (int)Math.Min(_skipRemaining, src.Length - ip);
                        if (handler is null)
                        {
                            ip += n;
                            _skipRemaining -= n;
                        }
                        else
                        {
                            src.Slice(ip, n).CopyTo(_skipBuffer.AsSpan(_skipFill));
                            ip += n;
                            _skipFill += n;
                            _skipRemaining -= n;
                        }

                        if (_skipRemaining > 0)
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        handler?.Invoke(_skipVariant, _skipBuffer.AsSpan(0, _skipFill));
                        _skipFill = 0;
                        _state = State.FrameStart;
                        return OperationStatus.Done;
                    }

                case State.BlockHeader:
                    {
                        if (!Fill(src, ref ip, 4, out ReadOnlySpan<byte> h))
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(h);
                        long at = _inputOffset + ip - 4;
                        if (v == 0)
                        {
                            // Frame format, "Data Blocks": a zero block size is the EndMark.
                            if (_contentSize >= 0 && _frameOutput != _contentSize)
                            {
                                Lz4Format.ThrowInvalid(at, $"the frame to hold the {_contentSize} bytes its header declares, not {_frameOutput}");
                            }

                            if (_hasContentChecksum)
                            {
                                _state = State.ContentChecksum;
                                break;
                            }

                            _state = State.FrameStart;
                            return OperationStatus.Done;
                        }

                        int size = (int)(v & 0x7FFFFFFF);
                        if (size > _blockMax)
                        {
                            Lz4Format.ThrowInvalid(at, $"a block size of at most {_blockMax} (the frame's block maximum)");
                        }

                        if ((v & 0x80000000) != 0)
                        {
                            if (_contentRemaining >= 0 && size > _contentRemaining)
                            {
                                Lz4Format.ThrowInvalid(at, "an uncompressed block that fits the declared content size");
                            }

                            if (size > _options.MaxOutputSize - _totalOutput)
                            {
                                Lz4Format.ThrowLimit("MaxOutputSize", _options.MaxOutputSize, _totalOutput + size);
                            }

                            _blockRemaining = size;
                            _blockHash = default;
                            _state = State.RawBlock;
                            if (RawBlocksUseWindow)
                            {
                                StartWindowBlock();
                            }
                        }
                        else
                        {
                            _blockSize = size;
                            _blockFill = 0;
                            _state = State.CompressedBlock;
                        }

                        break;
                    }

                case State.RawBlock:
                    {
                        if (_blockRemaining == 0)
                        {
                            _state = _hasBlockChecksum ? State.BlockChecksum : State.BlockHeader;
                            break;
                        }

                        int avail = src.Length - ip;
                        if (avail == 0)
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        int n = Math.Min(_blockRemaining, avail);
                        bool direct = !RawBlocksUseWindow;
                        ReadOnlySpan<byte> chunk;
                        if (direct)
                        {
                            n = Math.Min(n, dst.Length - dpos);
                            if (n == 0)
                            {
                                return OperationStatus.DestinationTooSmall;
                            }

                            src.Slice(ip, n).CopyTo(dst.Slice(dpos));
                            chunk = dst.Slice(dpos, n);
                            dpos += n;
                        }
                        else
                        {
                            // The window was sized and positioned when the block header was read.
                            src.Slice(ip, n).CopyTo(_window.AsSpan(_windowPosition));
                            chunk = _window.AsSpan(_windowPosition, n);
                            _windowPosition += n;
                        }

                        if (_hasBlockChecksum && _checkBlock)
                        {
                            _blockHash.Append(src.Slice(ip, n));
                        }

                        if (_hasContentChecksum && _checkContent)
                        {
                            _contentHash.Append(chunk);
                        }

                        ip += n;
                        _blockRemaining -= n;
                        AddOutput(n);
                        break;
                    }

                case State.BlockChecksum:
                    {
                        if (!Fill(src, ref ip, 4, out ReadOnlySpan<byte> h))
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        if (_checkBlock)
                        {
                            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(h);
                            uint actual = _blockHash.GetCurrentValue();
                            if (stored != actual)
                            {
                                Lz4Format.ThrowInvalid(_inputOffset + ip - 4, $"block checksum 0x{actual:X8}");
                            }
                        }

                        _state = State.BlockHeader;
                        break;
                    }

                case State.CompressedBlock:
                    {
                        OperationStatus s = DecodeCompressedBlock(src, dst, ref dpos, ref ip);
                        if (s != OperationStatus.Done)
                        {
                            return s;
                        }

                        break;
                    }

                case State.ContentChecksum:
                    {
                        if (!Fill(src, ref ip, 4, out ReadOnlySpan<byte> h))
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        if (_checkContent)
                        {
                            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(h);
                            uint actual = _contentHash.GetCurrentValue();
                            if (stored != actual)
                            {
                                Lz4Format.ThrowInvalid(_inputOffset + ip - 4, $"content checksum 0x{actual:X8}");
                            }
                        }

                        _state = State.FrameStart;
                        return OperationStatus.Done;
                    }

                case State.LegacyBlockHeader:
                    {
                        ReadOnlySpan<byte> h;
                        bool inPlace = _smallLen == 0 && src.Length - ip >= 4;
                        if (inPlace)
                        {
                            h = src.Slice(ip, 4);
                        }
                        else
                        {
                            int take = Math.Min(4 - _smallLen, src.Length - ip);
                            src.Slice(ip, take).CopyTo(_small.AsSpan(_smallLen));
                            _smallLen += take;
                            ip += take;
                            if (_smallLen < 4)
                            {
                                return OperationStatus.NeedMoreData;
                            }

                            h = _small.AsSpan(0, 4);
                        }

                        uint v = BinaryPrimitives.ReadUInt32LittleEndian(h);
                        long at = _inputOffset + ip - (inPlace ? 0 : 4);
                        if (Lz4Format.IsFrameStart(v))
                        {
                            // The legacy frame ends where the next frame's magic number starts; that frame is read next.
                            // Bytes buffered in the small buffer stay there for the frame-start state.
                            _state = State.FrameStart;
                            return OperationStatus.Done;
                        }

                        _smallLen = 0;
                        if (inPlace)
                        {
                            ip += 4;
                        }

                        if (v == 0)
                        {
                            // Frame format, "Legacy frame": the Linux kernel's end marker, accepted as an extension.
                            _state = State.FrameStart;
                            return OperationStatus.Done;
                        }

                        if (v > Lz4Format.LegacyCompressedMax)
                        {
                            Lz4Format.ThrowInvalid(at, $"a legacy block size of at most {Lz4Format.LegacyCompressedMax}, or the magic number of the next frame");
                        }

                        _blockSize = (int)v;
                        _blockFill = 0;
                        _state = State.CompressedBlock;
                        break;
                    }

                default:
                    throw new InvalidOperationException("unreachable");
            }
        }
    }

    // Uncompressed blocks go straight to the caller's buffer unless history must be kept for later blocks (linked blocks
    // in a stream) or the block must sit after a dictionary.
    private bool RawBlocksUseWindow => _dictionary is not null || (!_contiguous && !_independent);

    private void AddOutput(int n)
    {
        _frameOutput += n;
        _totalOutput += n;
        if (_contentRemaining >= 0)
        {
            _contentRemaining -= n;
        }
    }

    private void BeginFrame(Lz4FrameHeader header, long start, int dpos)
    {
        if (header.BlockMaxSize > _options.MaxBlockSize)
        {
            Lz4Format.ThrowLimit("MaxBlockSize", _options.MaxBlockSize, header.BlockMaxSize);
        }

        long contentSize = header.ContentSizeOrMinusOne;
        if (contentSize >= 0 && contentSize > _options.MaxOutputSize - _totalOutput)
        {
            Lz4Format.ThrowLimit("MaxOutputSize", _options.MaxOutputSize, _totalOutput + contentSize);
        }

        Lz4Dictionary? dictionary = _options.Dictionary;
        if (header.DictionaryId is uint id && id != 0)
        {
            dictionary = null;
            if (_options.Dictionaries is { } list)
            {
                foreach (Lz4Dictionary d in list)
                {
                    if (d.Id == id)
                    {
                        dictionary = d;
                        break;
                    }
                }
            }

            if (dictionary is null && _options.Dictionary is { } fallback && (fallback.Id == 0 || fallback.Id == id))
            {
                dictionary = fallback;
            }

            if (dictionary is null)
            {
                throw new CompressionDictionaryException(Lz4Format.Name, id, $"the frame at byte offset {start} names dictionary ID {id}, which was not supplied");
            }
        }

        _legacy = false;
        _independent = header.BlocksIndependent;
        _hasBlockChecksum = header.HasBlockChecksum;
        _checkBlock = _hasBlockChecksum && _options.VerifyChecksum;
        _hasContentChecksum = header.HasContentChecksum;
        _checkContent = _hasContentChecksum && _options.VerifyChecksum;
        _blockMax = header.BlockMaxSize;
        _contentSize = contentSize;
        _contentRemaining = contentSize;
        _frameOutput = 0;
        _frameBase = dpos;
        _dictionary = dictionary is { Length: > 0 } ? dictionary : null;
        _windowReady = false;
        _contentHash = default;
        _state = State.BlockHeader;
    }

    private void BeginLegacy()
    {
        if (_options.MaxBlockSize < Lz4Format.LegacyBlockSize)
        {
            Lz4Format.ThrowLimit("MaxBlockSize", _options.MaxBlockSize, Lz4Format.LegacyBlockSize);
        }

        _legacy = true;
        _independent = true;
        _hasBlockChecksum = false;
        _checkBlock = false;
        _hasContentChecksum = false;
        _checkContent = false;
        _blockMax = Lz4Format.LegacyBlockSize;
        _contentSize = -1;
        _contentRemaining = -1;
        _frameOutput = 0;
        _dictionary = null;
        _windowReady = false;
        _state = State.LegacyBlockHeader;
    }

    private void BeginSkippable(uint magic, uint size)
    {
        _skipVariant = (int)(magic & 0xF);
        _skipRemaining = size;
        _skipFill = 0;
        if (_options.SkippableFrameHandler is not null)
        {
            if (size > _options.MaxSkippableFrameSize)
            {
                Lz4Format.ThrowLimit("MaxSkippableFrameSize", _options.MaxSkippableFrameSize, size);
            }

            if (_skipBuffer.Length < size)
            {
                _skipBuffer = new byte[size];
            }
        }

        _state = State.SkippableBody;
    }

    // Makes the window ready for the frame: sized for the frame's blocks, holding the dictionary where there is one.
    private void PrepareWindow()
    {
        if (_windowReady)
        {
            return;
        }

        int dictionaryLength = _dictionary?.Length ?? 0;
        int blockCap = _blockMax;
        if (_contentSize >= 0 && _contentSize < blockCap)
        {
            blockCap = (int)Math.Max(_contentSize, 1);
        }

        // Linked blocks keep the last 64 KiB between blocks and slide when the window fills; two blocks of room make that rare.
        int need = _independent ? dictionaryLength + blockCap : HistorySize + (2 * blockCap);
        if (_window.Length < need)
        {
            _window = new byte[need];
            _windowDictionary = null;
        }

        if (_dictionary is { } dictionary)
        {
            // A dictionary already installed by an earlier frame is still there unless the window was reused since.
            if (!ReferenceEquals(_windowDictionary, dictionary))
            {
                dictionary.Content.CopyTo(_window);
                _windowDictionary = dictionary;
            }
        }
        else
        {
            _windowDictionary = null;
        }

        _windowPosition = dictionaryLength;
        _flushed = dictionaryLength;
        _windowReady = true;
    }

    // Positions the window for a block of up to _blockMax bytes: after the dictionary again for independent blocks,
    // or after sliding the history to the front when a linked block no longer fits.
    private void StartWindowBlock()
    {
        PrepareWindow();
        if (_independent)
        {
            int length = _dictionary?.Length ?? 0;
            _windowPosition = length;
            _flushed = length;
            return;
        }

        int blockCap = _blockMax;
        if (_contentSize >= 0 && _contentSize < blockCap)
        {
            blockCap = (int)Math.Max(_contentSize, 1);
        }

        if (_windowPosition + blockCap > _window.Length)
        {
            int keep = Math.Min(_windowPosition, HistorySize);
            Buffer.BlockCopy(_window, _windowPosition - keep, _window, 0, keep);
            _windowPosition = keep;
            _flushed = keep;
            _windowDictionary = null;
        }
    }

    private OperationStatus DecodeCompressedBlock(ReadOnlySpan<byte> src, Span<byte> dst, ref int dpos, ref int ip)
    {
        int checksumLength = _hasBlockChecksum ? 4 : 0;
        int total = _blockSize + checksumLength;
        ReadOnlySpan<byte> block;
        ReadOnlySpan<byte> checksum = default;
        if (_blockFill == 0 && src.Length - ip >= total)
        {
            block = src.Slice(ip, _blockSize);
            checksum = src.Slice(ip + _blockSize, checksumLength);
            ip += total;
        }
        else
        {
            if (_blockBuffer.Length < total)
            {
                _blockBuffer = new byte[total];
            }

            int take = Math.Min(total - _blockFill, src.Length - ip);
            src.Slice(ip, take).CopyTo(_blockBuffer.AsSpan(_blockFill));
            _blockFill += take;
            ip += take;
            if (_blockFill < total)
            {
                return OperationStatus.NeedMoreData;
            }

            _blockFill = 0;
            block = _blockBuffer.AsSpan(0, _blockSize);
            checksum = _blockBuffer.AsSpan(_blockSize, checksumLength);
        }

        long blockOffset = _inputOffset + ip - total;
        if (_checkBlock)
        {
            uint actual = XxHash32.Compute(block);
            if (BinaryPrimitives.ReadUInt32LittleEndian(checksum) != actual)
            {
                Lz4Format.ThrowInvalid(blockOffset + _blockSize, $"block checksum 0x{actual:X8}");
            }
        }

        // The most this block may produce, and which rule sets that bound (for the error if the block exceeds it).
        long cap = _blockMax;
        int bound = 0;
        if (_contentRemaining >= 0 && _contentRemaining < cap)
        {
            cap = _contentRemaining;
            bound = 1;
        }

        long outputLeft = _options.MaxOutputSize - _totalOutput;
        if (outputLeft < cap)
        {
            cap = outputLeft;
            bound = 2;
        }

        Lz4BlockDecoder.BlockResult result;
        int written;
        int errorOffset;
        Span<byte> region;
        int outStart;
        bool useWindow = false;
        if (_dictionary is null && (_contiguous || _independent) && (_contiguous || dst.Length - dpos >= cap))
        {
            if (_independent)
            {
                // Independent blocks see nothing before their own start.
                region = dst.Slice(dpos, (int)Math.Min(cap, dst.Length - dpos));
                outStart = 0;
            }
            else
            {
                // Linked blocks in a caller's buffer see the frame's earlier output, which is already there.
                int historyLength = dpos - _frameBase;
                region = dst.Slice(_frameBase, (int)Math.Min(historyLength + cap, dst.Length - _frameBase));
                outStart = historyLength;
            }
        }
        else
        {
            useWindow = true;
            StartWindowBlock();
            outStart = _windowPosition;
            region = _window.AsSpan(0, (int)Math.Min(_windowPosition + cap, _window.Length));
        }

        result = Lz4BlockDecoder.Decode(block, region, outStart, out written, out errorOffset);
        if (result != Lz4BlockDecoder.BlockResult.Ok)
        {
            if (result == Lz4BlockDecoder.BlockResult.OutputOverflow)
            {
                if (!useWindow && region.Length - outStart < cap)
                {
                    // The caller's buffer ran out before the rule that bounds the block did.
                    return OperationStatus.DestinationTooSmall;
                }

                switch (bound)
                {
                    case 0:
                        Lz4Format.ThrowInvalid(blockOffset + errorOffset, $"a block that decodes to at most {_blockMax} bytes");
                        break;
                    case 1:
                        Lz4Format.ThrowInvalid(blockOffset + errorOffset, $"the frame to hold the {_contentSize} bytes its header declares");
                        break;
                    default:
                        Lz4Format.ThrowLimit("MaxOutputSize", _options.MaxOutputSize, _options.MaxOutputSize + 1);
                        break;
                }
            }

            Lz4BlockDecoder.Throw(result, errorOffset, blockOffset);
        }

        ReadOnlySpan<byte> output = region.Slice(outStart, written);
        if (_hasContentChecksum && _checkContent)
        {
            _contentHash.Append(output);
        }

        if (useWindow)
        {
            _windowPosition += written;
        }
        else
        {
            dpos += written;
        }

        AddOutput(written);
        _state = _legacy ? State.LegacyBlockHeader : State.BlockHeader;
        return OperationStatus.Done;
    }
}
