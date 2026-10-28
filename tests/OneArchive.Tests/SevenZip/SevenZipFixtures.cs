using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneArchive.Tests.SevenZip;

/// <summary>The expected content of the archives in testdata/7z/vectors (see testdata/7z/generate.py, which writes it) and a hand-assembly kit for 7z headers.</summary>
internal static class SevenZipFixtures
{
    public const long Mtime = 1700000000;

    public static string Vector(string name, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "7z", "vectors", name + ".7z"));

    public static byte[] Lcg(int seed, int n)
    {
        long x = seed;
        var o = new byte[n];
        for (int i = 0; i < n; i++)
        {
            x = (x * 1103515245 + 12345) & 0x7FFFFFFF;
            o[i] = (byte)((x >> 16) & 0xFF);
        }

        return o;
    }

    public static byte[] Repeat(string s, int times)
    {
        byte[] one = Encoding.UTF8.GetBytes(s);
        var o = new byte[one.Length * times];
        for (int i = 0; i < times; i++)
        {
            one.CopyTo(o, i * one.Length);
        }

        return o;
    }

    public static Dictionary<string, byte[]> Files()
    {
        var d = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["a.txt"] = "hello 7z\n"u8.ToArray(),
            ["empty.txt"] = [],
            ["dir/b.bin"] = Lcg(7, 20000),
            ["dir/sub/c.txt"] = Repeat("the quick brown fox jumps over the lazy dog\n", 800),
            ["\u00fcn\u00ef/\u65e5\u672c\u8a9e.txt"] = Encoding.UTF8.GetBytes("unicode name \u2603\n"),
            ["z.txt"] = "last file\n"u8.ToArray(),
        };
        for (int i = 0; i < 120; i++)
        {
            d[$"many/f{i:D4}.txt"] = Repeat($"file number {i}\n", i % 7 + 1);
        }

        return d;
    }

    public static readonly string[] Directories = ["dir", "dir/emptysub", "dir/sub", "emptydir", "many", "\u00fcn\u00ef"];

    // ---- header assembly ----

    public static byte[] Num(ulong v)
    {
        if (v < 0x80)
        {
            return [(byte)v];
        }

        var o = new List<byte>();
        for (int extra = 1; extra <= 8; extra++)
        {
            if (extra == 8 || v < (1UL << (7 * (extra + 1))))
            {
                byte mask = (byte)(0xFF00 >> extra);
                if (extra == 8)
                {
                    o.Add(0xFF);
                }
                else
                {
                    o.Add((byte)(mask | (byte)(v >> (8 * extra))));
                }

                for (int i = 0; i < extra; i++)
                {
                    o.Add((byte)(v >> (8 * i)));
                }

                return [.. o];
            }
        }

        throw new InvalidOperationException();
    }

    public static byte[] Cat(params object[] parts)
    {
        var o = new List<byte>();
        foreach (object p in parts)
        {
            switch (p)
            {
                case byte b: o.Add(b); break;
                case int i: o.Add((byte)i); break;
                case byte[] a: o.AddRange(a); break;
                case ulong u: o.AddRange(Num(u)); break;
                case uint c: o.AddRange(BitConverter.GetBytes(c)); break;
                default: throw new ArgumentException(p.GetType().Name);
            }
        }

        return [.. o];
    }

    public static byte[] Names(params string[] names)
    {
        var body = new List<byte> { 0 };
        foreach (string n in names)
        {
            body.AddRange(Encoding.Unicode.GetBytes(n));
            body.AddRange([0, 0]);
        }

        return Cat(0x11, (ulong)body.Count, body.ToArray());
    }

    /// <summary>One Copy folder over the packed bytes with the given file sizes; a CRC for each file.</summary>
    public static byte[] CopyStreams(byte[] packed, params byte[][] fileData)
    {
        var o = new List<byte>();
        o.AddRange(Cat(0x06, 0UL, 1UL, 0x09, (ulong)packed.Length, 0x00));
        o.AddRange(Cat(0x07, 0x0B, 1UL, 0, 1UL, 0x01, 0x00, 0x0C, (ulong)packed.Length, 0x00));
        o.AddRange(Cat(0x08, 0x0D, (ulong)fileData.Length));
        if (fileData.Length > 1)
        {
            o.Add(0x09);
            for (int i = 0; i < fileData.Length - 1; i++)
            {
                o.AddRange(Num((ulong)fileData[i].Length));
            }
        }

        o.AddRange(Cat(0x0A, 1));
        foreach (byte[] f in fileData)
        {
            o.AddRange(BitConverter.GetBytes(Crc32.Compute(f)));
        }

        o.Add(0x00);
        o.Add(0x00);
        return [.. o];
    }

    public static byte[] Header(byte[] streams, int numFiles, params byte[][] fileProps)
    {
        var o = new List<byte> { 0x01 };
        if (streams.Length > 0)
        {
            o.Add(0x04);
            o.AddRange(streams);
        }

        o.AddRange(Cat(0x05, (ulong)numFiles));
        foreach (byte[] p in fileProps)
        {
            o.AddRange(p);
        }

        o.AddRange([0x00, 0x00]);
        return [.. o];
    }

    /// <summary>The signature header, the packed bytes and the header, with all CRCs right.</summary>
    public static byte[] Build(byte[] packed, byte[] header)
    {
        var next = new byte[20];
        BitConverter.GetBytes((ulong)packed.Length).CopyTo(next, 0);
        BitConverter.GetBytes((ulong)header.Length).CopyTo(next, 8);
        BitConverter.GetBytes(Crc32.Compute(header)).CopyTo(next, 16);
        var o = new List<byte> { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4 };
        o.AddRange(BitConverter.GetBytes(Crc32.Compute(next)));
        o.AddRange(next);
        o.AddRange(packed);
        o.AddRange(header);
        return [.. o];
    }

    public static byte[] Bits(params bool[] bits)
    {
        var o = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                o[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return o;
    }

    public static byte[] Prop(byte id, byte[] body) => Cat(id, (ulong)body.Length, body);
}
