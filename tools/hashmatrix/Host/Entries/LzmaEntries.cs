using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// LZMA, xz and lzip decoding of files written by the xz, lzip and 7-Zip tools (embedded in LzmaVectors.g.cs), and the branch
/// and delta filters over a fixed pseudo-random buffer. Every file is decoded in one shot, through a stream that reads in
/// odd-sized pieces, and through the span decoder with input and output cut at awkward places; a fourth entry flips bits and
/// records every outcome. The decoders are deterministic by construction; these entries prove the bytes and the errors do not
/// change with the platform, vector support or runtime.
/// </summary>
internal static class LzmaEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach ((string name, string data) in LzmaVectors.Files)
        {
            byte[] file = Convert.FromBase64String(data);
            Func<byte[], byte[]> oneShot = Pick(name, file);
            Matrix.Add("lzma-decode", $"{name}-oneshot", (_, _) => Guard(oneShot, file), "empty");
            Matrix.Add("lzma-decode", $"{name}-stream", (_, _) => StreamDecode(name, file), "empty");
            Matrix.Add("lzma-decode", $"{name}-pieces", (_, _) => PieceDecode(name, file), "empty");
            Matrix.Add("lzma-decode", $"{name}-corrupt", (_, _) => CorruptDecode(name, file, oneShot), "empty");
        }

        byte[] buffer = Pattern(20011);
        foreach (BranchArchitecture arch in Enum.GetValues<BranchArchitecture>())
        {
            Matrix.Add("lzma-filter", $"branch-{arch}", (_, _) => BranchBoth(arch, buffer), "empty");
        }

        foreach (int distance in new[] { 1, 2, 3, 7, 16, 255, 256 })
        {
            Matrix.Add("lzma-filter", $"delta-{distance}", (_, _) => DeltaBoth(distance, buffer), "empty");
        }
    }

    private static Func<byte[], byte[]> Pick(string name, byte[] file)
    {
        // The allowances keep the files that are deliberately odd (padding, trailing members) decodable.
        if (name.EndsWith(".xz", StringComparison.Ordinal))
        {
            return f => Xz.Decompress(f, XzOptions);
        }

        if (name.EndsWith(".lz", StringComparison.Ordinal))
        {
            return f => Lzip.Decompress(f, LzipOptions);
        }

        return f => Lzma.Decompress(f, LzmaOptions);
    }

    private static byte[] Guard(Func<byte[], byte[]> decode, byte[] file)
    {
        try
        {
            return decode(file);
        }
        catch (CompressionException e)
        {
            return System.Text.Encoding.UTF8.GetBytes(e.GetType().Name + ":" + e.Message);
        }
    }

    private static readonly XzDecompressionOptions XzOptions = new() { MaxOutputSize = 1 << 22 };
    private static readonly LzipDecompressionOptions LzipOptions = new() { MaxOutputSize = 1 << 22 };
    private static readonly LzmaDecompressionOptions LzmaOptions = new() { MaxOutputSize = 1 << 22 };

    private static Stream Open(string name, Stream source)
    {
        if (name.EndsWith(".xz", StringComparison.Ordinal))
        {
            return new XzStream(source, XzOptions);
        }

        if (name.EndsWith(".lz", StringComparison.Ordinal))
        {
            return new LzipStream(source, LzipOptions);
        }

        return new LzmaStream(source, LzmaOptions);
    }

    private static byte[] StreamDecode(string name, byte[] file)
    {
        try
        {
            using Stream stream = Open(name, new SlowStream(file));
            var output = new MemoryStream();
            byte[] buffer = new byte[3001];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
            }

            return output.ToArray();
        }
        catch (CompressionException e)
        {
            return System.Text.Encoding.UTF8.GetBytes(e.GetType().Name + ":" + e.Message);
        }
    }

    // The span decoder with input cut at 997 bytes and a 1013-byte output window.
    private static byte[] PieceDecode(string name, byte[] file)
    {
        try
        {
            if (name.EndsWith(".xz", StringComparison.Ordinal))
            {
                var d = new XzDecoder(XzOptions);
                return Pieces(file, d.Decompress, d.ThrowIfIncomplete);
            }

            if (name.EndsWith(".lz", StringComparison.Ordinal))
            {
                var d = new LzipDecoder(LzipOptions);
                return Pieces(file, d.Decompress, d.ThrowIfIncomplete);
            }

            var l = new LzmaDecoder(LzmaOptions);
            return Pieces(file, l.Decompress, l.ThrowIfIncomplete);
        }
        catch (CompressionException e)
        {
            return System.Text.Encoding.UTF8.GetBytes(e.GetType().Name + ":" + e.Message);
        }
    }

    private delegate OperationStatus Step(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written);

    private static byte[] Pieces(byte[] file, Step step, Action finish)
    {
        var output = new MemoryStream();
        byte[] buffer = new byte[1013];
        int pos = 0;
        while (true)
        {
            OperationStatus status = step(file.AsSpan(pos, Math.Min(997, file.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done || (pos >= file.Length && status != OperationStatus.DestinationTooSmall))
            {
                break;
            }
        }

        finish();
        return output.ToArray();
    }

    // Flips one bit at each of a fixed set of positions and records the outcome (error text or output length).
    private static byte[] CorruptDecode(string name, byte[] file, Func<byte[], byte[]> decode)
    {
        var report = new System.Text.StringBuilder();
        byte[] copy = file.ToArray();
        int step = Math.Max(1, copy.Length / 61);
        for (int i = 0; i < copy.Length; i += step)
        {
            copy[i] ^= (byte)(1 << (i % 8));
            try
            {
                byte[] decoded = decode(copy);
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

    // Bytes with plenty of call-like and branch-like words for every instruction set, from a fixed generator.
    private static byte[] Pattern(int length)
    {
        byte[] data = new byte[length];
        uint x = 0x9E3779B9;
        for (int i = 0; i < length; i++)
        {
            x = (x * 1664525) + 1013904223;
            data[i] = (byte)(x >> 24);
            switch ((x >> 8) & 31)
            {
                case 0: data[i] = 0xE8; break;
                case 1: data[i] = 0xEB; break;
                case 2: data[i] = 0x00; break;
                case 3: data[i] = 0xFF; break;
                case 4: data[i] = 0x94; break;
                case 5: data[i] = 0x97; break;
                case 6: data[i] = 0x48; break;
                case 7: data[i] = 0xEF; break;
            }
        }

        return data;
    }

    // Encodes in uneven chunks, then decodes in other chunks; the two results are concatenated so a broken direction shows.
    private static byte[] BranchBoth(BranchArchitecture arch, byte[] input)
    {
        byte[] encoded = Chunked(new BranchFilter(arch, 0x1000, encode: true), input, 4093);
        byte[] decoded = Chunked(new BranchFilter(arch, 0x1000, encode: false), encoded, 1021);
        return [.. encoded, .. decoded];
    }

    private static byte[] Chunked(BranchFilter filter, byte[] input, int chunk)
    {
        byte[] data = input.ToArray();
        int done = 0;
        int end = 0;
        // Convert reports how many leading bytes are final; the rest is presented again with more data behind it.
        while (end < data.Length)
        {
            end = Math.Min(data.Length, end + chunk);
            done += filter.Convert(data.AsSpan(done, end - done));
        }

        return data;
    }

    private static byte[] DeltaBoth(int distance, byte[] input)
    {
        byte[] encoded = input.ToArray();
        var enc = new DeltaFilter(distance, encode: true);
        for (int p = 0; p < encoded.Length; p += 997)
        {
            enc.Convert(encoded.AsSpan(p, Math.Min(997, encoded.Length - p)));
        }

        byte[] decoded = encoded.ToArray();
        var dec = new DeltaFilter(distance);
        for (int p = 0; p < decoded.Length; p += 1013)
        {
            dec.Convert(decoded.AsSpan(p, Math.Min(1013, decoded.Length - p)));
        }

        return [.. encoded, .. decoded];
    }

    private sealed class SlowStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1021));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1021)]);
    }
}
