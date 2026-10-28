using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Filters;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The 7z reader over archives assembled in memory from the matrix input (the library has no 7z writer): a Copy folder and a
/// Deflate folder, each with two files, an empty file and a directory, with times and Unix modes, once with a plain header
/// and once with a Deflate-encoded header. The output is the listing (names, sizes, times, modes, CRCs) and the SHA-256 of
/// every file read back, both through the reader and through <c>Archive</c>. Nothing here depends on the platform.
/// </summary>
internal static class SevenZipEntries
{
    private const long Mtime = 1_700_000_000;

    [ModuleInitializer]
    internal static void Register()
    {
        // Delta and x86 branch filters chained (decode order: BCJ, then Delta), packed by the library's own filter encoders.
        Matrix.Add("sevenzip-read", "filter-chain-plain-header", (input, _) => Read(input, 2, false));
        Matrix.Add("sevenzip-read", "filter-chain-encoded-header", (input, _) => Read(input, 2, true));
        foreach (bool deflate in new[] { false, true })
        {
            foreach (bool encodedHeader in new[] { false, true })
            {
                bool d = deflate;
                bool e = encodedHeader;
                Matrix.Add("sevenzip-read", $"{(d ? "deflate" : "copy")}-{(e ? "encoded" : "plain")}-header", (input, _) => Read(input, d ? 1 : 0, e));
            }
        }
    }

    private static byte[] Num(ulong v)
    {
        if (v < 0x80)
        {
            return [(byte)v];
        }

        // 7zFormat.txt: the first byte's leading one bits count the extra little-endian bytes.
        for (int extra = 1; extra <= 8; extra++)
        {
            if (extra == 8 || v < (1UL << (7 * (extra + 1))))
            {
                var o = new List<byte> { extra == 8 ? (byte)0xFF : (byte)((0xFF00 >> extra) | (int)(v >> (8 * extra))) };
                for (int i = 0; i < extra; i++)
                {
                    o.Add((byte)(v >> (8 * i)));
                }

                return [.. o];
            }
        }

        throw new InvalidOperationException();
    }

    private static byte[] Cat(params object[] parts)
    {
        var o = new List<byte>();
        foreach (object p in parts)
        {
            switch (p)
            {
                case int i: o.Add((byte)i); break;
                case byte[] a: o.AddRange(a); break;
                case ulong u: o.AddRange(Num(u)); break;
                case uint c: o.AddRange(BitConverter.GetBytes(c)); break;
                case long t: o.AddRange(BitConverter.GetBytes(t)); break;
                default: throw new ArgumentException(p.GetType().Name);
            }
        }

        return [.. o];
    }

    private static byte[] Prop(int id, byte[] body) => Cat(id, (ulong)body.Length, body);

    private static byte[] Build(ReadOnlySpan<byte> input, int mode, bool encodedHeader)
    {
        int cut = input.Length / 3;
        byte[] f1 = input[..cut].ToArray();
        byte[] f2 = input[cut..].ToArray();
        byte[] unpacked = input.ToArray();
        byte[] packed;
        byte[] folder;
        byte[] sizes;
        if (mode == 2)
        {
            packed = (byte[])unpacked.Clone();
            new DeltaFilter(4, encode: true).Convert(packed);
            var bcj = new BranchFilter(BranchArchitecture.X86, 0, encode: true);
            int done = bcj.Convert(packed);
            _ = done;
            // Two coders: 0 = Delta(4) (the result), 1 = BCJ x86, whose output feeds the Delta coder's input.
            folder = Cat(2UL, Cat(0x21, 0x03, 0x01, 0x03), Cat(0x01, 0x04), 0UL, 1UL);
            sizes = Cat((ulong)unpacked.Length, (ulong)unpacked.Length);
        }
        else
        {
            packed = mode == 1 ? Deflate.Compress(unpacked, new DeflateOptions { Level = 6 }) : unpacked;
            folder = Cat(1UL, mode == 1 ? Cat(0x03, 0x04, 0x01, 0x08) : Cat(0x01, 0x00));
            sizes = Cat((ulong)unpacked.Length);
        }

        byte[] streams = Cat(
            0x06, 0UL, 1UL, 0x09, (ulong)packed.Length, 0x00,
            0x07, 0x0B, 1UL, 0, folder, 0x0C, sizes, 0x00,
            0x08, 0x0D, 2UL, 0x09, (ulong)f1.Length, 0x0A, 1, Crc32.Compute(f1), Crc32.Compute(f2), 0x00,
            0x00);
        long ft = (Mtime * 10_000_000L) + 116444736000000000L;
        byte[] names = Cat(0, Encoding.Unicode.GetBytes("a.bin\0"), Encoding.Unicode.GetBytes("d\u00efr/b.bin\0"), Encoding.Unicode.GetBytes("empty\0"), Encoding.Unicode.GetBytes("dir\0"));
        byte[] header = Cat(
            0x01, 0x04, streams,
            0x05, 4UL,
            Prop(0x0E, [0b0011_0000]), Prop(0x0F, [0b1000_0000]),
            Prop(0x11, names),
            Prop(0x14, Cat(1, 0, ft, ft + 10_000_000L, ft + 20_000_000L, ft + 30_000_000L)),
            Prop(0x15, Cat(1, 0, 0x81A48020u, 0x81ED8020u, 0x81A48020u, 0x41ED8010u)),
            0x00, 0x00);
        byte[] tail = header;
        byte[] body = packed;
        if (encodedHeader)
        {
            byte[] stored = Deflate.Compress(header, new DeflateOptions { Level = 6 });
            tail = Cat(0x17, 0x06, (ulong)packed.Length, 1UL, 0x09, (ulong)stored.Length, 0x00,
                0x07, 0x0B, 1UL, 0, 1UL, Cat(0x03, 0x04, 0x01, 0x08), 0x0C, (ulong)header.Length, 0x0A, 1, Crc32.Compute(header), 0x00, 0x00);
            body = [.. packed, .. stored];
        }

        var next = new byte[20];
        BitConverter.GetBytes((ulong)body.Length).CopyTo(next, 0);
        BitConverter.GetBytes((ulong)tail.Length).CopyTo(next, 8);
        BitConverter.GetBytes(Crc32.Compute(tail)).CopyTo(next, 16);
        return Cat(new byte[] { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }, Crc32.Compute(next), next, body, tail);
    }

    private static byte[] Read(ReadOnlySpan<byte> input, int mode, bool encodedHeader)
    {
        byte[] archive = Build(input, mode, encodedHeader);
        var sb = new StringBuilder();
        using (var reader = new SevenZipReader(new MemoryStream(archive)))
        {
            sb.Append("encoded=").Append(reader.IsHeaderEncoded).Append('\n');
            while (reader.GetNextEntry() is { } e)
            {
                sb.Append(e.Name).Append(' ').Append(e.Kind).Append(' ').Append(e.Size).Append(' ')
                    .Append(e.ModifiedTime!.Value.UtcTicks).Append(' ').Append((int)e.Mode!.Value).Append(' ')
                    .Append(e.Crc32?.ToString("x8") ?? "-").Append(' ');
                using Stream s = reader.OpenEntryStream();
                sb.Append(Convert.ToHexString(SHA256.HashData(ReadAll(s)))).Append('\n');
            }
        }

        using (Archive a = Archive.Open(new MemoryStream(archive)))
        {
            foreach (ArchiveEntry e in a.Entries)
            {
                if (e.Kind == ArchiveEntryKind.File)
                {
                    using Stream s = a.OpenEntry(e);
                    sb.Append("archive ").Append(e.Name).Append(' ').Append(Convert.ToHexString(SHA256.HashData(ReadAll(s)))).Append('\n');
                }
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] ReadAll(Stream s)
    {
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}
