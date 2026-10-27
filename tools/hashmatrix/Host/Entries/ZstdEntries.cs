using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Zstandard decoding of frames written by the reference tool (embedded in ZstdVectors.g.cs). The decoder is
/// deterministic by construction; these entries prove the decoded bytes, and the errors, do not change with the
/// platform, vector support or runtime. Each frame is decoded in one shot (straight into the caller's buffer) and
/// through a stream that reads and writes in odd-sized pieces (the sliding window and block buffering paths).
/// </summary>
internal static class ZstdEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach ((string name, string frame, string? dictionary) in ZstdVectors.Frames)
        {
            byte[] compressed = Convert.FromBase64String(frame);
            ZstdDecompressionOptions options = Options(dictionary);
            Matrix.Add("zstd-decode", $"{name}-oneshot", (_, _) => Zstd.Decompress(compressed, options), "empty");
            Matrix.Add("zstd-decode", $"{name}-stream", (_, _) => StreamDecode(compressed, options), "empty");
        }

        // Corrupt input must fail the same way everywhere: the manifest records the error's offset and text.
        foreach ((string name, string frame, string? dictionary) in ZstdVectors.Frames)
        {
            byte[] compressed = Convert.FromBase64String(frame);
            ZstdDecompressionOptions options = Options(dictionary);
            Matrix.Add("zstd-decode", $"{name}-corrupt", (_, _) => CorruptDecode(compressed, options), "empty");
        }
    }

    private static ZstdDecompressionOptions Options(string? dictionary)
    {
        ZstdDictionary? d = null;
        if (dictionary is not null)
        {
            foreach ((string name, string data) in ZstdVectors.Dictionaries)
            {
                if (name == dictionary)
                {
                    d = ZstdDictionary.Parse(Convert.FromBase64String(data));
                }
            }
        }

        return new ZstdDecompressionOptions { Dictionary = d };
    }

    private static byte[] StreamDecode(byte[] compressed, ZstdDecompressionOptions options)
    {
        using var stream = new ZstdStream(new SlowStream(compressed), options);
        var output = new MemoryStream();
        byte[] buffer = new byte[3001];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }

    // Flips one bit at each of a fixed set of positions and records the outcome (error text or output length).
    private static byte[] CorruptDecode(byte[] compressed, ZstdDecompressionOptions options)
    {
        var report = new System.Text.StringBuilder();
        byte[] copy = compressed.ToArray();
        int step = Math.Max(1, copy.Length / 61);
        for (int i = 0; i < copy.Length; i += step)
        {
            copy[i] ^= (byte)(1 << (i % 8));
            try
            {
                byte[] decoded = Zstd.Decompress(copy, options);
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
