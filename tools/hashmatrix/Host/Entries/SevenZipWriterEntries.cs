using System.Runtime.CompilerServices;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.SevenZip;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The 7z writer over an entry mix (directory, file, empty file, symbolic link, Unicode name, times, modes) for each method,
/// solid and non-solid folders, encoded and plain headers, and every filter. The bytes depend only on the input and the options.
/// </summary>
internal static class SevenZipWriterEntries
{
    private static readonly DateTimeOffset s_base = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [ModuleInitializer]
    internal static void Register()
    {
        foreach (SevenZipMethod method in new[] { SevenZipMethod.Lzma2, SevenZipMethod.Lzma, SevenZipMethod.Copy, SevenZipMethod.Deflate, SevenZipMethod.Zstd })
        {
            SevenZipMethod m = method;
            string n = m.ToString().ToLowerInvariant();
            Matrix.Add("sevenzip-write", n + "-solid", (input, _) => Write(input, new SevenZipWriterOptions { Method = m }));
            Matrix.Add("sevenzip-write", n + "-nonsolid-plain-header", (input, _) => Write(input, new SevenZipWriterOptions { Method = m, Solid = false, EncodeHeader = false }));
        }

        Matrix.Add("sevenzip-write", "lzma2-level1", (input, _) => Write(input, new SevenZipWriterOptions { Level = 1 }));
        Matrix.Add("sevenzip-write", "lzma2-level9", (input, _) => Write(input, new SevenZipWriterOptions { Level = 9 }));
        foreach (SevenZipFilter filter in Enum.GetValues<SevenZipFilter>())
        {
            SevenZipFilter f = filter;
            Matrix.Add("sevenzip-write", "filter-" + f.ToString().ToLowerInvariant(), (input, _) => Write(input, new SevenZipWriterOptions { Filter = f, DeltaDistance = 4 }));
        }
    }

    private static byte[] Write(ReadOnlySpan<byte> content, SevenZipWriterOptions options)
    {
        byte[] data = content.ToArray();
        var ms = new MemoryStream();
        using (var w = new SevenZipWriter(ms, options, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "root", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)0b111_101_101, ModifiedTime = s_base }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = "root/data.bin", Size = data.Length, Mode = (UnixFileMode)0b110_100_100, ModifiedTime = s_base.AddSeconds(1) }, data);
            w.WriteEntry(new ArchiveEntry { Name = "root/empty", Size = 0, ModifiedTime = s_base.AddSeconds(2) }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = "root/link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "data.bin", ModifiedTime = s_base.AddSeconds(3) }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = "root/ünïcödé-日本語.txt", Size = 2, ModifiedTime = s_base.AddTicks(1234567), AccessTime = s_base.AddSeconds(10), ChangeTime = s_base.AddSeconds(20) }, "hi"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "root/second.bin", Size = data.Length, ModifiedTime = s_base.AddSeconds(4) }, data);
        }

        return ms.ToArray();
    }
}
