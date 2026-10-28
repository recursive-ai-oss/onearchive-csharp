using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Deflate64;

namespace RecursiveAI.HashMatrix;

/// <summary>Deflate64 compression at every level, and streamed with sync flushes: the bytes must not depend on the platform or vector support.</summary>
internal static class Deflate64EncoderEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        for (int level = 0; level <= 9; level++)
        {
            int l = level;
            Matrix.Add("deflate64-enc", $"level{l}", (input, _) => Deflate64.Compress(input, new Deflate64Options { Level = l }));
        }

        foreach (int level in new[] { 1, 6, 9 })
        {
            int l = level;
            Matrix.Add("deflate64-enc", $"level{l}-flushes", (input, _) => Streamed(input, l));
        }
    }

    private static byte[] Streamed(ReadOnlySpan<byte> input, int level)
    {
        var encoder = new Deflate64Encoder(new Deflate64Options { Level = level });
        var output = new List<byte>();
        byte[] buffer = new byte[777];
        int[] pieces = [1, 4999, 10000, 33333, 70001, 250000];
        int offset = 0;
        int step = 0;
        while (true)
        {
            int take = Math.Min(pieces[step % pieces.Length], input.Length - offset);
            bool last = offset + take == input.Length;
            ReadOnlySpan<byte> source = input.Slice(offset, take);
            while (true)
            {
                OperationStatus status = encoder.Compress(source, buffer, out int consumed, out int written, last);
                output.AddRange(buffer.AsSpan(0, written).ToArray());
                source = source[consumed..];
                if (status == OperationStatus.Done || (status == OperationStatus.NeedMoreData && source.IsEmpty))
                {
                    break;
                }
            }

            offset += take;
            step++;
            if (last)
            {
                return [.. output];
            }

            if (step % 3 == 1)
            {
                OperationStatus status;
                do
                {
                    status = encoder.Flush(buffer, out int written);
                    output.AddRange(buffer.AsSpan(0, written).ToArray());
                }
                while (status == OperationStatus.DestinationTooSmall);
            }
        }
    }
}
