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
        uint[] literals = literalTable.Entries;
        uint[] distances = distanceTable.Entries;
        if (literalTable.PrimaryBits != LiteralPrimaryBits || distanceTable.PrimaryBits != DistancePrimaryBits)
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
            ip += (63 - count) >> 3;
            count |= 56;

            uint e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
            if ((e & KindMask) == LinkKind)
            {
                e = DeflateRaw.Get(literals, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits >> LiteralPrimaryBits, (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15)));
            }

            int n = (int)(e & 0xFF);
            bits >>= n;
            count -= n;
            uint kind = e & KindMask;
            if (kind == LiteralKind)
            {
                DeflateRaw.Set(dst, op++, (byte)(e >> DeflateHuffmanEntry.ValueShift));

                // The refill left at least 56 bits and this code used at most 15, so two more codes found directly in the first-level
                // table (at most 10 bits each) can be decoded before the next refill. Anything else is left for the loop's next turn.
                e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
                if ((e & KindMask) == LiteralKind)
                {
                    n = (int)(e & 0xFF);
                    bits >>= n;
                    count -= n;
                    DeflateRaw.Set(dst, op++, (byte)(e >> DeflateHuffmanEntry.ValueShift));
                    e = DeflateRaw.Get(literals, (int)(bits & LiteralMask));
                    if ((e & KindMask) == LiteralKind)
                    {
                        n = (int)(e & 0xFF);
                        bits >>= n;
                        count -= n;
                        DeflateRaw.Set(dst, op++, (byte)(e >> DeflateHuffmanEntry.ValueShift));
                    }
                }

                continue;
            }

            if (kind != LengthKind)
            {
                result = kind == EndOfBlockKind ? FastResult.EndOfBlock : FastResult.InvalidLiteralLength;
                break;
            }

            int extra = (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
            int length = (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits, extra);
            bits >>= extra;
            count -= extra;

            e = DeflateRaw.Get(distances, (int)(bits & DistanceMask));
            if ((e & KindMask) == LinkKind)
            {
                e = DeflateRaw.Get(distances, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits >> DistancePrimaryBits, (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15)));
            }

            n = (int)(e & 0xFF);
            bits >>= n;
            count -= n;
            if ((e & KindMask) != DistanceKind)
            {
                result = FastResult.InvalidDistance;
                break;
            }

            extra = (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
            int distance = (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)LowBits(bits, extra);
            bits >>= extra;
            count -= extra;

            int end = op + length;
            if (distance >= 8 && distance <= op)
            {
                // Eight bytes at a time: each read is at least 8 bytes behind its write, so it only sees bytes already written.
                int s = op - distance;
                do
                {
                    DeflateRaw.Copy8(dst, op, s);
                    op += 8;
                    s += 8;
                }
                while (op < end);
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
        ip -= count >> 3;
        count &= 7;
        bits &= (1UL << count) - 1;
        br.Set(ip, bits, count);
        dp = op;
        return result;
    }

    // The low `count` bits of `value` (count 0 to 31). The same result with or without BMI2.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong LowBits(ulong value, int count) =>
        System.Runtime.Intrinsics.X86.Bmi2.X64.IsSupported ? System.Runtime.Intrinsics.X86.Bmi2.X64.ZeroHighBits(value, (uint)count) : value & ((1UL << count) - 1);

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
