using System.Diagnostics.CodeAnalysis;
using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Decompresses Zstandard data (RFC 8878) across arbitrary splits of input and output. A decoder holds the
/// buffers and tables it needs and reuses them, so decoding many frames with one instance allocates nothing
/// once its buffers have reached the size the frames need.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Decompress"/> follows the shape of <c>BrotliDecoder</c>, with one difference: malformed input
/// raises <see cref="CompressionFormatException"/> instead of returning a status, so a corrupt stream can
/// never be mistaken for a short one. After an exception the decoder refuses further input until
/// <see cref="Reset"/> is called.
/// </para>
/// <para>
/// The decoder stops at the end of each frame and returns <see cref="OperationStatus.Done"/>; calling it again
/// continues with the next frame, so concatenated frames are decoded by looping. Skippable frames count as frames
/// and are reported to <see cref="ZstdDecompressionOptions.SkippableFrameHandler"/>.
/// </para>
/// <para>An instance is not thread-safe. Dictionaries (<see cref="ZstdDictionary"/>) are, and may be shared.</para>
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdDecoder
{
    private enum State
    {
        FrameStart,
        SkippableBody,
        BlockHeader,
        RawBlock,
        RleBlock,
        CompressedBlock,
        Checksum,
    }

    private const int WindowSlack = 64;

    private readonly ZstdDecompressionOptions _options;
    private readonly ZstdBlockDecoder _blocks = new();
    private readonly byte[] _small = new byte[ZstdFormat.FrameHeaderSizeMax];

    private State _state;
    private bool _failed;
    private bool _frameDone;
    private long _inputOffset;  // input bytes consumed by earlier calls
    private long _totalOutput;  // output produced since the last reset, for MaxOutputSize
    private int _smallLen;

    // The current frame.
    private ZstdFrameHeader _header;
    private ZstdDictionary? _dictionary;
    private long _windowSize;
    private int _blockMax;
    private long _frameOutput;
    private XxHash64 _checksum;
    private bool _lastBlock;
    private int _blockSize;
    private int _blockRemaining;

    // Output awaiting delivery, and the history that matches copy from (a window plus room for a block).
    private byte[] _window = [];
    private int _capacity;
    private int _position;
    private int _flushed;

    // A compressed block that arrives in pieces.
    private byte[] _blockBuffer = [];
    private int _blockFill;

    // A skippable frame's payload, when a handler wants it.
    private byte[] _skipBuffer = [];
    private int _skipFill;
    private long _skipRemaining;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">The limits and dictionaries to use; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit in <paramref name="options"/> is out of range.</exception>
    public ZstdDecoder(ZstdDecompressionOptions? options = null)
    {
        _options = options ?? ZstdDecompressionOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxWindowSize, 1024L, "MaxWindowSize");
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxOutputSize, "MaxOutputSize");
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxSkippableFrameSize, "MaxSkippableFrameSize");
    }

    /// <summary>Gets a value indicating whether the decoder is between frames with nothing buffered, so input may end here.</summary>
    public bool IsAtFrameBoundary => _state == State.FrameStart && _smallLen == 0 && _flushed == _position && !_frameDone;

    /// <summary>Gets the number of input bytes consumed since construction or the last <see cref="Reset"/>.</summary>
    public long TotalConsumed => _inputOffset;

    /// <summary>Gets the number of output bytes produced since construction or the last <see cref="Reset"/>.</summary>
    public long TotalProduced => _totalOutput;

    /// <summary>
    /// Parses the header at the start of <paramref name="source"/> without decoding anything.
    /// </summary>
    /// <param name="source">Bytes starting at a frame boundary.</param>
    /// <param name="header">The header, when the method returns true.</param>
    /// <returns>True when <paramref name="source"/> held a complete header.</returns>
    /// <exception cref="CompressionFormatException">The data does not start with a valid frame header.</exception>
    public static bool TryReadFrameHeader(ReadOnlySpan<byte> source, out ZstdFrameHeader header) =>
        ZstdFrameHeader.TryParse(source, out header, out _);

    /// <summary>Forgets any partly decoded frame and clears the failed state, so the decoder can start on new data.</summary>
    public void Reset()
    {
        _state = State.FrameStart;
        _failed = false;
        _frameDone = false;
        _inputOffset = 0;
        _totalOutput = 0;
        _smallLen = 0;
        _position = 0;
        _flushed = 0;
        _blockFill = 0;
        _skipFill = 0;
        _skipRemaining = 0;
        _dictionary = null;
    }

    /// <summary>Throws if the input ended inside a frame; call it when no more input will arrive.</summary>
    /// <exception cref="CompressionFormatException">The decoder is in the middle of a frame (<see cref="CompressionFormatException.IsTruncated"/> is true).</exception>
    public void ThrowIfIncomplete()
    {
        if (_state == State.FrameStart && _smallLen == 0 && !_frameDone)
        {
            return;
        }

        string expected = _state switch
        {
            State.FrameStart => "the rest of a frame header",
            State.SkippableBody => $"{_skipRemaining} more bytes of a skippable frame",
            State.BlockHeader => "a block header",
            State.RawBlock => $"{_blockRemaining} more bytes of a raw block",
            State.RleBlock => "the byte of an RLE block",
            State.CompressedBlock => $"{_blockSize - _blockFill} more bytes of a compressed block",
            _ => "the 4-byte content checksum",
        };
        ZstdFormat.ThrowTruncated(_inputOffset, expected);
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
    /// frame is not complete.
    /// </returns>
    /// <exception cref="CompressionFormatException">The data is not valid Zstandard, or its content checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">A frame exceeds <see cref="ZstdDecompressionOptions.MaxWindowSize"/>, <see cref="ZstdDecompressionOptions.MaxOutputSize"/> or the skippable frame limit.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    /// <exception cref="InvalidOperationException">An earlier call threw and <see cref="Reset"/> has not been called.</exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        consumed = 0;
        written = 0;
        if (_failed)
        {
            throw new InvalidOperationException("The decoder failed on earlier input; call Reset before reusing it.");
        }

        try
        {
            return Run(source, destination, ref consumed, ref written);
        }
        catch (CompressionException)
        {
            _failed = true;
            throw;
        }
        finally
        {
            _inputOffset += consumed;
        }
    }

    private OperationStatus Run(ReadOnlySpan<byte> source, Span<byte> destination, ref int consumed, ref int written)
    {
        while (true)
        {
            // Output decoded earlier goes first.
            if (_flushed < _position)
            {
                int n = Math.Min(_position - _flushed, destination.Length - written);
                _window.AsSpan(_flushed, n).CopyTo(destination[written..]);
                _flushed += n;
                written += n;
                if (_flushed < _position)
                {
                    return OperationStatus.DestinationTooSmall;
                }
            }

            if (_frameDone)
            {
                _frameDone = false;
                return OperationStatus.Done;
            }

            ReadOnlySpan<byte> rest = source[consumed..];
            switch (_state)
            {
                case State.FrameStart:
                    if (_smallLen == 0)
                    {
                        if (rest.Length == 0)
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        if (TryDecodeWholeFrame(rest, destination[written..], _inputOffset + consumed, out int frameBytes, out int frameOutput))
                        {
                            consumed += frameBytes;
                            written += frameOutput;
                            return OperationStatus.Done;
                        }
                    }

                    if (!ReadFrameHeader(rest, ref consumed, out ZstdFrameHeader header))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    StartFrame(header);
                    break;

                case State.SkippableBody:
                    {
                        int n = (int)Math.Min(rest.Length, _skipRemaining);
                        if (_options.SkippableFrameHandler is not null)
                        {
                            rest[..n].CopyTo(_skipBuffer.AsSpan(_skipFill));
                            _skipFill += n;
                        }

                        consumed += n;
                        _skipRemaining -= n;
                        if (_skipRemaining > 0)
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        _options.SkippableFrameHandler?.Invoke(_header.SkippableVariant, _skipBuffer.AsSpan(0, _skipFill));
                        _state = State.FrameStart;
                        _frameDone = true;
                        break;
                    }

                case State.BlockHeader:
                    if (!ReadBlockHeader(rest, ref consumed))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    break;

                case State.RawBlock:
                    {
                        int n = Math.Min(rest.Length, _blockRemaining);
                        if (n == 0)
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        CheckOutputLimit(n);
                        rest[..n].CopyTo(_window.AsSpan(_position));
                        AddOutput(n);
                        consumed += n;
                        _blockRemaining -= n;
                        if (_blockRemaining == 0)
                        {
                            EndBlock();
                        }

                        break;
                    }

                case State.RleBlock:
                    if (rest.Length == 0)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    CheckOutputLimit(_blockSize);
                    _window.AsSpan(_position, _blockSize).Fill(rest[0]);
                    AddOutput(_blockSize);
                    consumed++;
                    EndBlock();
                    break;

                case State.CompressedBlock:
                    if (!ReadCompressedBlock(rest, ref consumed))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    break;

                default: // State.Checksum
                    if (!ReadChecksum(rest, ref consumed))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    break;
            }
        }
    }

    // RFC 8878 §3.1.1.1: collects a frame header that may arrive in pieces.
    private bool ReadFrameHeader(ReadOnlySpan<byte> rest, ref int consumed, out ZstdFrameHeader header)
    {
        long start = _inputOffset + consumed - _smallLen;
        if (_smallLen == 0 && ZstdFrameHeader.TryParse(rest, out header, out _, start))
        {
            consumed += header.HeaderSize;
            return true;
        }

        int take = Math.Min(rest.Length, _small.Length - _smallLen);
        rest[..take].CopyTo(_small.AsSpan(_smallLen));
        int have = _smallLen + take;
        if (!ZstdFrameHeader.TryParse(_small.AsSpan(0, have), out header, out _, start))
        {
            _smallLen = have;
            consumed += take;
            return false;
        }

        consumed += header.HeaderSize - _smallLen;
        _smallLen = 0;
        return true;
    }

    private ZstdDictionary? ResolveDictionary(uint id)
    {
        if (id == 0)
        {
            return _options.Dictionary;
        }

        if (_options.Dictionaries is { } list)
        {
            foreach (ZstdDictionary candidate in list)
            {
                if (candidate.Id == id)
                {
                    return candidate;
                }
            }
        }

        // A raw-content dictionary has no ID of its own, so it serves whatever ID the frame names.
        if (_options.Dictionary is { } single && (single.Id == id || single.Id == 0))
        {
            return single;
        }

        throw new CompressionDictionaryException(ZstdFormat.Name, id, $"the frame needs dictionary {id}, which was not supplied");
    }

    // Checks a frame's declared sizes against the limits, before anything is allocated.
    private ZstdDictionary? CheckHeader(in ZstdFrameHeader header)
    {
        if (header.WindowSize > _options.MaxWindowSize)
        {
            throw new CompressionLimitException(ZstdFormat.Name, "MaxWindowSize", _options.MaxWindowSize, header.WindowSize);
        }

        if (header.HasContentSize && header.ContentSize > (ulong)(_options.MaxOutputSize - Math.Min(_totalOutput, _options.MaxOutputSize)))
        {
            long requested = header.ContentSize > (ulong)(long.MaxValue - _totalOutput) ? long.MaxValue : _totalOutput + (long)header.ContentSize;
            throw new CompressionLimitException(ZstdFormat.Name, "MaxOutputSize", _options.MaxOutputSize, requested);
        }

        return ResolveDictionary(header.DictionaryId);
    }

    private void BeginFrame(in ZstdFrameHeader header, ZstdDictionary? dictionary)
    {
        _header = header;
        _dictionary = dictionary;
        _windowSize = header.WindowSize;
        _blockMax = (int)Math.Min(header.WindowSize, ZstdFormat.BlockSizeMax);
        _frameOutput = 0;
        _lastBlock = false;
        _blocks.BeginFrame(dictionary);
        if (header.HasChecksum && _options.VerifyChecksum)
        {
            _checksum.Reset();
        }
    }

    private void StartFrame(in ZstdFrameHeader header)
    {
        if (header.IsSkippable)
        {
            _header = header;
            _skipRemaining = header.UserDataSize;
            _skipFill = 0;
            if (_options.SkippableFrameHandler is not null)
            {
                if (header.UserDataSize > _options.MaxSkippableFrameSize)
                {
                    throw new CompressionLimitException(ZstdFormat.Name, "MaxSkippableFrameSize", _options.MaxSkippableFrameSize, header.UserDataSize);
                }

                if (_skipBuffer.Length < header.UserDataSize)
                {
                    _skipBuffer = new byte[header.UserDataSize];
                }
            }

            _state = State.SkippableBody;
            return;
        }

        ZstdDictionary? dictionary = CheckHeader(header);
        BeginFrame(header, dictionary);

        // The window holds the history matches copy from plus one block. A frame that declares its size and
        // fits in two windows gets exactly that much and never has to slide.
        long capacity = header.WindowSize > long.MaxValue / 2 ? long.MaxValue : 2 * header.WindowSize;
        if (header.HasContentSize && header.ContentSize < (ulong)capacity)
        {
            capacity = (long)header.ContentSize;
        }

        if (capacity + WindowSlack > Array.MaxLength)
        {
            throw new CompressionLimitException(ZstdFormat.Name, "MaxWindowSize", Array.MaxLength / 2, header.WindowSize);
        }

        _capacity = (int)capacity;
        _position = 0;
        _flushed = 0;
        _state = State.BlockHeader;
    }

    // RFC 8878 §3.1.1.2: Block_Header, three bytes.
    private bool ReadBlockHeader(ReadOnlySpan<byte> rest, ref int consumed)
    {
        long start = _inputOffset + consumed - _smallLen;
        uint value;
        if (_smallLen == 0 && rest.Length >= 3)
        {
            value = (uint)(rest[0] | (rest[1] << 8) | (rest[2] << 16));
            consumed += 3;
        }
        else
        {
            int take = Math.Min(3 - _smallLen, rest.Length);
            rest[..take].CopyTo(_small.AsSpan(_smallLen));
            _smallLen += take;
            consumed += take;
            if (_smallLen < 3)
            {
                return false;
            }

            value = (uint)(_small[0] | (_small[1] << 8) | (_small[2] << 16));
            _smallLen = 0;
        }

        _lastBlock = (value & 1) != 0;
        int type = (int)((value >> 1) & 3);
        int size = (int)(value >> 3);
        if (type == 3)
        {
            ZstdFormat.ThrowCorrupt(start, "a block type of raw, RLE or compressed (type 3 is reserved)");
        }

        if (size > _blockMax)
        {
            ZstdFormat.ThrowCorrupt(start, $"a block size of at most {_blockMax}, found {size}");
        }

        if (type != 2 && _header.HasContentSize && (ulong)size > _header.ContentSize - (ulong)_frameOutput)
        {
            ZstdFormat.ThrowCorrupt(start, "block output within the frame content size");
        }

        MakeRoom(MaxBlockOutput(type == 2 ? _blockMax : size));
        _blockSize = size;
        switch (type)
        {
            case 0:
                _blockRemaining = size;
                if (size == 0)
                {
                    EndBlock();
                }
                else
                {
                    _state = State.RawBlock;
                }

                break;
            case 1:
                _state = State.RleBlock;
                break;
            default:
                _blockFill = 0;
                _state = State.CompressedBlock;
                break;
        }

        return true;
    }

    private int MaxBlockOutput(int limit) =>
        _header.HasContentSize ? (int)Math.Min(limit, (long)(_header.ContentSize - (ulong)_frameOutput)) : limit;

    // Makes room for `needed` more bytes: slides the window when the history has filled it, and grows the array
    // (in steps, so a frame that declares a huge window but is small never reserves it).
    private void MakeRoom(int needed)
    {
        if (_capacity - _position < needed)
        {
            int keep = (int)Math.Min(_position, _windowSize);
            Array.Copy(_window, _position - keep, _window, 0, keep);
            _position = keep;
            _flushed = keep;
        }

        int required = _position + needed;
        if (_window.Length - WindowSlack < required)
        {
            int target = Math.Min(_capacity, Math.Max(required, Math.Max(2 * (_window.Length - WindowSlack), 128 * 1024)));
            byte[] grown = new byte[target + WindowSlack];
            Array.Copy(_window, grown, _position);
            _window = grown;
        }
    }

    private bool ReadCompressedBlock(ReadOnlySpan<byte> rest, ref int consumed)
    {
        ReadOnlySpan<byte> block;
        if (_blockFill == 0 && rest.Length >= _blockSize)
        {
            block = rest[.._blockSize];
            consumed += _blockSize;
        }
        else
        {
            if (_blockBuffer.Length < _blockSize)
            {
                _blockBuffer = new byte[Math.Max(_blockSize, Math.Min(_blockBuffer.Length * 2, ZstdFormat.BlockSizeMax))];
            }

            int take = Math.Min(rest.Length, _blockSize - _blockFill);
            rest[..take].CopyTo(_blockBuffer.AsSpan(_blockFill));
            _blockFill += take;
            consumed += take;
            if (_blockFill < _blockSize)
            {
                return false;
            }

            block = _blockBuffer.AsSpan(0, _blockSize);
            _blockFill = 0;
        }

        long blockOffset = _inputOffset + consumed - _blockSize;
        int maxOutput = MaxBlockOutput(_blockMax);
        int produced = _blocks.Decode(
            block, _window, _position, maxOutput, _windowSize, _frameOutput, DictionaryContent, blockOffset);
        CheckOutputLimit(produced);
        AddOutput(produced);
        EndBlock();
        return true;
    }

    private ReadOnlySpan<byte> DictionaryContent => _dictionary is null ? default : _dictionary.Content.Span;

    private void CheckOutputLimit(int produced)
    {
        if (produced > _options.MaxOutputSize - _totalOutput)
        {
            throw new CompressionLimitException(ZstdFormat.Name, "MaxOutputSize", _options.MaxOutputSize, _totalOutput + produced);
        }
    }

    private void AddOutput(int produced)
    {
        if (_header.HasChecksum && _options.VerifyChecksum)
        {
            _checksum.Append(_window.AsSpan(_position, produced));
        }

        _position += produced;
        _frameOutput += produced;
        _totalOutput += produced;
    }

    private void EndBlock()
    {
        if (!_lastBlock)
        {
            _state = State.BlockHeader;
            return;
        }

        if (_header.HasContentSize && _header.ContentSize != (ulong)_frameOutput)
        {
            ZstdFormat.ThrowCorrupt(_inputOffset, $"a frame that decodes to its declared content size of {_header.ContentSize}, got {_frameOutput}");
        }

        if (_header.HasChecksum)
        {
            _state = State.Checksum;
            return;
        }

        _state = State.FrameStart;
        _frameDone = true;
    }

    // RFC 8878 §3.1.1: the optional Content_Checksum.
    private bool ReadChecksum(ReadOnlySpan<byte> rest, ref int consumed)
    {
        long start = _inputOffset + consumed - _smallLen;
        int take = Math.Min(4 - _smallLen, rest.Length);
        rest[..take].CopyTo(_small.AsSpan(_smallLen));
        _smallLen += take;
        consumed += take;
        if (_smallLen < 4)
        {
            return false;
        }

        _smallLen = 0;
        if (_options.VerifyChecksum)
        {
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(_small);
            uint computed = (uint)_checksum.GetCurrentValue();
            if (stored != computed)
            {
                ZstdFormat.ThrowCorrupt(start, $"a content checksum of 0x{computed:X8}, found 0x{stored:X8}");
            }
        }

        _state = State.FrameStart;
        _frameDone = true;
        return true;
    }

    // Decodes a whole frame straight into the caller's buffer when everything it needs is at hand: the header
    // states the content size, the output fits, and every block is present. No window copy is needed then.
    private bool TryDecodeWholeFrame(ReadOnlySpan<byte> source, Span<byte> destination, long start, out int consumedBytes, out int producedBytes)
    {
        consumedBytes = 0;
        producedBytes = 0;
        if (!ZstdFrameHeader.TryParse(source, out ZstdFrameHeader header, out _, start)
            || header.IsSkippable
            || !header.HasContentSize
            || header.ContentSize > (ulong)destination.Length)
        {
            return false;
        }

        // Is the whole frame in `source`? Walk the block headers.
        int blockMax = (int)Math.Min(header.WindowSize, ZstdFormat.BlockSizeMax);
        int p = header.HeaderSize;
        while (true)
        {
            if (source.Length - p < 3)
            {
                return false;
            }

            uint h = (uint)(source[p] | (source[p + 1] << 8) | (source[p + 2] << 16));
            int type = (int)((h >> 1) & 3);
            int size = (int)(h >> 3);
            if (type == 3 || size > blockMax)
            {
                return false; // the general path reports the error
            }

            p += 3 + (type == 1 ? 1 : size);
            if (p > source.Length)
            {
                return false;
            }

            if ((h & 1) != 0)
            {
                break;
            }
        }

        if (header.HasChecksum)
        {
            p += 4;
            if (p > source.Length)
            {
                return false;
            }
        }

        ZstdDictionary? dictionary = CheckHeader(header);
        BeginFrame(header, dictionary);
        ReadOnlySpan<byte> dictionaryContent = DictionaryContent;
        int size0 = (int)header.ContentSize;
        Span<byte> output = destination[..size0];
        int position = 0;
        int q = header.HeaderSize;
        while (true)
        {
            long blockStart = start + q;
            uint h = (uint)(source[q] | (source[q + 1] << 8) | (source[q + 2] << 16));
            int type = (int)((h >> 1) & 3);
            int size = (int)(h >> 3);
            q += 3;
            int room = size0 - position;
            if (type == 0)
            {
                if (size > room)
                {
                    ZstdFormat.ThrowCorrupt(blockStart, "block output within the frame content size");
                }

                source.Slice(q, size).CopyTo(output[position..]);
                position += size;
                q += size;
            }
            else if (type == 1)
            {
                if (size > room)
                {
                    ZstdFormat.ThrowCorrupt(blockStart, "block output within the frame content size");
                }

                output.Slice(position, size).Fill(source[q]);
                position += size;
                q++;
            }
            else
            {
                position += _blocks.Decode(
                    source.Slice(q, size), output, position, Math.Min(blockMax, room), _windowSize, position, dictionaryContent, start + q);
                q += size;
            }

            if ((h & 1) != 0)
            {
                break;
            }
        }

        if (position != size0)
        {
            ZstdFormat.ThrowCorrupt(start + q, $"a frame that decodes to its declared content size of {size0}, got {position}");
        }

        if (header.HasChecksum && _options.VerifyChecksum)
        {
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(source[q..]);
            uint computed = (uint)XxHash64.Compute(output);
            if (stored != computed)
            {
                ZstdFormat.ThrowCorrupt(start + q, $"a content checksum of 0x{computed:X8}, found 0x{stored:X8}");
            }
        }

        _totalOutput += size0;
        consumedBytes = p;
        producedBytes = size0;
        _state = State.FrameStart;
        return true;
    }
}
