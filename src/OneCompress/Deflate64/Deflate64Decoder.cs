using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Deflate64;

/// <summary>
/// Decompresses one raw Deflate64 stream (zip method 9): the Deflate format of RFC 1951 with a 64 KiB window, length symbol 285 meaning
/// 3 to 65538 bytes, and distance symbols 30 and 31 (docs/deflate64-derivation.md). Feed it any split of the input and output;
/// <see cref="OperationStatus.Done"/> means the final block has ended and all its output has been written.
/// </summary>
/// <remarks>
/// Memory: a 128 KiB window, allocated on first need, and a few hundred bytes of tables. No allocation per call. Errors are
/// <see cref="CompressionFormatException"/> with format "deflate64" and the byte offset in the compressed input; after one the decoder
/// refuses calls until <see cref="Reset"/>.
/// </remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Deflate64Decoder
{
    private const string Name = "deflate64";
    private const int WindowKeep = Deflate64Tables.WindowSize;
    private const int WindowCapacity = 2 * Deflate64Tables.WindowSize;

    private enum State : byte
    {
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
        Finished,
    }

    // The fixed codes of RFC 1951 section 3.2.6 with the Deflate64 meanings of the symbols (distance symbols 30 and 31 are valid
    // in a fixed block: facts F4 and F11). Built once and shared; never modified.
    private static class FixedTables
    {
        public static readonly DeflateHuffmanTable Literal = BuildLiteral();
        public static readonly DeflateHuffmanTable Distance = BuildDistance();

        private static DeflateHuffmanTable BuildLiteral()
        {
            var table = new DeflateHuffmanTable(DeflateAlphabet.LiteralLength, deflate64: true);
            Span<byte> lengths = stackalloc byte[DeflateTables.LiteralLengthSymbolCount];
            DeflateTables.GetFixedLiteralLengthCodeLengths(lengths);
            table.TryBuild(lengths, out _);
            return table;
        }

        private static DeflateHuffmanTable BuildDistance()
        {
            var table = new DeflateHuffmanTable(DeflateAlphabet.Distance, deflate64: true);
            Span<byte> lengths = stackalloc byte[Deflate64Tables.DistanceSymbolCount];
            DeflateTables.GetFixedDistanceCodeLengths(lengths);
            table.TryBuild(lengths, out _);
            return table;
        }
    }

    private readonly long _maxOutput;
    private State _state;
    private bool _faulted;
    private ulong _bits;
    private int _bitCount;
    private long _totalIn;
    private long _totalOut;
    private byte[]? _window;
    private int _windowLen;
    private bool _final;
    private DeflateHuffmanTable? _literalTable;
    private DeflateHuffmanTable? _distanceTable;
    private DeflateHuffmanTable? _codeLengthTable;
    private DeflateHuffmanTable? _dynamicLiteralTable;
    private DeflateHuffmanTable? _dynamicDistanceTable;
    private byte[]? _lengths;
    private byte[]? _codeLengthLengths;
    private int _storedRemaining;
    private int _hlit;
    private int _hdist;
    private int _hclen;
    private int _index;
    private int _repeatSymbol;
    private int _copyLength;
    private int _copyDistance;
    private int _extraBits;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">The limits; <see langword="null"/> for <see cref="Deflate64DecompressionOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is negative.</exception>
    public Deflate64Decoder(Deflate64DecompressionOptions? options = null)
    {
        options ??= Deflate64DecompressionOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxOutputSize, nameof(options));
        _maxOutput = options.MaxOutputSize;
    }

    /// <summary>Gets whether the final block has been decoded and written.</summary>
    public bool IsFinished => _state == State.Finished && !_faulted;

    /// <summary>Gets the number of input bytes consumed since construction or <see cref="Reset"/>.</summary>
    public long TotalConsumed => _totalIn;

    /// <summary>Gets the number of output bytes written since construction or <see cref="Reset"/>.</summary>
    public long TotalWritten => _totalOut;

    /// <summary>Returns the decoder to its initial state so it can decode another stream. The window is forgotten.</summary>
    public void Reset()
    {
        _state = State.BlockHeader;
        _faulted = false;
        _bits = 0;
        _bitCount = 0;
        _totalIn = 0;
        _totalOut = 0;
        _windowLen = 0;
        _final = false;
    }

    /// <summary>Throws if the input ended before the final block did.</summary>
    /// <exception cref="CompressionFormatException">The stream is incomplete (<see cref="CompressionFormatException.IsTruncated"/> is set).</exception>
    public void ThrowIfIncomplete()
    {
        if (!IsFinished)
        {
            throw new CompressionFormatException(Name, _totalIn, "the rest of the " + Name + " stream", isTruncated: true);
        }
    }

    /// <summary>Decodes as much as fits.</summary>
    /// <param name="source">Compressed bytes, any amount.</param>
    /// <param name="destination">Where decompressed bytes go.</param>
    /// <param name="consumed">The number of input bytes used. A call that returns <see cref="OperationStatus.NeedMoreData"/> has used all of its input (bits are kept inside the decoder).</param>
    /// <param name="written">The number of output bytes written.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when the stream is complete (bytes after it are not consumed);
    /// <see cref="OperationStatus.DestinationTooSmall"/> when more output is waiting; <see cref="OperationStatus.NeedMoreData"/> when the input ran out.
    /// </returns>
    /// <exception cref="CompressionFormatException">The data is corrupt: a forbidden block type, a bad stored length, an invalid Huffman code, a symbol that is not allowed, or a match that reaches before the start of the output.</exception>
    /// <exception cref="CompressionLimitException">The output would pass <see cref="Deflate64DecompressionOptions.MaxOutputSize"/>; nothing is written past the limit.</exception>
    /// <exception cref="InvalidOperationException">The decoder failed earlier and has not been reset.</exception>
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
        OperationStatus status;
        try
        {
            status = Run(ref br, dst, ref dp);
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
            throw new CompressionLimitException(Name, nameof(Deflate64DecompressionOptions.MaxOutputSize), _maxOutput, _totalOut + 1);
        }

        if (status != OperationStatus.Done)
        {
            AppendWindow(dst[..dp]);
        }

        return status;
    }

    private CompressionFormatException Bad(in DeflateBitReader br, string expected) => new(Name, _totalIn + br.Position, expected);

    // Keeps this call's output as history for the next one: up to 64 KiB, moved once per 64 KiB written.
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

    // Copies n bytes of a match of the given distance to dst[dp..]. History before this call's output comes from the window.
    private void CopyMatch(Span<byte> dst, int dp, int distance, int n)
    {
        if (n == 0)
        {
            return;
        }

        if (distance <= dp)
        {
            int s = dp - distance;
            if (distance >= n)
            {
                dst.Slice(s, n).CopyTo(dst[dp..]);
            }
            else if (distance == 1)
            {
                dst.Slice(dp, n).Fill(dst[s]);
            }
            else
            {
                // Overlapping copy (RFC 1951 section 3.2.3): the pattern of `distance` bytes repeats; copy it in doubling pieces.
                int done = 0;
                while (done < n)
                {
                    int piece = Math.Min(distance + done, n - done);
                    dst.Slice(s, piece).CopyTo(dst[(dp + done)..]);
                    done += piece;
                }
            }

            return;
        }

        // The match begins before this call's output, in the window, and may run on into it.
        int fromWindow = distance - dp;
        int m = Math.Min(n, fromWindow);
        _window.AsSpan(_windowLen - fromWindow, m).CopyTo(dst[dp..]);
        for (int i = m; i < n; i++)
        {
            dst[dp + i] = dst[i - fromWindow];
        }
    }

    private bool EndBlock(ref DeflateBitReader br)
    {
        if (_final)
        {
            // The bits up to the byte boundary after the final block are padding (RFC 1951 section 3.2.3).
            br.AlignToByte();
            _state = State.Finished;
            return true;
        }

        _state = State.BlockHeader;
        return false;
    }

    private void BuildDynamicTables(in DeflateBitReader br)
    {
        byte[] lengths = _lengths!;
        if (lengths[DeflateTables.EndOfBlockSymbol] == 0)
        {
            throw Bad(br, "a code for the end-of-block symbol (256) in the literal/length code");
        }

        _dynamicLiteralTable ??= new DeflateHuffmanTable(DeflateAlphabet.LiteralLength, deflate64: true);
        _dynamicDistanceTable ??= new DeflateHuffmanTable(DeflateAlphabet.Distance, deflate64: true);
        if (!_dynamicLiteralTable.TryBuild(lengths.AsSpan(0, _hlit), out string? problem))
        {
            throw Bad(br, "the literal/length code to be " + problem);
        }

        if (!_dynamicDistanceTable.TryBuild(lengths.AsSpan(_hlit, _hdist), out problem))
        {
            throw Bad(br, "the distance code to be " + problem);
        }

        _literalTable = _dynamicLiteralTable;
        _distanceTable = _dynamicDistanceTable;
    }

    private OperationStatus Run(ref DeflateBitReader br, Span<byte> dst, ref int dp)
    {
        while (true)
        {
            switch (_state)
            {
                case State.Finished:
                    return OperationStatus.Done;

                case State.BlockHeader:
                {
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
                    // The bit buffer holds no whole byte here: the decoder only ever loads the bytes a read needs, and LEN and NLEN
                    // were taken at a byte boundary.
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
                    if (!br.TryReadBits(14, out uint counts))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    // Facts F9: 7-Zip accepts every value the fields can hold, so symbols 30 and 31 can be coded.
                    _hlit = (int)(counts & 31) + 257;
                    _hdist = (int)((counts >> 5) & 31) + 1;
                    _hclen = (int)(counts >> 10) + 4;
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

                    _lengths ??= new byte[Deflate64Tables.LiteralLengthSymbolCount + Deflate64Tables.DistanceSymbolCount];
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
                    if (br.Remaining >= Deflate64FastLoop.InputMargin && dst.Length - dp >= Deflate64FastLoop.OutputMargin)
                    {
                        FastResult result = Deflate64FastLoop.Run(ref br, dst, ref dp, _literalTable!, _distanceTable!, out int matchLength, out int matchDistance);
                        switch (result)
                        {
                            case FastResult.HistoryMatch:
                                // A match that reaches into the window or is longer than the loop's output margin: the copy state does it.
                                if (matchDistance > _windowLen + dp)
                                {
                                    throw Bad(br, "a match distance within the output so far");
                                }

                                _copyLength = matchLength;
                                _copyDistance = matchDistance;
                                _state = State.Copy;
                                continue;
                            case FastResult.EndOfBlock:
                                if (EndBlock(ref br))
                                {
                                    return OperationStatus.Done;
                                }

                                continue;
                            case FastResult.InvalidLiteralLength:
                                throw Bad(br, "a valid literal/length code (symbols 286 and 287 are not allowed)");
                            case FastResult.InvalidDistance:
                                throw Bad(br, "a valid distance code");
                        }
                    }

                    if (!br.TryPeekSymbol(_literalTable!, out DeflateHuffmanEntry e))
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
                            throw Bad(br, "a valid literal/length code (symbols 286 and 287 are not allowed)");
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

                default:
                    throw new InvalidOperationException();
            }
        }
    }
}
