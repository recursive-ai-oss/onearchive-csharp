using System.Buffers;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>One-call helpers over the streaming loop, for the encoders' static entry points.</summary>
internal static class OneShotEncode
{
    public static byte[] Run<T>(T encoder, ReadOnlySpan<byte> source)
        where T : class, IQueuedEncoder
    {
        byte[] scratch = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            var result = new ArrayBufferWriter<byte>(Math.Max(source.Length / 3, 64));
            int consumed = 0;
            while (true)
            {
                OperationStatus status = EncodeLoop.Compress(encoder, source[consumed..], scratch, out int c, out int w, true);
                consumed += c;
                result.Write(scratch.AsSpan(0, w));
                if (status == OperationStatus.Done)
                {
                    return result.WrittenSpan.ToArray();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public static bool TryRun<T>(T encoder, ReadOnlySpan<byte> source, Span<byte> destination, out int written)
        where T : class, IQueuedEncoder
    {
        OperationStatus status = EncodeLoop.Compress(encoder, source, destination, out _, out written, true);
        if (status == OperationStatus.Done)
        {
            return true;
        }

        written = 0;
        return false;
    }
}
