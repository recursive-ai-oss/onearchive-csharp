namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>What both PPMd models offer to the span-based encoder and decoder drivers.</summary>
internal interface IPpmdModel
{
    ReadOnlySpan<byte> PendingOutput { get; }

    bool IsFinishedOk { get; }

    void ConsumeOutput(int count);

    void StartEncoding();

    void EncodeSymbol(int symbol);

    void FinishEncoding();

    bool StartDecoding(ReadOnlySpan<byte> input, ref int pos);

    int DecodeSymbol(ReadOnlySpan<byte> input, ref int pos);
}
