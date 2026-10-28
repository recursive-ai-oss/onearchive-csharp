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

    private const uint LinkKind = (uint)DeflateSymbolKind.Link;
    private const uint LiteralKind = (uint)DeflateSymbolKind.Literal;
    private const uint LengthKind = (uint)DeflateSymbolKind.Length;
    private const uint EndOfBlockKind = (uint)DeflateSymbolKind.EndOfBlock;
    private const uint DistanceKind = (uint)DeflateSymbolKind.Distance;

    public static FastResult Run(ref DeflateBitReader br, Span<byte> dst, ref int dp, DeflateHuffmanTable literalTable, DeflateHuffmanTable distanceTable, ReadOnlySpan<byte> history)
    {
        ReadOnlySpan<byte> src = br.Source;
        ulong bits = br.BitBuffer;
        int count = br.BitCount;
        int ip = br.Position;
        int op = dp;
        uint[] literals = literalTable.Entries;
        uint[] distances = distanceTable.Entries;
        int literalPrimary = literalTable.PrimaryBits;
        int distancePrimary = distanceTable.PrimaryBits;
        uint literalMask = literalTable.PrimaryMask;
        uint distanceMask = distanceTable.PrimaryMask;
        int inputLimit = src.Length - 8;
        int outputLimit = dst.Length - OutputMargin;
        int historyLength = history.Length;
        FastResult result = FastResult.Exhausted;

        while (ip <= inputLimit && op <= outputLimit)
        {
            // Refill: add the next 7 or so bytes above the bits we hold. The bits above `count` may already be the following
            // input bits from the previous refill; they are the same bits, so OR-ing is harmless.
            bits |= DeflateRaw.Read64(src, ip) << count;
            ip += (63 - count) >> 3;
            count |= 56;

            uint e = DeflateRaw.Get(literals, (int)(bits & literalMask));
            if (((e >> DeflateHuffmanEntry.KindShift) & 15) == LinkKind)
            {
                e = DeflateRaw.Get(literals, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)((bits >> literalPrimary) & ((1u << (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15)) - 1)));
            }

            int n = (int)(e & 0xFF);
            bits >>= n;
            count -= n;
            uint kind = (e >> DeflateHuffmanEntry.KindShift) & 15;
            if (kind == LiteralKind)
            {
                DeflateRaw.Set(dst, op++, (byte)(e >> DeflateHuffmanEntry.ValueShift));
                continue;
            }

            if (kind != LengthKind)
            {
                result = kind == EndOfBlockKind ? FastResult.EndOfBlock : FastResult.InvalidLiteralLength;
                break;
            }

            int extra = (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
            int length = (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)(bits & ((1UL << extra) - 1));
            bits >>= extra;
            count -= extra;

            e = DeflateRaw.Get(distances, (int)(bits & distanceMask));
            if (((e >> DeflateHuffmanEntry.KindShift) & 15) == LinkKind)
            {
                e = DeflateRaw.Get(distances, (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)((bits >> distancePrimary) & ((1u << (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15)) - 1)));
            }

            n = (int)(e & 0xFF);
            bits >>= n;
            count -= n;
            if (((e >> DeflateHuffmanEntry.KindShift) & 15) != DistanceKind)
            {
                result = FastResult.InvalidDistance;
                break;
            }

            extra = (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
            int distance = (int)(e >> DeflateHuffmanEntry.ValueShift) + (int)(bits & ((1UL << extra) - 1));
            bits >>= extra;
            count -= extra;

            int end = op + length;
            if (distance <= op)
            {
                int s = op - distance;
                if (distance >= 8)
                {
                    // Eight bytes at a time: each read is at least 8 bytes behind its write, so it only sees bytes already written.
                    do
                    {
                        DeflateRaw.Copy8(dst, op, s);
                        op += 8;
                        s += 8;
                    }
                    while (op < end);
                    op = end;
                }
                else if (distance == 1)
                {
                    dst.Slice(op, length).Fill(DeflateRaw.Get(dst, op - 1));
                    op = end;
                }
                else
                {
                    do
                    {
                        DeflateRaw.Set(dst, op, DeflateRaw.Get(dst, s));
                        op++;
                        s++;
                    }
                    while (op < end);
                }
            }
            else if (distance <= op + historyLength)
            {
                // The match starts in the history (earlier calls' output) and may continue into this call's output.
                int fromHistory = distance - op;
                int m = Math.Min(length, fromHistory);
                history.Slice(historyLength - fromHistory, m).CopyTo(dst[op..]);
                for (int i = m; i < length; i++)
                {
                    dst[op + i] = dst[i - fromHistory];
                }

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
}
