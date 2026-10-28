using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// A resumable decoder for raw Deflate (RFC 1951), zlib (RFC 1950) and gzip (RFC 1952). It follows the shape in
/// <c>docs/conventions.md</c>: input and output may be split anywhere, malformed input throws, and a finished unit
/// (one Deflate stream, one zlib stream or one gzip member) returns <see cref="OperationStatus.Done"/>.
/// </summary>
/// <remarks>
/// <para>The decoder writes straight into the caller's buffer and keeps the last 32 KiB of output in a window of its
/// own, so a call may start with matches that reach into earlier calls' output. It takes every input byte it is
/// given into its own state before it asks for more (a call that returns <see cref="OperationStatus.NeedMoreData"/>
/// has consumed all its input), so callers never have to keep input around.</para>
/// <para>After <see cref="OperationStatus.Done"/> the next call starts a new unit: for gzip the next member, for raw
/// Deflate and zlib the next stream. Concatenated gzip members are therefore decoded by calling again with the bytes
/// not consumed. A decoder is not thread-safe. Memory is allocated on first need (tables, window) and reused afterwards.</para>
/// </remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed partial class DeflateDecoder
{
    private const int WindowKeep = DeflateTables.WindowSize;
    private const int WindowCapacity = 2 * DeflateTables.WindowSize;

    private enum State : byte
    {
        Start,
        ZHeader,
        ZDictId,
        GFixed,
        GExtraLength,
        GExtra,
        GName,
        GComment,
        GHeaderCrc,
        BlockHeader,
        StoredLength,
        StoredCopy,
        DynCounts,
        DynCodeLengths,
        DynSymbols,
        DynRepeat,
        Symbol,
        LengthExtra,
        DistanceSymbol,
        DistanceExtra,
        Copy,
        TrailerZ,
        TrailerGCrc,
        TrailerGLength,
    }

    private readonly DeflateFormat _format;
    private readonly string _name;
    private readonly long _maxOutput;
    private readonly bool _verify;
    private readonly ReadOnlyMemory<byte> _dictionary;
    private readonly int _maxField;

    private State _state;
    private bool _faulted;
    private ulong _bits;
    private int _bitCount;
    private long _totalIn;
    private long _totalOut;

    private byte[]? _window;
    private int _windowLen;
    private uint _check;
    private uint _checkedOutputLength;
    private int _ckPos;
    private bool _final;

    // Block state.
    private DeflateHuffmanTable? _literalTable;
    private DeflateHuffmanTable? _distanceTable;
    private DeflateHuffmanTable? _codeLengthTable;
    private DeflateHuffmanTable? _dynamicLiteralTable;
    private DeflateHuffmanTable? _dynamicDistanceTable;
    private int _storedRemaining;
    private int _hlit;
    private int _hdist;
    private int _hclen;
    private int _index;
    private int _repeatSymbol;
    private byte[]? _lengths;
    private byte[]? _codeLengthLengths;
    private int _copyLength;
    private int _copyDistance;
    private int _extraBits;

    // gzip member header.
    private byte[]? _gzipFixed;
    private byte[]? _extra;
    private byte[]? _nameBytes;
    private byte[]? _commentBytes;
    private byte[]? _scratch;
    private int _gzipFixedCount;
    private int _extraLength;
    private int _extraWanted;
    private int _nameLength;
    private int _commentLength;
    private uint _headerCrc;
    private byte _gzipFlags;
    private uint _storedCrc;

    /// <summary>Creates a decoder.</summary>
    /// <param name="format">The wrapper to expect.</param>
    /// <param name="options">Limits and the preset dictionary; <see langword="null"/> for <see cref="DeflateDecompressionOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined value, or an option is out of range.</exception>
    public DeflateDecoder(DeflateFormat format, DeflateDecompressionOptions? options = null)
    {
        options ??= DeflateDecompressionOptions.Default;
        if (format is not (DeflateFormat.Raw or DeflateFormat.ZLib or DeflateFormat.GZip))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxOutputSize, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxHeaderFieldSize, nameof(options));
        _format = format;
        _name = format switch { DeflateFormat.Raw => "deflate", DeflateFormat.ZLib => "zlib", _ => "gzip" };
        _maxOutput = options.MaxOutputSize;
        _verify = options.VerifyChecksum;
        _dictionary = options.Dictionary;
        _maxField = options.MaxHeaderFieldSize;
    }

    /// <summary>Gets the wrapper this decoder expects.</summary>
    public DeflateFormat Format => _format;

    /// <summary>Gets a value indicating whether the decoder is between units: nothing of a unit has been consumed, so it is a valid place to end the input.</summary>
    public bool IsAtFrameBoundary => _state == State.Start && _bitCount == 0 && !_faulted;

    /// <summary>Gets the total number of compressed bytes consumed since construction or the last <see cref="Reset"/>.</summary>
    public long TotalConsumed => _totalIn;

    /// <summary>Gets the total number of bytes written since construction or the last <see cref="Reset"/>.</summary>
    public long TotalWritten => _totalOut;

    /// <summary>
    /// Gets the header of the gzip member being decoded, or of the last one decoded. It is valid once the header has been
    /// consumed, until the decoder starts the next member; before that, and for other formats, it is the default value.
    /// </summary>
    public GZipHeader CurrentGZipHeader { get; private set; }

    /// <summary>Forgets everything, including a fault from an earlier exception, and starts at a unit boundary. The total counters restart at 0.</summary>
    public void Reset()
    {
        _state = State.Start;
        _faulted = false;
        _bits = 0;
        _bitCount = 0;
        _totalIn = 0;
        _totalOut = 0;
        _windowLen = 0;
        _final = false;
        CurrentGZipHeader = default;
    }

    /// <summary>Throws when the input ended inside a unit. Call it when no more input will come.</summary>
    /// <exception cref="CompressionFormatException">The decoder is inside a unit (<see cref="CompressionFormatException.IsTruncated"/> is set).</exception>
    public void ThrowIfIncomplete()
    {
        if (!IsAtFrameBoundary)
        {
            throw new CompressionFormatException(_name, _totalIn, _state < State.BlockHeader ? "a complete " + _name + " header" : "the rest of the " + _name + " stream", isTruncated: true);
        }
    }

    /// <summary>
    /// Decodes as much as the buffers allow.
    /// </summary>
    /// <param name="source">Compressed bytes: the ones not consumed by earlier calls plus any new ones (a call that returned <see cref="OperationStatus.NeedMoreData"/> consumed everything).</param>
    /// <param name="destination">Receives decoded bytes.</param>
    /// <param name="consumed">The number of bytes of <paramref name="source"/> used; always set, also when the call throws.</param>
    /// <param name="written">The number of bytes written to <paramref name="destination"/>; always set.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when a unit is complete and fully written (the next call starts a new unit);
    /// <see cref="OperationStatus.DestinationTooSmall"/> when more output is waiting; <see cref="OperationStatus.NeedMoreData"/>
    /// when all input is used and the unit is incomplete. It never returns <see cref="OperationStatus.InvalidData"/>: bad input throws.
    /// </returns>
    /// <exception cref="CompressionFormatException">The data is malformed, or a checksum does not match. After this the decoder refuses calls until <see cref="Reset"/>.</exception>
    /// <exception cref="CompressionLimitException">The output would pass <see cref="DeflateDecompressionOptions.MaxOutputSize"/>, or a gzip header field is too long.</exception>
    /// <exception cref="CompressionDictionaryException">A zlib stream needs a dictionary that was not supplied or does not match.</exception>
    /// <exception cref="InvalidOperationException">An earlier call threw and <see cref="Reset"/> was not called.</exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The decoder failed earlier; call Reset() before using it again.");
        }

        consumed = 0;
        written = 0;
        long allowed = _maxOutput - _totalOut;
        bool limited = destination.Length > allowed;
        Span<byte> dst = limited ? destination[..(int)Math.Max(0, allowed)] : destination;
        var br = new DeflateBitReader(source, _bits, _bitCount);
        int dp = 0;
        _ckPos = 0;
        OperationStatus status;
        try
        {
            status = Run(ref br, dst, ref dp);
            UpdateChecksum(dst, dp);
        }
        catch
        {
            consumed = br.Position;
            written = dp;
            _faulted = true;
            throw;
        }

        _bits = br.BitBuffer;
        _bitCount = br.BitCount;
        consumed = br.Position;
        written = dp;
        _totalIn += consumed;
        _totalOut += dp;
        if (status == OperationStatus.DestinationTooSmall && limited)
        {
            _faulted = true;
            throw new CompressionLimitException(_name, nameof(DeflateDecompressionOptions.MaxOutputSize), _maxOutput, _totalOut + 1);
        }

        if (status != OperationStatus.Done)
        {
            AppendWindow(dst[..dp]);
        }

        return status;
    }

    private CompressionFormatException Bad(in DeflateBitReader br, string expected) => new(_name, _totalIn + br.Position, expected);

    private void UpdateChecksum(ReadOnlySpan<byte> dst, int dp)
    {
        if (dp > _ckPos)
        {
            ReadOnlySpan<byte> data = dst[_ckPos..dp];
            if (_format == DeflateFormat.ZLib)
            {
                _check = Adler32.Update(_check, data);
            }
            else if (_format == DeflateFormat.GZip)
            {
                _check = Crc32.Update(_check, data);
                _checkedOutputLength += (uint)data.Length;
            }

            _ckPos = dp;
        }
    }

    // Keeps the output of this call as history for the next one. Up to 64 KiB are held so the move happens once per 32 KiB.
    private void AppendWindow(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        byte[] window = _window ??= new byte[WindowCapacity];
        if (data.Length >= WindowKeep)
        {
            data[^WindowKeep..].CopyTo(window);
            _windowLen = WindowKeep;
        }
        else if (_windowLen + data.Length <= WindowCapacity)
        {
            data.CopyTo(window.AsSpan(_windowLen));
            _windowLen += data.Length;
        }
        else
        {
            int keep = WindowKeep - data.Length;
            window.AsSpan(_windowLen - keep, keep).CopyTo(window);
            data.CopyTo(window.AsSpan(keep));
            _windowLen = WindowKeep;
        }
    }

    private void LoadDictionary()
    {
        _windowLen = 0;
        ReadOnlySpan<byte> dictionary = _dictionary.Span;
        if (!dictionary.IsEmpty)
        {
            AppendWindow(dictionary);
        }
    }

    private void BeginUnit()
    {
        _windowLen = 0;
        _final = false;
        _check = _format == DeflateFormat.ZLib ? 1u : 0u;
        _checkedOutputLength = 0;
        _ckPos = 0;
        if (_format == DeflateFormat.Raw)
        {
            LoadDictionary();
        }
    }

    private OperationStatus Run(ref DeflateBitReader br, Span<byte> dst, ref int dp)
    {
        while (true)
        {
            switch (_state)
            {
                case State.Start:
                    if (br.Remaining == 0 && br.BitCount == 0)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    BeginUnit();
                    CurrentGZipHeader = default;
                    _gzipFixedCount = 0;
                    _state = _format switch { DeflateFormat.Raw => State.BlockHeader, DeflateFormat.ZLib => State.ZHeader, _ => State.GFixed };
                    break;

                case State.ZHeader:
                {
                    // RFC 1950 section 2.2: CMF, FLG.
                    if (!br.TryReadBits(16, out uint header))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    uint cmf = header & 0xFF;
                    uint flg = header >> 8;
                    if ((cmf & 15) != 8)
                    {
                        throw Bad(br, "compression method 8 (deflate) in the zlib header");
                    }

                    if ((cmf >> 4) > 7)
                    {
                        throw Bad(br, "a window size of at most 32768 bytes in the zlib header");
                    }

                    if (((cmf << 8) | flg) % 31 != 0)
                    {
                        throw Bad(br, "a zlib header whose check bits make it a multiple of 31");
                    }

                    _state = (flg & 0x20) != 0 ? State.ZDictId : State.BlockHeader;
                    break;
                }

                case State.ZDictId:
                {
                    if (!br.TryReadBits(32, out uint raw))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    uint id = BinaryPrimitives.ReverseEndianness(raw);
                    if (_dictionary.IsEmpty)
                    {
                        throw new CompressionDictionaryException(_name, id, $"zlib: the stream needs the preset dictionary with Adler-32 {id:X8}, and none was supplied");
                    }

                    if (Adler32.Compute(_dictionary.Span) != id)
                    {
                        throw new CompressionDictionaryException(_name, id, $"zlib: the stream needs the preset dictionary with Adler-32 {id:X8}, which does not match the supplied dictionary");
                    }

                    LoadDictionary();
                    _state = State.BlockHeader;
                    break;
                }

                case State.GFixed:
                {
                    // RFC 1952 section 2.3: ID1 ID2 CM FLG MTIME(4) XFL OS.
                    byte[] fixedPart = _gzipFixed ??= new byte[10];
                    while (_gzipFixedCount < 10)
                    {
                        if (!br.TryReadBits(8, out uint b))
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        fixedPart[_gzipFixedCount] = (byte)b;
                        if (_gzipFixedCount == 1 && (fixedPart[0] != 0x1F || fixedPart[1] != 0x8B))
                        {
                            throw Bad(br, "the gzip magic number 1F 8B");
                        }

                        _gzipFixedCount++;
                    }

                    if (fixedPart[2] != 8)
                    {
                        throw Bad(br, "compression method 8 (deflate) in the gzip header");
                    }

                    _gzipFlags = fixedPart[3];
                    if ((_gzipFlags & 0xE0) != 0)
                    {
                        throw Bad(br, "reserved gzip header flag bits to be zero");
                    }

                    _headerCrc = Crc32.Compute(fixedPart);
                    _extraLength = 0;
                    _nameLength = 0;
                    _commentLength = 0;
                    _state = State.GExtraLength;
                    goto case State.GExtraLength;
                }

                case State.GExtraLength:
                {
                    if ((_gzipFlags & 4) == 0)
                    {
                        _state = State.GName;
                        break;
                    }

                    if (!br.TryReadBits(16, out uint length))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    byte[] scratch = _scratch ??= new byte[4];
                    BinaryPrimitives.WriteUInt16LittleEndian(scratch, (ushort)length);
                    _headerCrc = Crc32.Update(_headerCrc, scratch.AsSpan(0, 2));
                    if (length > _maxField)
                    {
                        throw new CompressionLimitException(_name, nameof(DeflateDecompressionOptions.MaxHeaderFieldSize), _maxField, length);
                    }

                    _extraWanted = (int)length;
                    _extra ??= [];
                    if (_extra.Length < _extraWanted)
                    {
                        _extra = new byte[_extraWanted];
                    }

                    _state = State.GExtra;
                    break;
                }

                case State.GExtra:
                {
                    int n = Math.Min(_extraWanted - _extraLength, br.Remaining);
                    br.Source.Slice(br.Position, n).CopyTo(_extra!.AsSpan(_extraLength));
                    _headerCrc = Crc32.Update(_headerCrc, br.Source.Slice(br.Position, n));
                    br.Set(br.Position + n, br.BitBuffer, br.BitCount);
                    _extraLength += n;
                    if (_extraLength < _extraWanted)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _state = State.GName;
                    break;
                }

                case State.GName:
                    if ((_gzipFlags & 8) != 0)
                    {
                        if (!ReadZeroTerminated(ref br, ref _nameBytes, ref _nameLength))
                        {
                            return OperationStatus.NeedMoreData;
                        }
                    }

                    _state = State.GComment;
                    break;

                case State.GComment:
                    if ((_gzipFlags & 16) != 0)
                    {
                        if (!ReadZeroTerminated(ref br, ref _commentBytes, ref _commentLength))
                        {
                            return OperationStatus.NeedMoreData;
                        }
                    }

                    _state = State.GHeaderCrc;
                    break;

                case State.GHeaderCrc:
                {
                    if ((_gzipFlags & 2) != 0)
                    {
                        if (!br.TryReadBits(16, out uint crc16))
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        if (_verify && crc16 != (_headerCrc & 0xFFFF))
                        {
                            throw Bad(br, $"the gzip header CRC-16 to be {_headerCrc & 0xFFFF:X4}, found {crc16:X4}");
                        }
                    }

                    byte[] h = _gzipFixed!;
                    CurrentGZipHeader = new GZipHeader(
                        _gzipFlags,
                        BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)),
                        h[8],
                        h[9],
                        (_gzipFlags & 4) != 0 ? _extra.AsMemory(0, _extraLength) : default,
                        (_gzipFlags & 8) != 0 ? _nameBytes.AsMemory(0, _nameLength) : default,
                        (_gzipFlags & 16) != 0 ? _commentBytes.AsMemory(0, _commentLength) : default);
                    _state = State.BlockHeader;
                    break;
                }

                case State.BlockHeader:
                {
                    // RFC 1951 section 3.2.3: BFINAL, BTYPE.
                    if (!br.TryReadBits(3, out uint header))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _final = (header & 1) != 0;
                    switch (header >> 1)
                    {
                        case 0:
                            br.AlignToByte();
                            _state = State.StoredLength;
                            break;
                        case 1:
                            _literalTable = FixedTables.Literal;
                            _distanceTable = FixedTables.Distance;
                            _state = State.Symbol;
                            break;
                        case 2:
                            _state = State.DynCounts;
                            break;
                        default:
                            throw Bad(br, "a block type of 0, 1 or 2 (3 is reserved)");
                    }

                    break;
                }

                case State.StoredLength:
                {
                    // RFC 1951 section 3.2.4: LEN and its one's complement NLEN.
                    if (!br.TryReadBits(32, out uint lengths))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    if ((lengths & 0xFFFF) != (~lengths >> 16 & 0xFFFF))
                    {
                        throw Bad(br, "NLEN of a stored block to be the one's complement of LEN");
                    }

                    _storedRemaining = (int)(lengths & 0xFFFF);
                    _state = State.StoredCopy;
                    break;
                }

                case State.StoredCopy:
                {
                    int n = Math.Min(_storedRemaining, Math.Min(br.Remaining, dst.Length - dp));
                    br.Source.Slice(br.Position, n).CopyTo(dst[dp..]);
                    br.Set(br.Position + n, br.BitBuffer, br.BitCount);
                    dp += n;
                    _storedRemaining -= n;
                    if (_storedRemaining > 0)
                    {
                        return dp == dst.Length ? OperationStatus.DestinationTooSmall : OperationStatus.NeedMoreData;
                    }

                    if (EndBlock(ref br))
                    {
                        return OperationStatus.Done;
                    }

                    break;
                }

                case State.DynCounts:
                {
                    // RFC 1951 section 3.2.7: HLIT, HDIST, HCLEN.
                    if (!br.TryReadBits(14, out uint counts))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _hlit = (int)(counts & 31) + 257;
                    _hdist = (int)((counts >> 5) & 31) + 1;
                    _hclen = (int)(counts >> 10) + 4;
                    if (_hlit > 286)
                    {
                        throw Bad(br, "at most 286 literal/length codes (HLIT at most 29)");
                    }

                    if (_hdist > 30)
                    {
                        throw Bad(br, "at most 30 distance codes (HDIST at most 29)");
                    }

                    _codeLengthLengths ??= new byte[DeflateTables.CodeLengthSymbolCount];
                    Array.Clear(_codeLengthLengths);
                    _index = 0;
                    _state = State.DynCodeLengths;
                    break;
                }

                case State.DynCodeLengths:
                {
                    while (_index < _hclen)
                    {
                        if (!br.TryReadBits(3, out uint v))
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        _codeLengthLengths![DeflateTables.CodeLengthOrder[_index++]] = (byte)v;
                    }

                    _codeLengthTable ??= new DeflateHuffmanTable(DeflateAlphabet.CodeLength);
                    if (!_codeLengthTable.TryBuild(_codeLengthLengths!, out string? problem))
                    {
                        throw Bad(br, "the code-length code to be " + problem);
                    }

                    _lengths ??= new byte[286 + 30];
                    _index = 0;
                    _state = State.DynSymbols;
                    break;
                }

                case State.DynSymbols:
                {
                    int total = _hlit + _hdist;
                    while (_index < total)
                    {
                        if (!br.TryPeekSymbol(_codeLengthTable!, out DeflateHuffmanEntry e))
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        if (e.Kind != DeflateSymbolKind.CodeLengthSymbol)
                        {
                            throw Bad(br, "a valid code-length code");
                        }

                        br.DropBits(e.CodeLength);
                        int symbol = e.Value;
                        if (symbol < 16)
                        {
                            _lengths![_index++] = (byte)symbol;
                        }
                        else
                        {
                            _repeatSymbol = symbol;
                            _state = State.DynRepeat;
                            break;
                        }
                    }

                    if (_state == State.DynSymbols)
                    {
                        BuildDynamicTables(in br);
                        _state = State.Symbol;
                    }

                    break;
                }

                case State.DynRepeat:
                {
                    // RFC 1951 section 3.2.7: 16 repeats the previous length 3-6 times, 17 and 18 repeat zero 3-10 and 11-138 times.
                    int extraBits = _repeatSymbol == 16 ? 2 : _repeatSymbol == 17 ? 3 : 7;
                    if (!br.TryReadBits(extraBits, out uint extra))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    int count = (int)extra + (_repeatSymbol == 18 ? 11 : 3);
                    byte value = 0;
                    if (_repeatSymbol == 16)
                    {
                        if (_index == 0)
                        {
                            throw Bad(br, "a code length before a repeat-previous code");
                        }

                        value = _lengths![_index - 1];
                    }

                    if (_index + count > _hlit + _hdist)
                    {
                        throw Bad(br, "a repeat that stays within the HLIT + HDIST code lengths");
                    }

                    _lengths.AsSpan(_index, count).Fill(value);
                    _index += count;
                    _state = State.DynSymbols;
                    break;
                }

                case State.Symbol:
                {
                    DeflateHuffmanTable literal = _literalTable!;
                    if (br.Remaining >= DeflateFastLoop.InputMargin && dst.Length - dp >= DeflateFastLoop.OutputMargin)
                    {
                        ReadOnlySpan<byte> history = _window is null ? default : _window.AsSpan(0, _windowLen);
                        FastResult result = DeflateFastLoop.Run(ref br, dst, ref dp, literal, _distanceTable!, out int matchLength, out int matchDistance);
                        switch (result)
                        {
                            case FastResult.HistoryMatch:
                                if (!DeflateFastLoop.CopyFromHistory(dst, dp, matchLength, matchDistance, history))
                                {
                                    throw Bad(br, "a match distance within the output so far");
                                }

                                dp += matchLength;
                                continue;
                            case FastResult.EndOfBlock:
                                if (EndBlock(ref br))
                                {
                                    return OperationStatus.Done;
                                }

                                continue;
                            case FastResult.InvalidLiteralLength:
                                throw Bad(br, "a valid literal/length code");
                            case FastResult.InvalidDistance:
                                throw Bad(br, "a valid distance code");
                            case FastResult.DistanceTooFar:
                                throw Bad(br, "a match distance within the output so far");
                        }
                    }

                    if (!br.TryPeekSymbol(literal, out DeflateHuffmanEntry e))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    switch (e.Kind)
                    {
                        case DeflateSymbolKind.Literal:
                            if (dp == dst.Length)
                            {
                                return OperationStatus.DestinationTooSmall;
                            }

                            br.DropBits(e.CodeLength);
                            dst[dp++] = (byte)e.Value;
                            break;
                        case DeflateSymbolKind.EndOfBlock:
                            br.DropBits(e.CodeLength);
                            if (EndBlock(ref br))
                            {
                                return OperationStatus.Done;
                            }

                            break;
                        case DeflateSymbolKind.Length:
                            br.DropBits(e.CodeLength);
                            _copyLength = e.Value;
                            _extraBits = e.ExtraBits;
                            _state = State.LengthExtra;
                            break;
                        default:
                            throw Bad(br, "a valid literal/length code");
                    }

                    break;
                }

                case State.LengthExtra:
                {
                    if (!br.TryReadBits(_extraBits, out uint extra))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _copyLength += (int)extra;
                    _state = State.DistanceSymbol;
                    break;
                }

                case State.DistanceSymbol:
                {
                    if (!br.TryPeekSymbol(_distanceTable!, out DeflateHuffmanEntry e))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    if (e.Kind != DeflateSymbolKind.Distance)
                    {
                        throw Bad(br, "a valid distance code");
                    }

                    br.DropBits(e.CodeLength);
                    _copyDistance = e.Value;
                    _extraBits = e.ExtraBits;
                    _state = State.DistanceExtra;
                    break;
                }

                case State.DistanceExtra:
                {
                    if (!br.TryReadBits(_extraBits, out uint extra))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _copyDistance += (int)extra;
                    if (_copyDistance > _windowLen + dp)
                    {
                        throw Bad(br, "a match distance within the output so far");
                    }

                    _state = State.Copy;
                    break;
                }

                case State.Copy:
                {
                    int n = Math.Min(_copyLength, dst.Length - dp);
                    CopyMatch(dst, dp, _copyDistance, n);
                    dp += n;
                    _copyLength -= n;
                    if (_copyLength > 0)
                    {
                        return OperationStatus.DestinationTooSmall;
                    }

                    _state = State.Symbol;
                    break;
                }

                case State.TrailerZ:
                {
                    // RFC 1950 section 2.2: ADLER32 of the uncompressed data, most significant byte first.
                    if (!br.TryReadBits(32, out uint raw))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    UpdateChecksum(dst, dp);
                    uint stored = BinaryPrimitives.ReverseEndianness(raw);
                    if (_verify && stored != _check)
                    {
                        throw Bad(br, $"the zlib Adler-32 to be {_check:X8}, found {stored:X8}");
                    }

                    _state = State.Start;
                    return OperationStatus.Done;
                }

                case State.TrailerGCrc:
                    // RFC 1952 section 2.3: CRC32 then ISIZE, each least significant byte first.
                    if (!br.TryReadBits(32, out _storedCrc))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _state = State.TrailerGLength;
                    break;

                case State.TrailerGLength:
                {
                    if (!br.TryReadBits(32, out uint isize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    UpdateChecksum(dst, dp);
                    if (_verify)
                    {
                        if (_storedCrc != _check)
                        {
                            throw Bad(br, $"the gzip CRC-32 to be {_check:X8}, found {_storedCrc:X8}");
                        }

                        if (isize != _checkedOutputLength)
                        {
                            throw Bad(br, $"the gzip ISIZE to be {_checkedOutputLength}, found {isize}");
                        }
                    }

                    _state = State.Start;
                    return OperationStatus.Done;
                }

                default:
                    throw new InvalidOperationException("Unreachable decoder state.");
            }
        }
    }

    // Returns true when the unit is complete (raw Deflate, which has no trailer).
    private bool EndBlock(ref DeflateBitReader br)
    {
        if (!_final)
        {
            _state = State.BlockHeader;
            return false;
        }

        // The bits left in the last byte are padding (RFC 1951 section 3.3); the buffer then holds no whole byte.
        br.AlignToByte();
        switch (_format)
        {
            case DeflateFormat.Raw:
                _state = State.Start;
                return true;
            case DeflateFormat.ZLib:
                _state = State.TrailerZ;
                return false;
            default:
                _state = State.TrailerGCrc;
                return false;
        }
    }
}
