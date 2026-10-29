using RecursiveAI.OneArchive;

// The slim path: switch the all-codecs default off (AotTrimSlim.csproj) and register only Zstandard.
ArchiveCodecs.AddZstd();

byte[] content = new byte[5000];
for (int i = 0; i < content.Length; i++)
{
    content[i] = (byte)('a' + (i * 7 % 13));
}

// Write a .tar.zst to memory and read it back.
var packed = new MemoryStream();
Archive.Create(packed, new ArchiveCreateOptions { Format = ArchiveFormat.Tar, Compression = ArchiveCompression.Zstd, LeaveOpen = true })
    .Add(new ArchiveEntry { Name = "docs/a.txt", Size = content.Length }, content)
    .Save();
packed.Position = 0;
using (Archive archive = Archive.Open(packed, new ArchiveOpenOptions { LeaveOpen = true }))
{
    if (archive.Entries.Count != 1 || archive.Entries[0].Name != "docs/a.txt")
    {
        return Fail("tar.zst entries");
    }

    using var s = archive.OpenEntry(archive.Entries[0]);
    var got = new MemoryStream();
    s.CopyTo(got);
    if (!got.ToArray().AsSpan().SequenceEqual(content))
    {
        return Fail("tar.zst content");
    }
}

// A codec the app left out is a typed error naming the format and the method, never a crash.
int rc = Expect("xz", () =>
{
    byte[] xz = [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0, 0, 4, 0xE6, 0xD6, 0xB4, 0x46, 0, 0, 0, 0, 0, 0, 0, 0];
    Archive.Open(new MemoryStream(xz)).Dispose();
});
rc |= Expect("zip", () =>
    Archive.Create(new MemoryStream(), new ArchiveCreateOptions { Format = ArchiveFormat.Zip })
        .Add(new ArchiveEntry { Name = "a", Size = 3 }, [1, 2, 3]).Save());
rc |= Expect("7z", () =>
    Archive.Create(new MemoryStream(), new ArchiveCreateOptions { Format = ArchiveFormat.SevenZip })
        .Add(new ArchiveEntry { Name = "a", Size = 3 }, [1, 2, 3]).Save());
if (rc != 0)
{
    return 1;
}

Console.WriteLine("slim smoke ok");
return 0;

static int Fail(string what)
{
    Console.Error.WriteLine("slim smoke failed: " + what);
    return 1;
}

static int Expect(string name, Action action)
{
    try
    {
        action();
    }
    catch (ArchiveUnsupportedException ex) when (ex.Message.Contains("call ArchiveCodecs.Add", StringComparison.Ordinal))
    {
        return 0;
    }
    catch (Exception ex)
    {
        return Fail(name + ": " + ex.GetType().Name + ": " + ex.Message);
    }

    return Fail(name + ": no error");
}
