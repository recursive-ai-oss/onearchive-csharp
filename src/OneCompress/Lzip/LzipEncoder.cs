using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Lzip;

/// <summary>Options for compressing to the lzip format.</summary>
/// <remarks>The levels 0 to 9 take the dictionary size and match length of the reference <c>lzip</c> tool's levels; lc, lp and pb are fixed by the format to 3, 0 and 2.</remarks>
public sealed class LzipOptions
{
    /// <summary>The level used when none is set: 6, like the reference tool.</summary>
    public const int DefaultLevel = 6;

    /// <summary>The largest dictionary size the format allows: 512 MiB.</summary>
    public const uint MaxDictionarySize = 1u << 29;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static LzipOptions Default { get; } = new();

    /// <summary>Gets the level, 0 to 9. Level 0 uses the fast parser, the others the optimal parser. Default <see cref="DefaultLevel"/>.</summary>
    public int Level { get; init; } = DefaultLevel;

    /// <summary>Gets the dictionary size in bytes, 4 KiB to <see cref="MaxDictionarySize"/>; 0 takes the level's. The header stores the smallest size the format can express that is at least this. Default 0.</summary>
    public uint DictionarySize { get; init; }

    /// <summary>Gets the match length at which the search stops (the reference tool's "match length limit"), 5 to 273; 0 takes the level's. Default 0.</summary>
    public int MatchLength { get; init; }

    /// <summary>Gets the uncompressed size of a member. 0 (the default) writes one member; a smaller size writes consecutive independent members, each with its own header and trailer.</summary>
    public long MemberSize { get; init; }

    internal LzmaOptions ToLzmaOptions()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Level, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Level, 9);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DictionarySize, MaxDictionarySize);
        ArgumentOutOfRangeException.ThrowIfNegative(MemberSize);
        if (DictionarySize != 0 && DictionarySize < LzmaProperties.MinimumDictionarySize)
        {
            throw new ArgumentOutOfRangeException(nameof(DictionarySize), "The dictionary size must be 0 (the level's) or at least 4096.");
        }

        if (MatchLength != 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(MatchLength, 5);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(MatchLength, LzmaModel.MaxMatchLength);
        }

        uint[] dict = [1u << 16, 1u << 20, 3u << 19, 1u << 21, 3u << 20, 1u << 22, 1u << 23, 1u << 24, 3u << 23, 1u << 25];
        int[] nice = [16, 5, 6, 8, 12, 20, 36, 68, 132, 273];
        return new LzmaOptions
        {
            DictionarySize = DictionarySize != 0 ? DictionarySize : dict[Level],
            NiceLength = MatchLength != 0 ? MatchLength : nice[Level],
            Mode = Level == 0 ? LzmaMode.Fast : LzmaMode.Normal,
            MatchFinder = Level == 0 ? LzmaMatchFinderKind.HashChain4 : LzmaMatchFinderKind.BinaryTree4,
            Lc = 3,
            Lp = 0,
            Pb = 2,
        };
    }
}

/// <summary>
/// Compresses data to the lzip format: one or more members, each a 6-byte header (magic, version, dictionary size), an LZMA stream that
/// ends with the end-of-stream marker, and a 20-byte trailer (CRC32, data size, member size).
/// </summary>
/// <remarks>
/// The lzip manual, "File format". Shaped like <c>BrotliEncoder</c>: call <see cref="Compress"/> until it returns
/// <see cref="OperationStatus.Done"/>, passing <c>isFinalBlock</c> with the last input. Empty input gives one empty member. Output depends
/// only on the input and the options, including <see cref="LzipOptions.MemberSize"/>. An instance is not thread-safe.
/// </remarks>
public sealed class LzipEncoder : IQueuedEncoder
{
    private const int PumpBytes = 1 << 20;
    private const int HeaderSize = 6;
    private const int TrailerSize = 20;

    private readonly OutputQueue _queue = new();
    private readonly LzmaEncoderCore _core;
    private readonly long _memberSize;
    private readonly byte _dictionaryByte;
    private Crc32 _crc;
    private bool _inMember;
    private bool _coreDirty;
    private bool _finished;
    private int _members;
    private long _memberRaw;
    private long _memberLzmaBytes;

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The options; <see langword="null"/> for the defaults.</param>
    /// <param name="inputLength">The total input length when known, or -1. It only sizes memory.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public LzipEncoder(LzipOptions? options = null, long inputLength = -1)
    {
        options ??= LzipOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(inputLength, -1);
        LzmaEncoderSettings settings = options.ToLzmaOptions().Resolve();
        _memberSize = options.MemberSize;
        long hint = inputLength;
        if (_memberSize > 0)
        {
            hint = hint < 0 ? _memberSize : Math.Min(hint, _memberSize);
        }

        _core = new LzmaEncoderCore(settings, hint, _memberSize);
        _dictionaryByte = EncodeDictionarySize(settings.DictionarySize);
    }

    /// <summary>Gets a value indicating whether the final block was given and all output has been produced.</summary>
    public bool IsFinished => _finished;

    OutputQueue IQueuedEncoder.Queue => _queue;

    bool IQueuedEncoder.IsDone => _finished;

    /// <summary>Gets the dictionary size byte the header stores for a size: bits 0 to 4 are the base-2 logarithm of a power of two, bits 5 to 7 the number of sixteenths of it that are subtracted.</summary>
    /// <param name="size">The size in bytes, 4 KiB to 512 MiB.</param>
    /// <returns>The byte for the smallest expressible size that is at least <paramref name="size"/>.</returns>
    public static byte EncodeDictionarySize(uint size)
    {
        for (int log = 12; log <= 29; log++)
        {
            uint b = 1u << log;
            for (int wedges = 7; wedges >= 0; wedges--)
            {
                if (b - (uint)(wedges * (b >> 4)) >= size)
                {
                    return (byte)((wedges << 5) | log);
                }
            }
        }

        return 29;
    }

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

    /// <summary>Starts a new stream, keeping the allocated window and tables.</summary>
    public void Reset()
    {
        _queue.Clear();
        _inMember = false;
        _coreDirty = true;
        _finished = false;
        _members = 0;
        _memberRaw = 0;
    }

    int IQueuedEncoder.Accept(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0)
        {
            return 0;
        }

        if (!_inMember)
        {
            StartMember();
        }

        long room = _memberSize > 0 ? _memberSize - _memberRaw : long.MaxValue;
        if (room <= 0)
        {
            return 0; // the member is full; the next Pump closes it
        }

        ReadOnlySpan<byte> part = source[..(int)Math.Min(source.Length, room)];
        int taken = _core.Append(part);
        _crc.Append(part[..taken]);
        _memberRaw += taken;
        return taken;
    }

    bool IQueuedEncoder.Pump(bool drain)
    {
        bool progressed = MoveEncoded(drain);
        if (_inMember && (drain || (_memberSize > 0 && _memberRaw >= _memberSize)))
        {
            while (MoveEncoded(true))
            {
            }

            CloseMember();
            return true;
        }

        return progressed;
    }

    void IQueuedEncoder.Finish()
    {
        if (_members == 0)
        {
            StartMember();
            CloseMember();
        }

        _finished = true;
    }

    private bool MoveEncoded(bool drain)
    {
        if (!_inMember)
        {
            return false;
        }

        int n = _core.Encode(drain, PumpBytes);
        LzmaRangeEncoder rc = _core.RangeEncoder;
        if (rc.WrittenLength > 0)
        {
            _queue.Write(rc.WrittenSpan);
            _memberLzmaBytes += rc.WrittenLength;
            rc.ClearWritten();
        }

        return n > 0;
    }

    private void StartMember()
    {
        if (_coreDirty)
        {
            _core.Reset();
        }

        _coreDirty = true;
        _inMember = true;
        _crc = default;
        _memberRaw = 0;
        _memberLzmaBytes = 0;
        Span<byte> h = _queue.GetSpan(HeaderSize);
        h[0] = (byte)'L';
        h[1] = (byte)'Z';
        h[2] = (byte)'I';
        h[3] = (byte)'P';
        h[4] = 1;
        h[5] = _dictionaryByte;
        _queue.Advance(HeaderSize);
    }

    private void CloseMember()
    {
        _core.EncodeEndMarker();
        LzmaRangeEncoder rc = _core.RangeEncoder;
        rc.Flush();
        _queue.Write(rc.WrittenSpan);
        _memberLzmaBytes += rc.WrittenLength;
        rc.ClearWritten();
        Span<byte> t = _queue.GetSpan(TrailerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(t, _crc.GetCurrentValue());
        BinaryPrimitives.WriteUInt64LittleEndian(t[4..], (ulong)_memberRaw);
        BinaryPrimitives.WriteUInt64LittleEndian(t[12..], (ulong)(HeaderSize + _memberLzmaBytes + TrailerSize));
        _queue.Advance(TrailerSize);
        _inMember = false;
        _members++;
    }
}
