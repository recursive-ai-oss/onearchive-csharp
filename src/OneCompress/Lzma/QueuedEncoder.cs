using System.Buffers;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>What the span-based streaming loop needs from an LZMA family encoder.</summary>
internal interface IQueuedEncoder
{
    OutputQueue Queue { get; }

    bool IsDone { get; }

    // Copies input into the window; returns the number of bytes taken.
    int Accept(ReadOnlySpan<byte> source);

    // Encodes buffered input into the queue (all of it when drain is set, otherwise as much as the lookahead rule allows).
    // Returns true when it encoded something, so the caller should call again.
    bool Pump(bool drain);

    // Writes the trailer once Pump(true) has nothing left, and marks the encoder done.
    void Finish();
}

/// <summary>The span-based streaming loop shared by the LZMA family encoders: drains the queue into the caller's span and reports the status.</summary>
internal static class EncodeLoop
{
    public static OperationStatus Compress<T>(T encoder, ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock)
        where T : class, IQueuedEncoder
    {
        OutputQueue queue = encoder.Queue;
        consumed = 0;
        written = 0;
        while (true)
        {
            written += queue.Drain(destination[written..]);
            if (queue.Count > 0)
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (encoder.IsDone)
            {
                if (consumed < source.Length)
                {
                    throw new InvalidOperationException("The stream was already finished; more input was given after the final block.");
                }

                return OperationStatus.Done;
            }

            int took = encoder.Accept(source[consumed..]);
            consumed += took;
            bool allIn = consumed == source.Length;
            bool fin = isFinalBlock && allIn;
            bool progressed = encoder.Pump(fin);
            if (fin)
            {
                if (!progressed)
                {
                    encoder.Finish();
                }

                continue;
            }

            if (!progressed && queue.Count == 0)
            {
                if (allIn)
                {
                    return OperationStatus.NeedMoreData;
                }

                if (took == 0)
                {
                    throw new InvalidOperationException("The encoder window is full but nothing can be encoded.");
                }
            }
        }
    }

    // Encodes everything buffered, leaving the stream open.
    public static OperationStatus Flush<T>(T encoder, Span<byte> destination, out int written)
        where T : class, IQueuedEncoder
    {
        while (encoder.Pump(true))
        {
        }

        written = encoder.Queue.Drain(destination);
        return encoder.Queue.Count > 0 ? OperationStatus.DestinationTooSmall : OperationStatus.Done;
    }
}
