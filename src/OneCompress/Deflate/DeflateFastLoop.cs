namespace RecursiveAI.OneCompress.Deflate;

internal enum FastResult
{
    /// <summary>The input or output margin ran out; the slow path continues.</summary>
    Exhausted,

    /// <summary>The end-of-block symbol was consumed.</summary>
    EndOfBlock,

    InvalidLiteralLength,
    InvalidDistance,
    DistanceTooFar,
}

/// <summary>
/// The inner loop of the decoder: while at least 8 input bytes and 272 output bytes remain it decodes without any
/// per-symbol checks of the buffers. One 8-byte load tops the bit buffer up to at least 56 bits per symbol, enough for a
/// literal/length code (15 bits), its extra bits (5), a distance code (15) and its extra bits (13) together.
/// </summary>
internal static class DeflateFastLoop
{
    public const int InputMargin = 8;

    // The longest match (258) plus the 8-byte copies that may run past its end.
    public const int OutputMargin = 258 + 8 + 6;

    // The first-level sizes of the literal/length and distance tables (DeflateHuffmanTable fixes them per alphabet). As constants they
    // free registers in the loop.
    private const int LiteralPrimaryBits = 10;
    private const int DistancePrimaryBits = 8;
    private const uint LiteralMask = (1u << LiteralPrimaryBits) - 1;
    private const uint DistanceMask = (1u << DistancePrimaryBits) - 1;

    private const uint KindMask = 15u << DeflateHuffmanEntry.KindShift;
    private const uint LinkKind = (uint)DeflateSymbolKind.Link << DeflateHuffmanEntry.KindShift;
    private const uint LiteralKind = (uint)DeflateSymbolKind.Literal << DeflateHuffmanEntry.KindShift;
    private const uint LengthKind = (uint)DeflateSymbolKind.Length << DeflateHuffmanEntry.KindShift;
    private const uint EndOfBlockKind = (uint)DeflateSymbolKind.EndOfBlock << DeflateHuffmanEntry.KindShift;
    private const uint DistanceKind = (uint)DeflateSymbolKind.Distance << DeflateHuffmanEntry.KindShift;

    public static FastResult Run(ref DeflateBitReader br, Span<byte> dst, ref int dp, DeflateHuffmanTable literalTable, DeflateHuffmanTable distanceTable, ReadOnlySpan<byte> history)
    {
        ReadOnlySpan<byte> src = br.Source;
        ulong bits = br.BitBuffer;
        int count = br.BitCount;
        int ip = br.Position;
        int op = dp;
        uint[] literals = literalTable.FastEntries;
        uint[] distances = distanceTable.Entries;
        if (literalTable.PrimaryBits != LiteralPrimaryBits || distanceTable.PrimaryBits != DistancePrimaryBits || literalTable.FastEntries.Length == 0)
        {
            return FastResult.Exhausted;
        }

        int inputLimit = src.Length - 8;
        int outputLimit = dst.Length - OutputMargin;
        FastResult result = FastResult.Exhausted;

        while (ip <= inputLimit && op <= outputLimit)
        {
            // Refill: add the next 7 or so bytes above the bits we hold. The bits above `count` may already be the following
            // input bits from the previous refill; they are the same bits, so OR-ing is harmless.
            bits |= DeflateRaw.Read64(src, ip) << count;
            ip += (~count & 63) >> 3;
            count |= 56;

            uint e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
            if ((e & KindMask) == LinkKind)
            {
                e = DeflateRaw.Get(literals, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits >> LiteralPrimaryBits, (e >> DeflateHuffmanEntry.ExtraShift) & 15));
            }

            // The low byte of an entry is the code length plus the extra bits that follow it, so one shift drops both; the extra
            // bits' value is read from the bits as they were before the shift.
            ulong before = bits;
            uint used = e;
            bits >>= (int)e;
            count -= (int)e;
            uint kind = e & KindMask;
            if (kind == LiteralKind)
            {
                // A fast-table literal entry holds one or two bytes and their count in bits 8 to 11; the second byte of a single
                // is zero and gets overwritten by the next write.
                DeflateRaw.Write16(dst, op, (ushort)(e >> DeflateHuffmanEntry.ValueShift));
                op += (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);

                // The refill left at least 56 bits and this code used at most 15, so two more codes found directly in the first-level
                // table (at most 10 bits each) can be decoded before the next refill. Anything else is left for the loop's next turn.
                e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
                if ((e & KindMask) == LiteralKind)
                {
                    bits >>= (int)e;
                    count -= (int)e;
                    DeflateRaw.Write16(dst, op, (ushort)(e >> DeflateHuffmanEntry.ValueShift));
                    op += (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
                    e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
                    if ((e & KindMask) == LiteralKind)
                    {
                        bits >>= (int)e;
                        count -= (int)e;
                        DeflateRaw.Write16(dst, op, (ushort)(e >> DeflateHuffmanEntry.ValueShift));
                        op += (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
                    }
                }

                continue;
            }

            if (kind != LengthKind)
            {
                result = kind == EndOfBlockKind ? FastResult.EndOfBlock : FastResult.InvalidLiteralLength;
                break;
            }

            int length = (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)(LowBits(before, used) >> (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15));

            e = DeflateRaw.Get(distances, (int)(bits & DistanceMask));
            if ((e & KindMask) == LinkKind)
            {
                e = DeflateRaw.Get(distances, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits >> DistancePrimaryBits, (e >> DeflateHuffmanEntry.ExtraShift) & 15));
            }

            before = bits;
            used = e;
            bits >>= (int)e;
            count -= (int)e;
            if ((e & KindMask) != DistanceKind)
            {
                result = FastResult.InvalidDistance;
                break;
            }

            int distance = (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)(LowBits(before, used) >> (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15));

            int end = op + length;
            if (distance >= 8 && distance <= op)
            {
                // Eight bytes at a time, each read at least 8 bytes behind its write, so it only sees bytes already written. The
                // first 24 bytes are copied without a loop test (most matches are shorter), which keeps the branch predictable; the
                // stores may run past the match, and the output margin covers that.
                int s = op - distance;
                DeflateRaw.Copy8(dst, op, s);
                DeflateRaw.Copy8(dst, op + 8, s + 8);
                DeflateRaw.Copy8(dst, op + 16, s + 16);
                if (length > 24)
                {
                    int p = op + 24;
                    s += 24;
                    do
                    {
                        DeflateRaw.Copy8(dst, p, s);
                        p += 8;
                        s += 8;
                    }
                    while (p < end);
                }

                op = end;
            }
            else if (CopyUncommon(dst, op, distance, length, history))
            {
                op = end;
            }
            else
            {
                result = FastResult.DistanceTooFar;
                break;
            }
        }

        // Give back the whole bytes still in the buffer so the slow path (and the caller) see exactly the bytes in use.
        count &= 63;
        ip -= count >> 3;
        count &= 7;
        bits &= (1UL << count) - 1;
        br.Set(ip, bits, count);
        dp = op;
        return result;
    }

    // The low `count & 63` bits of `value`. Callers pass a table entry whose low byte is a bit count below 64, so the upper
    // bits of `count` are ignored by both paths (BZHI reads the low byte, the shift is taken modulo 64). The same result with
    // or without BMI2.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong LowBits(ulong value, uint count) =>
        System.Runtime.Intrinsics.X86.Bmi2.X64.IsSupported ? System.Runtime.Intrinsics.X86.Bmi2.X64.ZeroHighBits(value, count) : value & ((1UL << (int)(count & 63)) - 1);

    // The matches the main loop does not copy eight bytes at a time: distances below 8, and distances reaching into the history
    // (earlier calls' output). Returns false when the distance reaches before the start of the unit.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool CopyUncommon(Span<byte> dst, int op, int distance, int length, ReadOnlySpan<byte> history)
    {
        int end = op + length;
        if (distance <= op)
        {
            int s = op - distance;
            if (distance == 1)
            {
                // Whole eight-byte stores of the repeated byte; the last may run past the match (the output margin covers it).
                ulong pattern = DeflateRaw.Get(dst, op - 1) * 0x0101010101010101UL;
                for (int p = op; p < end; p += 8)
                {
                    DeflateRaw.Write64(dst, p, pattern);
                }

                return true;
            }

            // Distance 2 to 7: write the first eight bytes one by one (each read is behind its write), after which the pattern
            // repeats with a period of the smallest multiple of the distance that is at least 8, and eight-byte copies from that
            // far back only read bytes that exist. The copy may run up to 7 bytes past the match; the output margin covers that
            // and the next symbols overwrite it.
            int first = Math.Min(length, 8);
            for (int i = 0; i < first; i++)
            {
                DeflateRaw.Set(dst, op + i, DeflateRaw.Get(dst, s + i));
            }

            if (length <= 8)
            {
                return true;
            }

            int period = distance;
            while (period < 8)
            {
                period += distance;
            }

            for (int p = op + 8; p < end; p += 8)
            {
                DeflateRaw.Copy8(dst, p, p - period);
            }

            return true;
        }

        if (distance <= op + history.Length)
        {
            // The match starts in the history and may continue into this call's output.
            int fromHistory = distance - op;
            int m = Math.Min(length, fromHistory);
            history.Slice(history.Length - fromHistory, m).CopyTo(dst[op..]);
            for (int i = m; i < length; i++)
            {
                dst[op + i] = dst[i - fromHistory];
            }

            return true;
        }

        return false;
    }
}
