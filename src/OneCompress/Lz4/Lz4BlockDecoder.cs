namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// Decodes LZ4 blocks (LZ4 Block Format): a run of sequences, each a token, literals and a match copy, ending in a
/// sequence of literals only. This is the raw layer the frame decoder is built on; use it directly to decode blocks
/// whose compressed size is known from outside the data.
/// </summary>
/// <remarks>
/// <para>History: matches may reach back into the first <c>historyLength</c> bytes of the destination, which the caller
/// fills with the dictionary or the earlier blocks of a linked sequence. A match never reaches before the start of the
/// destination, nor further than 65535 bytes.</para>
/// <para>The decoder rejects blocks that break the format's end rules ("End of block conditions"): the last sequence must
/// hold at least five literals and the last match must start at least twelve bytes before the end of the block, unless the
/// block is a single sequence of literals. Offset zero, offsets reaching before the history, and a block that ends inside a
/// sequence are errors.</para>
/// </remarks>
public static class Lz4BlockDecoder
{
    /// <summary>
    /// Decodes one block into <paramref name="destination"/>, after <paramref name="historyLength"/> bytes of history.
    /// </summary>
    /// <param name="source">The compressed block, exactly: the block ends where this span ends.</param>
    /// <param name="destination">History followed by room for the output.</param>
    /// <param name="historyLength">How many bytes at the start of <paramref name="destination"/> are history, not output.</param>
    /// <param name="written">The number of output bytes written after the history; on a false return, the number written before the room ran out.</param>
    /// <returns>True when the block was decoded; false when the output does not fit in the room that follows the history.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="historyLength"/> is negative or larger than <paramref name="destination"/>.</exception>
    /// <exception cref="CompressionFormatException">The block is truncated or breaks a rule of the block format; the offset is into <paramref name="source"/>.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> source, Span<byte> destination, int historyLength, out int written)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(historyLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(historyLength, destination.Length);
        BlockResult result = Decode(source, destination, historyLength, out written, out int errorOffset);
        if (result == BlockResult.Ok)
        {
            return true;
        }

        if (result == BlockResult.OutputOverflow)
        {
            return false;
        }

        Throw(result, errorOffset, 0);
        return false;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    // Decodes a block that may reach back into a dictionary kept outside the output; the false and exception results are those of TryDecode.
    internal static bool TryDecodeWithDictionary(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> dictionary, out int written)
    {
        BlockResult result = Decode(source, destination, 0, dictionary, out written, out int errorOffset);
        if (result == BlockResult.Ok)
        {
            return true;
        }

        if (result == BlockResult.OutputOverflow)
        {
            return false;
        }

        Throw(result, errorOffset, 0);
        return false;
    }

    /// <summary>Measures the output of a block without writing it, validating the block's structure on the way.</summary>
    /// <param name="source">The compressed block.</param>
    /// <param name="historyLength">The number of history bytes that will precede the output, for the offset checks.</param>
    /// <returns>The decoded size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="historyLength"/> is negative.</exception>
    /// <exception cref="CompressionFormatException">The block is truncated or breaks a rule of the block format.</exception>
    public static long GetDecompressedSize(ReadOnlySpan<byte> source, int historyLength = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(historyLength);
        BlockResult result = Measure(source, historyLength, out long size, out int errorOffset);
        if (result != BlockResult.Ok)
        {
            Throw(result, errorOffset, 0);
        }

        return size;
    }

    internal enum BlockResult
    {
        Ok,
        Truncated,
        ZeroOffset,
        OffsetTooFar,
        OutputOverflow,
        ShortLastLiterals,
        LateLastMatch,
    }

    /// <summary>Raises the exception for a failed block; <paramref name="baseOffset"/> is the block's position in the caller's input.</summary>
    internal static void Throw(BlockResult result, int errorOffset, long baseOffset)
    {
        long at = baseOffset + errorOffset;
        switch (result)
        {
            case BlockResult.Truncated:
                Lz4Format.ThrowTruncated(at, "more block data: a sequence ends inside its token, literals, offset or length bytes");
                break;
            case BlockResult.ZeroOffset:
                Lz4Format.ThrowInvalid(at, "a match offset from 1 to 65535 (0 is invalid)");
                break;
            case BlockResult.OffsetTooFar:
                Lz4Format.ThrowInvalid(at, "a match offset that stays inside the decoded data and dictionary");
                break;
            case BlockResult.ShortLastLiterals:
                Lz4Format.ThrowInvalid(at, "at least 5 literals in the last sequence");
                break;
            case BlockResult.LateLastMatch:
                Lz4Format.ThrowInvalid(at, "the last match to start at least 12 bytes before the end of the block");
                break;
            default:
                Lz4Format.ThrowInvalid(at, "a block that decodes within the allowed size");
                break;
        }
    }

    /// <summary>
    /// The block decoder. <c>dst[0..outStart)</c> is history; output goes to <c>dst[outStart..]</c> and may fill all of it.
    /// <paramref name="errorOffset"/> is the position in <paramref name="src"/> the error was found at.
    /// </summary>
    internal static BlockResult Decode(ReadOnlySpan<byte> src, Span<byte> dst, int outStart, out int written, out int errorOffset)
        => Decode(src, dst, outStart, default, out written, out errorOffset);

    /// <summary>
    /// As above, with a separate dictionary: it counts as history that precedes <c>dst[outStart..]</c>, so a match may reach back into
    /// it without the dictionary being copied next to the output.
    /// </summary>
    // Jitted fully optimized on the first call: a caller that decodes many tiny blocks would otherwise spend its first
    // hundred milliseconds in unoptimized code.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static BlockResult Decode(ReadOnlySpan<byte> src, Span<byte> dst, int outStart, ReadOnlySpan<byte> dictionary, out int written, out int errorOffset)
    {
        int ip = 0;
        int op = outStart;
        int iend = src.Length;
        int oend = dst.Length;
        int lastMatchStart = 0;
        bool onlySequence = true;
        errorOffset = 0;
        BlockResult result;

        while (true)
        {
            // The fast loop takes every complete sequence it can prove safe; the general code below handles the rest (the
            // ends of the spans, malformed sequences and the end of the block), one sequence at a time.
            // It is only worth calling while it has the margins it needs, which only shrink as the block is decoded.
            if (ip < iend - 19 && op < oend - 40)
            {
                int before = ip;
                (ip, op, lastMatchStart) = FastLoop(src, dst, dictionary, ip, op, lastMatchStart);
                if (ip != before)
                {
                    onlySequence = false;
                }
            }

            // Short sequences (no length bytes, a match that does not overlap itself) are the bulk of small blocks, which the fast
            // loop's margins never let it near. They are decoded here with exact copies; anything else, and every error, is left
            // to the general code, which sees the sequence untouched.
            while (ip < iend)
            {
                int t = Lz4Raw.Get(src, ip);  // ip < iend
                int literals = t >> 4;
                int length = (t & 15) + Lz4Format.MinMatch;
                int shortOffsetAt = ip + 1 + literals;
                if (literals == 15 || (t & 15) == 15 || shortOffsetAt + 2 > iend || length + literals > oend - op)
                {
                    break;
                }

                int shortOffset = Lz4Raw.ReadUInt16(src, shortOffsetAt);  // shortOffsetAt + 2 <= iend
                int matchAt = op + literals;
                if (shortOffset == 0 || shortOffset > matchAt + dictionary.Length)
                {
                    break;
                }

                if (shortOffset <= matchAt)
                {
                    if (shortOffset < length)
                    {
                        break;
                    }

                    Lz4Raw.CopyUpTo16(dst, op, src, ip + 1, literals);  // inside both spans: checked above
                    if (length <= 16)
                    {
                        Lz4Raw.CopyUpTo16(dst, matchAt, dst, matchAt - shortOffset, length);
                    }
                    else
                    {
                        Lz4Raw.Copy16(dst, matchAt, dst, matchAt - shortOffset);
                        Lz4Raw.CopyUpTo16(dst, matchAt + 16, dst, matchAt - shortOffset + 16, length - 16);
                    }
                }
                else
                {
                    int inDictionary = shortOffset - matchAt;
                    if (length > inDictionary)
                    {
                        break;
                    }

                    Lz4Raw.CopyUpTo16(dst, op, src, ip + 1, literals);
                    int from = dictionary.Length - inDictionary;
                    if (length <= 16)
                    {
                        Lz4Raw.CopyUpTo16(dst, matchAt, dictionary, from, length);
                    }
                    else
                    {
                        Lz4Raw.Copy16(dst, matchAt, dictionary, from);
                        Lz4Raw.CopyUpTo16(dst, matchAt + 16, dictionary, from + 16, length - 16);
                    }
                }

                lastMatchStart = matchAt;
                onlySequence = false;
                ip = shortOffsetAt + 2;
                op = matchAt + length;
            }

            if (ip >= iend)
            {
                // The block ended after a match (or was empty): the last sequence must carry literals only.
                result = BlockResult.Truncated;
                errorOffset = iend;
                break;
            }

            int token = Lz4Raw.Get(src, ip++);  // ip < iend, checked above

            // Block format, "Compressed block format": the high nibble is the literal count, 15 meaning more bytes follow.
            int literalLength = token >> 4;
            if (literalLength == 15)
            {
                long extended = 15;
                while (true)
                {
                    if (ip >= iend)
                    {
                        result = BlockResult.Truncated;
                        errorOffset = iend;
                        goto done;
                    }

                    int b = src[ip++];
                    extended += b;
                    if (b != 255)
                    {
                        break;
                    }
                }

                if (extended > iend - ip)
                {
                    result = BlockResult.Truncated;
                    errorOffset = iend;
                    goto done;
                }

                literalLength = (int)extended;
            }

            if (literalLength > iend - ip)
            {
                result = BlockResult.Truncated;
                errorOffset = iend;
                break;
            }

            if (literalLength > oend - op)
            {
                result = BlockResult.OutputOverflow;
                errorOffset = ip;
                break;
            }

            // Literals go in 16-byte steps when both spans have 16 bytes to spare beyond the run (the surplus is overwritten
            // by what follows); near the ends of the spans the copy is exact.
            if (literalLength <= 16)
            {
                Lz4Raw.CopyUpTo16(dst, op, src, ip, literalLength);  // both ranges were checked above
            }
            else if (literalLength <= iend - ip - 16 && literalLength <= oend - op - 16)
            {
                int k = 0;
                do
                {
                    Lz4Raw.Copy16(dst, op + k, src, ip + k);
                    k += 16;
                }
                while (k < literalLength);
            }
            else
            {
                src.Slice(ip, literalLength).CopyTo(dst.Slice(op));
            }

            ip += literalLength;
            op += literalLength;

            if (ip == iend)
            {
                // "End of block conditions": the last sequence holds only literals.
                if (!onlySequence)
                {
                    if (literalLength < Lz4Format.LastLiterals)
                    {
                        result = BlockResult.ShortLastLiterals;
                        errorOffset = iend;
                        break;
                    }

                    if (op - lastMatchStart < Lz4Format.MatchFindLimit)
                    {
                        result = BlockResult.LateLastMatch;
                        errorOffset = iend;
                        break;
                    }
                }

                result = BlockResult.Ok;
                break;
            }

            if (iend - ip < 2)
            {
                result = BlockResult.Truncated;
                errorOffset = iend;
                break;
            }

            int offsetAt = ip;
            int offset = Lz4Raw.ReadUInt16(src, ip);
            ip += 2;
            if (offset == 0)
            {
                result = BlockResult.ZeroOffset;
                errorOffset = offsetAt;
                break;
            }

            // Bytes of the offset that lie in the dictionary rather than in the output.
            int dictionaryBytes = 0;
            if (offset > op)
            {
                if (offset - op > dictionary.Length)
                {
                    result = BlockResult.OffsetTooFar;
                    errorOffset = offsetAt;
                    break;
                }

                dictionaryBytes = offset - op;
            }

            long matchExtended = token & 15;
            if (matchExtended == 15)
            {
                while (true)
                {
                    if (ip >= iend)
                    {
                        result = BlockResult.Truncated;
                        errorOffset = iend;
                        goto done;
                    }

                    int b = src[ip++];
                    matchExtended += b;
                    if (b != 255)
                    {
                        break;
                    }
                }
            }

            matchExtended += Lz4Format.MinMatch;
            if (matchExtended > oend - op)
            {
                result = BlockResult.OutputOverflow;
                errorOffset = ip;
                break;
            }

            int matchLength = (int)matchExtended;
            lastMatchStart = op;
            onlySequence = false;

            if (dictionaryBytes != 0)
            {
                // The match starts in the dictionary and, if it is longer than what is left of it, continues from the start of the
                // output, which is exactly where the dictionary's end is followed by.
                int fromDictionary = Math.Min(matchLength, dictionaryBytes);
                if (fromDictionary <= 16)
                {
                    Lz4Raw.CopyUpTo16(dst, op, dictionary, dictionary.Length - dictionaryBytes, fromDictionary);
                }
                else
                {
                    dictionary.Slice(dictionary.Length - dictionaryBytes, fromDictionary).CopyTo(dst.Slice(op));
                }

                int rest = matchLength - fromDictionary;
                for (int k = 0; k < rest; k++)
                {
                    dst[op + fromDictionary + k] = dst[k];
                }

                op += matchLength;
                continue;
            }

            // Match bytes are copied in increasing order, so a match longer than its offset repeats the last `offset` bytes.
            int mp = op - offset;
            int room = oend - op;
            if (matchLength <= 16 && offset >= matchLength)
            {
                Lz4Raw.CopyUpTo16(dst, op, dst, mp, matchLength);  // inside the output (checked above), and the ranges do not overlap
            }
            else if (offset >= 16 && room >= matchLength + 16)
            {
                // Sixteen-byte steps never read bytes the same step writes (offset >= 16) and overshoot by at most 15 bytes,
                // which the room check covers.
                int k = 0;
                do
                {
                    Lz4Raw.Copy16(dst, op + k, dst, mp + k);
                    k += 16;
                }
                while (k < matchLength);
            }
            else if (offset >= 8 && room >= matchLength + 8)
            {
                int k = 0;
                do
                {
                    Lz4Raw.Copy8(dst, op + k, dst, mp + k);
                    k += 8;
                }
                while (k < matchLength);
            }
            else if (offset >= matchLength)
            {
                dst.Slice(mp, matchLength).CopyTo(dst.Slice(op));
            }
            else if (offset >= 8)
            {
                for (int k = 0; k < matchLength; k++)
                {
                    dst[op + k] = dst[mp + k];
                }
            }
            else
            {
                Lz4Raw.RepeatShort(dst, op, offset, matchLength);
            }

            op += matchLength;
        }

    done:
        written = op - outStart;
        return result;
    }

    // The fast loop. It decodes complete sequences, with wide copies, for as long as the input has 19 bytes left (a 16-byte
    // literal copy and the 2-byte offset can read that far) and the output has 40 bytes of room (16 for the literals, then at
    // most 24 for a short match), and returns where it stopped. A sequence it cannot prove safe or valid (length bytes that run
    // out, too little room for the wide copies, a bad offset) is left undone, with the position back at its token, for the
    // general code in Decode to finish or to report. The sequence is never the block's last: 19 bytes of input remain after its
    // token, so the offset field exists.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static (int Ip, int Op, int LastMatchStart) FastLoop(ReadOnlySpan<byte> src, Span<byte> dst, ReadOnlySpan<byte> dictionary, int ipStart, int opStart, int matchStart)
    {
        nint ip = ipStart;
        nint op = opStart;
        nint ms = matchStart;
        nint iend = src.Length;
        nint oend = dst.Length;
        nint inputLimit = iend - 19;
        nint outputLimit = oend - 40;

        while (ip < inputLimit && op < outputLimit)
        {
            nint sequence = ip;
            int token = Lz4Raw.Get(src, ip);  // ip < inputLimit < iend
            ip++;
            nint literals = token >> 4;
            if (literals != 15)
            {
                Lz4Raw.Copy16(dst, op, src, ip);
                ip += literals;
                op += literals;
            }
            else
            {
                // Block format: 15 means length bytes follow, each added, and 255 means another follows.
                nint p = ip;
                nint l = 15;
                int b;
                do
                {
                    if (p >= iend || l > iend)
                    {
                        ip = sequence;
                        goto exit;
                    }

                    b = Lz4Raw.Get(src, p);
                    p++;
                    l += b;
                }
                while (b == 255);

                if (l > iend - p - 18 || l > oend - op - 40)
                {
                    ip = sequence;
                    goto exit;
                }

                nint k = 0;
                do
                {
                    Lz4Raw.Copy16(dst, op + k, src, p + k);
                    k += 16;
                }
                while (k < l);

                literals = l;
                ip = p + l;
                op += l;
            }

            // At least 18 bytes of input follow the literals, so the offset can be read.
            nint offset = Lz4Raw.ReadUInt16(src, ip);
            ip += 2;
            if ((nuint)(offset - 1) >= (nuint)op)
            {
                // A short match that lies wholly inside the dictionary is copied here; anything else that reaches before the
                // start of the output (a long match, one that runs on into the output, offset 0, a bad offset) is left for the
                // general code to finish or to report.
                nint dictionaryBytes = offset - op;
                nint shortLength = (token & 15) + Lz4Format.MinMatch;
                if (offset == 0 || (token & 15) == 15 || dictionaryBytes > dictionary.Length || shortLength > dictionaryBytes)
                {
                    ip = sequence;
                    op -= literals;
                    goto exit;
                }

                ms = op;
                nint from = dictionary.Length - dictionaryBytes;
                if (shortLength <= 16)
                {
                    Lz4Raw.CopyUpTo16(dst, op, dictionary, from, shortLength);
                }
                else
                {
                    Lz4Raw.Copy16(dst, op, dictionary, from);
                    Lz4Raw.CopyUpTo16(dst, op + 16, dictionary, from + 16, shortLength - 16);
                }

                op += shortLength;
                continue;
            }

            nint length = token & 15;
            nint source = op - offset;
            if (length != 15)
            {
                // Matches of 4 to 18 bytes: 24 bytes are always written (the surplus is overwritten by what follows), which
                // costs less than a branch on the length.
                length += Lz4Format.MinMatch;
                ms = op; // the block format's end rule needs the start of the last match
                if (offset >= 16)
                {
                    Lz4Raw.Copy16(dst, op, dst, source);
                    Lz4Raw.Copy8(dst, op + 16, dst, source + 16);
                }
                else if (offset >= 8)
                {
                    Lz4Raw.Copy8(dst, op, dst, source);
                    Lz4Raw.Copy8(dst, op + 8, dst, source + 8);
                    Lz4Raw.Copy8(dst, op + 16, dst, source + 16);
                }
                else if (offset == 1)
                {
                    ulong v = (ulong)Lz4Raw.Get(dst, source) * 0x0101010101010101UL;
                    Lz4Raw.Write8(dst, op, v);
                    Lz4Raw.Write8(dst, op + 8, v);
                    Lz4Raw.Write8(dst, op + 16, v);
                }
                else if (offset == 2)
                {
                    ulong v = (ulong)Lz4Raw.ReadUInt16(dst, source) * 0x0001000100010001UL;
                    Lz4Raw.Write8(dst, op, v);
                    Lz4Raw.Write8(dst, op + 8, v);
                    Lz4Raw.Write8(dst, op + 16, v);
                }
                else if (offset == 4)
                {
                    ulong v = (ulong)Lz4Raw.ReadUInt32(dst, source) * 0x0000000100000001UL;
                    Lz4Raw.Write8(dst, op, v);
                    Lz4Raw.Write8(dst, op + 8, v);
                    Lz4Raw.Write8(dst, op + 16, v);
                }
                else
                {
                    Lz4Raw.RepeatShortInline(dst, op, offset, length);
                }
            }
            else
            {
                nint p = ip;
                length = 15 + Lz4Format.MinMatch;
                int b;
                do
                {
                    if (p >= iend || length > oend)
                    {
                        ip = sequence;
                        op -= literals;
                        goto exit;
                    }

                    b = Lz4Raw.Get(src, p);
                    p++;
                    length += b;
                }
                while (b == 255);

                if (length > oend - op - 16)
                {
                    ip = sequence;
                    op -= literals;
                    goto exit;
                }

                ms = op; // the block format's end rule needs the start of the last match
                if (offset >= 16)
                {
                    nint k = 0;
                    do
                    {
                        Lz4Raw.Copy16(dst, op + k, dst, source + k);
                        k += 16;
                    }
                    while (k < length);
                }
                else if (offset >= 8)
                {
                    nint k = 0;
                    do
                    {
                        Lz4Raw.Copy8(dst, op + k, dst, source + k);
                        k += 8;
                    }
                    while (k < length);
                }
                else
                {
                    Lz4Raw.RepeatShortInline(dst, op, offset, length);
                }

                ip = p;
            }

            op += length;
        }

    exit:
        return ((int)ip, (int)op, (int)ms);
    }

    private static BlockResult Measure(ReadOnlySpan<byte> src, int history, out long size, out int errorOffset)
    {
        int ip = 0;
        long op = history;
        int iend = src.Length;
        long lastMatchStart = 0;
        bool onlySequence = true;
        size = 0;
        errorOffset = 0;
        while (true)
        {
            if (ip >= iend)
            {
                errorOffset = iend;
                return BlockResult.Truncated;
            }

            int token = src[ip++];
            long literalLength = token >> 4;
            if (literalLength == 15)
            {
                int b;
                do
                {
                    if (ip >= iend)
                    {
                        errorOffset = iend;
                        return BlockResult.Truncated;
                    }

                    b = src[ip++];
                    literalLength += b;
                }
                while (b == 255);
            }

            if (literalLength > iend - ip)
            {
                errorOffset = iend;
                return BlockResult.Truncated;
            }

            ip += (int)literalLength;
            op += literalLength;
            if (ip == iend)
            {
                if (!onlySequence)
                {
                    if (literalLength < Lz4Format.LastLiterals)
                    {
                        errorOffset = iend;
                        return BlockResult.ShortLastLiterals;
                    }

                    if (op - lastMatchStart < Lz4Format.MatchFindLimit)
                    {
                        errorOffset = iend;
                        return BlockResult.LateLastMatch;
                    }
                }

                size = op - history;
                return BlockResult.Ok;
            }

            if (iend - ip < 2)
            {
                errorOffset = iend;
                return BlockResult.Truncated;
            }

            int offsetAt = ip;
            int offset = src[ip] | (src[ip + 1] << 8);
            ip += 2;
            if (offset == 0)
            {
                errorOffset = offsetAt;
                return BlockResult.ZeroOffset;
            }

            if (offset > op)
            {
                errorOffset = offsetAt;
                return BlockResult.OffsetTooFar;
            }

            long matchLength = token & 15;
            if (matchLength == 15)
            {
                int b;
                do
                {
                    if (ip >= iend)
                    {
                        errorOffset = iend;
                        return BlockResult.Truncated;
                    }

                    b = src[ip++];
                    matchLength += b;
                }
                while (b == 255);
            }

            lastMatchStart = op;
            onlySequence = false;
            op += matchLength + Lz4Format.MinMatch;
        }
    }
}
