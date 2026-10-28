using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Ar;
using RecursiveAI.OneArchive.Cpio;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The cpio and ar writers over fixed entry mixes (every variant, with the standard input as file content), and the readers'
/// view as text: of the writers' own output and of four small archives made by GNU cpio, GNU ar and bsdtar (embedded below,
/// made by running the tools on a tree of a.txt, a hard link, a sub/b.bin, a symlink and an empty file). Nothing here depends
/// on the platform: fields are fixed width and the writers never read a clock.
/// </summary>
internal static class CpioArEntries
{
    private const string GnuNewc = "MDcwNzAxMjRGMjVEQkQwMDAwNDFGRDAwMDAwM0U4MDAwMDAzRTgwMDAwMDAwMzZBRTIzMTJGMDAwMDAwMDAwMDAwMDEwMzAwMDAwMDAxMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAyMDAwMDAwMDAuADA3MDcwMTJEQTE1RDhBMDAwMDQxRkQwMDAwMDNFODAwMDAwM0U4MDAwMDAwMDI2QUUyMzEyRjAwMDAwMDAwMDAwMDAxMDMwMDAwMDAwMTAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMjAwMDAwMDAwZAAwNzA3MDEyREExNUQ4QjAwMDA4MUI0MDAwMDAzRTgwMDAwMDNFODAwMDAwMDAxNkFFMjMxMkYwMDAwMDAwMzAwMDAwMTAzMDAwMDAwMDEwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDgwMDAwMDAwMGQvYi5iaW4AAAB4eXoAMDcwNzAxMjRGMURDNTkwMDAwODFCNDAwMDAwM0U4MDAwMDAzRTgwMDAwMDAwMTZBRTIzMTJGMDAwMDAwMDAwMDAwMDEwMzAwMDAwMDAxMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA2MDAwMDAwMDBlbXB0eQAwNzA3MDEyNEYyNURCRTAwMDA4MUI0MDAwMDAzRTgwMDAwMDNFODAwMDAwMDAyNkFFMjMxMkYwMDAwMDAwMDAwMDAwMTAzMDAwMDAwMDEwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDYwMDAwMDAwMGEudHh0ADA3MDcwMTI0RjI1REJFMDAwMDgxQjQwMDAwMDNFODAwMDAwM0U4MDAwMDAwMDI2QUUyMzEyRjAwMDAwMDA2MDAwMDAxMDMwMDAwMDAwMTAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwNjAwMDAwMDAwaGFyZDEAaGVsbG8KAAAwNzA3MDEyNEYyNURCRjAwMDBBMUZGMDAwMDAzRTgwMDAwMDNFODAwMDAwMDAxNkFFMjMxMkYwMDAwMDAwNTAwMDAwMTAzMDAwMDAwMDEwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDMwMDAwMDAwMHNsAAAAAGEudHh0AAAAMDcwNzAxMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMTAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDBCMDAwMDAwMDBUUkFJTEVSISEhAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
    private const string GnuOdc = "MDcwNzA3MjAxNDAxNDU2Njc1MDQwNzc1MDAxNzUwMDAxNzUwMDAwMDAzMDAwMDAwMTUyNzA0MzA0NTcwMDAwMDIwMDAwMDAwMDAwMC4AMDcwNzA3MjAxNDAxNDU2Njc2MTAwNjY0MDAxNzUwMDAxNzUwMDAwMDAyMDAwMDAwMTUyNzA0MzA0NTcwMDAwMDYwMDAwMDAwMDAwNmEudHh0AGhlbGxvCjA3MDcwNzIwMTQwMTI1NjYxMjA0MDc3NTAwMTc1MDAwMTc1MDAwMDAwMjAwMDAwMDE1MjcwNDMwNDU3MDAwMDAyMDAwMDAwMDAwMDBkADA3MDcwNzIwMTQwMTI1NjYxMzEwMDY2NDAwMTc1MDAwMTc1MDAwMDAwMTAwMDAwMDE1MjcwNDMwNDU3MDAwMDEwMDAwMDAwMDAwMDNkL2IuYmluAHh5ejA3MDcwNzIwMTQwMTM1NjEzMTEwMDY2NDAwMTc1MDAwMTc1MDAwMDAwMTAwMDAwMDE1MjcwNDMwNDU3MDAwMDA2MDAwMDAwMDAwMDBlbXB0eQAwNzA3MDcyMDE0MDE0NTY2NzYxMDA2NjQwMDE3NTAwMDE3NTAwMDAwMDIwMDAwMDAxNTI3MDQzMDQ1NzAwMDAwNjAwMDAwMDAwMDA2aGFyZDEAaGVsbG8KMDcwNzA3MjAxNDAxNDU2Njc3MTIwNzc3MDAxNzUwMDAxNzUwMDAwMDAxMDAwMDAwMTUyNzA0MzA0NTcwMDAwMDMwMDAwMDAwMDAwNXNsAGEudHh0MDcwNzA3MDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAxMDAwMDAwMDAwMDAwMDAwMDAwMDAwMTMwMDAwMDAwMDAwMFRSQUlMRVIhISEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";
    private const string GnuAr = "ITxhcmNoPgovLyAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAzNCAgICAgICAgYAphLXZlcnktbG9uZy1tZW1iZXItbmFtZS1oZXJlLnR4dC8KYS50eHQvICAgICAgICAgIDAgICAgICAgICAgIDAgICAgIDAgICAgIDY0NCAgICAgNiAgICAgICAgIGAKaGVsbG8KLzAgICAgICAgICAgICAgIDAgICAgICAgICAgIDAgICAgIDAgICAgIDY0NCAgICAgNiAgICAgICAgIGAKaGVsbG8K";
    private const string BsdAr = "ITxhcmNoPgphLnR4dCAgICAgICAgICAgMTc5MzIwODYyMyAgMTAwMCAgMTAwMCAgMTAwNjY0ICA2ICAgICAgICAgYApoZWxsbwpiLmJpbiAgICAgICAgICAgMTc5MzIwODYyMyAgMTAwMCAgMTAwMCAgMTAwNjY0ICAzICAgICAgICAgYAp4eXoKIzEvMzIgICAgICAgICAgIDE3OTMyMDg2MzcgIDEwMDAgIDEwMDAgIDEwMDY2NCAgMzggICAgICAgIGAKYS12ZXJ5LWxvbmctbWVtYmVyLW5hbWUtaGVyZS50eHRoZWxsbwo=";

    private static readonly DateTimeOffset s_base = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [ModuleInitializer]
    internal static void Register()
    {
        foreach (CpioFormat f in new[] { CpioFormat.Newc, CpioFormat.Crc, CpioFormat.Odc })
        {
            CpioFormat format = f;
            string n = format.ToString().ToLowerInvariant();
            Matrix.Add("cpio-write", n + "-mix", (input, _) => WriteCpio(input, format, 512));
            Matrix.Add("cpio-read", n + "-mix-summary", (input, _) => SummaryCpio(WriteCpio(input, format, 512), true));
        }

        Matrix.Add("cpio-write", "newc-mix-10240", (input, _) => WriteCpio(input, CpioFormat.Newc, 10240));
        Matrix.Add("cpio-read", "gnu-newc-summary", (_, _) => SummaryCpio(Convert.FromBase64String(GnuNewc), true), "empty");
        Matrix.Add("cpio-read", "gnu-newc-no-resolve-summary", (_, _) => SummaryCpio(Convert.FromBase64String(GnuNewc), false), "empty");
        Matrix.Add("cpio-read", "gnu-odc-summary", (_, _) => SummaryCpio(Convert.FromBase64String(GnuOdc), true), "empty");
        foreach (ArFormat f in new[] { ArFormat.Gnu, ArFormat.Bsd })
        {
            ArFormat format = f;
            string n = format.ToString().ToLowerInvariant();
            Matrix.Add("ar-write", n + "-mix", (input, _) => WriteAr(input, format, ArWriterOptions.DefaultMaxMemoryBuffer));
            Matrix.Add("ar-read", n + "-mix-summary", (input, _) => SummaryAr(WriteAr(input, format, ArWriterOptions.DefaultMaxMemoryBuffer)));
        }

        Matrix.Add("ar-write", "gnu-mix-spooled", (input, _) => WriteAr(input, ArFormat.Gnu, 64));
        Matrix.Add("ar-read", "gnu-summary", (_, _) => SummaryAr(Convert.FromBase64String(GnuAr)), "empty");
        Matrix.Add("ar-read", "bsd-summary", (_, _) => SummaryAr(Convert.FromBase64String(BsdAr)), "empty");
    }

    private static byte[] WriteCpio(ReadOnlySpan<byte> content, CpioFormat format, int blockSize)
    {
        byte[] data = content.ToArray();
        var ms = new MemoryStream();
        using (var w = new CpioWriter(ms, new CpioWriterOptions { Format = format, BlockSize = blockSize }, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "root", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)0b111_101_101, ModifiedTime = s_base }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = "root/data.bin", Size = data.Length, Mode = (UnixFileMode)0b110_100_100, ModifiedTime = s_base.AddSeconds(1), Uid = 1000, Gid = 1000 }, data);
            w.WriteEntry(new ArchiveEntry { Name = "root/empty", ModifiedTime = s_base.AddSeconds(2) }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = "root/link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "data.bin", ModifiedTime = s_base.AddSeconds(3) }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = "root/tty", Kind = ArchiveEntryKind.CharacterDevice, DeviceMajor = 5, DeviceMinor = 1, Mode = (UnixFileMode)0b110_110_110 }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = "root/fifo", Kind = ArchiveEntryKind.Fifo }, (Stream?)null);
            w.WriteEntry(new CpioEntry { Name = "root/first", Inode = 500, NLink = 2, ModifiedTime = s_base }, (Stream?)null);
            w.WriteEntry(new CpioEntry { Name = "root/second", Inode = 500, NLink = 2, Size = 4, ModifiedTime = s_base }, "abcd"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "root/\u00fcn\u00ef-\u65e5\u672c.txt", Size = 2, ModifiedTime = s_base.AddSeconds(9) }, "hi"u8.ToArray());
        }

        return ms.ToArray();
    }

    private static byte[] WriteAr(ReadOnlySpan<byte> content, ArFormat format, long memory)
    {
        byte[] data = content.ToArray();
        var ms = new MemoryStream();
        using (var w = new ArWriter(ms, new ArWriterOptions { Format = format, MaxMemoryBuffer = memory }, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "data.bin", Size = data.Length, Mode = (UnixFileMode)0b110_100_100, ModifiedTime = s_base, Uid = 1000, Gid = 1000 }, data);
            w.WriteEntry(new ArchiveEntry { Name = "empty", ModifiedTime = s_base.AddSeconds(2) }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = "a-member-name-longer-than-sixteen-bytes.txt", Size = 3, ModifiedTime = s_base }, "abc"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "with space", Size = 1 }, "x"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "\u00fcn\u00ef-\u65e5\u672c.txt", Size = 2 }, "hi"u8.ToArray());
        }

        return ms.ToArray();
    }

    private static byte[] SummaryCpio(byte[] archive, bool resolve)
    {
        using var reader = new CpioReader(new MemoryStream(archive), new CpioReaderOptions { ResolveLinkStubs = resolve });
        var sb = new StringBuilder();
        while (reader.GetNextEntry() is { } e)
        {
            sb.Append(e.Name).Append('|').Append(e.Kind).Append('|').Append(e.Size).Append('|').Append(e.StoredSize).Append('|').Append(e.RawMode).Append('|').Append(e.Uid).Append(',').Append(e.Gid)
                .Append('|').Append(e.ModifiedTime?.ToUnixTimeSeconds()).Append('|').Append(e.LinkTarget).Append('|').Append(e.Inode).Append(',').Append(e.NLink).Append('|').Append(e.DeviceMajor).Append(',').Append(e.DeviceMinor)
                .Append('|').Append(e.IsLinkStub).Append(e.ContentFromLaterLink).Append('|').Append(e.Checksum).Append('|');
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

    private static byte[] SummaryAr(byte[] archive)
    {
        using var reader = new ArReader(new MemoryStream(archive), new ArReaderOptions { IncludeSpecialMembers = true });
        var sb = new StringBuilder();
        while (reader.GetNextEntry() is { } e)
        {
            sb.Append(e.Name).Append('|').Append(e.MemberKind).Append('|').Append(e.NameStyle).Append('|').Append(e.Size).Append('|').Append(e.RawName).Append('|').Append(e.RawMode).Append('|').Append(e.Uid).Append(',').Append(e.Gid)
                .Append('|').Append(e.ModifiedTime?.ToUnixTimeSeconds()).Append('|').Append(e.HeaderOffset).Append(',').Append(e.DataOffset).Append('|');
            using Stream s = reader.OpenEntryStream();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            sb.Append(Convert.ToHexString(SHA256.HashData(ms.ToArray()))).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
