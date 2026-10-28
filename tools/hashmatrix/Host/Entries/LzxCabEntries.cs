using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneArchive.Cab;
using RecursiveAI.OneArchive.Tests.Cab;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Tests.Lzx;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// LZX decoding and cabinet reading. The streams are built at run time by the deterministic LZX test compressor and cabinet builder
/// (shared with the tests), then decoded; the output must be the input on every platform, and a family of damaged copies must give
/// the same errors at the same offsets. The cabinet entries cover stored, MSZIP and LZX folders.
/// </summary>
internal static class LzxCabEntries
{
    private static readonly string[] s_inputs = ["empty", "one-byte", "text-small", "text-64k", "binary-1m", "runs-1m"];

    [ModuleInitializer]
    internal static void Register()
    {
        LzxTestEncoder.Kind[] kinds = [LzxTestEncoder.Kind.Verbatim, LzxTestEncoder.Kind.Aligned, LzxTestEncoder.Kind.Uncompressed, LzxTestEncoder.Kind.Aligned];
        foreach (int window in new[] { 15, 16, 18, 21 })
        {
            int w = window;
            Matrix.Add("lzx-decode", $"window{w}", (input, _) => Decode(input.ToArray(), w, 40_000, kinds, null), s_inputs);
        }

        Matrix.Add("lzx-decode", "window17-calls", (input, _) => Decode(WithCalls(input), 17, 33_333, kinds, 1_000_000), s_inputs);
        Matrix.Add("lzx-decode-corrupt", "window16", (input, _) => CorruptLzx(input.ToArray()), "text-small", "text-64k");

        foreach (CabTestBuilder.Method method in new[] { CabTestBuilder.Method.None, CabTestBuilder.Method.Mszip, CabTestBuilder.Method.Lzx })
        {
            CabTestBuilder.Method m = method;
            string name = m.ToString().ToLowerInvariant();
            Matrix.Add("cab-read", name, (input, _) => ReadCab(Build(input.ToArray(), m)), s_inputs);
            Matrix.Add("cab-read-corrupt", name, (input, _) => CorruptCab(Build(input.ToArray(), m)), "text-small", "text-64k");
        }
    }

    private static byte[] WithCalls(ReadOnlySpan<byte> input)
    {
        // Plant call opcodes with small operands so that the translation has something to do.
        byte[] data = input.ToArray();
        for (int i = 0; i + 5 < data.Length; i += 97)
        {
            data[i] = 0xE8;
            data[i + 3] = 0;
            data[i + 4] = 0;
        }

        return data;
    }

    private static byte[] Decode(byte[] data, int window, int blockSize, LzxTestEncoder.Kind[] kinds, uint? translation)
    {
        List<byte[]> frames = LzxTestEncoder.Encode(data, new LzxTestEncoder.Settings { WindowBits = window, BlockSize = blockSize, Kinds = kinds, TranslationSize = translation });
        return RecursiveAI.OneCompress.Lzx.Lzx.Decompress([.. frames.SelectMany(f => f)], window, data.Length);
    }

    private static byte[] CorruptLzx(byte[] data)
    {
        byte[] stream = [.. LzxTestEncoder.Encode(data, new LzxTestEncoder.Settings { WindowBits = 16, BlockSize = 20_000 }).SelectMany(f => f)];
        var report = new StringBuilder();
        int step = Math.Max(1, stream.Length / 101);
        for (int i = 0; i < stream.Length; i += step)
        {
            stream[i] ^= (byte)(1 << (i % 8));
            try
            {
                byte[] decoded = RecursiveAI.OneCompress.Lzx.Lzx.Decompress(stream, 16, data.Length);
                report.Append(CultureInfo.InvariantCulture, $"{i}:ok:{decoded.Length}:{decoded.StableHash()}\n");
            }
            catch (CompressionException e)
            {
                report.Append(CultureInfo.InvariantCulture, $"{i}:{e.GetType().Name}:{e.Message}\n");
            }

            stream[i] ^= (byte)(1 << (i % 8));
        }

        return Encoding.UTF8.GetBytes(report.ToString());
    }

    private static byte[] Build(byte[] data, CabTestBuilder.Method method)
    {
        int cut = data.Length / 3;
        var b = new CabTestBuilder();
        var f1 = new CabTestBuilder.FolderSpec { Method = method, WindowBits = 17 };
        f1.Files.Add(new CabTestBuilder.FileSpec { Name = "dir/first.bin", Data = data[..cut] });
        f1.Files.Add(new CabTestBuilder.FileSpec { Name = "second.bin", Data = data[cut..(2 * cut)] });
        var f2 = new CabTestBuilder.FolderSpec { Method = method, WindowBits = 15 };
        f2.Files.Add(new CabTestBuilder.FileSpec { Name = "third.bin", Data = data[(2 * cut)..] });
        b.Folders.Add(f1);
        b.Folders.Add(f2);
        return b.Build();
    }

    private static byte[] ReadCab(byte[] cab)
    {
        using var reader = new CabReader(new MemoryStream(cab));
        var output = new MemoryStream();
        while (reader.GetNextEntry() is { } e)
        {
            byte[] name = Encoding.UTF8.GetBytes(e.Name + "\n");
            output.Write(name);
            using Stream s = reader.OpenEntryStream();
            s.CopyTo(output);
        }

        return output.ToArray();
    }

    private static byte[] CorruptCab(byte[] cab)
    {
        var report = new StringBuilder();
        int step = Math.Max(1, cab.Length / 97);
        for (int i = 0; i < cab.Length; i += step)
        {
            cab[i] ^= (byte)(1 << (i % 8));
            try
            {
                byte[] result = ReadCab(cab);
                report.Append(CultureInfo.InvariantCulture, $"{i}:ok:{result.Length}:{result.StableHash()}\n");
            }
            catch (CompressionException e)
            {
                report.Append(CultureInfo.InvariantCulture, $"{i}:{e.GetType().Name}:{e.Message}\n");
            }

            cab[i] ^= (byte)(1 << (i % 8));
        }

        return Encoding.UTF8.GetBytes(report.ToString());
    }

    // A fixed FNV-1a, so that the report does not depend on the runtime's string or array hashing.
    private static uint StableHash(this byte[] data)
    {
        uint h = 2166136261;
        foreach (byte b in data)
        {
            h = (h ^ b) * 16777619;
        }

        return h;
    }
}
