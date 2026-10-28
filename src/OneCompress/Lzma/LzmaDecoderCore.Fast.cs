using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

public sealed partial class LzmaDecoderCore
{
    // The range coder state while a symbol is decoded by the non-inlined helpers below. The loop itself keeps range, code and
    // the input index in locals so the JIT can hold them in registers; the helpers take the state in, work on their own locals
    // and hand it back, instead of going through memory once per bit.
    private struct Rc
    {
        public uint Range;
        public uint Code;
        public nint Ip;
    }

    // LZMA specification, "The Range Decoder": one adaptive bit, then normalization.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FBit(ushort[] p, int index, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref nint ip)
    {
        ref ushort prob = ref LzmaRaw.P(p, index);
        uint pv = prob;
        uint bound = (range >> 11) * pv;
        int bit;
        if (code < bound)
        {
            range = bound;
            prob = (ushort)(pv + ((2048 - pv) >> 5));
            bit = 0;
        }
        else
        {
            range -= bound;
            code -= bound;
            prob = (ushort)(pv - (pv >> 5));
            bit = 1;
        }

        if (range < (1u << 24))
        {
            range <<= 8;
            code = (code << 8) | LzmaRaw.In(input, ip);
            ip++;
        }

        return bit;
    }

    // The same decoding step without a branch on the decoded bit: the bit picks between two results with masks, which suits the
    // places where the bit only feeds an index (literal and tree bits) and the branch would be taken at random.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FBitNb(ushort[] p, int index, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref nint ip)
    {
        ref ushort prob = ref LzmaRaw.P(p, index);
        uint pv = prob;
        uint bound = (range >> 11) * pv;
        uint mask = (uint)((long)((ulong)code - bound) >> 63); // all ones when code < bound (bit 0)
        range = (bound & mask) | ((range - bound) & ~mask);
        code -= bound & ~mask;
        prob = (ushort)(pv - (uint)((int)(pv - (mask & 2017u)) >> 5));
        if (range < (1u << 24))
        {
            range <<= 8;
            code = (code << 8) | LzmaRaw.In(input, ip);
            ip++;
        }

        return (int)mask + 1;
    }

    // One bit of a literal tree with the probability already in hand. Returns all ones for a 0 bit and 0 for a 1 bit, so the
    // caller can select the next probability without a branch. The caller loads the two possible next probabilities while this
    // bit is still being resolved, which takes the load off the chain that links one bit to the next.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint NbStep(ref ushort prob, uint pv, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref nint ip)
    {
        uint bound = (range >> 11) * pv;
        uint mask = (uint)((long)((ulong)code - bound) >> 63);
        range = (bound & mask) | ((range - bound) & ~mask);
        code -= bound & ~mask;
        prob = (ushort)(pv - (uint)((int)(pv - (mask & 2017u)) >> 5));
        if (range < (1u << 24))
        {
            range <<= 8;
            code = (code << 8) | LzmaRaw.In(input, ip);
            ip++;
        }

        return mask;
    }

    // A 3-bit tree, unrolled: no loop counter, and the JIT can keep every node's values in registers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FTree3(ushort[] p, int offset, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref nint ip)
    {
        int m = 1;
        uint pv = LzmaRaw.P(p, offset + 1);
        uint c0;
        uint c1;
        uint mask;
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        m = (m << 1) + (int)(NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip) + 1);
        return m - 8;
    }

    // A 6-bit tree, unrolled: no loop counter, and the JIT can keep every node's values in registers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FTree6(ushort[] p, int offset, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref nint ip)
    {
        int m = 1;
        uint pv = LzmaRaw.P(p, offset + 1);
        uint c0;
        uint c1;
        uint mask;
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        m = (m << 1) + (int)(NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip) + 1);
        return m - 64;
    }

    // A 8-bit tree, unrolled: no loop counter, and the JIT can keep every node's values in registers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FTree8(ushort[] p, int offset, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref nint ip)
    {
        int m = 1;
        uint pv = LzmaRaw.P(p, offset + 1);
        uint c0;
        uint c1;
        uint mask;
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        c0 = LzmaRaw.P(p, offset + (m << 1));
        c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
        mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
        m = (m << 1) + (int)(mask + 1);
        pv = c1 ^ ((c0 ^ c1) & mask);
        m = (m << 1) + (int)(NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip) + 1);
        return m - 256;
    }

    // The same walk with the decoded bits collected least significant first ("Reverse Bit Tree Decoding").
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FReverseTree(ushort[] p, int offset, int bits, ref uint range, ref uint code, ReadOnlySpan<byte> input, ref nint ip)
    {
        int m = 1;
        int symbol = 0;
        uint pv = LzmaRaw.P(p, offset + 1);
        for (int i = 1; i < bits; i++)
        {
            uint c0 = LzmaRaw.P(p, offset + (m << 1));
            uint c1 = LzmaRaw.P(p, offset + (m << 1) + 1);
            uint mask = NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip);
            int bit = (int)(mask + 1);
            m = (m << 1) + bit;
            symbol |= bit << (i - 1);
            pv = c1 ^ ((c0 ^ c1) & mask);
        }

        int last = (int)(NbStep(ref LzmaRaw.P(p, offset + m), pv, ref range, ref code, input, ref ip) + 1);
        return symbol | (last << (bits - 1));
    }

    // The eight bits of a literal after a literal or a short run of non-matches (LZMA specification, "The Literal Decoding"). Kept
    // out of the symbol loop on purpose: with its own method the range coder state and the probability table stay in registers,
    // where inside the loop they were spilled around every bit. The tree's seven inner nodes read both children before the bit is
    // known.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PlainLiteral(ushort[] p, int lit, ref Rc rc, ReadOnlySpan<byte> input)
    {
        uint range = rc.Range;
        uint code = rc.Code;
        nint ip = rc.Ip;
        int sym = 1;
        uint pv = LzmaRaw.P(p, lit + 1);
        while (sym < 0x80)
        {
            uint c0 = LzmaRaw.P(p, lit + (sym << 1));
            uint c1 = LzmaRaw.P(p, lit + (sym << 1) + 1);
            uint mask = NbStep(ref LzmaRaw.P(p, lit + sym), pv, ref range, ref code, input, ref ip);
            sym = (sym << 1) + (int)(mask + 1);
            pv = c1 ^ ((c0 ^ c1) & mask);
        }

        sym = (sym << 1) + (int)(NbStep(ref LzmaRaw.P(p, lit + sym), pv, ref range, ref code, input, ref ip) + 1);
        rc.Range = range;
        rc.Code = code;
        rc.Ip = ip;
        return sym;
    }

    // The same after a match ("The Literal Decoding", matched literal): while the decoded bits equal the match byte's, the
    // probabilities come from the group chosen by the match bit. offs is 0x100 while they agree and 0 after the first
    // difference, which turns the same index formula into the plain literal tree without a branch.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int MatchedLiteral(ushort[] p, int lit, uint matchByte, ref Rc rc, ReadOnlySpan<byte> input)
    {
        uint range = rc.Range;
        uint code = rc.Code;
        nint ip = rc.Ip;
        int sym = 1;
        uint offs = 0x100;
        do
        {
            matchByte <<= 1;
            uint matchBit = matchByte & offs;
            int bit = FBitNb(p, lit + (int)(offs + matchBit) + sym, ref range, ref code, input, ref ip);
            sym = (sym << 1) | bit;
            offs &= matchBit ^ (uint)(bit - 1);
        }
        while (sym < 0x100);
        rc.Range = range;
        rc.Code = code;
        rc.Ip = ip;
        return sym;
    }

    // LZMA specification, "The match length decoding": a zero-based length 0..271.
    private static int FLength(ushort[] p, int coder, int posState, ref Rc rc, ReadOnlySpan<byte> input)
    {
        uint range = rc.Range;
        uint code = rc.Code;
        nint ip = rc.Ip;
        int r;
        if (FBit(p, coder, ref range, ref code, input, ref ip) == 0)
        {
            r = FTree3(p, coder + LzmaModel.LengthLow + (posState << 3), ref range, ref code, input, ref ip);
        }
        else if (FBit(p, coder + LzmaModel.LengthChoice2, ref range, ref code, input, ref ip) == 0)
        {
            r = 8 + FTree3(p, coder + LzmaModel.LengthMid + (posState << 3), ref range, ref code, input, ref ip);
        }
        else
        {
            r = 16 + FTree8(p, coder + LzmaModel.LengthHigh, ref range, ref code, input, ref ip);
        }

        rc.Range = range;
        rc.Code = code;
        rc.Ip = ip;
        return r;
    }

    // LZMA specification, "The match distance decoding": the zero-based distance; 0xFFFFFFFF is the end marker.
    private static uint FDistance(ushort[] p, int length, ref Rc rc, ReadOnlySpan<byte> input)
    {
        uint range = rc.Range;
        uint code = rc.Code;
        nint ip = rc.Ip;
        int lenState = Math.Min(length, LzmaModel.LengthToPositionStates - 1);
        int slot = FTree6(p, LzmaModel.PositionSlot + (lenState << 6), ref range, ref code, input, ref ip);
        uint dist;
        if (slot < 4)
        {
            dist = (uint)slot;
        }
        else
        {
            int directBits = (slot >> 1) - 1;
            dist = (2u | (uint)(slot & 1)) << directBits;
            if (slot < LzmaModel.EndPositionModelIndex)
            {
                dist += (uint)FReverseTree(p, LzmaModel.PositionSpecial + (int)dist - slot, directBits, ref range, ref code, input, ref ip);
            }
            else
            {
                uint direct = 0;
                for (int n = directBits - LzmaModel.AlignBits; n > 0; n--)
                {
                    range >>= 1;
                    code -= range;
                    uint t = 0 - (code >> 31);
                    code += range & t;
                    if (range < (1u << 24))
                    {
                        range <<= 8;
                        code = (code << 8) | LzmaRaw.In(input, ip);
                        ip++;
                    }

                    direct = (direct << 1) + t + 1;
                }

                dist += direct << LzmaModel.AlignBits;
                dist += (uint)FReverseTree(p, LzmaModel.Align, LzmaModel.AlignBits, ref range, ref code, input, ref ip);
            }
        }

        rc.Range = range;
        rc.Code = code;
        rc.Ip = ip;
        return dist;
    }

    // The symbol loop for the common case: no end-marker probing, and at least SymbolInputMargin bytes of input at the start of
    // every symbol, so no symbol can run out of input and nothing needs an undo log. Returns Low when the margin is gone (the
    // caller finishes the segment with the journalling loop), or a LzmaStatus. Same decisions as Run<JournalOff>.
    private int RunFast(ReadOnlySpan<byte> input, int limit)
    {
        byte[] w = _window;
        ushort[] p = _probs;
        int capacity = _capacity;
        uint range = _rc.Range;
        uint code = _rc.Code;
        nint ip = _rc.Position;
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
        int matchLeft = _matchLeft;
        int result;

        while (true)
        {
            if (matchLeft > 0)
            {
                int n = Math.Min(matchLeft, limit - pos);
                if (n > 0)
                {
                    CopyMatch(w, pos, capacity, (int)rep0 + 1, n);
                    pos += n;
                    total += n;
                    matchLeft -= n;
                }

                if (matchLeft > 0)
                {
                    result = (int)LzmaStatus.OutputFull;
                    break;
                }
            }

            if (pos >= limit)
            {
                result = (int)LzmaStatus.OutputFull;
                break;
            }

            if (input.Length - ip < SymbolInputMargin)
            {
                result = Low;
                break;
            }

            int posState = (int)total & pbMask;
            if (FBit(p, LzmaModel.IsMatch + (state << 4) + posState, ref range, ref code, input, ref ip) == 0)
            {
                // LZMA specification, "The Literal Decoding".
                int prev = pos > 0 ? w[pos - 1] : (_full ? w[capacity - 1] : 0);
                int lit = LzmaModel.Literal + (LzmaModel.LiteralCoderSize * ((((int)total & lpMask) << lc) + (prev >> (8 - lc))));
                int sym;
                var lrc = new Rc { Range = range, Code = code, Ip = ip };
                if (state >= LzmaModel.LiteralStateLimit)
                {
                    int mpos = pos - (int)rep0 - 1;
                    if (mpos < 0)
                    {
                        mpos += capacity;
                    }

                    sym = MatchedLiteral(p, lit, w[mpos], ref lrc, input);
                }
                else
                {
                    sym = PlainLiteral(p, lit, ref lrc, input);
                }

                range = lrc.Range;
                code = lrc.Code;
                ip = lrc.Ip;

                w[pos++] = (byte)sym;
                total++;
                state = LzmaModel.NextStateAfterLiteral(state);
                continue;
            }

            int length;
            if (FBit(p, LzmaModel.IsRep + state, ref range, ref code, input, ref ip) == 0)
            {
                // A match with a newly coded distance.
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                var rc = new Rc { Range = range, Code = code, Ip = ip };
                length = FLength(p, LzmaModel.LengthCoder, posState, ref rc, input);
                state = LzmaModel.NextStateAfterMatch(state);
                uint dist = FDistance(p, length, ref rc, input);
                range = rc.Range;
                code = rc.Code;
                ip = rc.Ip;
                if (dist == 0xFFFFFFFF)
                {
                    result = (int)LzmaStatus.EndMarker;
                    break;
                }

                if (dist >= dictSize || dist >= total)
                {
                    result = (int)LzmaStatus.InvalidData;
                    break;
                }

                rep0 = dist;
            }
            else
            {
                if (total == 0)
                {
                    result = (int)LzmaStatus.InvalidData;
                    break;
                }

                if (FBit(p, LzmaModel.IsRepG0 + state, ref range, ref code, input, ref ip) == 0)
                {
                    if (FBit(p, LzmaModel.IsRep0Long + (state << 4) + posState, ref range, ref code, input, ref ip) == 0)
                    {
                        // A short rep: one byte at the last distance.
                        int sp = pos - (int)rep0 - 1;
                        if (sp < 0)
                        {
                            sp += capacity;
                        }

                        w[pos++] = w[sp];
                        total++;
                        state = LzmaModel.NextStateAfterShortRep(state);
                        continue;
                    }
                }
                else
                {
                    uint d;
                    if (FBit(p, LzmaModel.IsRepG1 + state, ref range, ref code, input, ref ip) == 0)
                    {
                        d = rep1;
                    }
                    else
                    {
                        if (FBit(p, LzmaModel.IsRepG2 + state, ref range, ref code, input, ref ip) == 0)
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

                var rc = new Rc { Range = range, Code = code, Ip = ip };
                length = FLength(p, LzmaModel.RepLengthCoder, posState, ref rc, input);
                range = rc.Range;
                code = rc.Code;
                ip = rc.Ip;
                state = LzmaModel.NextStateAfterRep(state);
            }

            length += LzmaModel.MinMatchLength;
            int copy = Math.Min(length, limit - pos);
            if (rep0 >= 15 && pos > (int)rep0 && copy > 0)
            {
                // The common case: the source is behind the destination by a whole sixteen-byte step and has not wrapped.
                LzmaRaw.CopyWide16(w, pos, pos - (int)rep0 - 1, copy);
            }
            else
            {
                CopyMatch(w, pos, capacity, (int)rep0 + 1, copy);
            }

            pos += copy;
            total += copy;
            matchLeft = length - copy;
        }

        _matchLeft = matchLeft;
        _rc.Load(range, code, (int)ip);
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
