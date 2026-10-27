using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// Writes the sequences of an LZ4 block (LZ4 Block Format, "Compressed block format"): a token holding the literal count
/// and the match length minus four, the literals, a 16-bit offset, and extra length bytes when a count reaches 15. A
/// match finder decides where the matches are; this type turns each decision into bytes. A block built from
/// <see cref="WriteSequence"/> calls ends with <see cref="WriteLastLiterals"/>, whose literals the block format requires
/// to be at least five bytes (and the last match to start at least twelve bytes before the end), unless the whole block is
/// literals.
/// </summary>
public static class Lz4BlockWriter
{
    /// <summary>The shortest match a sequence can carry.</summary>
    public const int MinMatch = Lz4Format.MinMatch;

    /// <summary>The largest offset a sequence can carry: 65535.</summary>
    public const int MaxOffset = Lz4Format.MaxOffset;

    /// <summary>
    /// Gets the most bytes a block of <paramref name="inputLength"/> bytes can compress to: the input, one byte per 255
    /// input bytes for length fields, and 16 bytes of slack for the token and final literals (the same bound the
    /// compressors require of the destination).
    /// </summary>
    /// <param name="inputLength">The number of bytes to compress; must not be negative.</param>
    /// <returns>The size a destination must have to hold any compressed block, saturating at <see cref="int.MaxValue"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative.</exception>
    public static int GetMaxCompressedLength(int inputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        long bound = inputLength + (inputLength / 255) + 16L;
        return bound > int.MaxValue ? int.MaxValue : (int)bound;
    }

    /// <summary>Gets the number of bytes <see cref="WriteSequence"/> writes for the given counts.</summary>
    /// <param name="literalCount">The number of literals; not negative.</param>
    /// <param name="matchLength">The match length, at least <see cref="MinMatch"/>.</param>
    /// <returns>The size in bytes: token, literal length bytes, literals, offset and match length bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is out of range.</exception>
    public static long GetSequenceLength(int literalCount, int matchLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(literalCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(matchLength, MinMatch);
        return 1 + ExtraBytes(literalCount) + literalCount + 2 + ExtraBytes(matchLength - MinMatch);
    }

    /// <summary>Writes one sequence: <paramref name="literals"/>, then a match of <paramref name="matchLength"/> bytes at <paramref name="offset"/> bytes back.</summary>
    /// <param name="destination">Receives the sequence; it must hold <see cref="GetSequenceLength"/> bytes.</param>
    /// <param name="literals">The literal bytes copied to the output before the match; may be empty.</param>
    /// <param name="offset">The distance back to the match source, from 1 to <see cref="MaxOffset"/>.</param>
    /// <param name="matchLength">The match length, at least <see cref="MinMatch"/>.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="matchLength"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static int WriteSequence(Span<byte> destination, ReadOnlySpan<byte> literals, int offset, int matchLength)
    {
        if (offset < 1 || offset > MaxOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset must be from 1 to 65535.");
        }

        long length = GetSequenceLength(literals.Length, matchLength);
        if (length > destination.Length)
        {
            throw new ArgumentException("The destination is too small for the sequence.", nameof(destination));
        }

        int op = 0;
        int lit = literals.Length;
        int token = (lit >= 15 ? 15 : lit) << 4;
        int tokenAt = op++;
        if (lit >= 15)
        {
            op = PutLength(destination, op, lit - 15);
        }

        literals.CopyTo(destination.Slice(op));
        op += lit;
        Lz4EncRaw.Put16(destination, op, (ushort)offset);
        op += 2;
        int ml = matchLength - MinMatch;
        if (ml >= 15)
        {
            token |= 15;
            op = PutLength(destination, op, ml - 15);
        }
        else
        {
            token |= ml;
        }

        destination[tokenAt] = (byte)token;
        return op;
    }

    /// <summary>Writes the final sequence of a block: literals only, no offset.</summary>
    /// <param name="destination">Receives the sequence; it must hold 1 + (extra length bytes) + <paramref name="literals"/>.Length bytes.</param>
    /// <param name="literals">The last literals of the block; may be empty (a block holding no data is the single byte 0x00).</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public static int WriteLastLiterals(Span<byte> destination, ReadOnlySpan<byte> literals)
    {
        long length = 1 + ExtraBytes(literals.Length) + (long)literals.Length;
        if (length > destination.Length)
        {
            throw new ArgumentException("The destination is too small for the literals.", nameof(destination));
        }

        int lit = literals.Length;
        int op = 0;
        destination[op++] = (byte)((lit >= 15 ? 15 : lit) << 4);
        if (lit >= 15)
        {
            op = PutLength(destination, op, lit - 15);
        }

        literals.CopyTo(destination.Slice(op));
        return op + lit;
    }

    // Number of length bytes after the token for a count: none below 15, else (count - 15) / 255 + 1.
    private static int ExtraBytes(int count) => count >= 15 ? ((count - 15) / 255) + 1 : 0;

    private static int PutLength(Span<byte> destination, int op, int remainder)
    {
        while (remainder >= 255)
        {
            destination[op++] = 255;
            remainder -= 255;
        }

        destination[op++] = (byte)remainder;
        return op;
    }

    /// <summary>
    /// Writes a sequence without checking the destination: the caller guarantees room for the whole sequence, the literals
    /// to lie inside <paramref name="source"/>, <c>1 &lt;= offset &lt;= 65535</c> and <c>matchLength &gt;= 4</c>. When both
    /// spans have 15 spare bytes beyond the literals they are copied 16 bytes at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Sequence(Span<byte> destination, int op, ReadOnlySpan<byte> source, int anchor, int literalCount, int offset, int matchLength)
    {
        int tokenAt = op++;
        // The wide copy reads and writes up to 15 bytes past the literals, so it needs that much room on both sides.
        bool wide = anchor + literalCount + 15 <= source.Length && op + literalCount + (literalCount / 255) + 18 <= destination.Length;
        int token;
        if (literalCount >= 15)
        {
            token = 15 << 4;
            int r = literalCount - 15;
            while (r >= 255)
            {
                Lz4EncRaw.Put(destination, op++, 255);
                r -= 255;
            }

            Lz4EncRaw.Put(destination, op++, (byte)r);
        }
        else
        {
            token = literalCount << 4;
        }

        if (wide)
        {
            // Copies in 16-byte steps; the bytes written past the literals are overwritten by what follows.
            for (int i = 0; i < literalCount; i += 16)
            {
                Lz4EncRaw.Copy16(destination, op + i, source, anchor + i);
            }
        }
        else
        {
            source.Slice(anchor, literalCount).CopyTo(destination.Slice(op));
        }

        op += literalCount;
        Lz4EncRaw.Put16(destination, op, (ushort)offset);
        op += 2;
        int ml = matchLength - MinMatch;
        if (ml >= 15)
        {
            token |= 15;
            ml -= 15;
            while (ml >= 255)
            {
                Lz4EncRaw.Put(destination, op++, 255);
                ml -= 255;
            }

            Lz4EncRaw.Put(destination, op++, (byte)ml);
        }
        else
        {
            token |= ml;
        }

        Lz4EncRaw.Put(destination, tokenAt, (byte)token);
        return op;
    }

    /// <summary>Writes the final literals without the public checks; the caller guarantees room.</summary>
    internal static int LastLiterals(Span<byte> destination, int op, ReadOnlySpan<byte> source, int anchor, int literalCount)
    {
        if (literalCount >= 15)
        {
            destination[op++] = 15 << 4;
            op = PutLength(destination, op, literalCount - 15);
        }
        else
        {
            destination[op++] = (byte)(literalCount << 4);
        }

        source.Slice(anchor, literalCount).CopyTo(destination.Slice(op));
        return op + literalCount;
    }
}
