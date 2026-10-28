using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The LZMA symbol encoder: it owns a sliding window of input, a match finder, the adaptive probabilities and a range encoder, parses
/// the window into literals, matches and repeated matches, and codes them. The <c>.lzma</c> writer, the LZMA2 chunker and the lzip
/// writer are thin layers over it.
/// </summary>
/// <remarks>
/// <para>Usage: <see cref="Append"/> input, call <see cref="Encode(bool, int, long, out bool)"/> (which codes as much as the parsers can decide on) and read the
/// range coder's bytes from <see cref="RangeEncoder"/>. <see cref="Encode(bool, int, long, out bool)"/> without <c>drain</c> holds back the last
/// <see cref="LookaheadLength"/> bytes so the parse never depends on how the input was split into calls.</para>
/// <para>Positions in the window are indexes; index 0 is reserved (a zero byte at the start of a stream, the previous byte of the first
/// literal). The probability layout is <see cref="LzmaModel"/>'s. Two parsers choose the symbols (see <see cref="LzmaMode"/>); both only
/// ever use integers, so the choices are the same on every platform.</para>
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed partial class LzmaEncoderCore
{
    /// <summary>The most bytes the normal parser looks ahead for one decision.</summary>
    public const int MaxParseLength = 4096;

    /// <summary>The number of encoded bytes <see cref="GetEncoded"/> can always return.</summary>
    public const int MinimumHistory = 1 << 16;

    /// <summary>
    /// The number of unencoded bytes <see cref="Encode(bool, int, long, out bool)"/> keeps in the window unless it is told to drain: the parse window plus the longest
    /// match plus a small margin.
    /// </summary>
    public const int LookaheadLength = MaxParseLength + LzmaModel.MaxMatchLength + 16;

    // The largest number of compressed bytes one input byte can turn into (a 2-byte match with the least probable bits everywhere is
    // about 20 bytes); budgets use it so a size limit can never be passed in the middle of a parse.
    private const int WorstBytesPerInputByte = 10;
    private const int WorstBytesPerSymbol = 24;

    private readonly LzmaEncoderSettings _settings;
    private readonly int _windowDictionary;
    private readonly int _history;
    private readonly int _maxCapacity;
    private readonly int _lpMask;
    private readonly int _pbMask;
    private readonly ushort[] _probs;
    private readonly LzmaRangeEncoder _rc = new();
    private readonly LzmaMatchFinder _finder;
    private readonly uint[] _reps = new uint[4];
    private readonly LzmaMatch[] _matches = new LzmaMatch[LzmaMatchFinder.MaxMatches];
    private readonly LzmaMatch[] _matchesNext = new LzmaMatch[LzmaMatchFinder.MaxMatches];

    private byte[] _buf;
    private int _bufEnd;
    private int _pos;
    private long _posBase;
    private int _state;
    private int _finderPos;
    private int _cachePos;
    private int _cacheCount;

    /// <summary>Creates an encoder.</summary>
    /// <param name="settings">The resolved settings (see <see cref="LzmaOptions.Resolve"/>).</param>
    /// <param name="sizeHint">The expected total input size when it is known, or -1. It only sizes memory: the window and the match finder never exceed what the input can use, and the output does not depend on it.</param>
    /// <param name="unitSize">When the stream is cut into independent units (xz blocks, lzip members) of at most this many bytes, the unit size, otherwise 0. No match can reach across units, so a dictionary larger than a unit is not used and the tables are sized for the unit.</param>
    /// <exception cref="ArgumentOutOfRangeException">The settings were not produced by <see cref="LzmaOptions.Resolve"/>.</exception>
    public LzmaEncoderCore(LzmaEncoderSettings settings, long sizeHint = -1, long unitSize = 0)
    {
        if (settings.DictionarySize < LzmaProperties.MinimumDictionarySize || settings.Depth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The settings must come from LzmaOptions.Resolve.");
        }

        _settings = settings;
        Properties = new LzmaProperties(settings.Lc, settings.Lp, settings.Pb, settings.DictionarySize);
        _lpMask = (1 << settings.Lp) - 1;
        _pbMask = (1 << settings.Pb) - 1;
        long dict = settings.DictionarySize;
        if (unitSize > 0)
        {
            dict = Math.Min(dict, Math.Max(unitSize, LzmaProperties.MinimumDictionarySize));
        }

        int hashDictionary = (int)dict;
        if (sizeHint >= 0)
        {
            // A dictionary larger than the whole input can't be used; the stream header still declares the requested size.
            dict = Math.Min(dict, Math.Max(sizeHint, LzmaProperties.MinimumDictionarySize));
        }

        _windowDictionary = (int)dict;

        // History kept when the window slides: the dictionary, but at least 64 KiB so an LZMA2 chunk that turns out not to compress can
        // still be stored from the window.
        _history = Math.Max(_windowDictionary, MinimumHistory) + 16;
        int slack = Math.Clamp(_windowDictionary, 1 << 16, 1 << 23);
        long max = 1L + _history + slack + LookaheadLength;
        _maxCapacity = (int)Math.Min(max, int.MaxValue - 64);
        long initial = sizeHint >= 0 ? Math.Min(sizeHint + 1, _maxCapacity) : Math.Min(1 << 16, _maxCapacity);
        _buf = new byte[(int)Math.Max(initial, 1) + LzmaMatchFinder.BufferPadding];
        int nice = settings.NiceLength;
        _finder = settings.MatchFinder switch
        {
            LzmaMatchFinderKind.HashChain3 => new LzmaHashChainMatchFinder(3, _windowDictionary, nice, settings.Depth, hashDictionary),
            LzmaMatchFinderKind.HashChain4 => new LzmaHashChainMatchFinder(4, _windowDictionary, nice, settings.Depth, hashDictionary),
            LzmaMatchFinderKind.BinaryTree2 => new LzmaBinaryTreeMatchFinder(2, _windowDictionary, nice, settings.Depth, hashDictionary),
            LzmaMatchFinderKind.BinaryTree3 => new LzmaBinaryTreeMatchFinder(3, _windowDictionary, nice, settings.Depth, hashDictionary),
            _ => new LzmaBinaryTreeMatchFinder(4, _windowDictionary, nice, settings.Depth, hashDictionary),
        };
        _probs = new ushort[LzmaModel.ProbabilityCount(settings.Lc, settings.Lp)];
        InitializeNormalParser();
        Reset();
    }

    /// <summary>Gets the stream properties: lc, lp, pb and the dictionary size the settings ask for.</summary>
    public LzmaProperties Properties { get; }

    /// <summary>Gets the range encoder the symbols are coded into.</summary>
    public LzmaRangeEncoder RangeEncoder => _rc;

    /// <summary>Gets the number of input bytes appended but not yet encoded.</summary>
    public int Buffered => _bufEnd - _pos;

    /// <summary>Gets the stream position of the next byte to encode: the number of bytes encoded since the last <see cref="Reset"/>.</summary>
    public long Position => _posBase + _pos;

    /// <summary>Starts a new stream: empty window, forgotten matches, initial probabilities and an empty range encoder.</summary>
    public void Reset()
    {
        _finder.Reset();
        Array.Clear(_buf, 0, Math.Min(_buf.Length, 1));
        _bufEnd = 1;
        _pos = 1;
        _posBase = -1;
        _finderPos = 1;
        _cacheCount = -1;
        _cachePos = 0;
        _rc.Reset();
        ResetState();
    }

    /// <summary>Resets the probabilities, the state machine and the repeated distances to their initial values, as a state reset in LZMA2 does. The range encoder is not touched.</summary>
    public void ResetState()
    {
        LzmaModel.ResetProbabilities(_probs);
        _state = 0;
        Array.Clear(_reps);
        _priceAge = int.MaxValue;
    }

    /// <summary>Copies input into the window.</summary>
    /// <param name="data">The input.</param>
    /// <returns>The number of bytes taken; less than <paramref name="data"/>'s length when the window is full, in which case <see cref="Encode(bool, int, long, out bool)"/> must run first.</returns>
    public int Append(ReadOnlySpan<byte> data)
    {
        int total = 0;
        while (data.Length > 0)
        {
            int capacity = _buf.Length - LzmaMatchFinder.BufferPadding;
            int free = capacity - _bufEnd;
            if (free == 0)
            {
                if (capacity < _maxCapacity)
                {
                    int grown = (int)Math.Min((long)capacity * 2, _maxCapacity);
                    Array.Resize(ref _buf, grown + LzmaMatchFinder.BufferPadding);
                    continue;
                }

                if (!Slide())
                {
                    break;
                }

                continue;
            }

            int n = Math.Min(free, data.Length);
            data[..n].CopyTo(_buf.AsSpan(_bufEnd));
            _bufEnd += n;
            data = data[n..];
            total += n;
        }

        return total;
    }

    /// <summary>Gets the bytes encoded most recently, for a caller that decides to store a stretch uncompressed.</summary>
    /// <param name="length">How many of the last encoded bytes; at most what <see cref="Encode(bool, int, long, out bool)"/> returned last.</param>
    /// <returns>A view into the window; valid until the next <see cref="Append"/>.</returns>
    public ReadOnlySpan<byte> GetEncoded(int length) => _buf.AsSpan(_pos - length, length);

    /// <summary>Encodes buffered input.</summary>
    /// <param name="drain">True to encode everything buffered; false to keep the last <see cref="LookaheadLength"/> bytes back.</param>
    /// <param name="maxBytes">The most input bytes to encode.</param>
    /// <param name="maxCompressed">The size the range encoder's stream (including its flush) must not exceed, counted from its last reset; <see cref="long.MaxValue"/> for no limit.</param>
    /// <returns>The number of input bytes encoded.</returns>
    public int Encode(bool drain, int maxBytes = int.MaxValue, long maxCompressed = long.MaxValue) =>
        Encode(drain, maxBytes, maxCompressed, out _);

    /// <summary>Encodes buffered input and says why it stopped.</summary>
    /// <param name="drain">True to encode everything buffered; false to keep the last <see cref="LookaheadLength"/> bytes back.</param>
    /// <param name="maxBytes">The most input bytes to encode.</param>
    /// <param name="maxCompressed">The size the range encoder's stream (including its flush) must not exceed, counted from its last reset; <see cref="long.MaxValue"/> for no limit.</param>
    /// <param name="limitReached">True when it stopped because <paramref name="maxBytes"/> or <paramref name="maxCompressed"/> left no room for more; false when it ran out of input (or lookahead).</param>
    /// <returns>The number of input bytes encoded.</returns>
    public int Encode(bool drain, int maxBytes, long maxCompressed, out bool limitReached)
    {
        limitReached = false;
        int start = _pos;
        bool normal = _settings.Mode == LzmaMode.Normal;
        while (true)
        {
            int avail = _bufEnd - _pos;
            if (avail == 0 || (!drain && avail < LookaheadLength))
            {
                break;
            }

            int room = maxBytes - (_pos - start);
            if (room <= 0)
            {
                limitReached = true;
                break;
            }

            int horizon = Math.Min(Math.Min(avail, MaxParseLength), room);
            if (maxCompressed != long.MaxValue)
            {
                long budget = maxCompressed - _rc.PendingLength - WorstBytesPerSymbol;
                if (budget < WorstBytesPerInputByte)
                {
                    limitReached = true;
                    break;
                }

                horizon = (int)Math.Min(horizon, budget / WorstBytesPerInputByte);
            }

            if (normal)
            {
                ParseNormal(horizon);
            }
            else
            {
                ParseFast(horizon);
            }
        }

        return _pos - start;
    }

    /// <summary>Codes the end-of-stream marker: a match of length 2 at the distance 0xFFFFFFFF, which no real match can have.</summary>
    public void EncodeEndMarker()
    {
        int posState = (int)((_posBase + _pos) & _pbMask);
        _rc.EncodeBit(ref _probs[LzmaModel.IsMatch + (_state << 4) + posState], 1);
        _rc.EncodeBit(ref _probs[LzmaModel.IsRep + _state], 0);
        EncodeLength(LzmaModel.LengthCoder, 2, posState);
        EncodeDistance(uint.MaxValue, 0);
        _state = LzmaModel.NextStateAfterMatch(_state);
    }

    // Moves the data down so that about one dictionary of history stays, and tells everything that holds indexes.
    private bool Slide()
    {
        int delta = _pos - _history;
        if (delta <= 0)
        {
            return false;
        }

        Buffer.BlockCopy(_buf, delta, _buf, 0, _bufEnd - delta);
        _bufEnd -= delta;
        _pos -= delta;
        _posBase += delta;
        _finderPos -= delta;
        _cachePos -= delta;
        _finder.Slide(delta);
        return true;
    }

    private void AdvanceFinder(int target)
    {
        while (_finderPos < target)
        {
            _finder.Skip(_buf, _finderPos, _bufEnd - _finderPos);
            _finderPos++;
        }
    }

    // Matches at position p into `destination`; the finder must be at p, or hold the cached result for p.
    private int MatchesAt(int p, LzmaMatch[] destination)
    {
        if (_cacheCount >= 0 && _cachePos == p)
        {
            int n = _cacheCount;
            Array.Copy(_matchesNext, destination, n);
            _cacheCount = -1;
            return n;
        }

        _cacheCount = -1;
        int count = _finder.FindMatches(_buf, p, _bufEnd - p, destination);
        _finderPos = p + 1;
        return count;
    }

    // Matches at p + 1, kept for the next decision when the parser decides to code a literal at p.
    private int PeekNext(int p)
    {
        if (_cacheCount < 0 || _cachePos != p + 1)
        {
            _cacheCount = _finder.FindMatches(_buf, p + 1, _bufEnd - p - 1, _matchesNext);
            _cachePos = p + 1;
            _finderPos = p + 2;
        }

        return _cacheCount;
    }

    private int RepLength(int p, uint rep, int maxLen)
    {
        int src = p - (int)rep - 1;
        if (src < 1 || maxLen < 2)
        {
            return 0;
        }

        byte[] b = _buf;
        if (b[src] != b[p] || b[src + 1] != b[p + 1])
        {
            return 0;
        }

        int len = 2;
        while (len < maxLen && b[src + len] == b[p + len])
        {
            len++;
        }

        return len;
    }

    private int LiteralBase(int p)
    {
        int ctx = (((int)((_posBase + p) & _lpMask)) << _settings.Lc) + (_buf[p - 1] >> (8 - _settings.Lc));
        return LzmaModel.Literal + (LzmaModel.LiteralCoderSize * ctx);
    }

    // ---- Coding ----

    private void EmitLiteral()
    {
        int p = _pos;
        int posState = (int)((_posBase + p) & _pbMask);
        _rc.EncodeBit(ref _probs[LzmaModel.IsMatch + (_state << 4) + posState], 0);
        int lit = LiteralBase(p);
        int symbol = _buf[p] | 0x100;
        if (_state < LzmaModel.LiteralStateLimit)
        {
            int m = 1;
            for (int i = 7; i >= 0; i--)
            {
                int bit = (symbol >> i) & 1;
                _rc.EncodeBit(ref _probs[lit + m], bit);
                m = (m << 1) | bit;
            }
        }
        else
        {
            int matchByte = _buf[p - (int)_reps[0] - 1];
            int m = 1;
            bool same = true;
            for (int i = 7; i >= 0; i--)
            {
                int bit = (symbol >> i) & 1;
                if (same)
                {
                    int matchBit = (matchByte >> i) & 1;
                    _rc.EncodeBit(ref _probs[lit + ((1 + matchBit) << 8) + m], bit);
                    same = matchBit == bit;
                }
                else
                {
                    _rc.EncodeBit(ref _probs[lit + m], bit);
                }

                m = (m << 1) | bit;
            }
        }

        _state = LzmaModel.NextStateAfterLiteral(_state);
        _pos = p + 1;
    }

    private void EmitShortRep()
    {
        int posState = (int)((_posBase + _pos) & _pbMask);
        _rc.EncodeBit(ref _probs[LzmaModel.IsMatch + (_state << 4) + posState], 1);
        _rc.EncodeBit(ref _probs[LzmaModel.IsRep + _state], 1);
        _rc.EncodeBit(ref _probs[LzmaModel.IsRepG0 + _state], 0);
        _rc.EncodeBit(ref _probs[LzmaModel.IsRep0Long + (_state << 4) + posState], 0);
        _state = LzmaModel.NextStateAfterShortRep(_state);
        _pos++;
    }

    private void EmitRep(int index, int length)
    {
        int posState = (int)((_posBase + _pos) & _pbMask);
        _rc.EncodeBit(ref _probs[LzmaModel.IsMatch + (_state << 4) + posState], 1);
        _rc.EncodeBit(ref _probs[LzmaModel.IsRep + _state], 1);
        uint[] reps = _reps;
        if (index == 0)
        {
            _rc.EncodeBit(ref _probs[LzmaModel.IsRepG0 + _state], 0);
            _rc.EncodeBit(ref _probs[LzmaModel.IsRep0Long + (_state << 4) + posState], 1);
        }
        else
        {
            _rc.EncodeBit(ref _probs[LzmaModel.IsRepG0 + _state], 1);
            uint dist;
            if (index == 1)
            {
                _rc.EncodeBit(ref _probs[LzmaModel.IsRepG1 + _state], 0);
                dist = reps[1];
            }
            else
            {
                _rc.EncodeBit(ref _probs[LzmaModel.IsRepG1 + _state], 1);
                _rc.EncodeBit(ref _probs[LzmaModel.IsRepG2 + _state], index - 2);
                if (index == 2)
                {
                    dist = reps[2];
                }
                else
                {
                    dist = reps[3];
                    reps[3] = reps[2];
                }

                reps[2] = reps[1];
            }

            reps[1] = reps[0];
            reps[0] = dist;
        }

        EncodeLength(LzmaModel.RepLengthCoder, length, posState);
        _state = LzmaModel.NextStateAfterRep(_state);
        _pos += length;
    }

    private void EmitMatch(int length, uint distance0)
    {
        int posState = (int)((_posBase + _pos) & _pbMask);
        _rc.EncodeBit(ref _probs[LzmaModel.IsMatch + (_state << 4) + posState], 1);
        _rc.EncodeBit(ref _probs[LzmaModel.IsRep + _state], 0);
        EncodeLength(LzmaModel.LengthCoder, length, posState);
        EncodeDistance(distance0, Math.Min(length - 2, LzmaModel.LengthToPositionStates - 1));
        uint[] reps = _reps;
        reps[3] = reps[2];
        reps[2] = reps[1];
        reps[1] = reps[0];
        reps[0] = distance0;
        _state = LzmaModel.NextStateAfterMatch(_state);
        _pos += length;
    }

    private void EncodeLength(int coder, int length, int posState)
    {
        int l = length - LzmaModel.MinMatchLength;
        if (l < 8)
        {
            _rc.EncodeBit(ref _probs[coder], 0);
            _rc.EncodeTree(_probs, coder + LzmaModel.LengthLow + (posState << 3), 3, (uint)l);
        }
        else if (l < 16)
        {
            _rc.EncodeBit(ref _probs[coder], 1);
            _rc.EncodeBit(ref _probs[coder + LzmaModel.LengthChoice2], 0);
            _rc.EncodeTree(_probs, coder + LzmaModel.LengthMid + (posState << 3), 3, (uint)(l - 8));
        }
        else
        {
            _rc.EncodeBit(ref _probs[coder], 1);
            _rc.EncodeBit(ref _probs[coder + LzmaModel.LengthChoice2], 1);
            _rc.EncodeTree(_probs, coder + LzmaModel.LengthHigh, 8, (uint)(l - 16));
        }
    }

    private static int GetSlot(uint distance)
    {
        if (distance < 4)
        {
            return (int)distance;
        }

        int n = 31 - BitOperations.LeadingZeroCount(distance);
        return (n << 1) | (int)((distance >> (n - 1)) & 1);
    }

    private void EncodeDistance(uint distance, int lenState)
    {
        int slot = GetSlot(distance);
        _rc.EncodeTree(_probs, LzmaModel.PositionSlot + (lenState << 6), 6, (uint)slot);
        if (slot < 4)
        {
            return;
        }

        int footer = (slot >> 1) - 1;
        uint baseValue = (2u | (uint)(slot & 1)) << footer;
        uint reduced = distance - baseValue;
        if (slot < LzmaModel.EndPositionModelIndex)
        {
            _rc.EncodeReverseTree(_probs, LzmaModel.PositionSpecial + (int)baseValue - slot, footer, reduced);
        }
        else
        {
            _rc.EncodeDirectBits(reduced >> LzmaModel.AlignBits, footer - LzmaModel.AlignBits);
            _rc.EncodeReverseTree(_probs, LzmaModel.Align, LzmaModel.AlignBits, reduced & 15);
        }
    }
}
