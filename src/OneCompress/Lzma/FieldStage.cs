using System.Buffers;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>Collects the bytes of a small field that arrives in pieces (headers, trailers, chunk headers).</summary>
internal sealed class FieldStage
{
    private byte[] _buffer = new byte[32];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public void Append(byte value)
    {
        if (Length == _buffer.Length)
        {
            Array.Resize(ref _buffer, Length * 2);
        }

        _buffer[Length++] = value;
    }

    /// <summary>Moves bytes from <paramref name="source"/> at <paramref name="position"/> until <paramref name="need"/> bytes are held.</summary>
    /// <returns>True when the field is complete.</returns>
    public bool Fill(ReadOnlySpan<byte> source, ref int position, int need)
    {
        if (Length >= need)
        {
            return true;
        }

        if (_buffer.Length < need)
        {
            Array.Resize(ref _buffer, Math.Max(need, _buffer.Length * 2));
        }

        int n = Math.Min(need - Length, source.Length - position);
        source.Slice(position, n).CopyTo(_buffer.AsSpan(Length));
        Length += n;
        position += n;
        return Length >= need;
    }
}

/// <summary>A decoder that can be driven one step at a time; <see cref="OutputLimiter"/> adds the output limit around it.</summary>
internal interface IStepDecoder
{
    OperationStatus Step(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written);

    OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written);

    void ThrowIfIncomplete();

    bool IsAtFrameBoundary { get; }
}

/// <summary>
/// Enforces <c>MaxOutputSize</c> for a step decoder without ever writing a byte past it: the destination is cut at the limit, and when
/// the cut destination fills up a one-byte probe tells whether the decoder has more output (a limit error) or is at its end.
/// </summary>
internal static class OutputLimiter
{
    public static OperationStatus Run<T>(
        T decoder,
        ref long totalOut,
        long maxOutput,
        string format,
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        out int consumed,
        out int written)
        where T : class, IStepDecoder
    {
        long allowed = maxOutput - totalOut;
        Span<byte> window = destination.Length <= allowed ? destination : destination[..(int)allowed];
        OperationStatus status = decoder.Step(source, window, out consumed, out written);
        totalOut += written;
        if (status == OperationStatus.DestinationTooSmall && totalOut >= maxOutput)
        {
            Span<byte> probe = stackalloc byte[1];
            status = decoder.Step(source[consumed..], probe, out int c, out int w);
            consumed += c;
            if (w > 0)
            {
                throw new CompressionLimitException(format, "MaxOutputSize", maxOutput, maxOutput + 1);
            }
        }

        return status;
    }
}


/// <summary>The one-call wrappers over a step decoder, shared by LZMA, LZMA2, xz and lzip.</summary>
internal static class OneShot
{
    private const int InitialFactor = 4;

    public static byte[] Decompress<T>(T decoder, ReadOnlySpan<byte> source, string format, long maxOutput, bool failOnTrailing, bool multiStream = false)
        where T : class, IStepDecoder
    {
        if (source.IsEmpty)
        {
            throw new CompressionFormatException(format, 0, "a " + format + " stream", isTruncated: true);
        }

        long hint = Math.Min(Math.Min((long)InitialFactor * source.Length + 64, maxOutput), 1 << 20);
        byte[] output = new byte[Math.Min(hint, Array.MaxLength)];
        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source[consumed..], output.AsSpan(written), out int c, out int w);
            consumed += c;
            written += w;
            switch (status)
            {
                case OperationStatus.Done:
                    if (multiStream && consumed < source.Length && (c > 0 || w > 0))
                    {
                        break;
                    }

                    if (failOnTrailing && consumed < source.Length)
                    {
                        throw new CompressionFormatException(format, consumed, "the end of the data after the " + format + " stream");
                    }

                    return written == output.Length ? output : output.AsSpan(0, written).ToArray();
                case OperationStatus.NeedMoreData:
                    decoder.ThrowIfIncomplete();
                    return written == output.Length ? output : output.AsSpan(0, written).ToArray();
                default: // DestinationTooSmall: grow
                    if (output.Length >= Array.MaxLength)
                    {
                        throw new CompressionLimitException(format, "MaxOutputSize", Array.MaxLength, (long)written + 1);
                    }

                    // Doubling, but no further than one byte past the output limit (so the decoder sees the limit being passed). A limit
                    // of long.MaxValue must not be added to, or the sum wraps and the buffer would grow one byte at a time.
                    long grown = Math.Min((long)output.Length * 2, Array.MaxLength);
                    if (maxOutput < Array.MaxLength)
                    {
                        grown = Math.Min(grown, Math.Max(maxOutput + 1, output.Length + 1));
                    }

                    Array.Resize(ref output, (int)grown);
                    break;
            }
        }
    }

    public static bool TryDecompress<T>(T decoder, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, string format, bool failOnTrailing, bool multiStream = false)
        where T : class, IStepDecoder
    {
        bytesWritten = 0;
        if (source.IsEmpty)
        {
            throw new CompressionFormatException(format, 0, "a " + format + " stream", isTruncated: true);
        }

        Span<byte> probe = stackalloc byte[1];
        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source[consumed..], destination[written..], out int c, out int w);
            consumed += c;
            written += w;
            if (status == OperationStatus.DestinationTooSmall)
            {
                // The destination is full. A stream of unknown size may still be about to end (an end marker), which only
                // trying one more byte shows.
                status = decoder.Decompress(source[consumed..], probe, out c, out int w2);
                if (w2 > 0)
                {
                    return false;
                }

                consumed += c;
                w = 0;
            }

            switch (status)
            {
                case OperationStatus.Done:
                    if (multiStream && consumed < source.Length && (c > 0 || w > 0))
                    {
                        break;
                    }

                    if (failOnTrailing && consumed < source.Length)
                    {
                        throw new CompressionFormatException(format, consumed, "the end of the data after the " + format + " stream");
                    }

                    bytesWritten = written;
                    return true;
                default: // NeedMoreData
                    decoder.ThrowIfIncomplete();
                    bytesWritten = written;
                    return true;
            }
        }
    }
}
