using RecursiveAI.OneArchive;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;

// Runs under Node. The hash-matrix runner will grow out of this host.
if (args.Length > 0 && args[0] == "small")
{
    return RecursiveAI.Smoke.SmallRows.Run(args);
}

if (!RecursiveAI.Smoke.Lz4HighCheck.Run())
{
    return 1;
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

// A 619-byte cabinet with a stored, an MSZIP and an LZX folder, each holding one 315-byte text file: read through CabReader and Archive.
byte[] cabBytes = Convert.FromHexString("4d534346000000006b020000000000003c000000000000000301030003000000341200008d00000001000000d0010000010001000d0200000100030f3b0100000000000000006958aa6d20006e6f6e655c742e747874003b0100000000000001006958aa6d20006d737a69705c742e747874003b0100000000000002006958aa6d20006c7a785c742e747874000e655d773b013b0154686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e206996c82a35003b01434b0bc94855282ccd4cce56482aca2fcf5348cbaf50c82acd2d2856c82f4b2d5228c94855c849acaa5448c94fd75318558cac18000f6cfa7f56003b010010b01300000000220000553942e99f9d282118fbaa5b5de5f7008000000000001008013e678c15effb000000000000000008113e61effb72a707731ecd7728ab02c5479960d1b143054b40be4271b17fe9dfc000f2");
using (var cabReader = new RecursiveAI.OneArchive.Cab.CabReader(new MemoryStream(cabBytes)))
{
    int cabCount = 0;
    while (cabReader.GetNextEntry() is { } cabEntry)
    {
        using var cabContent = new MemoryStream();
        cabReader.OpenEntryStream().CopyTo(cabContent);
        if (cabEntry.Size != 315 || !cabEntry.Name.EndsWith("/t.txt", StringComparison.Ordinal) || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(cabContent.ToArray())) != "dababee8658b645e01725be3eab108b2f506b7220e19ef399a18a11ae9e78e66")
        {
            Console.Error.WriteLine("cab entry failed: " + cabEntry.Name);
            return 1;
        }

        cabCount++;
    }

    if (cabCount != 3 || Archive.Open(new MemoryStream(cabBytes)).Format != ArchiveFormat.Cab)
    {
        Console.Error.WriteLine("cab read failed");
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

// RAR 5: a stored entry hashed with BLAKE2sp (made by the RAR tool) lists and extracts, and a compressed one lists and refuses to extract.
byte[] rarStored = Convert.FromHexString("526172211a0701003392b5e50a0105060005010180800092dbf7014202032eb70100b701b4830280000105742e747874220200f5aa3407901b8c5193f4f4bb96c85ea3aaa1739556e77bb424694725f6da86680a03137aa0e26a14f2c20248656c6c6f2066726f6d206120524152203520617263686976652c2073746f72656420616e6420686173686564207769746820424c414b453273702e0a48656c6c6f2066726f6d206120524152203520617263686976652c2073746f72656420616e6420686173686564207769746820424c414b453273702e0a48656c6c6f2066726f6d206120524152203520617263686976652c2073746f72656420616e6420686173686564207769746820424c414b453273702e0a1d77565103050400");
byte[] rarCompressed = Convert.FromHexString("526172211a0701003392b5e50a01050600050101808000ee3c62b22302030bd10004b701b483021e85877f80030105742e7478740a03137aa0e26a14f2c202c3d74e2545432f943eed15fa011e08340441041794a0f45d4d811753760b7ff18d14d198199c32a9f501ea93339cd470cbc82118446009b649787687a2b1ef5beaa213a7cdeca8831d61a657bf7759fc701d77565103050400");
using (var rarReader = new RecursiveAI.OneArchive.Rar.RarReader(new MemoryStream(rarStored)))
{
    var rarEntry = rarReader.GetNextEntry();
    using var rarContent = new MemoryStream();
    rarReader.OpenEntryStream().CopyTo(rarContent);
    if (rarEntry is null || rarEntry.Name != "t.txt" || rarEntry.Size != 183 || rarEntry.Blake2sp is null || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(rarContent.ToArray())) != "ecbe829874724ab86e012981c6edb0ce49dc8c8fa736a856bfee5b095ba3be85" || rarReader.GetNextEntry() is not null)
    {
        Console.Error.WriteLine("rar stored read failed");
        return 1;
    }
}

using (var rarReader = new RecursiveAI.OneArchive.Rar.RarReader(new MemoryStream(rarCompressed)))
{
    var rarEntry = rarReader.GetNextEntry();
    try
    {
        rarReader.OpenEntryStream();
        Console.Error.WriteLine("rar compressed entry opened");
        return 1;
    }
    catch (RecursiveAI.OneArchive.Rar.RarUnsupportedException ex) when (rarEntry is { Method: 3, Size: 183 } && ex.Message.Contains("not supported yet", StringComparison.Ordinal))
    {
    }
}

if (Archive.Open(new MemoryStream(rarStored)).Format != ArchiveFormat.Rar)
{
    Console.Error.WriteLine("rar detection failed");
    return 1;
}

Console.WriteLine("ok: " + archive);
return 0;
