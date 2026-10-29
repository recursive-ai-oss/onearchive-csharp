using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Iso;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The ISO 9660 writer over a fixed entry mix in several option sets (the standard input is the content of the big file, so sizes
/// run from 0 to 4 MiB), and the reader's view as text: of the writer's own output and of an image made by xorriso. Nothing here
/// depends on the platform: the writer reads no clock, every field is fixed width and every ordering is by name.
/// </summary>
internal static class IsoEntries
{
    private static readonly DateTimeOffset s_base = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [ModuleInitializer]
    internal static void Register()
    {
        Matrix.Add("iso-write", "default-mix", (input, _) => Write(input, new IsoWriterOptions()));
        Matrix.Add("iso-write", "plain-level1-mix", (input, _) => Write(input, new IsoWriterOptions { Joliet = false, RockRidge = false, Level = 1 }));
        Matrix.Add("iso-write", "plain-level2-mix", (input, _) => Write(input, new IsoWriterOptions { Joliet = false, RockRidge = false, Level = 2 }));
        Matrix.Add("iso-write", "joliet-mix", (input, _) => Write(input, new IsoWriterOptions { RockRidge = false }));
        Matrix.Add("iso-write", "rockridge-mix", (input, _) => Write(input, new IsoWriterOptions { Joliet = false }));
        Matrix.Add("iso-write", "dated-mix", (input, _) => Write(input, new IsoWriterOptions { VolumeTime = s_base, VolumeId = "MATRIX", DefaultTime = s_base }));
        Matrix.Add("iso-write", "default-mix-spooled", (input, _) => Write(input, new IsoWriterOptions { MaxMemoryBuffer = 64 }));
        Matrix.Add("iso-read", "default-mix-summary", (input, _) => Summary(Write(input, new IsoWriterOptions()), IsoTree.Auto, true));
        Matrix.Add("iso-read", "joliet-mix-summary", (input, _) => Summary(Write(input, new IsoWriterOptions()), IsoTree.Joliet, false));
        Matrix.Add("iso-read", "xorriso-summary", (_, _) => Summary(XorrisoImage(), IsoTree.Auto, true), "empty");
        Matrix.Add("iso-read", "xorriso-primary-summary", (_, _) => Summary(XorrisoImage(), IsoTree.Primary, false), "empty");
        Matrix.Add("iso-read", "xorriso-joliet-summary", (_, _) => Summary(XorrisoImage(), IsoTree.Joliet, false), "empty");
    }

    private static byte[] XorrisoImage() => [.. new byte[16 * 2048], .. Convert.FromBase64String(IsoVectors.RockRidgeJoliet)];

    private static byte[] Write(ReadOnlySpan<byte> content, IsoWriterOptions options)
    {
        byte[] data = content.ToArray();
        var ms = new MemoryStream();
        using (var w = new IsoWriter(ms, options, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "root/data.bin", Size = data.Length, Mode = (UnixFileMode)0b110_100_100, ModifiedTime = s_base.AddSeconds(1), Uid = 1000, Gid = 1000 }, data);
            w.WriteEntry(new ArchiveEntry { Name = "root/empty", ModifiedTime = s_base.AddSeconds(2) }, (Stream?)null);
            if (options.RockRidge)
            {
                // Symbolic links need Rock Ridge; the writer refuses them otherwise.
                WriteLinks(w);
            }

            w.WriteEntry(new ArchiveEntry { Name = "root/" + new string('n', 200) + ".txt", Size = 3, ModifiedTime = s_base.AddSeconds(4) }, "abc"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "root/\u00fcn\u00ef-\u65e5\u672c.txt", Size = 2, ModifiedTime = s_base.AddSeconds(9) }, "hi"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "root/dup.txt", Size = 1 }, "1"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "root/DUP.TXT", Size = 1 }, "2"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "root/sub/deeper/f", Size = 1, Mode = (UnixFileMode)0b111_101_101 }, "x"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "root/dir", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)0b111_000_000, ModifiedTime = s_base }, (Stream?)null);
        }

        return ms.ToArray();
    }

    private static void WriteLinks(IsoWriter w)
    {
        w.WriteEntry(new ArchiveEntry { Name = "root/link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "data.bin", ModifiedTime = s_base.AddSeconds(3) }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/long-link", Kind = ArchiveEntryKind.Symlink, LinkTarget = string.Join('/', Enumerable.Range(0, 40).Select(static i => "segment-" + i)) }, (Stream?)null);
    }

    private static byte[] Summary(byte[] image, IsoTree tree, bool rockRidge)
    {
        using var reader = new IsoReader(new MemoryStream(image), new IsoReaderOptions { Tree = tree, UseRockRidge = rockRidge });
        var sb = new StringBuilder();
        sb.Append(reader.Volume.VolumeId).Append('|').Append(reader.Volume.HasRockRidge).Append('|').Append(reader.Volume.HasJoliet).Append('\n');
        while (reader.GetNextEntry() is { } e)
        {
            sb.Append(e.Name).Append('|').Append(e.IsoPath).Append('|').Append(e.Kind).Append('|').Append(e.Size).Append('|').Append((int?)e.Mode).Append('|').Append(e.LinkTarget)
                .Append('|').Append(e.ModifiedTime?.ToUnixTimeSeconds()).Append('|').Append(e.ExtentLocation).Append('|');
            if (e.Kind == ArchiveEntryKind.File)
            {
                using Stream s = reader.OpenEntryStream();
                var ms = new MemoryStream();
                s.CopyTo(ms);
                sb.Append(Convert.ToHexString(SHA256.HashData(ms.ToArray())));
            }

            sb.Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
