using System.Buffers;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Round-trip fuzz target for the Deflate encoder. The input chooses the level, wrapper, strategy, dictionary, how the data is
/// cut into writes, which flushes fall between them and how small the output buffer is; the rest of the input is a program that
/// expands into compressible data. Any output our decoder does not turn back into the input, or that differs between the one-call
/// path and the streaming path when no flush is involved, is a finding.
/// </summary>
internal static class DeflateEncoderTargets
{
    private static readonly byte[] s_dictionary = MakeDictionary();

    public static FuzzTarget RoundTrip { get; } = new("deflate-encode-roundtrip", static data => Run(data), static _ => false);

    private static byte[] MakeDictionary()
    {
        byte[] data = new byte[5000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)("tick entity pos health "[i % 23] ^ (i / 997));
        }

        return data;
    }

    // Turns fuzzer bytes into compressible data: bytes below 0x80 start a literal run taken from the input, the rest are copies.
    internal static byte[] Expand(ReadOnlySpan<byte> program, int limit)
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

                int length = 3 + (op & 0x1F) + ((op & 0x60) == 0x60 ? program[i] * 8 : 0);
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

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 6)
        {
            return;
        }

        int level = input[0] % 13;
        int flags = input[1];
        DeflateFormat format = (DeflateFormat)((flags & 3) % 3);
        var options = new DeflateOptions
        {
            Level = level,
            Strategy = (DeflateStrategy)((flags >> 2) & 3),
            Dictionary = (flags & 16) != 0 && format != DeflateFormat.GZip ? s_dictionary : default,
            GZipFileName = (flags & 32) != 0 ? "f" : null,
            GZipHeaderCrc = (flags & 64) != 0,
        };
        int piece = 1 + (input[2] * 509 % 70_000);
        int outSize = 1 + (input[3] * 31 % 3000);
        int flushPlan = input[4];
        byte[] data = Expand(input[5..], level >= 10 ? 80_000 : 400_000);
        var decodeOptions = new DeflateDecompressionOptions { Dictionary = options.Dictionary };

        byte[] whole = Encode(format, options, data);
        Check(format, whole, data, decodeOptions, "one call");

        // Streaming: odd write sizes, an odd-sized output buffer, and flushes chosen by the input.
        var encoder = new DeflateEncoder(format, options);
        var compressed = new List<byte>();
        byte[] buffer = new byte[outSize];
        int offset = 0;
        int writes = 0;
        bool flushed = false;
        while (true)
        {
            int take = Math.Min(piece, data.Length - offset);
            bool last = offset + take == data.Length;
            DeflateFlush mode = last ? DeflateFlush.Finish
                : (flushPlan >> (writes % 4 * 2) & 3) == 3 ? (writes % 2 == 0 ? DeflateFlush.Sync : DeflateFlush.Full) : DeflateFlush.None;
            flushed |= mode is DeflateFlush.Sync or DeflateFlush.Full;
            ReadOnlySpan<byte> source = data.AsSpan(offset, take);
            while (true)
            {
                OperationStatus status = encoder.Compress(source, buffer, out int consumed, out int written, mode);
                compressed.AddRange(buffer.AsSpan(0, written).ToArray());
                source = source[consumed..];
                if (status == OperationStatus.Done || (status == OperationStatus.NeedMoreData && source.IsEmpty))
                {
                    break;
                }
            }

            offset += take;
            writes++;
            if (last)
            {
                break;
            }
        }

        byte[] streamed = [.. compressed];
        Check(format, streamed, data, decodeOptions, "streaming");
        if (!flushed && !whole.AsSpan().SequenceEqual(streamed))
        {
            throw new InvalidOperationException($"streaming output differs from the one-call output (level {level}, {format})");
        }
    }

    private static byte[] Encode(DeflateFormat format, DeflateOptions options, byte[] data) => format switch
    {
        DeflateFormat.Raw => Deflate.Compress(data, options),
        DeflateFormat.ZLib => ZLib.Compress(data, options),
        _ => GZip.Compress(data, options),
    };

    private static void Check(DeflateFormat format, byte[] compressed, byte[] expected, DeflateDecompressionOptions options, string how)
    {
        byte[] back = format switch
        {
            DeflateFormat.Raw => Deflate.Decompress(compressed, options),
            DeflateFormat.ZLib => ZLib.Decompress(compressed, options),
            _ => GZip.Decompress(compressed, options),
        };
        if (!back.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException($"{how}: round trip differs ({format}, {expected.Length} bytes)");
        }
    }
}
