using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

public sealed partial class BrotliDecoder
{
    // The fast loop works on plain locals while at least this many bytes of input remain at the start of each command. A command header
    // reads at most 20 bytes, a literal run of up to 8 literals at most 57 more, and every other step is guarded again, so no read in
    // the loop can pass the end of the input and it never needs the restart checkpoints of the general path.
    private const int FastMargin = 128;

    // For every literal context (block type * 64 + context), the offset of its prefix code in the arena: the literal context map with
    // the code offsets folded in.
    private int[] _literalContextOffset = [];
    private readonly bool[] _literalSingleTree = new bool[256];

    // The same for distances (block type * 4 + context).
    private int[] _distanceContextOffset = [];

    // For every insert-and-copy symbol (RFC 7932 section 5), packed into one word: bits 0-15 the insert length base, 16-20 its extra
    // bits, 21-36 the copy length base, 37-41 its extra bits, bit 42 set when the distance is implied (symbols below 128).
    private static readonly ulong[] s_commandInfo = BuildCommandInfo();

    private static ulong[] BuildCommandInfo()
    {
        var table = new ulong[704];
        for (int symbol = 0; symbol < 704; symbol++)
        {
            int insertCode = BrotliCodes.InsertCodeOf[symbol];
            int copyCode = BrotliCodes.CopyCodeOf[symbol];
            table[symbol] = (uint)BrotliCodes.InsertBase[insertCode]
                | ((ulong)BrotliCodes.InsertExtra[insertCode] << 16)
                | ((ulong)(uint)BrotliCodes.CopyBase[copyCode] << 21)
                | ((ulong)BrotliCodes.CopyExtra[copyCode] << 37)
                | (symbol < 128 ? 1UL << 42 : 0);
        }

        return table;
    }

    // RFC 7932 section 4, for every distance symbol of the meta-block, packed into one word so that a distance costs one table read: the
    // distance is (last[index] if bit 39 is set) + the signed base in bits 0-31 + (the extra bits, of the count in bits 32-36, shifted left
    // by NPOSTFIX). Symbols 0 to 3 take one of the four last distances, 4 to 9 and 10 to 15 the last or second to last distance changed
    // by -1, +1, -2, +2, -3 or +3, and the rest are direct distances and distances with extra bits.
    private ulong[] _distanceInfo = [];

    private static readonly byte[] s_lastDistanceIndex = [0, 1, 2, 3, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1];
    private static readonly sbyte[] s_lastDistanceDelta = [0, 0, 0, 0, -1, 1, -2, 2, -3, 3, -1, 1, -2, 2, -3, 3];

    private void BuildDistanceInfo()
    {
        int size = _distanceAlphabetSize;
        if (_distanceInfo.Length < size)
        {
            _distanceInfo = new ulong[size];
        }

        ulong[] table = _distanceInfo;
        int postfix = _postfixBits;
        int direct = _directCodes;
        for (int code = 0; code < size; code++)
        {
            if (code < 16)
            {
                table[code] = (uint)(int)s_lastDistanceDelta[code] | ((ulong)s_lastDistanceIndex[code] << 37) | (1UL << 39);
            }
            else if (code < 16 + direct)
            {
                table[code] = (uint)(code - 15);
            }
            else
            {
                int extraCode = code - direct - 16;
                int extraBits = 1 + (extraCode >> (postfix + 1));
                int high = extraCode >> postfix;
                int low = extraCode & ((1 << postfix) - 1);
                int offset = ((2 + (high & 1)) << extraBits) - 4;
                table[code] = (uint)((offset << postfix) + low + direct + 1) | ((ulong)extraBits << 32);
            }
        }
    }

    private void BuildLiteralOffsets()
    {
        int n = 64 * _blockTypes[Literals];
        if (_literalContextOffset.Length < n)
        {
            _literalContextOffset = new int[n];
        }

        int[] offsets = _literalContextOffset;
        for (int i = 0; i < n; i++)
        {
            offsets[i] = _literalCodeOffset[_literalContextMap[i]];
        }

        // A block type whose 64 contexts all use one prefix code does not need its context worked out at all.
        for (int t = 0; t < _blockTypes[Literals]; t++)
        {
            bool single = true;
            for (int c = 1; c < 64; c++)
            {
                single &= offsets[(t << 6) + c] == offsets[t << 6];
            }

            _literalSingleTree[t] = single;
        }

        int m = 4 * _blockTypes[Distances];
        if (_distanceContextOffset.Length < m)
        {
            _distanceContextOffset = new int[m];
        }

        for (int i = 0; i < m; i++)
        {
            _distanceContextOffset[i] = _distanceCodeOffset[_distanceContextMap[i]];
        }

        Array.Clear(_utf8Valid);
        BuildDistanceInfo();
    }

    // Makes at least 56 bits available. Unlike the general reader's refill it does not test first: with 56 bits or more it ORs in bits that
    // are already there and moves nothing, which costs less than a branch the predictor cannot learn.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FastRefill(ReadOnlySpan<byte> src, ref int ip, ref ulong buf, ref int cnt)
    {
        buf |= BrotliDecRaw.Load64(src, ip) << cnt;
        ip += (63 - cnt) >> 3;
        cnt |= 56;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FastSymbol(uint[] arena, int offset, ref ulong buf, ref int cnt)
    {
        uint e = BrotliDecRaw.At(arena, offset + (int)(buf & BrotliPrefixTables.RootMask));
        if ((e & BrotliPrefixTables.LinkFlag) != 0)
        {
            buf >>= BrotliPrefixTables.RootBits;
            cnt -= BrotliPrefixTables.RootBits;
            e = BrotliDecRaw.At(arena, offset + (int)(e >> 8) + (int)(LowBits(buf, (int)(e & 0x7F))));
        }

        int len = (int)(e & 0xFF);
        buf >>= len;
        cnt -= len;
        return (int)(e >> 8);
    }

    // The low n bits (n below 64) as a number. BZHI does it in one instruction where the CPU has it; the result is the same either way.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong LowBits(ulong value, int n) =>
        System.Runtime.Intrinsics.X86.Bmi2.X64.IsSupported ? System.Runtime.Intrinsics.X86.Bmi2.X64.ZeroHighBits(value, (uint)n) : value & ((1UL << n) - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FastTake(ref ulong buf, ref int cnt, int n)
    {
        int v = (int)LowBits(buf, n);
        buf >>= n;
        cnt -= n;
        return v;
    }

    // RFC 7932 section 7.1: Lut0, Lut1 and Lut2 one after the other, so that the UTF8 and Signed modes address one array.
    private static readonly byte[] s_contextLut = BuildContextLut();

    private static byte[] BuildContextLut()
    {
        var table = new byte[768];
        BrotliContextTables.Lut0.CopyTo(table);
        BrotliContextTables.Lut1.CopyTo(table.AsSpan(256));
        BrotliContextTables.Lut2.CopyTo(table.AsSpan(512));
        return table;
    }

    // RFC 7932 section 7.1: the four ways to derive a literal's context from the two bytes before it. Every context is below 64.
    private interface ILiteralContext
    {
        static abstract int Of(byte[] lut, int p1, int p2);

        // True when the offsets are a table made for this mode (see Utf8TableContext) rather than the 64 offsets of the block type.
        static abstract bool UsesTable { get; }
    }

    // A literal block type that has one prefix code for every context.
    private struct SingleTreeContext : ILiteralContext
    {
        public static int Of(byte[] lut, int p1, int p2) => 0;

        public static bool UsesTable => false;
    }

    private struct Lsb6Context : ILiteralContext
    {
        public static int Of(byte[] lut, int p1, int p2) => p1 & 63;

        public static bool UsesTable => false;
    }

    private struct Msb6Context : ILiteralContext
    {
        public static int Of(byte[] lut, int p1, int p2) => p1 >> 2;

        public static bool UsesTable => false;
    }

    private struct Utf8Context : ILiteralContext
    {
        public static int Of(byte[] lut, int p1, int p2) => (BrotliDecRaw.At768(lut, p1) | BrotliDecRaw.At768(lut, 256 + p2)) & 63;

        public static bool UsesTable => false;
    }

    private struct SignedContext : ILiteralContext
    {
        public static int Of(byte[] lut, int p1, int p2) => ((BrotliDecRaw.At768(lut, 512 + p1) << 3) | BrotliDecRaw.At768(lut, 512 + p2)) & 63;

        public static bool UsesTable => false;
    }

    // The UTF8 mode with the second lookup folded away. The context is (Lut0[p1] | Lut1[p2]) & 63, and Lut1 holds values below 4, so a
    // table of the prefix code offsets indexed by (p1 << 2) | Lut1[p2] gives the offset in one read from the byte just decoded. The
    // read of Lut1[p2] depends on an older byte and is off the critical path. Lut0 is below 64, so the mask changes nothing.
    private struct Utf8TableContext : ILiteralContext
    {
        public static int Of(byte[] lut, int p1, int p2) => (p1 << 2) | BrotliDecRaw.At768(lut, 256 + p2);

        public static bool UsesTable => true;
    }

    // A meta-block shorter than this is not worth building a 1024-entry table for.
    private const int TableMinimumLength = 4096;

    // The tables are kept between meta-blocks and streams so that decoding allocates nothing in the steady state; the flag says whether
    // the table of a type belongs to the current meta-block.
    private readonly int[]?[] _utf8Table = new int[]?[256];
    private readonly bool[] _utf8Valid = new bool[256];

    // The offsets of Utf8TableContext for a literal block type, built on first use in the meta-block.
    private int[] Utf8Table(int type)
    {
        int[]? table = _utf8Table[type];
        if (table != null && _utf8Valid[type])
        {
            return table;
        }

        table ??= new int[1024];
        int[] offsets = _literalContextOffset;
        int baseIndex = type << 6;
        for (int p1 = 0; p1 < 256; p1++)
        {
            int a = BrotliContextTables.Lut0[p1];
            for (int b = 0; b < 4; b++)
            {
                table[(p1 << 2) | b] = offsets[baseIndex + ((a | b) & 63)];
            }
        }

        _utf8Table[type] = table;
        _utf8Valid[type] = true;
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FastLiteral<TContext>(uint[] arena, ReadOnlySpan<int> offsets, byte[] ring, ref ulong buf, ref int cnt, ref int pos, ref int p1, ref int p2)
        where TContext : struct, ILiteralContext
    {
        int literal = FastSymbol(arena, BrotliDecRaw.At64(offsets, TContext.Of(s_contextLut, p1, p2)), ref buf, ref cnt);
        BrotliDecRaw.Set(ring, pos++, (byte)literal);
        p2 = p1;
        p1 = literal;
    }

    // Tests set this to compare the fast loop with the general path.
    internal bool FastPathDisabled { get; set; }

    private void RunFast(ref BrotliBitReader br, Span<byte> dest)
    {
        if (FastPathDisabled)
        {
            return;
        }

        int type = _blockType[Literals];
        if (_literalSingleTree[type])
        {
            RunFast<SingleTreeContext>(ref br, dest);
            return;
        }

        switch (_contextModes[type])
        {
            case 0:
                RunFast<Lsb6Context>(ref br, dest);
                break;
            case 1:
                RunFast<Msb6Context>(ref br, dest);
                break;
            case 2:
                if (_metaRemaining >= TableMinimumLength)
                {
                    RunFast<Utf8TableContext>(ref br, dest);
                }
                else
                {
                    RunFast<Utf8Context>(ref br, dest);
                }

                break;
            default:
                RunFast<SignedContext>(ref br, dest);
                break;
        }
    }

    // A transformed dictionary word for a copy whose distance reaches past the window (RFC 7932 section 8), written into the ring at pos.
    // Returns its length, which can be under two bytes when the transformation omits letters, or -1 when it does not fit before end
    // (then the general path writes it from the state set here). Kept out of the loop because it is rare and large.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int WriteDictionaryWord(ref BrotliBitReader br, int ip, ulong buf, int cnt, int copy, int distance, int maxDistance, int produced, byte[] ring, int pos, int end)
    {
        br.Attach(ip, buf, cnt);
        _copyLen = copy;
        int metaAtEntry = _metaRemaining;
        _metaRemaining = metaAtEntry - produced;
        StartDictionaryWord(br, distance, maxDistance);
        _metaRemaining = metaAtEntry;
        int length = _wordLen;
        if (pos + length > end)
        {
            return -1;
        }

        _word.AsSpan(0, length).CopyTo(ring.AsSpan(pos, length));
        _cmd = Cmd.Start;
        return length;
    }

    // Decodes whole commands - insert-and-copy length, literals, distance, copy (RFC 7932 sections 5 to 10) - for as long as the input
    // has a margin left and each command is the plain case: it stays within the current block of every category, its output fits the
    // ring before its end, the destination and the meta-block, and it is not a static dictionary reference. It leaves the decoder exactly
    // where the general path would be at the same point: between commands (_cmd = Start), or inside one with the state that the general
    // path keeps for it, and the general path handles whatever stopped it (a block switch, the end of the ring, a dictionary word, the
    // end of the input or the meta-block). The literal context mode is fixed by the literal block type, so it is a type argument.
    //
    // Everything the output position decides is derived from it: the bytes produced since the call began are pos - startPos, so the
    // meta-block's remaining length and the total output are not kept as counters. That, and reading rarely used tables from the
    // decoder's fields when they are needed, keeps the loop's live values within the registers.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void RunFast<TContext>(ref BrotliBitReader br, Span<byte> dest)
        where TContext : struct, ILiteralContext
    {
        ReadOnlySpan<byte> src = br.Source;
        int limit = src.Length - FastMargin;
        if (!br.CanDetach || br.RawPosition > limit)
        {
            return;
        }

        byte[] ring = _ring;
        int pos = _pos;
        if (pos >= _cap)
        {
            return;
        }

        int startPos = pos;
        int ip = br.RawPosition;
        ulong buf = br.RawBuffer;
        int cnt = br.RawCount;

        // The position the commands must stay below: the nearest of the end of the meta-block, the ring end and the free output. When the
        // loop stops at the second or third the general path takes the command that did not fit.
        int end = Math.Min(pos + _metaRemaining, pos + Math.Min(dest.Length - _written - (pos - _flush), _cap - pos));

        // RFC 7932 section 4: how far back a copy may reach, the window or the bytes produced if fewer. Once the stream has produced a
        // whole window it is the window; before that nothing has wrapped, so the output position is the number of bytes produced.
        bool windowFull = _totalOut >= _window;
        int p1 = _p1;
        int p2 = _p2;
        int litType = _blockType[Literals];
        int litLeft = _blockLeft[Literals];
        int icLeft = _blockLeft[InsertAndCopy];
        int distLeft = _blockLeft[Distances];
        ulong[] distanceTable = _distanceInfo;
        int postfix = _postfixBits;
        int window = _window;
        int cap = _cap;
        int icOffset = _insertCodeOffset[_blockType[InsertAndCopy]];
        int dType = _blockType[Distances];
        ReadOnlySpan<int> distanceOffsets = _distanceContextOffset.AsSpan(dType << 2, 4);
        uint[] arena = _arena;
        ReadOnlySpan<int> literalOffsets = TContext.UsesTable ? Utf8Table(litType) : _literalContextOffset.AsSpan(litType << 6, 64);
        Span<int> last = stackalloc int[4];
        last[0] = _d1;
        last[1] = _d2;
        last[2] = _d3;
        last[3] = _d4;
        Cmd exitCmd = Cmd.Start;
        int bailInsert = 0;
        int bailCopy = 0;
        bool bailImplicit = false;

        while (pos < end && ip <= limit && icLeft > 0)
        {
            // RFC 7932 section 5: the insert-and-copy length code.
            FastRefill(src, ref ip, ref buf, ref cnt);
            int symbol = FastSymbol(arena, icOffset, ref buf, ref cnt);
            ulong info = BrotliDecRaw.At(s_commandInfo, symbol);
            int insert = (int)(info & 0xFFFF) + FastTake(ref buf, ref cnt, (int)(info >> 16) & 31);
            FastRefill(src, ref ip, ref buf, ref cnt);
            int copy = (int)((info >> 21) & 0xFFFF) + FastTake(ref buf, ref cnt, (int)(info >> 37) & 31);
            icLeft--;
            bool implicitDistance = (info & (1UL << 42)) != 0;
            if (pos + insert + copy > end || litLeft < insert)
            {
                if (pos + insert > startPos + _metaRemaining)
                {
                    br.Attach(ip, buf, cnt);
                    throw Error(br, "an insertion length that fits in the meta-block");
                }

                bailInsert = insert;
                bailCopy = copy;
                bailImplicit = implicitDistance;
                exitCmd = Cmd.Literals;
                break;
            }

            // RFC 7932 section 7.1: the literals. A step of the loop advances the input by at most 7 bytes. Up to eight literals fit the
            // margin whatever the position; longer runs are cut where the margin ends.
            int run = insert;
            if (run > 8)
            {
                run = Math.Max(0, Math.Min(run, ((limit - ip) / 7) + 1));
            }

            // Three literals take at most 45 bits, so one refill serves a group of three.
            int k = 0;
            for (; k + 3 <= run; k += 3)
            {
                FastRefill(src, ref ip, ref buf, ref cnt);
                FastLiteral<TContext>(arena, literalOffsets, ring, ref buf, ref cnt, ref pos, ref p1, ref p2);
                FastLiteral<TContext>(arena, literalOffsets, ring, ref buf, ref cnt, ref pos, ref p1, ref p2);
                FastLiteral<TContext>(arena, literalOffsets, ring, ref buf, ref cnt, ref pos, ref p1, ref p2);
            }

            for (; k < run; k++)
            {
                FastRefill(src, ref ip, ref buf, ref cnt);
                FastLiteral<TContext>(arena, literalOffsets, ring, ref buf, ref cnt, ref pos, ref p1, ref p2);
            }

            litLeft -= run;
            if (run < insert)
            {
                bailInsert = insert - run;
                bailCopy = copy;
                bailImplicit = implicitDistance;
                exitCmd = Cmd.Literals;
                break;
            }

            // RFC 7932 sections 4 and 7.2: the distance.
            int distance;
            bool pushes;
            if (implicitDistance)
            {
                distance = last[0];
                pushes = false;
            }
            else
            {
                if (ip > limit || distLeft == 0)
                {
                    bailInsert = 0;
                    bailCopy = copy;
                    bailImplicit = false;
                    exitCmd = Cmd.Distance;
                    break;
                }

                distLeft--;
                FastRefill(src, ref ip, ref buf, ref cnt);
                int code = FastSymbol(arena, BrotliDecRaw.At64(distanceOffsets, Math.Min(copy, 5) - 2), ref buf, ref cnt);
                ulong distanceInfo = BrotliDecRaw.At(distanceTable, code);
                int extra = FastTake(ref buf, ref cnt, (int)(distanceInfo >> 32) & 31);
                int lastMask = -(int)((distanceInfo >> 39) & 1);
                distance = (last[(int)(distanceInfo >> 37) & 3] & lastMask) + (int)distanceInfo + (extra << postfix);
                pushes = code != 0;
            }

            if (distance <= 0)
            {
                br.Attach(ip, buf, cnt);
                throw Error(br, "a distance above zero");
            }

            int maxDistance = Math.Min(window, windowFull ? int.MaxValue : pos);
            if (distance > maxDistance)
            {
                // A static dictionary word (RFC 7932 section 8).
                int wordLength = WriteDictionaryWord(ref br, ip, buf, cnt, copy, distance, maxDistance, pos - startPos, ring, pos, end);
                if (wordLength < 0)
                {
                    exitCmd = Cmd.Word;
                    break;
                }

                pos += wordLength;
                if (wordLength >= 2)
                {
                    p1 = BrotliDecRaw.At(ring, pos - 1);
                    p2 = BrotliDecRaw.At(ring, pos - 2);
                }
                else if (wordLength == 1)
                {
                    p2 = p1;
                    p1 = BrotliDecRaw.At(ring, pos - 1);
                }

                continue;
            }

            if (pushes)
            {
                last[3] = last[2];
                last[2] = last[1];
                last[1] = last[0];
                last[0] = distance;
            }

            if (pos < distance)
            {
                // The source lies at the far end of the ring, a whole window behind. It is ahead of the destination, so a plain
                // move copies it correctly. A source that runs over the end of the ring is left to the general path.
                int source = pos - distance + cap;
                if (source + copy > cap)
                {
                    bailCopy = copy;
                    exitCmd = Cmd.Copy;
                    _copyDistance = distance;
                    break;
                }

                ring.AsSpan(source, copy).CopyTo(ring.AsSpan(pos, copy));
            }
            else if (distance >= 16 && copy <= 16)
            {
                BrotliDecRaw.Copy16(ring, pos, distance);
            }
            else
            {
                BrotliDecRaw.CopyMatch(ring, pos, distance, copy);
            }

            pos += copy;
            p1 = BrotliDecRaw.At(ring, pos - 1);
            p2 = BrotliDecRaw.At(ring, pos - 2);
        }

        br.Attach(ip, buf, cnt);
        int produced = pos - startPos;
        _pos = pos;
        _p1 = p1;
        _p2 = p2;
        _totalOut += produced;
        _metaRemaining -= produced;
        _blockLeft[Literals] = litLeft;
        _blockLeft[InsertAndCopy] = icLeft;
        _blockLeft[Distances] = distLeft;
        _d1 = last[0];
        _d2 = last[1];
        _d3 = last[2];
        _d4 = last[3];
        if (exitCmd == Cmd.Literals || exitCmd == Cmd.Distance)
        {
            _insertLeft = bailInsert;
            _copyLen = bailCopy;
            _implicitDistance = bailImplicit;
        }
        else if (exitCmd == Cmd.Copy)
        {
            _copyLeft = bailCopy;
        }

        _cmd = exitCmd;
    }
}
