using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Deflate, zlib and gzip decoding of streams written by the reference implementations (embedded in DeflateVectors.g.cs).
/// The decoder is deterministic by construction; these entries prove the decoded bytes, and the errors, do not change with
/// the platform, vector support or runtime. Each stream is decoded in one shot (straight into the caller's buffer), through a
/// stream that reads in odd-sized pieces, and through the span decoder with input and output cut at awkward places.
/// </summary>
internal static class DeflateEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach ((string name, string format, string data, string? dictionary) in DeflateVectors.Streams)
        {
            byte[] compressed = Convert.FromBase64String(data);
            DeflateFormat f = format switch { "raw" => DeflateFormat.Raw, "zlib" => DeflateFormat.ZLib, _ => DeflateFormat.GZip };
            DeflateDecompressionOptions options = Options(dictionary);
            Matrix.Add("deflate-decode", $"{name}-oneshot", (_, _) => OneShot(f, compressed, options), "empty");
            Matrix.Add("deflate-decode", $"{name}-stream", (_, _) => StreamDecode(f, compressed, options), "empty");
            Matrix.Add("deflate-decode", $"{name}-pieces", (_, _) => PieceDecode(f, compressed, options), "empty");

            // Corrupt input must fail the same way everywhere: the manifest records the error's offset and text.
            Matrix.Add("deflate-decode", $"{name}-corrupt", (_, _) => CorruptDecode(f, compressed, options), "empty");
        }
    }

    private static DeflateDecompressionOptions Options(string? dictionary)
    {
        byte[]? d = null;
        if (dictionary is not null)
        {
            foreach ((string name, string data) in DeflateVectors.Dictionaries)
            {
                if (name == dictionary)
                {
                    d = Convert.FromBase64String(data);
                }
            }
        }

        return new DeflateDecompressionOptions { Dictionary = d };
    }

    private static byte[] OneShot(DeflateFormat format, byte[] compressed, DeflateDecompressionOptions options) => format switch
    {
        DeflateFormat.Raw => Deflate.Decompress(compressed, options),
        DeflateFormat.ZLib => ZLib.Decompress(compressed, options),
        _ => GZip.Decompress(compressed, options),
    };

    private static byte[] StreamDecode(DeflateFormat format, byte[] compressed, DeflateDecompressionOptions options)
    {
        using var stream = new DeflateCodecStream(new SlowStream(compressed), format, options);
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
    private static byte[] PieceDecode(DeflateFormat format, byte[] compressed, DeflateDecompressionOptions options)
    {
        var decoder = new DeflateDecoder(format, options);
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
    private static byte[] CorruptDecode(DeflateFormat format, byte[] compressed, DeflateDecompressionOptions options)
    {
        var report = new System.Text.StringBuilder();
        byte[] copy = compressed.ToArray();
        int step = Math.Max(1, copy.Length / 61);
        for (int i = 0; i < copy.Length; i += step)
        {
            copy[i] ^= (byte)(1 << (i % 8));
            try
            {
                byte[] decoded = OneShot(format, copy, options);
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
