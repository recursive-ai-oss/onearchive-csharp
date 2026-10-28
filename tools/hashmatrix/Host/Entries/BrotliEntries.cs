using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Brotli;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// brotli decoding of streams written by the brotli tool (embedded in BrotliVectors.g.cs). The decoder is deterministic by
/// construction; these entries prove the decoded bytes, and the errors, do not change with the platform, vector support or
/// runtime. Each stream is decoded in one shot, through a stream that reads in odd-sized pieces, and through the span decoder
/// with input and output cut at awkward places; a fourth entry flips bits and records every outcome.
/// </summary>
internal static class BrotliEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach ((string name, string data) in BrotliVectors.Streams)
        {
            byte[] compressed = Convert.FromBase64String(data);
            Matrix.Add("brotli-decode", $"{name}-oneshot", (_, _) => BR.Decompress(compressed), "empty");
            Matrix.Add("brotli-decode", $"{name}-stream", (_, _) => StreamDecode(compressed), "empty");
            Matrix.Add("brotli-decode", $"{name}-pieces", (_, _) => PieceDecode(compressed), "empty");
            Matrix.Add("brotli-decode", $"{name}-corrupt", (_, _) => CorruptDecode(compressed), "empty");
        }
    }

    private static byte[] StreamDecode(byte[] compressed)
    {
        using var stream = new BrotliStream(new SlowStream(compressed));
        var output = new MemoryStream();
        byte[] buffer = new byte[3001];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }

    // The span decoder with input cut at 997 bytes and a 1013-byte output window.
    private static byte[] PieceDecode(byte[] compressed)
    {
        var decoder = new BrotliDecoder();
        var output = new MemoryStream();
        byte[] buffer = new byte[1013];
        int pos = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(compressed.AsSpan(pos, Math.Min(997, compressed.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (pos >= compressed.Length && status != OperationStatus.DestinationTooSmall && !(status == OperationStatus.Done && pos < compressed.Length))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }

    // Flips one bit at each of a fixed set of positions and records the outcome (error text or output length). The output
    // limit keeps a flipped length field from asking for much.
    private static byte[] CorruptDecode(byte[] compressed)
    {
        var report = new System.Text.StringBuilder();
        var options = new BrotliDecompressionOptions { MaxOutputSize = 1 << 22 };
        byte[] copy = compressed.ToArray();
        int step = Math.Max(1, copy.Length / 61);
        for (int i = 0; i < copy.Length; i += step)
        {
            copy[i] ^= (byte)(1 << (i % 8));
            try
            {
                byte[] decoded = BR.Decompress(copy, options);
                report.Append(CultureInfo.InvariantCulture, $"{i}:ok:{decoded.Length}\n");
            }
            catch (CompressionException e)
            {
                report.Append(CultureInfo.InvariantCulture, $"{i}:{e.GetType().Name}:{e.Message}\n");
            }

            copy[i] ^= (byte)(1 << (i % 8));
        }

        return System.Text.Encoding.UTF8.GetBytes(report.ToString());
    }

    // A stream that hands out at most 1021 bytes per read, so streams are split at awkward places.
    private sealed class SlowStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1021));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1021)]);
    }
}
