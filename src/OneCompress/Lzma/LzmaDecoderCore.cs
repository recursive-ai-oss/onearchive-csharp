using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>What stopped <see cref="LzmaDecoderCore.Decode"/>.</summary>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum LzmaStatus
{
    /// <summary>The input ended inside a symbol. Nothing of that symbol was applied: pass its bytes again, followed by more.</summary>
    NeedInput = 0,

    /// <summary>The destination is full or the requested amount of output was produced. A match may be partly copied; the rest is copied by the next call.</summary>
    OutputFull = 1,

    /// <summary>The end-of-stream marker (a match with distance 2^32 - 1) was decoded.</summary>
    EndMarker = 2,

    /// <summary>The data is corrupt: a match distance reaches before the start of the dictionary or beyond its size, or a symbol that must be the end marker is not.</summary>
    InvalidData = 3,
}

/// <summary>
/// The LZMA symbol decoder: the probability model, the 12-state machine, the four recent distances and the sliding window,
/// without any container around them. <see cref="LzmaDecoder"/>, <see cref="Lzma2Decoder"/> and the lzip decoder drive it, and an
/// encoder can share <see cref="LzmaModel"/> with it.
/// </summary>
/// <remarks>
/// <para>Usage: <see cref="Configure"/> the literal and position bits, <see cref="SetDictionary"/>, <see cref="ResetDictionary"/>,
/// <see cref="ResetState"/>, <see cref="StartRangeDecoder"/> on the first 5 bytes of the range-coded data, then call
/// <see cref="Decode"/> with the bytes that follow, as often as needed. LZMA2 calls the reset methods at chunk boundaries.</para>
/// <para>The window is the dictionary: it grows from a small start up to the dictionary size as output is produced (so a stream that
/// declares a large dictionary but is short costs little), then wraps. A symbol is applied only when all its bytes were present; a
/// short input rolls the symbol back and reports <see cref="LzmaStatus.NeedInput"/>, so any split of the input gives the same result.</para>
/// <para>Not thread-safe. LZMA specification, "LZMA Decoding".</para>
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed partial class LzmaDecoderCore
{
    // One symbol reads at most about 20 bytes: 22 adaptive decisions of at most 6.05 bits each plus 26 direct bits.
    // With this much input at hand a symbol cannot run out, so it needs no undo log.
    private const int SymbolInputMargin = 32;

    private const int JournalCapacity = 128;
    private const int InitialWindow = 1 << 16;

    // Spare bytes behind the ring (the ring is the dictionary size plus this much, so bytes a wide copy overshoots onto were
    // already too old for any match to reach) and behind the array (so the overshoot of a copy that ends at the ring's end
    // stays inside it).
    internal const int WindowSlack = 16;

    private ushort[] _probs = [];
    private int _lc;
    private int _lp;
    private int _pb;
    private byte[] _window = [];
    private int _capacity;
    private int _pos;
    private bool _full;
    private uint _dictionarySize = LzmaProperties.MinimumDictionarySize;
    private long _total;
    private int _state;
    private uint _rep0;
    private uint _rep1;
    private uint _rep2;
    private uint _rep3;
    private int _matchLeft;
    private LzmaRangeDecoder _rc;
    private readonly int[] _journalIndex = new int[JournalCapacity];
    private readonly ushort[] _journalValue = new ushort[JournalCapacity];
    private int _journalCount;
    private readonly byte[] _carry = new byte[SymbolInputMargin * 2];
    private readonly byte[] _bridge = new byte[SymbolInputMargin * 2];
    private int _carryLen;

    /// <summary>Gets the dictionary size in effect, in bytes.</summary>
    public uint DictionarySize => _dictionarySize;

    /// <summary>Gets the number of bytes produced since the last <see cref="ResetDictionary"/>.</summary>
    public long TotalPosition => _total;

    /// <summary>Gets the number of bytes of the last match that are still to be copied because the output was full.</summary>
    public int PendingMatchLength => _matchLeft;

    /// <summary>Gets a value indicating whether the range decoder's code is 0, as it is after the last decision of an undamaged stream.</summary>
    public bool IsRangeDecoderFinished => _rc.IsFinishedOk;

    /// <summary>Gets the literal context bits in effect.</summary>
    public int Lc => _lc;

    /// <summary>Gets the literal position bits in effect.</summary>
    public int Lp => _lp;

    /// <summary>Gets the position bits in effect.</summary>
    public int Pb => _pb;

    /// <summary>Sets the literal and position bits. The probabilities keep stale values until <see cref="ResetState"/>.</summary>
    /// <param name="lc">Literal context bits, 0 to 8.</param>
    /// <param name="lp">Literal position bits, 0 to 4.</param>
    /// <param name="pb">Position bits, 0 to 4.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its range.</exception>
    public void Configure(int lc, int lp, int pb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lc);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lc, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(lp);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lp, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(pb);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pb, 4);
        _lc = lc;
        _lp = lp;
        _pb = pb;
        int n = LzmaModel.ProbabilityCount(lc, lp);
        if (_probs.Length < n)
        {
            _probs = new ushort[n];
        }
    }

    /// <summary>Sets the dictionary size. Call <see cref="ResetDictionary"/> afterwards.</summary>
    /// <param name="size">The size in bytes; values below 4 KiB are raised to 4 KiB.</param>
    /// <exception cref="CompressionLimitException">The size cannot be held in one array on this runtime.</exception>
    public void SetDictionary(uint size)
    {
        size = Math.Max(size, LzmaProperties.MinimumDictionarySize);
        if (size > Array.MaxLength - 2 * WindowSlack)
        {
            throw new CompressionLimitException("lzma", "MaxDictionarySize", Array.MaxLength - 2 * WindowSlack, size);
        }

        _dictionarySize = size;
    }

    /// <summary>Empties the dictionary: the next match may only reach back to bytes produced after this call.</summary>
    /// <param name="expectedOutput">How much output is expected, or 0 when unknown; it only picks the starting size of the window.</param>
    public void ResetDictionary(long expectedOutput = 0)
    {
        _pos = 0;
        _full = false;
        _total = 0;
        _matchLeft = 0;
        long start = expectedOutput > 0 ? Math.Clamp(expectedOutput, LzmaProperties.MinimumDictionarySize, 1 << 22) : InitialWindow;
        _capacity = (int)Math.Min(_dictionarySize + (long)WindowSlack, start);
        if (_window.Length < _capacity + WindowSlack)
        {
            _window = new byte[_capacity + WindowSlack];
        }
    }

    /// <summary>Resets the probabilities, the state and the recent distances to their initial values.</summary>
    public void ResetState()
    {
        LzmaModel.ResetProbabilities(_probs.AsSpan(0, LzmaModel.ProbabilityCount(_lc, _lp)));
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        _matchLeft = 0;
        _carryLen = 0;
    }

    /// <summary>Starts a range-coded stream at the beginning of <paramref name="input"/>.</summary>
    /// <param name="input">At least 5 bytes.</param>
    /// <returns>False when the first byte is not 0, which the format requires.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than 5 bytes were given.</exception>
    public bool StartRangeDecoder(ReadOnlySpan<byte> input)
    {
        _carryLen = 0;
        return _rc.Init(input, 0);
    }

    /// <summary>Appends bytes that are stored rather than coded (an LZMA2 uncompressed chunk) to the dictionary and copies them to <paramref name="destination"/>.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="destination">Receives the same bytes.</param>
    /// <returns>The number of bytes taken: the smaller of the two lengths.</returns>
    public int Append(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int n = Math.Min(data.Length, destination.Length);
        int done = 0;
        while (done < n)
        {
            if (_pos == _capacity)
            {
                AdvanceWindow();
            }

            int k = Math.Min(n - done, _capacity - _pos);
            ReadOnlySpan<byte> part = data.Slice(done, k);
            part.CopyTo(_window.AsSpan(_pos));
            part.CopyTo(destination[done..]);
            _pos += k;
            _total += k;
            done += k;
        }

        return n;
    }

    /// <summary>Decodes symbols until the destination is full, <paramref name="remaining"/> bytes were produced, the input runs out, or the end marker comes.</summary>
    /// <param name="input">The range-coded bytes that follow those already consumed.</param>
    /// <param name="consumed">Receives how many bytes of <paramref name="input"/> were used. When a symbol is cut by the end of the input, its bytes are kept inside the decoder and counted as consumed, and the symbol is applied by the next call.</param>
    /// <param name="destination">Receives the output.</param>
    /// <param name="written">Receives the number of bytes written.</param>
    /// <param name="remaining">The most output wanted by this call, for a stream whose size is known; <see cref="long.MaxValue"/> otherwise.</param>
    /// <returns>Why decoding stopped.</returns>
    public LzmaStatus Decode(ReadOnlySpan<byte> input, out int consumed, Span<byte> destination, out int written, long remaining = long.MaxValue)
    {
        written = 0;
        int skip = 0;
        if (_carryLen > 0)
        {
            LzmaStatus s = Bridge(input, destination, ref written, remaining, markerOnly: false, out skip, out bool finished);
            if (finished)
            {
                consumed = skip;
                return s;
            }
        }

        ReadOnlySpan<byte> rest = input[skip..];
        _rc.Position = 0;
        LzmaStatus status;
        while (true)
        {
            long budget = Math.Min(remaining, destination.Length) - written;
            if (budget <= 0)
            {
                status = LzmaStatus.OutputFull;
                break;
            }

            if (_pos == _capacity)
            {
                AdvanceWindow();
            }

            int start = _pos;
            int limit = start + (int)Math.Min(budget, _capacity - _pos);
            int r = RunSegment(rest, limit, markerOnly: false);
            int produced = _pos - start;
            _window.AsSpan(start, produced).CopyTo(destination[written..]);
            written += produced;
            if (r != (int)LzmaStatus.OutputFull)
            {
                status = (LzmaStatus)r;
                break;
            }
        }

        int used = Math.Min(_rc.Position, rest.Length);
        if (status == LzmaStatus.NeedInput)
        {
            KeepTail(rest[used..]);
            used = rest.Length;
        }

        consumed = skip + used;
        return status;
    }

    /// <summary>Decodes one symbol that must be the end marker (used after the declared size was reached while the range decoder is not finished).</summary>
    /// <param name="input">The range-coded bytes that follow those already consumed.</param>
    /// <param name="consumed">Receives how many bytes were used (bytes of a cut symbol are kept inside the decoder, as for <see cref="Decode"/>).</param>
    /// <returns><see cref="LzmaStatus.EndMarker"/>, <see cref="LzmaStatus.NeedInput"/>, or <see cref="LzmaStatus.InvalidData"/> when the symbol is anything else.</returns>
    public LzmaStatus DecodeEndMarker(ReadOnlySpan<byte> input, out int consumed)
    {
        int skip = 0;
        int none = 0;
        if (_carryLen > 0)
        {
            LzmaStatus s = Bridge(input, default, ref none, long.MaxValue, markerOnly: true, out skip, out bool finished);
            if (finished)
            {
                consumed = skip;
                return s;
            }
        }

        ReadOnlySpan<byte> rest = input[skip..];
        _rc.Position = 0;
        var status = (LzmaStatus)Run<JournalOn>(rest, _pos, single: true, markerOnly: true);
        int used = Math.Min(_rc.Position, rest.Length);
        if (status == LzmaStatus.NeedInput)
        {
            KeepTail(rest[used..]);
            used = rest.Length;
        }

        consumed = skip + used;
        return status;
    }

    // A symbol cut by the end of the input: its bytes wait here until the next call brings the rest.
    private void KeepTail(ReadOnlySpan<byte> tail)
    {
        tail.CopyTo(_carry);
        _carryLen = tail.Length;
    }

    // Decodes the one symbol that was cut at the end of the previous input, from the saved bytes plus the first bytes of the new input.
    // finished is set when the caller's call is over (the result is then the call's status and skip the bytes of input it consumed).
    private LzmaStatus Bridge(ReadOnlySpan<byte> input, Span<byte> destination, ref int written, long remaining, bool markerOnly, out int skip, out bool finished)
    {
        skip = 0;
        finished = true;
        long budget = Math.Min(remaining, destination.Length) - written;
        if (!markerOnly && budget <= 0)
        {
            return LzmaStatus.OutputFull;
        }

        int take = Math.Min(input.Length, SymbolInputMargin);
        Span<byte> joined = _bridge.AsSpan(0, _carryLen + take);
        _carry.AsSpan(0, _carryLen).CopyTo(joined);
        input[..take].CopyTo(joined[_carryLen..]);
        if (_pos == _capacity)
        {
            AdvanceWindow();
        }

        int start = _pos;
        int limit = markerOnly ? start : start + (int)Math.Min(budget, _capacity - _pos);
        _rc.Position = 0;
        int r = Run<JournalOn>(joined, limit, single: true, markerOnly);
        int produced = _pos - start;
        if (produced > 0)
        {
            _window.AsSpan(start, produced).CopyTo(destination[written..]);
            written += produced;
        }

        if (r == (int)LzmaStatus.NeedInput)
        {
            if (take < input.Length)
            {
                // A symbol never needs this many bytes; treat it as damaged data rather than loop.
                return LzmaStatus.InvalidData;
            }

            joined.CopyTo(_carry);
            _carryLen = joined.Length;
            skip = input.Length;
            return LzmaStatus.NeedInput;
        }

        skip = Math.Max(0, _rc.Position - _carryLen);
        _carryLen = 0;
        if (r == SymbolDone)
        {
            finished = false;
            return LzmaStatus.OutputFull;
        }

        return (LzmaStatus)r;
    }

    private void AdvanceWindow()
    {
        if (_capacity < _dictionarySize + (long)WindowSlack)
        {
            // Still filling the dictionary: grow instead of wrapping.
            long target = Math.Min(_dictionarySize + (long)WindowSlack, (long)_capacity * 2);
            if (_window.Length < target + WindowSlack)
            {
                byte[] bigger = new byte[target + WindowSlack];
                _window.AsSpan(0, _pos).CopyTo(bigger);
                _window = bigger;
            }

            _capacity = (int)target;
        }
        else
        {
            _pos = 0;
            _full = true;
        }
    }

    private const int Low = 4;
    private const int SymbolDone = 5;

    private int RunSegment(ReadOnlySpan<byte> input, int limit, bool markerOnly)
    {
        while (true)
        {
            int r = markerOnly ? Run<JournalOff>(input, limit, single: false, markerOnly) : RunFast(input, limit);
            if (r != Low)
            {
                return r;
            }

            r = Run<JournalOn>(input, limit, single: true, markerOnly);
            if (r != SymbolDone)
            {
                return r;
            }
        }
    }

    private struct JournalOn;

    private struct JournalOff;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Bit<TJ>(ref LzmaRangeDecoder rc, ReadOnlySpan<byte> input, ushort[] p, int index)
    {
        if (typeof(TJ) == typeof(JournalOn))
        {
            int n = _journalCount;
            _journalIndex[n] = index;
            _journalValue[n] = p[index];
            _journalCount = n + 1;
        }

        return rc.DecodeBit(input, ref p[index]);
    }

    private int Tree<TJ>(ref LzmaRangeDecoder rc, ReadOnlySpan<byte> input, ushort[] p, int offset, int bits)
    {
        int m = 1;
        for (int i = 0; i < bits; i++)
        {
            m = (m << 1) + Bit<TJ>(ref rc, input, p, offset + m);
        }

        return m - (1 << bits);
    }

    private int ReverseTree<TJ>(ref LzmaRangeDecoder rc, ReadOnlySpan<byte> input, ushort[] p, int offset, int bits)
    {
        int m = 1;
        int symbol = 0;
        for (int i = 0; i < bits; i++)
        {
            int bit = Bit<TJ>(ref rc, input, p, offset + m);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }

        return symbol;
    }

    // LZMA specification, "The match length decoding": a zero-based length 0..271.
    private int Length<TJ>(ref LzmaRangeDecoder rc, ReadOnlySpan<byte> input, ushort[] p, int coder, int posState)
    {
        if (Bit<TJ>(ref rc, input, p, coder) == 0)
        {
            return Tree<TJ>(ref rc, input, p, coder + LzmaModel.LengthLow + (posState << 3), 3);
        }

        if (Bit<TJ>(ref rc, input, p, coder + LzmaModel.LengthChoice2) == 0)
        {
            return 8 + Tree<TJ>(ref rc, input, p, coder + LzmaModel.LengthMid + (posState << 3), 3);
        }

        return 16 + Tree<TJ>(ref rc, input, p, coder + LzmaModel.LengthHigh, 8);
    }

    // LZMA specification, "The match distance decoding": the zero-based distance; 0xFFFFFFFF is the end marker.
    private uint Distance<TJ>(ref LzmaRangeDecoder rc, ReadOnlySpan<byte> input, ushort[] p, int length)
    {
        int lenState = Math.Min(length, LzmaModel.LengthToPositionStates - 1);
        int slot = Tree<TJ>(ref rc, input, p, LzmaModel.PositionSlot + (lenState << 6), 6);
        if (slot < 4)
        {
            return (uint)slot;
        }

        int directBits = (slot >> 1) - 1;
        uint dist = (2u | (uint)(slot & 1)) << directBits;
        if (slot < LzmaModel.EndPositionModelIndex)
        {
            return dist + (uint)ReverseTree<TJ>(ref rc, input, p, LzmaModel.PositionSpecial + (int)dist - slot, directBits);
        }

        dist += rc.DecodeDirectBits(input, directBits - LzmaModel.AlignBits) << LzmaModel.AlignBits;
        return dist + (uint)ReverseTree<TJ>(ref rc, input, p, LzmaModel.Align, LzmaModel.AlignBits);
    }

    // Copies a match inside the window. dist is the distance (1 or more); the caller has checked that it reaches only bytes
    // produced since the dictionary was reset and no further than the dictionary size, so the source index is inside the array.
    private static void CopyMatch(byte[] w, int pos, int capacity, int dist, int length)
    {
        int src = pos - dist;
        if (src < 0)
        {
            src += capacity;
        }

        if (dist >= 8 && src + length <= capacity && pos + length <= capacity)
        {
            // The common case: a block move in eight-byte steps. Distances below eight overlap their own step and fall through.
            LzmaRaw.CopyWide(w, pos, src, length);
            return;
        }

        if (length <= 24 && src + length <= capacity && pos + length <= capacity)
        {
            // Copying forward byte by byte is right when the source overlaps the destination (distance below length), as the
            // format requires.
            for (int i = 0; i < length; i++)
            {
                w[pos + i] = w[src + i];
            }

            return;
        }

        if (src < pos)
        {
            if (pos - src >= length)
            {
                w.AsSpan(src, length).CopyTo(w.AsSpan(pos));
                return;
            }
        }
        else if (src + length <= capacity)
        {
            // The source lies ahead of the destination in the array (the dictionary has wrapped); copying from
            // the front never reads a byte it has already overwritten, so a block move gives the same bytes.
            w.AsSpan(src, length).CopyTo(w.AsSpan(pos));
            return;
        }

        for (int i = 0; i < length; i++)
        {
            w[pos++] = w[src++];
            if (src == capacity)
            {
                src = 0;
            }
        }
    }

    private int Run<TJ>(ReadOnlySpan<byte> input, int limit, bool single, bool markerOnly)
        where TJ : struct
    {
        bool journal = typeof(TJ) == typeof(JournalOn);
        byte[] w = _window;
        ushort[] p = _probs;
        int capacity = _capacity;
        LzmaRangeDecoder rc = _rc;
        int pos = _pos;
        long total = _total;
        int state = _state;
        uint rep0 = _rep0;
        uint rep1 = _rep1;
        uint rep2 = _rep2;
        uint rep3 = _rep3;
        int lc = _lc;
        int lpMask = (1 << _lp) - 1;
        int pbMask = (1 << _pb) - 1;
        uint dictSize = _dictionarySize;
        int result;

        // Undo information for a symbol decoded from too little input.
        LzmaRangeDecoder sRc = default;
        int sState = 0;
        uint sRep0 = 0, sRep1 = 0, sRep2 = 0, sRep3 = 0;
        _journalCount = 0;

        while (true)
        {
            if (_matchLeft > 0)
            {
                int n = Math.Min(_matchLeft, limit - pos);
                if (n > 0)
                {
                    CopyMatch(w, pos, capacity, (int)rep0 + 1, n);
                    pos += n;
                    total += n;
                    _matchLeft -= n;
                }

                if (_matchLeft > 0)
                {
                    result = (int)LzmaStatus.OutputFull;
                    break;
                }

                if (single)
                {
                    result = SymbolDone;
                    break;
                }
            }

            if (!markerOnly && pos >= limit)
            {
                result = (int)LzmaStatus.OutputFull;
                break;
            }

            if (!journal && input.Length - rc.Position < SymbolInputMargin)
            {
                result = Low;
                break;
            }

            if (journal)
            {
                sRc = rc;
                sState = state;
                sRep0 = rep0;
                sRep1 = rep1;
                sRep2 = rep2;
                sRep3 = rep3;
                _journalCount = 0;
            }

            int posState = (int)total & pbMask;
            if (Bit<TJ>(ref rc, input, p, LzmaModel.IsMatch + (state << 4) + posState) == 0)
            {
                // LZMA specification, "The Literal Decoding".
                if (journal && rc.Position > input.Length)
                {
                    goto Rollback;
                }

                if (markerOnly)
                {
                    result = (int)LzmaStatus.InvalidData;
                    break;
                }

                int prev = pos > 0 ? w[pos - 1] : (_full ? w[capacity - 1] : 0);
                int lit = LzmaModel.Literal + (LzmaModel.LiteralCoderSize * ((((int)total & lpMask) << lc) + (prev >> (8 - lc))));
                int sym = 1;
                if (state >= LzmaModel.LiteralStateLimit)
                {
                    int mpos = pos - (int)rep0 - 1;
                    if (mpos < 0)
                    {
                        mpos += capacity;
                    }

                    int matchByte = w[mpos];
                    do
                    {
                        int matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        int bit = Bit<TJ>(ref rc, input, p, lit + ((1 + matchBit) << 8) + sym);
                        sym = (sym << 1) | bit;
                        if (matchBit != bit)
                        {
                            break;
                        }
                    }
                    while (sym < 0x100);
                }

                while (sym < 0x100)
                {
                    sym = (sym << 1) | Bit<TJ>(ref rc, input, p, lit + sym);
                }

                if (journal && rc.Position > input.Length)
                {
                    goto Rollback;
                }

                w[pos++] = (byte)sym;
                total++;
                state = LzmaModel.NextStateAfterLiteral(state);
                if (single)
                {
                    result = SymbolDone;
                    break;
                }

                continue;
            }

            int length;
            if (Bit<TJ>(ref rc, input, p, LzmaModel.IsRep + state) == 0)
            {
                // A match with a newly coded distance.
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                length = Length<TJ>(ref rc, input, p, LzmaModel.LengthCoder, posState);
                state = LzmaModel.NextStateAfterMatch(state);
                uint dist = Distance<TJ>(ref rc, input, p, length);
                if (journal && rc.Position > input.Length)
                {
                    goto Rollback;
                }

                if (dist == 0xFFFFFFFF)
                {
                    result = (int)LzmaStatus.EndMarker;
                    break;
                }

                if (markerOnly || dist >= dictSize || dist >= total)
                {
                    result = (int)LzmaStatus.InvalidData;
                    break;
                }

                rep0 = dist;
            }
            else
            {
                if (journal && rc.Position > input.Length)
                {
                    goto Rollback;
                }

                if (markerOnly || total == 0)
                {
                    result = (int)LzmaStatus.InvalidData;
                    break;
                }

                if (Bit<TJ>(ref rc, input, p, LzmaModel.IsRepG0 + state) == 0)
                {
                    if (Bit<TJ>(ref rc, input, p, LzmaModel.IsRep0Long + (state << 4) + posState) == 0)
                    {
                        // A short rep: one byte at the last distance.
                        if (journal && rc.Position > input.Length)
                        {
                            goto Rollback;
                        }

                        int sp = pos - (int)rep0 - 1;
                        if (sp < 0)
                        {
                            sp += capacity;
                        }

                        w[pos++] = w[sp];
                        total++;
                        state = LzmaModel.NextStateAfterShortRep(state);
                        if (single)
                        {
                            result = SymbolDone;
                            break;
                        }

                        continue;
                    }
                }
                else
                {
                    uint d;
                    if (Bit<TJ>(ref rc, input, p, LzmaModel.IsRepG1 + state) == 0)
                    {
                        d = rep1;
                    }
                    else
                    {
                        if (Bit<TJ>(ref rc, input, p, LzmaModel.IsRepG2 + state) == 0)
                        {
                            d = rep2;
                        }
                        else
                        {
                            d = rep3;
                            rep3 = rep2;
                        }

                        rep2 = rep1;
                    }

                    rep1 = rep0;
                    rep0 = d;
                }

                length = Length<TJ>(ref rc, input, p, LzmaModel.RepLengthCoder, posState);
                state = LzmaModel.NextStateAfterRep(state);
                if (journal && rc.Position > input.Length)
                {
                    goto Rollback;
                }
            }

            length += LzmaModel.MinMatchLength;
            int copy = Math.Min(length, limit - pos);
            CopyMatch(w, pos, capacity, (int)rep0 + 1, copy);
            pos += copy;
            total += copy;
            _matchLeft = length - copy;
            if (single)
            {
                result = SymbolDone;
                break;
            }

            continue;

        Rollback:
            rc = sRc;
            state = sState;
            rep0 = sRep0;
            rep1 = sRep1;
            rep2 = sRep2;
            rep3 = sRep3;
            for (int k = _journalCount - 1; k >= 0; k--)
            {
                p[_journalIndex[k]] = _journalValue[k];
            }

            _journalCount = 0;
            result = (int)LzmaStatus.NeedInput;
            break;
        }

        _rc = rc;
        _pos = pos;
        _total = total;
        _state = state;
        _rep0 = rep0;
        _rep1 = rep1;
        _rep2 = rep2;
        _rep3 = rep3;
        return result;
    }
}
