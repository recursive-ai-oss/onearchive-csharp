using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Tar;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The tar writer over several entry mixes (ustar and pax, plain and with long names, big numbers, sub-second times,
/// extras), the same mixes behind <c>.tar.zst</c> and <c>.tar.lz4</c> through the Archive layer, and the reader's
/// view of the pax mix as text. The standard input is the content of the files, so sizes run from 0 to 4 MiB.
/// Nothing here depends on the platform: fields are fixed width and pax records are sorted.
/// </summary>
internal static class TarEntries
{
    private static readonly DateTimeOffset s_base = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [ModuleInitializer]
    internal static void Register()
    {
        Matrix.Add("tar-write", "ustar-mix", (input, _) => Write(input, TarFormat.Ustar, longNames: false));
        Matrix.Add("tar-write", "pax-mix", (input, _) => Write(input, TarFormat.Pax, longNames: false));
        Matrix.Add("tar-write", "pax-long-mix", (input, _) => Write(input, TarFormat.Pax, longNames: true));
        Matrix.Add("tar-write", "pax-mix-512", (input, _) => Write(input, TarFormat.Pax, longNames: true, recordSize: 512));
        Matrix.Add("tar-write", "pax-big-fields", (input, _) => BigFields(input));
        Matrix.Add("tar-write", "normalized-tree", (input, _) => Normalized(input));
        Matrix.Add("tar-zst", "pax-mix", (input, _) => Compressed(input, ArchiveCompression.Zstd, null));
        Matrix.Add("tar-zst", "pax-mix-level9", (input, _) => Compressed(input, ArchiveCompression.Zstd, 9));
        Matrix.Add("tar-lz4", "pax-mix", (input, _) => Compressed(input, ArchiveCompression.Lz4, null));
        Matrix.Add("tar-read", "pax-mix-summary", (input, _) => Summary(input));
    }

    private static byte[] Write(ReadOnlySpan<byte> content, TarFormat format, bool longNames, int recordSize = 10240)
    {
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, new TarWriterOptions { Format = format, RecordSize = recordSize }, leaveOpen: true))
        {
            Mix(w, content.ToArray(), longNames, format);
        }

        return ms.ToArray();
    }

    private static void Mix(TarWriter w, byte[] content, bool longNames, TarFormat format)
    {
        bool pax = format == TarFormat.Pax;
        w.WriteEntry(new ArchiveEntry { Name = "root", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)0b111_101_101, ModifiedTime = s_base, UserName = "root", GroupName = "root" }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/data.bin", Size = content.Length, Mode = (UnixFileMode)0b110_100_100, ModifiedTime = s_base.AddSeconds(1), Uid = 1000, Gid = 1000, UserName = "user", GroupName = "users" }, content);
        w.WriteEntry(new ArchiveEntry { Name = "root/empty", Size = 0, ModifiedTime = s_base.AddSeconds(2) }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "data.bin", ModifiedTime = s_base.AddSeconds(3) }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/hard", Kind = ArchiveEntryKind.HardLink, LinkTarget = "root/data.bin", ModifiedTime = s_base.AddSeconds(3) }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/tty", Kind = ArchiveEntryKind.CharacterDevice, DeviceMajor = 5, DeviceMinor = 0, Mode = (UnixFileMode)0b110_110_110 }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/fifo", Kind = ArchiveEntryKind.Fifo }, (Stream?)null);
        string deep = string.Join('/', Enumerable.Repeat("component-of-a-long-path", 4)) + "/leaf-" + new string('x', 20);
        w.WriteEntry(new ArchiveEntry { Name = deep, Size = 5, ModifiedTime = s_base }, "short"u8.ToArray());
        if (longNames)
        {
            w.WriteEntry(new ArchiveEntry { Name = string.Join('/', Enumerable.Repeat(new string('d', 60), 5)) + "/file", Size = 3, ModifiedTime = s_base }, "abc"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "root/long-link", Kind = ArchiveEntryKind.Symlink, LinkTarget = string.Join('/', Enumerable.Repeat(new string('t', 40), 4)) }, (Stream?)null);
        }

        if (pax)
        {
            w.WriteEntry(new ArchiveEntry
            {
                Name = "root/ünïcödé-日本語.txt",
                Size = 2,
                ModifiedTime = s_base.AddTicks(1234567),
                AccessTime = s_base.AddSeconds(10),
                ChangeTime = s_base.AddSeconds(20),
                Extras = new Dictionary<string, string> { ["SCHILY.xattr.user.b"] = "2", ["SCHILY.xattr.user.a"] = "1" },
            }, "hi"u8.ToArray());
        }
    }

    private static byte[] BigFields(ReadOnlySpan<byte> content)
    {
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, new TarWriterOptions { RecordSize = 512 }, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry
            {
                Name = "big",
                Size = content.Length,
                Uid = 40_000_000,
                Gid = 3_000_000,
                UserName = new string('u', 40),
                GroupName = new string('g', 33),
                ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(-86_400 * 365).AddTicks(5),
            }, content.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "future", ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(20_000_000_000L) });
        }

        return ms.ToArray();
    }

    private static byte[] Normalized(ReadOnlySpan<byte> content)
    {
        var ms = new MemoryStream();
        Archive.Create(ms, new ArchiveCreateOptions { Normalize = true, LeaveOpen = true })
            .Add(new ArchiveEntry { Name = "z/last", Size = content.Length, Mode = (UnixFileMode)0b111_101_101, ModifiedTime = s_base, Uid = 7, UserName = "x" }, content.ToArray())
            .Add(new ArchiveEntry { Name = "a", Kind = ArchiveEntryKind.Directory, ModifiedTime = s_base.AddDays(3) })
            .Add(new ArchiveEntry { Name = "m/first", Size = 1 }, [1])
            .Save();
        return ms.ToArray();
    }

    private static byte[] Compressed(ReadOnlySpan<byte> content, ArchiveCompression compression, int? level)
    {
        var ms = new MemoryStream();
        var builder = Archive.Create(ms, new ArchiveCreateOptions { Compression = compression, CompressionLevel = level, LeaveOpen = true });
        builder.Add(new ArchiveEntry { Name = "root", Kind = ArchiveEntryKind.Directory, ModifiedTime = s_base });
        builder.Add(new ArchiveEntry { Name = "root/data.bin", Size = content.Length, ModifiedTime = s_base, Mode = (UnixFileMode)0b110_100_100 }, content.ToArray());
        builder.Add(new ArchiveEntry { Name = "root/" + new string('n', 130), Size = 1, ModifiedTime = s_base }, [7]);
        builder.Save();
        return ms.ToArray();
    }

    private static byte[] Summary(ReadOnlySpan<byte> content)
    {
        byte[] tar = Write(content, TarFormat.Pax, longNames: true);
        var sb = new StringBuilder();
        using var reader = new TarReader(new MemoryStream(tar));
        while (reader.GetNextEntry() is { } e)
        {
            sb.Append(e.Name).Append('|').Append(e.Kind).Append('|').Append(e.Size).Append('|').Append(e.Mode).Append('|').Append(e.ModifiedTime?.UtcTicks)
                .Append('|').Append(e.Uid).Append('|').Append(e.UserName).Append('|').Append(e.LinkTarget).Append('|').Append(e.HeaderOffset).Append('|').Append(e.DataOffset);
            foreach (var kv in e.Extras.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
            }

            sb.Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
