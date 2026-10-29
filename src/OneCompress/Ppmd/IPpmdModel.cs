namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>What both PPMd models offer to the span-based encoder and decoder drivers.</summary>
internal interface IPpmdModel
{
    ReadOnlySpan<byte> PendingOutput { get; }

    bool IsFinishedOk { get; }

    void ConsumeOutput(int count);

    void StartEncoding();

    void EncodeSymbol(int symbol);

    /// <summary>Encodes bytes of <paramref name="source"/> in a row until all are coded or at least <paramref name="maxPending"/> output bytes are waiting; returns how many it coded.</summary>
    int EncodeBlock(ReadOnlySpan<byte> source, int maxPending);

    void FinishEncoding();

    bool StartDecoding(ReadOnlySpan<byte> input, ref int pos);

    int DecodeSymbol(ReadOnlySpan<byte> input, ref int pos);

    /// <summary>
    /// Decodes symbols into <paramref name="destination"/> until it is full or a symbol is not a byte. Returns 0 when it is full,
    /// otherwise what <see cref="DecodeSymbol"/> returned for the symbol that stopped it (-1 end marker, -2 invalid, -3 needs more input);
    /// <paramref name="count"/> is the number of bytes written either way.
    /// </summary>
    int DecodeBlock(ReadOnlySpan<byte> input, ref int pos, Span<byte> destination, out int count);
}
