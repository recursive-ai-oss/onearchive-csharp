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
            if (ip >= iend)
            {
                // The block ended after a match (or was empty): the last sequence must carry literals only.
                result = BlockResult.Truncated;
                errorOffset = iend;
                break;
            }

            int token = src[ip++];

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

            // Short literal runs are copied as one wide block; the bytes after the run are overwritten by what follows.
            if (literalLength <= 16 && iend - ip >= 16 && oend - op >= 16)
            {
                Lz4Raw.Copy16(dst, op, src, ip);
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
            int offset = src[ip] | (src[ip + 1] << 8);
            ip += 2;
            if (offset == 0)
            {
                result = BlockResult.ZeroOffset;
                errorOffset = offsetAt;
                break;
            }

            if (offset > op)
            {
                result = BlockResult.OffsetTooFar;
                errorOffset = offsetAt;
                break;
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
            CopyMatch(dst, op, offset, matchLength);
            op += matchLength;
        }

    done:
        written = op - outStart;
        return result;
    }

    // Copies matchLength bytes from offset bytes back; the caller has checked offset <= op and matchLength <= room.
    // When offset < matchLength the source overlaps what is being written, which repeats the last offset bytes.
    private static void CopyMatch(Span<byte> dst, int op, int offset, int matchLength)
    {
        int mp = op - offset;
        int room = dst.Length - op;
        if (offset >= 16 && room >= matchLength + 16)
        {
            // Sixteen-byte steps never read bytes the same step writes (offset >= 16) and overshoot by at most 15 bytes,
            // which the room check covers; the overshoot is overwritten by the next sequence.
            int end = op + matchLength;
            do
            {
                Lz4Raw.Copy16(dst, op, dst, mp);
                op += 16;
                mp += 16;
            }
            while (op < end);
        }
        else if (offset >= 8 && room >= matchLength + 8)
        {
            int end = op + matchLength;
            do
            {
                Lz4Raw.Copy8(dst, op, dst, mp);
                op += 8;
                mp += 8;
            }
            while (op < end);
        }
        else if (offset == 1)
        {
            dst.Slice(op, matchLength).Fill(dst[mp]);
        }
        else if (offset >= matchLength)
        {
            dst.Slice(mp, matchLength).CopyTo(dst.Slice(op));
        }
        else
        {
            for (int i = 0; i < matchLength; i++)
            {
                dst[op + i] = dst[mp + i];
            }
        }
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
