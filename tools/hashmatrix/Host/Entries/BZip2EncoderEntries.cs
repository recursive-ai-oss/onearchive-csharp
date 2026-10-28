using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.BZip2;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;

namespace RecursiveAI.HashMatrix;

/// <summary>bzip2 compression at every level, with the longest code length raised, and written in pieces: the bytes must not depend on the platform or vector support.</summary>
internal static class BZip2EncoderEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        for (int level = 1; level <= 9; level++)
        {
            int l = level;
            Matrix.Add("bzip2-encode", $"level{l}", (input, _) => BZ.Compress(input, new BZip2Options { Level = l }));
        }

        Matrix.Add("bzip2-encode", "level9-code20", (input, _) => BZ.Compress(input, new BZip2Options { Level = 9, MaxCodeLength = 20 }));
        Matrix.Add("bzip2-encode", "level3-pieces", (input, _) => Pieces(input, 3));
    }

    private static byte[] Pieces(ReadOnlySpan<byte> input, int level)
    {
        var encoder = new BZip2Encoder(new BZip2Options { Level = level });
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
        }
    }
}
