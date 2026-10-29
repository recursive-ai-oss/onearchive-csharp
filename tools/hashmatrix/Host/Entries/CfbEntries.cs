using System.Runtime.CompilerServices;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cfb;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The compound file writer over fixed trees (the standard input is the content of one stream, next to streams of fixed sizes around
/// the mini stream cutoff), in versions 3 and 4, and a tree of 300 siblings that exercises the red-black links. Times, CLSIDs and
/// state bits are fixed values; the writer never reads a clock, so nothing here depends on the platform.
/// </summary>
internal static class CfbEntries
{
    private static readonly DateTimeOffset s_base = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [ModuleInitializer]
    internal static void Register()
    {
        foreach (int version in new[] { 3, 4 })
        {
            int v = version;
            Matrix.Add("cfb-write", $"v{v}-mix", (input, _) => WriteMix(input, v));
            Matrix.Add("cfb-write", $"v{v}-siblings", (_, _) => WriteSiblings(v), "empty");
        }
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] b = new byte[length];
        uint x = (uint)seed * 2654435761u + 1;
        for (int i = 0; i < b.Length; i++)
        {
            x = (x * 1103515245) + 12345;
            b[i] = (byte)(x >> 16);
        }

        return b;
    }

    private static byte[] WriteMix(ReadOnlySpan<byte> content, int version)
    {
        byte[] data = content.ToArray();
        var ms = new MemoryStream();
        using (var w = new CompoundFileWriter(ms, new CompoundFileWriterOptions { MajorVersion = version }, leaveOpen: true))
        {
            w.WriteEntry(new CfbEntry { Name = string.Empty, RawName = string.Empty, Kind = ArchiveEntryKind.Directory, ClassId = new Guid("0b0d0200-0000-0000-c000-000000000046"), StateBits = 5, ModifiedFileTime = 132_000_000_000_000_000UL });
            w.WriteEntry(new ArchiveEntry { Name = "Data", Size = data.Length, ModifiedTime = s_base }, data);
            w.WriteEntry(new ArchiveEntry { Name = "Storage", Kind = ArchiveEntryKind.Directory, ModifiedTime = s_base.AddSeconds(1), ChangeTime = s_base });
            foreach (int n in new[] { 0, 1, 63, 64, 65, 511, 512, 513, 4095, 4096, 4097, 9000 })
            {
                w.WriteEntry(new ArchiveEntry { Name = $"Storage/s{n}", Size = n }, Pattern(n, n));
            }

            w.WriteEntry(new ArchiveEntry { Name = "Storage/Deep/er/est", Size = 3 }, new byte[] { 1, 2, 3 });
        }

        return ms.ToArray();
    }

    private static byte[] WriteSiblings(int version)
    {
        var ms = new MemoryStream();
        using (var w = new CompoundFileWriter(ms, new CompoundFileWriterOptions { MajorVersion = version }, leaveOpen: true))
        {
            for (int i = 0; i < 300; i++)
            {
                int k = (i * 7919) % 300;
                w.WriteEntry(new ArchiveEntry { Name = (k % 2 == 0 ? "e" : "E") + k.ToString(System.Globalization.CultureInfo.InvariantCulture), Size = k % 90 }, Pattern(k % 90, k));
            }
        }

        return ms.ToArray();
    }
}
