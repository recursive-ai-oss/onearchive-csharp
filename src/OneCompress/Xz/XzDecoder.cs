using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Xz;

/// <summary>
/// Decodes the <c>.xz</c> container: streams made of blocks, each block a filter chain (branch and delta filters, then LZMA2), a check, and
/// an index that is verified against the blocks. Several streams one after the other, with zero padding between them, are decoded in a row.
/// </summary>
/// <remarks>
/// <para>Format: the xz file format specification. Everything it says a decoder MUST verify is verified: header magic and CRCs, reserved
/// bits and padding, the stream flags in header and footer, sizes the block header declares, the check of each block, and the index (record count,
/// sizes, padding, CRC) against the real blocks. Unsupported filters, unsupported checks (see <see cref="XzDecompressionOptions.AllowUnsupportedChecks"/>) and
/// dictionaries over <see cref="XzDecompressionOptions.MaxDictionarySize"/> raise errors before any work is done for them.</para>
/// <para><see cref="Decompress"/> returns <see cref="OperationStatus.Done"/> after each stream; call it again for the next one.</para>
/// </remarks>
[Experimental("ONECOMPRESS006", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class XzDecoder : IStepDecoder
{
    private const string Format = "xz";
    private const int StreamHeaderSize = 12;
    private const int ChainBufferSize = 1 << 16;
    private const int MaxFilters = 4;
    private const byte Lzma2Id = 0x21;

    private static ReadOnlySpan<byte> HeaderMagic => [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00];

    private enum Phase
    {
        Padding,
        StreamHeader,
        BlockFirst,
        BlockHeader,
        BlockData,
        BlockPadding,
        BlockCheck,
        IndexCount,
        IndexUnpadded,
        IndexUncompressed,
        IndexPadding,
        IndexCrc,
        Footer,
    }

    private readonly XzDecompressionOptions _options;
    private readonly LzmaDecompressionOptions _innerOptions;
    private readonly FieldStage _stage = new();
    private Lzma2Decoder? _lzma2;
    private Phase _phase = Phase.StreamHeader;
    private bool _faulted;
    private bool _trailing;
    private long _inOffset;
    private long _totalOut;
    private long _streams;
    private long _padding;

    // Stream.
    private byte _flags;
    private int _checkId;
    private int _checkSize;

    // Block.
    private int _headerSize;
    private long _declaredCompressed;
    private long _declaredUncompressed;
    private long _blockCompressed;
    private long _blockUncompressed;
    private int _padLeft;
    private readonly object?[] _filters = new object?[MaxFilters];
    private int _filterCount;
    private byte _lzma2Property;
    private bool _lzmaDone;
    private byte[]? _chain;
    private int _filled;
    private int _emitted;
    private readonly int[] _resume = new int[MaxFilters];
    private uint _crc32;
    private ulong _crc64;
    private IncrementalHash? _sha;

    // Records of the blocks so far, and the index being read.
    private long _blockRecords;
    private ulong _recordHash;
    private ulong _recordsLeft;
    private ulong _indexUnpaddedValue;
    private long _indexSize;
    private uint _indexCrc;
    private ulong _indexHash;
    private ulong _varValue;
    private int _varShift;
    private int _varBytes;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Limits and checks; <see langword="null"/> for <see cref="XzDecompressionOptions.Default"/>.</param>
    public XzDecoder(XzDecompressionOptions? options = null)
    {
        _options = options ?? XzDecompressionOptions.Default;
        _innerOptions = new LzmaDecompressionOptions { MaxDictionarySize = _options.MaxDictionarySize, MaxOutputSize = long.MaxValue };
    }

    /// <summary>Gets the check type of the stream being read, or of the last stream read.</summary>
    public XzCheck Check => (XzCheck)_checkId;

    /// <summary>Gets a value indicating whether the decoder is between streams: nothing of a stream is partly read. Zero padding after a stream counts, as long as it is a multiple of four bytes.</summary>
    public bool IsAtFrameBoundary => !_faulted && ((_phase == Phase.StreamHeader && _stage.Length == 0 && _streams == 0) || _trailing || (_phase == Phase.Padding && (_padding & 3) == 0));

    /// <summary>Gets the number of input bytes consumed since construction or the last <see cref="Reset"/>.</summary>
    public long TotalConsumed => _inOffset;

    /// <summary>Gets the number of bytes written since construction or the last <see cref="Reset"/>.</summary>
    public long TotalWritten => _totalOut;

    /// <summary>Forgets everything about the stream being read and clears a failure. Buffers are kept.</summary>
    public void Reset()
    {
        _phase = Phase.StreamHeader;
        _faulted = false;
        _trailing = false;
        _inOffset = 0;
        _totalOut = 0;
        _streams = 0;
        _padding = 0;
        _stage.Clear();
        _lzmaDone = false;
    }

    /// <summary>Throws when the decoder is inside a stream, which means the input ended too early.</summary>
    /// <exception cref="CompressionFormatException">The input ended inside a stream, or the padding after the last one is not a multiple of four bytes.</exception>
    public void ThrowIfIncomplete()
    {
        if (!IsAtFrameBoundary)
        {
            throw new CompressionFormatException(Format, _inOffset, _phase == Phase.Padding ? "stream padding in multiples of four bytes" : "more compressed data", isTruncated: true);
        }
    }

    /// <summary>Decodes as much as the buffers allow.</summary>
    /// <param name="source">The compressed bytes not yet consumed, followed by any new ones.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="consumed">Receives the number of bytes taken from <paramref name="source"/>, also when the call throws.</param>
    /// <param name="written">Receives the number of bytes written, also when the call throws.</param>
    /// <returns><see cref="OperationStatus.Done"/> when a stream is complete (the footer was read), <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting, <see cref="OperationStatus.NeedMoreData"/> when all input was used.</returns>
    /// <exception cref="CompressionFormatException">The data is invalid.</exception>
    /// <exception cref="CompressionLimitException">A block declares a dictionary over the limit, or the output passes <see cref="XzDecompressionOptions.MaxOutputSize"/>.</exception>
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

    private static CompressionFormatException Bad(long offset, string expected) => new(Format, offset, expected);

    private OperationStatus Run(ReadOnlySpan<byte> source, Span<byte> destination, ref int pos, ref int written)
    {
        if (_trailing)
        {
            return OperationStatus.Done;
        }

        while (true)
        {
            switch (_phase)
            {
                case Phase.Padding:
                {
                    // Stream Padding: zero bytes in multiples of four between streams and after the last one.
                    while (pos < source.Length && source[pos] == 0)
                    {
                        pos++;
                        _padding++;
                    }

                    if (pos >= source.Length)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    if ((_padding & 3) != 0)
                    {
                        throw Bad(_inOffset + pos, "stream padding in multiples of four bytes");
                    }

                    _padding = 0;
                    _phase = Phase.StreamHeader;
                    break;
                }

                case Phase.StreamHeader:
                {
                    // The magic is checked byte by byte so that data after the last stream can be left unread.
                    while (_stage.Length < HeaderMagic.Length && pos < source.Length)
                    {
                        if (source[pos] != HeaderMagic[_stage.Length])
                        {
                            if (_streams > 0 && _options.AllowTrailingData)
                            {
                                _trailing = true;
                                _stage.Clear();
                                return OperationStatus.Done;
                            }

                            throw Bad(_inOffset + pos, _streams > 0 ? "another xz stream, stream padding or the end of the data" : "the xz magic bytes FD 37 7A 58 5A 00");
                        }

                        _stage.Append(source[pos++]);
                    }

                    if (!_stage.Fill(source, ref pos, StreamHeaderSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    ReadOnlySpan<byte> h = _stage.Bytes;
                    long at = _inOffset + pos - StreamHeaderSize;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(h[8..]) != Crc32.Compute(h.Slice(6, 2)))
                    {
                        throw Bad(at + 8, "a CRC-32 of the stream flags that matches");
                    }

                    // Spec 2.1.1.2: the first flags byte is 0 and the top four bits of the second are reserved.
                    if (h[6] != 0 || (h[7] & 0xF0) != 0)
                    {
                        throw Bad(at + 6, "stream flags with the reserved bits zero");
                    }

                    _flags = h[7];
                    _checkId = h[7] & 0x0F;
                    _checkSize = CheckSize(_checkId);
                    if (!IsSupportedCheck(_checkId) && !_options.AllowUnsupportedChecks)
                    {
                        throw Bad(at + 7, "a supported check type (none, CRC-32, CRC-64 or SHA-256)");
                    }

                    _stage.Clear();
                    _blockRecords = 0;
                    _recordHash = 0;
                    _phase = Phase.BlockFirst;
                    break;
                }

                case Phase.BlockFirst:
                {
                    if (pos >= source.Length)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    byte b = source[pos++];
                    if (b == 0)
                    {
                        // The index indicator (spec 4.1) overlaps the block header size.
                        _indexSize = 1;
                        _indexCrc = Crc32.Update(0, [0]);
                        _phase = Phase.IndexCount;
                        _varBytes = 0;
                        _varValue = 0;
                        _varShift = 0;
                        break;
                    }

                    _headerSize = (b + 1) * 4;
                    _stage.Clear();
                    _stage.Append(b);
                    _phase = Phase.BlockHeader;
                    break;
                }

                case Phase.BlockHeader:
                {
                    if (!_stage.Fill(source, ref pos, _headerSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    ParseBlockHeader(_stage.Bytes, _inOffset + pos - _headerSize);
                    _stage.Clear();
                    _phase = Phase.BlockData;
                    break;
                }

                case Phase.BlockData:
                {
                    OperationStatus status = RunBlockData(source, destination, ref pos, ref written);
                    if (status != OperationStatus.Done)
                    {
                        return status;
                    }

                    if (_declaredCompressed >= 0 && _blockCompressed != _declaredCompressed)
                    {
                        throw Bad(_inOffset + pos, $"a block whose compressed size is the declared {_declaredCompressed} bytes");
                    }

                    if (_declaredUncompressed >= 0 && _blockUncompressed != _declaredUncompressed)
                    {
                        throw Bad(_inOffset + pos, $"a block whose uncompressed size is the declared {_declaredUncompressed} bytes");
                    }

                    _padLeft = (int)((4 - (_blockCompressed & 3)) & 3);
                    _phase = Phase.BlockPadding;
                    break;
                }

                case Phase.BlockPadding:
                {
                    while (_padLeft > 0)
                    {
                        if (pos >= source.Length)
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        if (source[pos] != 0)
                        {
                            throw Bad(_inOffset + pos, "zero bytes of block padding");
                        }

                        pos++;
                        _padLeft--;
                    }

                    _stage.Clear();
                    _phase = Phase.BlockCheck;
                    break;
                }

                case Phase.BlockCheck:
                {
                    if (!_stage.Fill(source, ref pos, _checkSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    VerifyCheck(_inOffset + pos - _checkSize);
                    _stage.Clear();
                    AddRecord((ulong)(_headerSize + _blockCompressed + _checkSize), (ulong)_blockUncompressed);
                    _phase = Phase.BlockFirst;
                    break;
                }

                case Phase.IndexCount:
                case Phase.IndexUnpadded:
                case Phase.IndexUncompressed:
                {
                    if (!ReadIndexNumber(source, ref pos, out ulong value))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    switch (_phase)
                    {
                        case Phase.IndexCount:
                            _recordsLeft = value;
                            _indexHash = 0;
                            if ((long)value != _blockRecords)
                            {
                                throw Bad(_inOffset + pos, $"an index with {_blockRecords} records, one per block");
                            }

                            _phase = value == 0 ? Phase.IndexPadding : Phase.IndexUnpadded;
                            break;
                        case Phase.IndexUnpadded:
                            if (value == 0)
                            {
                                throw Bad(_inOffset + pos, "a non-zero unpadded size in the index");
                            }

                            _indexUnpaddedValue = value;
                            _phase = Phase.IndexUncompressed;
                            break;
                        default:
                            _indexHash = RecordHash(_indexHash, _indexUnpaddedValue, value);
                            _recordsLeft--;
                            _phase = _recordsLeft == 0 ? Phase.IndexPadding : Phase.IndexUnpadded;
                            break;
                    }

                    break;
                }

                case Phase.IndexPadding:
                {
                    // Spec 4.4: zero bytes up to a multiple of four, counting from the index indicator.
                    while ((_indexSize & 3) != 0)
                    {
                        if (pos >= source.Length)
                        {
                            return OperationStatus.NeedMoreData;
                        }

                        if (source[pos] != 0)
                        {
                            throw Bad(_inOffset + pos, "zero bytes of index padding");
                        }

                        _indexCrc = Crc32.Update(_indexCrc, source.Slice(pos, 1));
                        pos++;
                        _indexSize++;
                    }

                    if (_indexHash != _recordHash)
                    {
                        throw Bad(_inOffset + pos, "index records equal to the sizes of the blocks that were decoded");
                    }

                    _stage.Clear();
                    _phase = Phase.IndexCrc;
                    break;
                }

                case Phase.IndexCrc:
                {
                    if (!_stage.Fill(source, ref pos, 4))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    if (BinaryPrimitives.ReadUInt32LittleEndian(_stage.Bytes) != _indexCrc)
                    {
                        throw Bad(_inOffset + pos - 4, "a CRC-32 of the index that matches");
                    }

                    _indexSize += 4;
                    _stage.Clear();
                    _phase = Phase.Footer;
                    break;
                }

                default: // Footer
                {
                    if (!_stage.Fill(source, ref pos, StreamHeaderSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    ReadOnlySpan<byte> f = _stage.Bytes;
                    long at = _inOffset + pos - StreamHeaderSize;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(f) != Crc32.Compute(f.Slice(4, 6)))
                    {
                        throw Bad(at, "a CRC-32 of the stream footer that matches");
                    }

                    long backward = ((long)BinaryPrimitives.ReadUInt32LittleEndian(f[4..]) + 1) * 4;
                    if (backward != _indexSize)
                    {
                        throw Bad(at + 4, $"a backward size of {_indexSize} bytes, the size of the index");
                    }

                    if (f[8] != 0 || f[9] != _flags)
                    {
                        throw Bad(at + 8, "stream flags equal to those of the stream header");
                    }

                    if (f[10] != (byte)'Y' || f[11] != (byte)'Z')
                    {
                        throw Bad(at + 10, "the footer magic bytes 'YZ'");
                    }

                    _stage.Clear();
                    _streams++;
                    _padding = 0;
                    _phase = Phase.Padding;
                    return OperationStatus.Done;
                }
            }
        }
    }

    private static int CheckSize(int id) => id switch
    {
        0 => 0,
        <= 3 => 4,
        <= 6 => 8,
        <= 9 => 16,
        <= 12 => 32,
        _ => 64,
    };

    private static bool IsSupportedCheck(int id) => id is 0 or 1 or 4 or 10;

    private static ulong RecordHash(ulong hash, ulong unpadded, ulong uncompressed)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, unpadded);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], uncompressed);
        return Crc64.Update(hash, bytes);
    }

    private void AddRecord(ulong unpadded, ulong uncompressed)
    {
        _recordHash = RecordHash(_recordHash, unpadded, uncompressed);
        _blockRecords++;
    }

    // Reads one variable-length integer of the index (spec 1.2) a byte at a time, adding each byte to the index CRC.
    private bool ReadIndexNumber(ReadOnlySpan<byte> source, ref int pos, out ulong value)
    {
        value = 0;
        while (pos < source.Length)
        {
            byte b = source[pos];
            _indexCrc = Crc32.Update(_indexCrc, source.Slice(pos, 1));
            pos++;
            _indexSize++;
            _varValue |= (ulong)(b & 0x7F) << _varShift;
            _varBytes++;
            if ((b & 0x80) == 0)
            {
                if (_varBytes > 1 && b == 0)
                {
                    throw Bad(_inOffset + pos - 1, "a variable-length integer without a trailing zero byte");
                }

                value = _varValue;
                _varValue = 0;
                _varShift = 0;
                _varBytes = 0;
                return true;
            }

            _varShift += 7;
            if (_varBytes >= 9)
            {
                throw Bad(_inOffset + pos - 1, "a variable-length integer of at most 9 bytes");
            }
        }

        return false;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> h, ref int p, int end, out ulong value)
    {
        value = 0;
        int shift = 0;
        for (int i = 0; i < 9; i++)
        {
            if (p >= end)
            {
                return false;
            }

            byte b = h[p++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return i == 0 || b != 0;
            }

            shift += 7;
        }

        return false;
    }

    // Spec 3.1: the block header. Its CRC is checked first so that damage is told from an unsupported feature.
    private void ParseBlockHeader(ReadOnlySpan<byte> h, long at)
    {
        int size = h.Length;
        if (BinaryPrimitives.ReadUInt32LittleEndian(h[(size - 4)..]) != Crc32.Compute(h[..(size - 4)]))
        {
            throw Bad(at + size - 4, "a CRC-32 of the block header that matches");
        }

        byte flags = h[1];
        if ((flags & 0x3C) != 0)
        {
            throw Bad(at + 1, "block flags with the reserved bits zero");
        }

        int count = (flags & 3) + 1;
        int end = size - 4;
        int p = 2;
        _declaredCompressed = -1;
        _declaredUncompressed = -1;
        if ((flags & 0x40) != 0)
        {
            if (!TryReadVarint(h, ref p, end, out ulong c) || c == 0 || c > long.MaxValue)
            {
                throw Bad(at + p, "a non-zero compressed size in the block header");
            }

            _declaredCompressed = (long)c;
        }

        if ((flags & 0x80) != 0)
        {
            if (!TryReadVarint(h, ref p, end, out ulong u) || u > long.MaxValue)
            {
                throw Bad(at + p, "an uncompressed size in the block header");
            }

            _declaredUncompressed = (long)u;
        }

        _filterCount = 0;
        for (int i = 0; i < count; i++)
        {
            int fieldAt = p;
            if (!TryReadVarint(h, ref p, end, out ulong id) || !TryReadVarint(h, ref p, end, out ulong propSize) || propSize > (ulong)(end - p))
            {
                throw Bad(at + fieldAt, "a complete filter flags field in the block header");
            }

            ReadOnlySpan<byte> props = h.Slice(p, (int)propSize);
            p += (int)propSize;
            bool last = i == count - 1;
            if (id == Lzma2Id)
            {
                if (!last)
                {
                    throw Bad(at + fieldAt, "LZMA2 as the last filter only");
                }

                if (props.Length != 1 || props[0] > 40)
                {
                    throw Bad(at + fieldAt, "a one-byte LZMA2 property of at most 40");
                }

                _lzma2Property = props[0];
            }
            else if (last)
            {
                throw Bad(at + fieldAt, id is 3 or (>= 4 and <= 0x0B) ? "LZMA2 as the last filter" : "a supported filter ID (LZMA2 0x21 as the last filter)");
            }
            else if (id == 3)
            {
                if (props.Length != 1)
                {
                    throw Bad(at + fieldAt, "a one-byte delta distance property");
                }

                _filters[_filterCount++] = new DeltaFilter(props[0] + 1);
            }
            else if (id is >= 4 and <= 0x0B)
            {
                var arch = (BranchArchitecture)(int)id;
                uint start = 0;
                if (props.Length == 4)
                {
                    start = BinaryPrimitives.ReadUInt32LittleEndian(props);
                }
                else if (props.Length != 0)
                {
                    throw Bad(at + fieldAt, "a branch filter property of zero or four bytes");
                }

                if (start % (uint)BranchFilter.GetAlignment(arch) != 0)
                {
                    throw Bad(at + fieldAt, "a branch filter start offset that is a multiple of the instruction alignment");
                }

                if (arch == BranchArchitecture.RiscV)
                {
                    throw new NotSupportedException("The RISC-V filter is not available yet.");
                }

                _filters[_filterCount++] = new BranchFilter(arch, start);
            }
            else
            {
                throw Bad(at + fieldAt, "a supported filter ID (delta 0x03, a branch filter 0x04 to 0x0B, or LZMA2 0x21)");
            }
        }

        // Spec 3.1.6: the rest is zero padding.
        for (int i = p; i < end; i++)
        {
            if (h[i] != 0)
            {
                throw Bad(at + i, "zero bytes of block header padding");
            }
        }

        // Filters are listed in encoding order; decoding runs them in the opposite order.
        for (int a = 0, b = _filterCount - 1; a < b; a++, b--)
        {
            (_filters[a], _filters[b]) = (_filters[b], _filters[a]);
        }

        long hint = _declaredUncompressed > 0 ? _declaredUncompressed : 0;
        if (_lzma2 is null)
        {
            _lzma2 = new Lzma2Decoder(_lzma2Property, _innerOptions);
        }

        _lzma2.Reset(_lzma2Property, hint);
        _blockCompressed = 0;
        _blockUncompressed = 0;
        _lzmaDone = false;
        _filled = 0;
        _emitted = 0;
        Array.Clear(_resume);
        _crc32 = 0;
        _crc64 = 0;
        if (_checkId == 10 && _options.VerifyChecks)
        {
            _sha ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        if (_filterCount > 0)
        {
            _chain ??= new byte[ChainBufferSize];
        }
    }

    private void CheckOutput(ReadOnlySpan<byte> data)
    {
        if (!_options.VerifyChecks || data.IsEmpty)
        {
            return;
        }

        switch (_checkId)
        {
            case 1:
                _crc32 = Crc32.Update(_crc32, data);
                break;
            case 4:
                _crc64 = Crc64.Update(_crc64, data);
                break;
            case 10:
                _sha!.AppendData(data);
                break;
        }
    }

    // Spec 3.4: the check is over the uncompressed data of the block.
    private void VerifyCheck(long at)
    {
        if (!_options.VerifyChecks || !IsSupportedCheck(_checkId) || _checkId == 0)
        {
            return;
        }

        ReadOnlySpan<byte> stored = _stage.Bytes;
        bool ok;
        switch (_checkId)
        {
            case 1:
                ok = BinaryPrimitives.ReadUInt32LittleEndian(stored) == _crc32;
                break;
            case 4:
                ok = BinaryPrimitives.ReadUInt64LittleEndian(stored) == _crc64;
                break;
            default:
                Span<byte> hash = stackalloc byte[32];
                _sha!.TryGetHashAndReset(hash, out _);
                ok = stored.SequenceEqual(hash);
                break;
        }

        if (!ok)
        {
            throw Bad(at, "a block check that matches the uncompressed data");
        }
    }

    private OperationStatus RunBlockData(ReadOnlySpan<byte> source, Span<byte> destination, ref int pos, ref int written)
    {
        Lzma2Decoder lzma2 = _lzma2!;
        if (_filterCount == 0)
        {
            OperationStatus status = lzma2.Decompress(source[pos..], destination[written..], out int c, out int w);
            CheckOutput(destination.Slice(written, w));
            pos += c;
            written += w;
            _blockCompressed += c;
            _blockUncompressed += w;
            CheckDeclared(pos);
            return status;
        }

        byte[] buffer = _chain!;
        while (true)
        {
            int ready = _resume[_filterCount - 1];
            if (_emitted < ready)
            {
                int n = Math.Min(ready - _emitted, destination.Length - written);
                ReadOnlySpan<byte> part = buffer.AsSpan(_emitted, n);
                part.CopyTo(destination[written..]);
                CheckOutput(part);
                _emitted += n;
                written += n;
                if (_emitted < ready)
                {
                    return OperationStatus.DestinationTooSmall;
                }
            }

            if (_lzmaDone)
            {
                return OperationStatus.Done;
            }

            // Everything final was handed out: slide the bytes the filters still hold to the front and read more.
            if (_emitted > 0)
            {
                int keep = _filled - _emitted;
                buffer.AsSpan(_emitted, keep).CopyTo(buffer);
                for (int k = 0; k < _filterCount; k++)
                {
                    _resume[k] -= _emitted;
                }

                _filled = keep;
                _emitted = 0;
            }

            OperationStatus status = lzma2.Decompress(source[pos..], buffer.AsSpan(_filled), out int c, out int w);
            pos += c;
            _blockCompressed += c;
            _blockUncompressed += w;
            _filled += w;
            CheckDeclared(pos);
            bool finished = status == OperationStatus.Done;
            RunFilters(finished);
            if (finished)
            {
                _lzmaDone = true;
                continue;
            }

            if (status == OperationStatus.NeedMoreData && _emitted == _resume[_filterCount - 1])
            {
                return OperationStatus.NeedMoreData;
            }
        }
    }

    private void CheckDeclared(int pos)
    {
        if (_declaredCompressed >= 0 && _blockCompressed > _declaredCompressed)
        {
            throw Bad(_inOffset + pos, $"a block no longer than its declared {_declaredCompressed} compressed bytes");
        }

        if (_declaredUncompressed >= 0 && _blockUncompressed > _declaredUncompressed)
        {
            throw Bad(_inOffset + pos, $"a block no longer than its declared {_declaredUncompressed} uncompressed bytes");
        }
    }

    // Runs the chain over the new bytes. Each stage works from where it stopped to where the stage before it got to; at the end of the
    // block the bytes a stage could not convert stay as they are.
    private void RunFilters(bool final)
    {
        byte[] buffer = _chain!;
        int front = _filled;
        for (int k = 0; k < _filterCount; k++)
        {
            Span<byte> span = buffer.AsSpan(_resume[k], front - _resume[k]);
            int n;
            if (_filters[k] is BranchFilter branch)
            {
                n = branch.Convert(span);
            }
            else
            {
                ((DeltaFilter)_filters[k]!).Convert(span);
                n = span.Length;
            }

            _resume[k] += n;
            if (final)
            {
                _resume[k] = front;
            }

            front = _resume[k];
        }
    }
}
