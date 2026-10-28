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

    /// <summary>A match reaches back before this call's output; the caller copies it from the window and calls again.</summary>
    HistoryMatch,
}

/// <summary>
/// The inner loop of the decoder: while at least 8 input bytes and 272 output bytes remain it decodes without any
/// per-symbol checks of the buffers. One 8-byte load tops the bit buffer up to at least 56 bits per symbol, enough for a
/// literal/length code (15 bits), its extra bits (5), a distance code (15) and its extra bits (13) together.
/// </summary>
internal static class DeflateFastLoop
{
    public const int InputMargin = 16;

    // The longest match (258) plus the 16-byte copies that may run past its end.
    public const int OutputMargin = 258 + 16 + 6;

    // The first-level sizes of the literal/length and distance tables (DeflateHuffmanTable fixes them per alphabet). As constants they
    // free registers in the loop.
    private const int LiteralPrimaryBits = 10;
    private const int DistancePrimaryBits = 8;
    private const uint LiteralMask = (1u << LiteralPrimaryBits) - 1;
    private const uint DistanceMask = (1u << DistancePrimaryBits) - 1;

    // The kind bits of the literal/length fast table (DeflateHuffmanTable.BuildFast) are one flag per kind, so each test in the loop
    // is a single bit test. The plain table and the distance table keep DeflateSymbolKind.
    private const uint FastLiteralFlag = 8u << DeflateHuffmanEntry.KindShift;
    private const uint FastLengthFlag = 4u << DeflateHuffmanEntry.KindShift;
    private const uint FastLinkFlag = 2u << DeflateHuffmanEntry.KindShift;
    private const uint FastEndOfBlockFlag = 1u << DeflateHuffmanEntry.KindShift;
    // The distance table keeps DeflateSymbolKind (Invalid 0, Distance 4, Link 5), so bit 12 alone tells a link and bit 14 a distance
    // once a link is resolved.
    private const uint DistanceLinkBit = 1u << DeflateHuffmanEntry.KindShift;
    private const uint DistanceValidBit = 4u << DeflateHuffmanEntry.KindShift;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static FastResult Run(ref DeflateBitReader br, Span<byte> dst, ref int dp, DeflateHuffmanTable literalTable, DeflateHuffmanTable distanceTable, out int matchLength, out int matchDistance)
    {
        matchLength = 0;
        matchDistance = 0;
        ReadOnlySpan<byte> src = br.Source;
        ulong bits = br.BitBuffer;
        int count = br.BitCount;
        int ip = br.Position;
        int op = dp;
        ReadOnlySpan<uint> literals = literalTable.FastEntries;
        ReadOnlySpan<uint> distances = distanceTable.Entries;
        if (literalTable.PrimaryBits != LiteralPrimaryBits || distanceTable.PrimaryBits != DistancePrimaryBits || literalTable.FastEntries.Length == 0)
        {
            return FastResult.Exhausted;
        }

        // Two refills can happen per symbol (the next symbol's is issued before a match is copied), so keep 16 bytes in hand.
        int inputLimit = src.Length - 16;
        int outputLimit = dst.Length - OutputMargin;
        FastResult result = FastResult.Exhausted;
        uint e;
        if (ip > inputLimit || op > outputLimit)
        {
            goto Done;
        }

        // Refill: add the next 7 or so bytes above the bits we hold. The bits above `count` may already be the following
        // input bits from the previous refill; they are the same bits, so OR-ing is harmless.
        bits |= DeflateRaw.Read64(src, ip) << count;
        ip += (~count & 63) >> 3;
        count |= 56;
        e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
        if ((e & FastLinkFlag) != 0)
        {
            e = DeflateRaw.Get(literals, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits >> LiteralPrimaryBits, (e >> DeflateHuffmanEntry.ExtraShift) & 15));
        }

        while (true)
        {
            // Here the bit buffer holds at least 56 bits and e is the literal/length entry for them.
            // The low byte of an entry is the code length plus the extra bits that follow it, so one shift drops both; the extra
            // bits' value is read from the bits as they were before the shift.
            ulong before = bits;
            uint used = e;
            bits >>= (int)e;
            count -= (int)e;
            if ((e & FastLiteralFlag) != 0)
            {
                // A fast-table literal entry holds one or two bytes and their count in bits 8 to 11; the second byte of a single
                // is zero and gets overwritten by the next write.
                DeflateRaw.Write16(dst, op, (ushort)(e >> DeflateHuffmanEntry.ValueShift));
                op += (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);

                // The refill left at least 56 bits and this code used at most 15, so two more codes found directly in the first-level
                // table (at most 10 bits each) can be decoded before the next refill. Anything else is left for the loop's next turn.
                e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
                if ((e & FastLiteralFlag) != 0)
                {
                    bits >>= (int)e;
                    count -= (int)e;
                    DeflateRaw.Write16(dst, op, (ushort)(e >> DeflateHuffmanEntry.ValueShift));
                    op += (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
                    e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
                    if ((e & FastLiteralFlag) != 0)
                    {
                        bits >>= (int)e;
                        count -= (int)e;
                        DeflateRaw.Write16(dst, op, (ushort)(e >> DeflateHuffmanEntry.ValueShift));
                        op += (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
                    }
                }

                if (ip > inputLimit || op > outputLimit)
                {
                    break;
                }

                bits |= DeflateRaw.Read64(src, ip) << count;
                ip += (~count & 63) >> 3;
                count |= 56;
                e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
                if ((e & FastLinkFlag) != 0)
                {
                    e = DeflateRaw.Get(literals, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits >> LiteralPrimaryBits, (e >> DeflateHuffmanEntry.ExtraShift) & 15));
                }

                continue;
            }

            if ((e & FastLengthFlag) == 0)
            {
                result = (e & FastEndOfBlockFlag) != 0 ? FastResult.EndOfBlock : FastResult.InvalidLiteralLength;
                break;
            }

            int length = (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)(LowBits(before, used) >> (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15));

            e = DeflateRaw.Get(distances, (int)(bits & DistanceMask));
            if ((e & DistanceLinkBit) != 0)
            {
                e = DeflateRaw.Get(distances, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits >> DistancePrimaryBits, (e >> DeflateHuffmanEntry.ExtraShift) & 15));
            }

            before = bits;
            used = e;
            bits >>= (int)e;
            count -= (int)e;
            if ((e & DistanceValidBit) == 0)
            {
                result = FastResult.InvalidDistance;
                break;
            }

            int distance = (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)(LowBits(before, used) >> (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15));

            if (distance > op)
            {
                // Only the caller can see the window, and a call inside this loop would push most of its variables onto the stack.
                matchLength = length;
                matchDistance = distance;
                result = FastResult.HistoryMatch;
                break;
            }

            // Issue the next symbol's refill and table read now, so they overlap the copy below (which they do not depend on).
            // The loop test keeps ip at most src.Length - 16 on entry, so this load is in range.
            bits |= DeflateRaw.Read64(src, ip) << count;
            ip += (~count & 63) >> 3;
            count |= 56;
            e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
            if ((e & FastLinkFlag) != 0)
            {
                e = DeflateRaw.Get(literals, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits >> LiteralPrimaryBits, (e >> DeflateHuffmanEntry.ExtraShift) & 15));
            }

            int end = op + length;
            int s = op - distance;
            if (distance >= 16)
            {
                // Sixteen bytes at a time, each read at least 16 bytes behind its write. The first 32 bytes are copied without a
                // loop test; the stores may run past the match, and the output margin covers that.
                DeflateRaw.Copy16(dst, op, s);
                DeflateRaw.Copy16(dst, op + 16, s + 16);
                if (length > 32)
                {
                    int p = op + 32;
                    s += 32;
                    do
                    {
                        DeflateRaw.Copy16(dst, p, s);
                        p += 16;
                        s += 16;
                    }
                    while (p < end);
                }
            }
            else if (distance >= 8)
            {
                // Eight bytes at a time, each read at least 8 bytes behind its write, so it only sees bytes already written.
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
            }
            else if (distance == 1)
            {
                // Whole eight-byte stores of the repeated byte; the last may run past the match (the output margin covers it).
                ulong pattern = DeflateRaw.Get(dst, op - 1) * 0x0101010101010101UL;
                for (int p = op; p < end; p += 8)
                {
                    DeflateRaw.Write64(dst, p, pattern);
                }
            }
            else
            {
                // Distance 2 to 7: the first eight bytes are the d bytes behind the match repeated, built in a register (the bytes
                // read past the match are unwritten output and are masked off), after which the pattern repeats with a period of the
                // smallest multiple of the distance that is at least 8, and eight-byte copies from that far back only read bytes that
                // exist. The copy may run up to 7 bytes past the match; the output margin covers that and the next symbols overwrite it.
                ulong unit = DeflateRaw.Read64(dst, s) & ((1UL << (8 * distance)) - 1);
                ulong pattern8 = unit;
                for (int k = distance; k < 8; k += distance)
                {
                    pattern8 |= unit << (8 * k);
                }

                DeflateRaw.Write64(dst, op, pattern8);
                if (length > 8)
                {
                    int period = distance;
                    while (period < 8)
                    {
                        period += distance;
                    }

                    for (int p = op + 8; p < end; p += 8)
                    {
                        DeflateRaw.Copy8(dst, p, p - period);
                    }
                }
            }

            op = end;
            if (ip > inputLimit || op > outputLimit)
            {
                break;
            }
        }

    Done:
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

    /// <summary>
    /// Copies a match that starts before this call's output, in the window (earlier calls' output): <paramref name="distance"/> is
    /// more than the <paramref name="op"/> bytes written so far. It may continue into this call's output. Returns false when the
    /// distance reaches before the start of the unit.
    /// </summary>
    public static bool CopyFromHistory(Span<byte> dst, int op, int length, int distance, ReadOnlySpan<byte> history)
    {
        if (distance > op + history.Length)
        {
            return false;
        }

        int fromHistory = distance - op;
        int m = Math.Min(length, fromHistory);
        history.Slice(history.Length - fromHistory, m).CopyTo(dst[op..]);
        for (int i = m; i < length; i++)
        {
            dst[op + i] = dst[i - fromHistory];
        }

        return true;
    }
}
