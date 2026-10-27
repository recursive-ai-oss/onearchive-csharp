using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// LZ4 decoding of frames written by the reference tool (embedded in Lz4Vectors.g.cs). The decoder is deterministic by
/// construction; these entries prove the decoded bytes, and the errors, do not change with the platform, vector support
/// or runtime. Each frame is decoded in one shot (straight into the caller's buffer), through a stream that reads and
/// writes in odd-sized pieces (the window and block buffering paths) and block by block through the raw layer.
/// </summary>
internal static class Lz4Entries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach ((string name, string frame, string? dictionary) in Lz4Vectors.Frames)
        {
            byte[] compressed = Convert.FromBase64String(frame);
            Lz4DecompressionOptions options = Options(dictionary);
            Matrix.Add("lz4-decode", $"{name}-oneshot", (_, _) => Lz4.Decompress(compressed, options), "empty");
            Matrix.Add("lz4-decode", $"{name}-stream", (_, _) => StreamDecode(compressed, options), "empty");
            Matrix.Add("lz4-decode", $"{name}-pieces", (_, _) => PieceDecode(compressed, options), "empty");

            // Corrupt input must fail the same way everywhere: the manifest records the error's offset and text.
            Matrix.Add("lz4-decode", $"{name}-corrupt", (_, _) => CorruptDecode(compressed, options), "empty");
        }
    }

    private static Lz4DecompressionOptions Options(string? dictionary)
    {
        Lz4Dictionary? d = null;
        if (dictionary is not null)
        {
            foreach ((string name, string data) in Lz4Vectors.Dictionaries)
            {
                if (name == dictionary)
                {
                    d = new Lz4Dictionary(Convert.FromBase64String(data));
                }
            }
        }

        return new Lz4DecompressionOptions { Dictionary = d };
    }

    private static byte[] StreamDecode(byte[] compressed, Lz4DecompressionOptions options)
    {
        using var stream = new Lz4Stream(new SlowStream(compressed), options);
        var output = new MemoryStream();
        byte[] buffer = new byte[3001];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }

    // The streaming decoder with input cut at 997 bytes and a 1013-byte output window.
    private static byte[] PieceDecode(byte[] compressed, Lz4DecompressionOptions options)
    {
        var decoder = new Lz4Decoder(options);
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

    // Flips one bit at each of a fixed set of positions and records the outcome (error text or output length).
    private static byte[] CorruptDecode(byte[] compressed, Lz4DecompressionOptions options)
    {
        var report = new System.Text.StringBuilder();
        byte[] copy = compressed.ToArray();
        int step = Math.Max(1, copy.Length / 61);
        for (int i = 0; i < copy.Length; i += step)
        {
            copy[i] ^= (byte)(1 << (i % 8));
            try
            {
                byte[] decoded = Lz4.Decompress(copy, options);
                report.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i}:ok:{decoded.Length}\n");
            }
            catch (CompressionException e)
            {
                report.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i}:{e.GetType().Name}:{e.Message}\n");
            }

            copy[i] ^= (byte)(1 << (i % 8));
        }

        return System.Text.Encoding.UTF8.GetBytes(report.ToString());
    }

    // A stream that hands out at most 1021 bytes per read, so frames are split at awkward places.
    private sealed class SlowStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1021));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1021)]);
    }
}
