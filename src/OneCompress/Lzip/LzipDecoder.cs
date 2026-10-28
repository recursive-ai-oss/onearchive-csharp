using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Lzip;

/// <summary>Options for decompressing lzip data. The limits protect the caller from input that asks for more memory or output than it should.</summary>
[Experimental("ONECOMPRESS007", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzipDecompressionOptions
{
    /// <summary>The default for <see cref="MaxDictionarySize"/>: 64 MiB, above the 32 MiB dictionary of the reference tool's largest level (<c>-9</c>).</summary>
    public const long DefaultMaxDictionarySize = LzmaDecompressionOptions.DefaultMaxDictionarySize;

    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = LzmaDecompressionOptions.DefaultMaxOutputSize;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static LzipDecompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest dictionary size, in bytes, a member may declare (the format allows up to 512 MiB). A header that declares more raises
    /// <see cref="CompressionLimitException"/> before any memory is reserved; the window grows with the output. Default <see cref="DefaultMaxDictionarySize"/>.
    /// </summary>
    public long MaxDictionarySize { get; init; } = DefaultMaxDictionarySize;

    /// <summary>Gets the largest total output, in bytes, a decoder produces between resets, over all members. Default <see cref="DefaultMaxOutputSize"/>.</summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>Gets a value indicating whether each member's CRC-32 is verified. Default true. The data size and member size in the trailer are always verified.</summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether bytes after the last member that do not start another member are ignored. Default false: they raise
    /// <see cref="CompressionFormatException"/>.
    /// </summary>
    public bool AllowTrailingData { get; init; }
}

/// <summary>
/// Decodes lzip data: one or more members, each a 6-byte header, an LZMA stream (lc 3, lp 0, pb 2) that ends with an end marker, and a
/// 20-byte trailer with the CRC-32 and the sizes of the data and of the member.
/// </summary>
/// <remarks>
/// <para>Format: the file format and LZMA stream sections of the lzip manual. Verified: the magic and version, the dictionary size byte, the
/// end marker and the range decoder's final state, and the trailer (CRC-32, data size, member size). Members version 0 (an older trailer
/// without the member size) are not supported.</para>
/// <para><see cref="Decompress"/> returns <see cref="OperationStatus.Done"/> after each member; call it again for the next one.</para>
/// </remarks>
[Experimental("ONECOMPRESS007", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzipDecoder : IStepDecoder
{
    private const string Format = "lzip";
    private const int HeaderSize = 6;
    private const int TrailerSize = 20;
    private const long MinDictionary = 1 << 12;
    private const long MaxDictionary = 1 << 29;

    private static ReadOnlySpan<byte> Magic => "LZIP"u8;

    private enum Phase
    {
        Header,
        RangeInit,
        Data,
        Trailer,
    }

    private readonly LzipDecompressionOptions _options;
    private readonly LzmaDecoderCore _core = new();
    private readonly FieldStage _stage = new();
    private Phase _phase = Phase.Header;
    private bool _faulted;
    private bool _trailing;
    private long _inOffset;
    private long _totalOut;
    private long _members;
    private long _memberStart;
    private long _memberOut;
    private uint _crc;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Limits and checks; <see langword="null"/> for <see cref="LzipDecompressionOptions.Default"/>.</param>
    public LzipDecoder(LzipDecompressionOptions? options = null)
    {
        _options = options ?? LzipDecompressionOptions.Default;
    }

    /// <summary>Gets a value indicating whether the decoder is between members: nothing of a member is partly read.</summary>
    public bool IsAtFrameBoundary => !_faulted && (_trailing || (_phase == Phase.Header && _stage.Length == 0));

    /// <summary>Gets the number of input bytes consumed since construction or the last <see cref="Reset"/>.</summary>
    public long TotalConsumed => _inOffset;

    /// <summary>Gets the number of bytes written since construction or the last <see cref="Reset"/>.</summary>
    public long TotalWritten => _totalOut;

    /// <summary>Forgets everything about the member being read and clears a failure. Buffers are kept.</summary>
    public void Reset()
    {
        _phase = Phase.Header;
        _faulted = false;
        _trailing = false;
        _inOffset = 0;
        _totalOut = 0;
        _members = 0;
        _stage.Clear();
    }

    /// <summary>Throws when the decoder is inside a member, which means the input ended too early.</summary>
    /// <exception cref="CompressionFormatException">The input ended inside a member.</exception>
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
    /// <returns><see cref="OperationStatus.Done"/> when a member is complete (its trailer was read), <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting, <see cref="OperationStatus.NeedMoreData"/> when all input was used.</returns>
    /// <exception cref="CompressionFormatException">The data is invalid.</exception>
    /// <exception cref="CompressionLimitException">A member declares a dictionary over the limit, or the output passes <see cref="LzipDecompressionOptions.MaxOutputSize"/>.</exception>
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
                case Phase.Header:
                {
                    // The magic is checked byte by byte so that data after the last member can be left unread.
                    while (_stage.Length < Magic.Length && pos < source.Length)
                    {
                        if (source[pos] != Magic[_stage.Length])
                        {
                            if (_members > 0 && _options.AllowTrailingData)
                            {
                                _trailing = true;
                                _stage.Clear();
                                return OperationStatus.Done;
                            }

                            throw Bad(_inOffset + pos, _members > 0 ? "another lzip member or the end of the data" : "the lzip magic bytes 'LZIP'");
                        }

                        _stage.Append(source[pos++]);
                    }

                    if (!_stage.Fill(source, ref pos, HeaderSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    ReadOnlySpan<byte> h = _stage.Bytes;
                    long at = _inOffset + pos - HeaderSize;
                    if (h[4] != 1)
                    {
                        throw Bad(at + 4, "version 1");
                    }

                    // The dictionary size is a power of two minus 0 to 7 sixteenths of it.
                    int log = h[5] & 0x1F;
                    long baseSize = 1L << log;
                    long dictionary = baseSize - ((baseSize >> 4) * (h[5] >> 5));
                    if (log is < 12 or > 29 || dictionary < MinDictionary || dictionary > MaxDictionary)
                    {
                        throw Bad(at + 5, "a dictionary size byte for 4 KiB to 512 MiB");
                    }

                    if (dictionary > _options.MaxDictionarySize)
                    {
                        throw new CompressionLimitException(Format, nameof(LzipDecompressionOptions.MaxDictionarySize), _options.MaxDictionarySize, dictionary);
                    }

                    _stage.Clear();
                    _memberStart = at;
                    _memberOut = 0;
                    _crc = 0;
                    _core.Configure(3, 0, 2);
                    _core.SetDictionary((uint)dictionary);
                    _core.ResetDictionary();
                    _core.ResetState();
                    _phase = Phase.RangeInit;
                    break;
                }

                case Phase.RangeInit:
                {
                    if (!_stage.Fill(source, ref pos, LzmaRangeDecoder.InitBytes))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    if (!_core.StartRangeDecoder(_stage.Bytes))
                    {
                        throw Bad(_inOffset + pos - LzmaRangeDecoder.InitBytes, "a zero byte at the start of the LZMA stream");
                    }

                    _stage.Clear();
                    _phase = Phase.Data;
                    break;
                }

                case Phase.Data:
                {
                    LzmaStatus status = _core.Decode(source[pos..], out int c, destination[written..], out int w);
                    if (_options.VerifyChecksum)
                    {
                        _crc = Crc32.Update(_crc, destination.Slice(written, w));
                    }

                    pos += c;
                    written += w;
                    _memberOut += w;
                    switch (status)
                    {
                        case LzmaStatus.NeedInput:
                            return OperationStatus.NeedMoreData;
                        case LzmaStatus.OutputFull:
                            return OperationStatus.DestinationTooSmall;
                        case LzmaStatus.EndMarker:
                            if (!_core.IsRangeDecoderFinished)
                            {
                                throw Bad(_inOffset + pos, "the range decoder to end with a code value of 0");
                            }

                            _phase = Phase.Trailer;
                            break;
                        default:
                            throw Bad(_inOffset + pos, "a match distance inside the dictionary");
                    }

                    break;
                }

                default: // Trailer
                {
                    if (!_stage.Fill(source, ref pos, TrailerSize))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    ReadOnlySpan<byte> t = _stage.Bytes;
                    long at = _inOffset + pos - TrailerSize;
                    if (_options.VerifyChecksum && BinaryPrimitives.ReadUInt32LittleEndian(t) != _crc)
                    {
                        throw Bad(at, "a CRC-32 that matches the uncompressed data");
                    }

                    if (BinaryPrimitives.ReadUInt64LittleEndian(t[4..]) != (ulong)_memberOut)
                    {
                        throw Bad(at + 4, $"a data size of {_memberOut} bytes");
                    }

                    long memberSize = _inOffset + pos - _memberStart;
                    if (BinaryPrimitives.ReadUInt64LittleEndian(t[12..]) != (ulong)memberSize)
                    {
                        throw Bad(at + 12, $"a member size of {memberSize} bytes");
                    }

                    _stage.Clear();
                    _members++;
                    _phase = Phase.Header;
                    return OperationStatus.Done;
                }
            }
        }
    }
}
