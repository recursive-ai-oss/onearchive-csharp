using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cfb;

// A compound file needs no codec: no ArchiveCodecs.Add call is made, yet the file opens, lists and reads.
byte[] file;
using (Stream? res = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("v3-basic.cfb"))
{
    if (res is null)
    {
        return Fail("resource");
    }

    var ms = new MemoryStream();
    res.CopyTo(ms);
    file = ms.ToArray();
}

using (Archive archive = Archive.Open(new MemoryStream(file), new ArchiveOpenOptions { Limits = new ArchiveLimits { MaxPathDepth = 100 } }))
{
    if (archive.Format != ArchiveFormat.CompoundFile || archive.Entries.Count != 10)
    {
        return Fail("entries");
    }

    ArchiveEntry big = archive.Find("big") ?? throw new InvalidOperationException();
    using Stream s = archive.OpenEntry(big);
    var got = new MemoryStream();
    s.CopyTo(got);
    if (got.Length != 9000)
    {
        return Fail("stream size");
    }
}

using var reader = new CompoundFileReader(file, new CompoundFileReaderOptions { Limits = new ArchiveLimits { MaxPathDepth = 100 } });
CfbEntry? stream1 = reader.Find("storage 1/stream 1");
if (stream1 is null || stream1.Size != 544 || reader.Header.MajorVersion != 3)
{
    return Fail("find");
}

// Writing needs no codec either: rewrite the file in both versions and read each result back.
foreach (int version in new[] { 3, 4 })
{
    var written = new MemoryStream();
    using (var writer = new CompoundFileWriter(written, new CompoundFileWriterOptions { MajorVersion = version }, leaveOpen: true))
    {
        writer.WriteEntry(new ArchiveEntry { Name = "docs", Kind = ArchiveEntryKind.Directory });
        writer.WriteEntry(new ArchiveEntry { Name = "docs/small", Size = 3 }, new byte[] { 1, 2, 3 });
        writer.WriteEntry(new ArchiveEntry { Name = "large", Size = 9000 }, new byte[9000]);
    }

    using var back = new CompoundFileReader(written.ToArray());
    if (back.Header.MajorVersion != version || back.Find("large")?.Size != 9000 || back.Find("docs/small")?.Size != 3)
    {
        return Fail("write v" + version);
    }
}

Console.WriteLine("cfb smoke ok");
return 0;

static int Fail(string what)
{
    Console.Error.WriteLine("cfb smoke failed: " + what);
    return 1;
}
