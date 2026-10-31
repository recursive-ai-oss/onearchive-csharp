namespace RecursiveAI.OneCompress.Zstd;

public partial struct ZstdRepeatOffsets
{
    /// <summary>
    /// The encoder's side of the repeat offset rules: chooses the offset value to write for a match and updates the offsets as
    /// the decoder will (RFC 8878 §3.1.1.5).
    /// </summary>
    /// <param name="offset">The real offset of the match.</param>
    /// <param name="literalLength">The number of literals before the match (zero shifts the meaning of the repeat codes).</param>
    /// <returns>1 to 3 when a repeat code applies, otherwise <paramref name="offset"/> + 3.</returns>
    public uint Apply(uint offset, uint literalLength)
    {
        if (literalLength > 0)
        {
            if (offset == Offset1)
            {
                return 1;
            }

            if (offset == Offset2)
            {
                Offset2 = Offset1;
                Offset1 = offset;
                return 2;
            }

            if (offset == Offset3)
            {
                Offset3 = Offset2;
                Offset2 = Offset1;
                Offset1 = offset;
                return 3;
            }
        }
        else
        {
            if (offset == Offset2)
            {
                Offset2 = Offset1;
                Offset1 = offset;
                return 1;
            }

            if (offset == Offset3)
            {
                Offset3 = Offset2;
                Offset2 = Offset1;
                Offset1 = offset;
                return 2;
            }

            if (Offset1 > 1 && offset == Offset1 - 1)
            {
                Offset3 = Offset2;
                Offset2 = Offset1;
                Offset1 = offset;
                return 3;
            }
        }

        Offset3 = Offset2;
        Offset2 = Offset1;
        Offset1 = offset;
        return offset + 3;
    }
}
