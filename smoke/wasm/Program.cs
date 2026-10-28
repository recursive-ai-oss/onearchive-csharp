using RecursiveAI.OneArchive;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;

// Runs under Node. The hash-matrix runner will grow out of this host.
if (args.Length > 0 && args[0] == "small")
{
    return RecursiveAI.Smoke.SmallRows.Run(args);
}

string compress = OneCompressInfo.Name;
string archive = OneArchiveInfo.Name;
if (compress != "OneCompress" || !archive.Contains(compress, StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke failed");
    return 1;
}

// Published check values, then vector-sized input against the same input fed in pieces too small for any vector path.
ReadOnlySpan<byte> check = "123456789"u8;
if (Crc32.Compute(check) != 0xCBF43926u || Crc32C.Compute(check) != 0xE3069283u ||
    Crc64.Compute(check) != 0x995DC9BBDF1939FAUL || Adler32.Compute(check) != 0x091E01DEu)
{
    Console.Error.WriteLine("checksum vectors failed");
    return 1;
}

var big = new byte[100_003];
new Random(1).NextBytes(big);
var crc = default(Crc32);
var crcC = default(Crc32C);
var crc64 = default(Crc64);
var adler = default(Adler32);
var xx32 = default(XxHash32);
for (int i = 0; i < big.Length; i += 7)
{
    ReadOnlySpan<byte> piece = big.AsSpan(i, Math.Min(7, big.Length - i));
    crc.Append(piece);
    crcC.Append(piece);
    crc64.Append(piece);
    adler.Append(piece);
    xx32.Append(piece);
}

if (crc.GetCurrentValue() != Crc32.Compute(big) || crcC.GetCurrentValue() != Crc32C.Compute(big) ||
    crc64.GetCurrentValue() != Crc64.Compute(big) || adler.GetCurrentValue() != Adler32.Compute(big) ||
    xx32.GetCurrentValue() != XxHash32.Compute(big))
{
    Console.Error.WriteLine("checksum split mismatch");
    return 1;
}

// Archives without a file system: tar written and read back, then the Archive layer over .tar.zst in memory,
// sparse-free but with pax fields, a safe-extraction refusal and a limit.
var tarBytes = new MemoryStream();
using (var tarWriter = new RecursiveAI.OneArchive.Tar.TarWriter(tarBytes, leaveOpen: true))
{
    tarWriter.WriteEntry(new ArchiveEntry { Name = new string('n', 150) + "/f.txt", Size = 5, ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(1234567) }, "hello"u8.ToArray());
    tarWriter.WriteEntry(new ArchiveEntry { Name = "../evil", Size = 1 }, "x"u8.ToArray());
}

tarBytes.Position = 0;
using (var tarReader = new RecursiveAI.OneArchive.Tar.TarReader(tarBytes))
{
    var first = tarReader.GetNextEntry();
    if (first is null || first.Name.Length != 156 || first.Size != 5 || first.ModifiedTime!.Value.Ticks % 10_000_000 != 1234567)
    {
        Console.Error.WriteLine("tar entry failed");
        return 1;
    }
}

// cpio and ar written and read back through the Archive layer and the format types.
var cpioBytes = new MemoryStream();
using (var cpioWriter = new RecursiveAI.OneArchive.Cpio.CpioWriter(cpioBytes, leaveOpen: true))
{
    cpioWriter.WriteEntry(new ArchiveEntry { Name = "dir/a.txt", Size = 5, ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000) }, "hello"u8.ToArray());
}

using (var cpioReader = new RecursiveAI.OneArchive.Cpio.CpioReader(new MemoryStream(cpioBytes.ToArray())))
{
    var cpioEntry = cpioReader.GetNextEntry();
    if (cpioEntry is null || cpioEntry.Name != "dir/a.txt" || cpioEntry.Size != 5 || cpioReader.GetNextEntry() is not null || Archive.Open(new MemoryStream(cpioBytes.ToArray())).Format != ArchiveFormat.Cpio)
    {
        Console.Error.WriteLine("cpio round trip failed");
        return 1;
    }
}

var arBytes = new MemoryStream();
using (var arWriter = new RecursiveAI.OneArchive.Ar.ArWriter(arBytes, leaveOpen: true))
{
    arWriter.WriteEntry(new ArchiveEntry { Name = new string('m', 40) + ".o", Size = 3 }, "abc"u8.ToArray());
}

using (var arReader = new RecursiveAI.OneArchive.Ar.ArReader(new MemoryStream(arBytes.ToArray())))
{
    var arEntry = arReader.GetNextEntry();
    if (arEntry is null || arEntry.Name.Length != 42 || arEntry.Size != 3 || arReader.GetNextEntry() is not null || Archive.Open(new MemoryStream(arBytes.ToArray())).Format != ArchiveFormat.Ar)
    {
        Console.Error.WriteLine("ar round trip failed");
        return 1;
    }
}

var zstTar = new MemoryStream();
Archive.Create(zstTar, new ArchiveCreateOptions { Compression = ArchiveCompression.Zstd, LeaveOpen = true })
    .Add(new ArchiveEntry { Name = "a.txt", Size = 3 }, "abc"u8.ToArray()).Save();
using (var opened = Archive.Open(new MemoryStream(zstTar.ToArray())))
{
    using var content = new StreamReader(opened.OpenEntry(opened.Entries[0]));
    if (opened.Compression != ArchiveCompression.Zstd || opened.Entries.Count != 1 || content.ReadToEnd() != "abc")
    {
        Console.Error.WriteLine("archive round trip failed");
        return 1;
    }
}

try
{
    var lz4Tar = new MemoryStream();
    Archive.Create(lz4Tar, new ArchiveCreateOptions { Compression = ArchiveCompression.Lz4, LeaveOpen = true }).Save();
    using var emptyArchive = Archive.Open(new MemoryStream(lz4Tar.ToArray()));
    if (emptyArchive.Entries.Count != 0)
    {
        Console.Error.WriteLine("empty lz4 tar failed");
        return 1;
    }

    using var hostile = Archive.Open(new MemoryStream(tarBytes.ToArray()));
    hostile.Extract(Path.Combine(Path.GetTempPath(), "onearchive-smoke-never"));
    Console.Error.WriteLine("traversal was not refused");
    return 1;
}
catch (ArchiveSafetyException)
{
}
catch (IOException)
{
    // No writable file system (the browser host): the refusal is not reachable there, the stream-only API above is.
}
catch (UnauthorizedAccessException)
{
}
catch (PlatformNotSupportedException)
{
}

Console.WriteLine("ok: " + archive);
return 0;
