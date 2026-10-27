using System.Buffers;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Round-trip fuzz targets for the LZ4 encoders. The input bytes choose the options (level, block layout, checksums,
/// dictionary, how the data is split) and then describe the data to compress as a small program of literal runs and
/// copies, so the fuzzer reaches long matches, overlapping copies, runs and incompressible stretches. Any exception, any
/// mismatch after decoding, and any difference between splitting the input differently is a finding.
/// </summary>
internal static class Lz4EncoderTargets
{
    private static readonly int[] s_levels = [-65536, -1000, -5, -2, -1, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    private static readonly Lz4Dictionary s_dictionary = new(Expand(Enumerable.Range(0, 6000).Select(i => (byte)((i * 7) ^ (i >> 3))).ToArray(), 6000), 0x4242);

    public static FuzzTarget Frame { get; } = new("lz4-encode-frame", static data => RoundTripFrame(data), static _ => false);

    public static FuzzTarget Block { get; } = new("lz4-encode-block", static data => RoundTripBlock(data), static _ => false);

    // Turns fuzzer bytes into compressible data: bytes below 0x80 start a literal run taken from the input, the rest are copies.
    private static byte[] Expand(ReadOnlySpan<byte> program, int limit)
    {
        var output = new List<byte>(Math.Min(limit, program.Length * 4));
        int i = 0;
        while (i < program.Length && output.Count < limit)
        {
            byte op = program[i++];
            if (op < 0x80 || output.Count == 0)
            {
                int run = (op & 0x3F) + 1;
                for (int k = 0; k < run && i < program.Length; k++)
                {
                    output.Add(program[i++]);
                }
            }
            else
            {
                if (i + 1 >= program.Length)
                {
                    break;
                }

                // A copy: length from the opcode (more for large values), distance from the next two bytes.
                int length = 4 + (op & 0x1F) + ((op & 0x60) == 0x60 ? program[i] * 8 : 0);
                int distance = 1 + (((program[i] << 8) | program[i + 1]) % output.Count);
                i += 2;
                for (int k = 0; k < length && output.Count < limit; k++)
                {
                    output.Add(output[^distance]);
                }
            }
        }

        return [.. output];
    }

    private static void RoundTripFrame(ReadOnlySpan<byte> input)
    {
        if (input.Length < 4)
        {
            return;
        }

        int level = s_levels[input[0] % s_levels.Length];
        int flags = input[1];
        var options = new Lz4CompressionOptions
        {
            Level = level,
            BlockSize = (Lz4BlockSize)(4 + (flags & 3)),
            BlocksIndependent = (flags & 4) != 0,
            BlockChecksum = (flags & 8) != 0,
            ContentChecksum = (flags & 16) != 0,
            IncludeContentSize = (flags & 32) != 0,
            Dictionary = (flags & 64) != 0 ? s_dictionary : null,
        };
        int split = 1 + (input[2] * 257 % 20_000);
        int outSize = 16 + (input[3] * 61 % 5000);

        // Slow levels get less data to keep each execution short.
        byte[] data = Expand(input[4..], level >= 10 ? 60_000 : 400_000);

        byte[] whole = Lz4.Compress(data, options);
        byte[] back = Lz4.Decompress(whole, new Lz4DecompressionOptions { Dictionary = options.Dictionary });
        if (!back.AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException($"frame round trip differs (level {level}, flags {flags:X2})");
        }

        // The same frame from the streaming encoder, fed in pieces and drained through a small buffer (no content size: the
        // one-shot call knows it, the stream does not).
        var streaming = new Lz4Encoder(new Lz4CompressionOptions
        {
            Level = level,
            BlockSize = options.BlockSize,
            BlocksIndependent = options.BlocksIndependent,
            BlockChecksum = options.BlockChecksum,
            ContentChecksum = options.ContentChecksum,
            ContentSize = (flags & 32) != 0 ? data.Length : null,
            Dictionary = options.Dictionary,
        });
        var result = new MemoryStream();
        byte[] buffer = new byte[outSize];
        int pos = 0;
        while (true)
        {
            int take = Math.Min(split, data.Length - pos);
            OperationStatus status = streaming.Compress(data.AsSpan(pos, take), buffer, out int c, out int w, pos + take == data.Length);
            result.Write(buffer, 0, w);
            pos += c;
            if (status == OperationStatus.Done && pos == data.Length)
            {
                break;
            }
        }

        byte[] streamed = result.ToArray();
        if (!streamed.AsSpan().SequenceEqual(whole))
        {
            throw new InvalidOperationException($"streaming output differs from one-shot (level {level}, flags {flags:X2}, split {split}, out {outSize}, data {data.Length}, lengths {streamed.Length} and {whole.Length}, first difference at {streamed.AsSpan().CommonPrefixLength(whole)})");
        }
    }

    private static void RoundTripBlock(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        int level = s_levels[input[0] % s_levels.Length];
        bool dictionary = (input[1] & 1) != 0;
        byte[] data = Expand(input[3..], level >= 10 ? 60_000 : 400_000);
        var options = new Lz4CompressionOptions { Level = level, Dictionary = dictionary ? s_dictionary : null };
        byte[] block = Lz4Block.Compress(data, options);
        byte[] back = Lz4Block.Decompress(block, new Lz4BlockOptions { Dictionary = options.Dictionary, MaxOutputSize = 1 << 24 });
        if (!back.AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException($"block round trip differs (level {level})");
        }

        // A reused compressor must give the same bytes as a fresh one, after a block of different data.
        Lz4BlockCompressor reused = Lz4BlockCompressor.Create(level);
        reused.SetDictionary(options.Dictionary);
        byte[] scratch = new byte[Lz4BlockWriter.GetMaxCompressedLength(data.Length)];
        byte[] other = Expand(input[1..], 5000); // may run a little over its limit
        reused.Compress(other, 0, new byte[Lz4BlockWriter.GetMaxCompressedLength(other.Length)]);
        int n = reused.Compress(data, 0, scratch);
        if (!scratch.AsSpan(0, n).SequenceEqual(block))
        {
            throw new InvalidOperationException($"reused compressor differs from a fresh one (level {level})");
        }

        // Linked blocks: the second half is compressed with the first as history, and decodes against it.
        if (data.Length >= 64)
        {
            int cut = Math.Max(16, input[2] * data.Length / 255);
            cut = Math.Min(cut, data.Length - 1);
            Lz4BlockCompressor linked = Lz4BlockCompressor.Create(level);
            byte[] first = new byte[Lz4BlockWriter.GetMaxCompressedLength(cut)];
            int n1 = linked.Compress(data.AsSpan(0, cut), 0, first);
            byte[] second = new byte[Lz4BlockWriter.GetMaxCompressedLength(data.Length - cut)];
            int n2 = linked.Compress(data, cut, second);
            byte[] decoded = new byte[data.Length];
            if (!Lz4BlockDecoder.TryDecode(first.AsSpan(0, n1), decoded, 0, out int w1) || w1 != cut
                || !Lz4BlockDecoder.TryDecode(second.AsSpan(0, n2), decoded, cut, out int w2) || w2 != data.Length - cut
                || !decoded.AsSpan().SequenceEqual(data))
            {
                throw new InvalidOperationException($"linked blocks differ (level {level}, cut {cut})");
            }
        }
    }
}
