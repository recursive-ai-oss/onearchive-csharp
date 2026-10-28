using System.Runtime.Versioning;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Encryption;

/// <summary>The files in testdata/encryption/vectors, rebuilt from the formulas testdata/encryption/generate.py uses.</summary>
internal static class EncryptionFixtures
{
    public const string Password = "fake-password-1";

    public static Dictionary<string, byte[]> Files() => new()
    {
        ["text.txt"] = [.. Enumerable.Range(0, 40).SelectMany(_ => "The quick brown fox jumps over the lazy dog.\n"u8.ToArray())],
        ["random.bin"] = Lcg(7, 3000),
        ["empty.txt"] = [],
    };

    public static byte[] Lcg(uint seed, int n)
    {
        byte[] r = new byte[n];
        uint x = seed;
        for (int i = 0; i < n; i++)
        {
            x = ((x * 1103515245) + 12345) & 0x7FFFFFFF;
            r[i] = (byte)(x >> 16);
        }

        return r;
    }

    public static byte[] Vector(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "encryption", name));

    /// <summary>A reproducible stand-in for random salts and IVs: each call continues the counter.</summary>
    public static Action<Span<byte>> Counter(byte start = 1)
    {
        byte next = start;
        return span =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = next++;
            }
        };
    }

    public static Dictionary<string, byte[]> ReadZip(byte[] zip, string? password, bool forward = false)
    {
        var result = new Dictionary<string, byte[]>();
        Stream s = forward ? new ForwardOnly(new MemoryStream(zip)) : new MemoryStream(zip);
        using var reader = new ZipReader(s, new ZipReaderOptions { Password = password });
        while (reader.GetNextEntry() is { } e)
        {
            if (e.Kind != ArchiveEntryKind.File)
            {
                continue;
            }

            using Stream content = reader.OpenEntryStream();
            var ms = new MemoryStream();
            content.CopyTo(ms);
            result[e.Name] = ms.ToArray();
        }

        return result;
    }

    public static Dictionary<string, byte[]> ReadSevenZip(byte[] archive, string? password)
    {
        var result = new Dictionary<string, byte[]>();
        using var reader = new SevenZipReader(new MemoryStream(archive), new SevenZipReaderOptions { Password = password });
        while (reader.GetNextEntry() is { } e)
        {
            if (e.Kind != ArchiveEntryKind.File)
            {
                continue;
            }

            using Stream content = reader.OpenEntryStream();
            var ms = new MemoryStream();
            content.CopyTo(ms);
            result[e.Name] = ms.ToArray();
        }

        return result;
    }

    public static void AssertSame(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] data) in expected)
        {
            Assert.True(data.AsSpan().SequenceEqual(actual[name]), $"content of {name}");
        }
    }
}

/// <summary>Zip: WinZip AES read and write, traditional encryption read.</summary>
public class ZipEncryptionTests
{
    private static readonly string[] s_vectors = ["zip-aes128.zip", "zip-aes192.zip", "zip-aes256.zip", "zip-aes256-store.zip", "zip-zipcrypto-7z.zip", "zip-zipcrypto-infozip.zip", "zip-zipcrypto-infozip-stream.zip"];

    public static IEnumerable<object[]> Vectors() => s_vectors.Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ReferenceToolVectorsDecode(string name)
    {
        byte[] zip = EncryptionFixtures.Vector(name);
        EncryptionFixtures.AssertSame(EncryptionFixtures.Files(), EncryptionFixtures.ReadZip(zip, EncryptionFixtures.Password));
    }

    [Theory]
    [InlineData("zip-zipcrypto-infozip.zip")]
    [InlineData("zip-zipcrypto-7z.zip")]
    [InlineData("zip-aes256.zip")]
    public void ForwardOnlyReadDecodes(string name)
    {
        // Entries with sizes in their local headers can be decrypted without the central directory.
        byte[] zip = EncryptionFixtures.Vector(name);
        try
        {
            EncryptionFixtures.AssertSame(EncryptionFixtures.Files(), EncryptionFixtures.ReadZip(zip, EncryptionFixtures.Password, forward: true));
        }
        catch (ArchiveUnsupportedException ex)
        {
            // 7-Zip puts the sizes in a data descriptor when it writes AES entries; that needs the directory.
            Assert.Contains("data descriptor", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ListingNeedsNoPassword()
    {
        using var reader = new ZipReader(new MemoryStream(EncryptionFixtures.Vector("zip-aes192.zip")));
        var seen = new List<ZipEntry>();
        while (reader.GetNextEntry() is { } e)
        {
            seen.Add(e);
        }

        Assert.Equal(3, seen.Count);
        Assert.All(seen, e =>
        {
            Assert.True(e.IsEncrypted);
            Assert.Equal(ZipEncryption.Aes192, e.Encryption);
            Assert.Equal(ZipMethod.Aes, e.Method);
            Assert.True(e.AesVersion is 1 or 2);
        });
        Assert.Contains(seen, e => e.Name == "text.txt" && e.CompressionMethod == ZipMethod.Deflate);

        using var legacy = new ZipReader(new MemoryStream(EncryptionFixtures.Vector("zip-zipcrypto-infozip.zip")));
        Assert.Equal(ZipEncryption.ZipCrypto, legacy.GetNextEntry()!.Encryption);
    }

    [Theory]
    [InlineData("zip-aes256.zip")]
    [InlineData("zip-zipcrypto-infozip.zip")]
    public void MissingPasswordNamesTheEntry(string name)
    {
        using var reader = new ZipReader(new MemoryStream(EncryptionFixtures.Vector(name)));
        ZipEntry e = reader.GetNextEntry()!;
        ArchivePasswordException ex = Assert.Throws<ArchivePasswordException>(() => reader.OpenEntryStream());
        Assert.Equal(ArchivePasswordProblem.Required, ex.Problem);
        Assert.Equal(e.Name, ex.EntryName);
        Assert.Contains(e.Name, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zip-aes128.zip")]
    [InlineData("zip-aes256.zip")]
    [InlineData("zip-zipcrypto-infozip.zip")]
    [InlineData("zip-zipcrypto-7z.zip")]
    public void WrongPasswordIsATypedErrorThatNeverEchoesIt(string name)
    {
        const string wrong = "not-the-password-9";
        using var reader = new ZipReader(new MemoryStream(EncryptionFixtures.Vector(name)), new ZipReaderOptions { Password = wrong });
        reader.GetNextEntry();
        ArchivePasswordException ex = Assert.Throws<ArchivePasswordException>(() =>
        {
            using Stream s = reader.OpenEntryStream();
            s.CopyTo(Stream.Null);
        });
        Assert.Equal(ArchivePasswordProblem.Incorrect, ex.Problem);
        Assert.DoesNotContain(wrong, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ZipAesStrength.Aes128, ZipAesVersion.Ae1, ZipMethod.Deflate, true)]
    [InlineData(ZipAesStrength.Aes192, ZipAesVersion.Ae2, ZipMethod.Deflate, true)]
    [InlineData(ZipAesStrength.Aes256, ZipAesVersion.Ae2, ZipMethod.Deflate, true)]
    [InlineData(ZipAesStrength.Aes256, ZipAesVersion.Ae1, ZipMethod.Store, true)]
    [InlineData(ZipAesStrength.Aes256, ZipAesVersion.Ae2, ZipMethod.Store, false)]
    [InlineData(ZipAesStrength.Aes128, ZipAesVersion.Ae2, ZipMethod.Zstd, false)]
    [InlineData(ZipAesStrength.Aes256, ZipAesVersion.Ae1, ZipMethod.BZip2, true)]
    public void RoundTripsThroughOurReaderAndWriter(ZipAesStrength strength, ZipAesVersion version, ZipMethod method, bool seekableOutput)
    {
        Dictionary<string, byte[]> files = EncryptionFixtures.Files();
        files["big.txt"] = ZipTestsAccess.Text(200_000);
        byte[] zip = Write(files, new ZipWriterOptions { Method = method, Password = EncryptionFixtures.Password, AesStrength = strength, AesVersion = version }, seekableOutput);

        EncryptionFixtures.AssertSame(files, EncryptionFixtures.ReadZip(zip, EncryptionFixtures.Password));
        using var reader = new ZipReader(new MemoryStream(zip));
        var entries = new List<ZipEntry>();
        while (reader.GetNextEntry() is { } e)
        {
            entries.Add(e);
        }

        Assert.All(entries, e =>
        {
            Assert.Equal((int)version, e.AesVersion);
            Assert.Equal(ZipMethod.Aes, e.Method);
            if (version == ZipAesVersion.Ae2)
            {
                Assert.Equal(0u, e.Crc32);
            }
        });
        Assert.Equal(strength == ZipAesStrength.Aes128 ? ZipEncryption.Aes128 : strength == ZipAesStrength.Aes192 ? ZipEncryption.Aes192 : ZipEncryption.Aes256, entries[0].Encryption);
    }

    [Fact]
    public void ForwardReadOfOurOwnOutput()
    {
        Dictionary<string, byte[]> files = EncryptionFixtures.Files();
        byte[] zip = Write(files, new ZipWriterOptions { Password = EncryptionFixtures.Password, Method = ZipMethod.Store }, seekable: true);
        EncryptionFixtures.AssertSame(files, EncryptionFixtures.ReadZip(zip, EncryptionFixtures.Password, forward: true));
    }

    [Fact]
    public void DefaultsToAes256Ae2AndDirectoriesStayPlain()
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, new ZipWriterOptions { Password = EncryptionFixtures.Password }, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "d", Kind = ArchiveEntryKind.Directory });
            w.WriteEntry(new ArchiveEntry { Name = "d/f", Size = 3 }, "abc"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "d/l", Kind = ArchiveEntryKind.Symlink, LinkTarget = "f" });
        }

        using var reader = new ZipReader(new MemoryStream(ms.ToArray()), new ZipReaderOptions { Password = EncryptionFixtures.Password });
        ZipEntry dir = reader.GetNextEntry()!;
        Assert.Equal(ZipEncryption.None, dir.Encryption);
        ZipEntry file = reader.GetNextEntry()!;
        Assert.Equal(ZipEncryption.Aes256, file.Encryption);
        Assert.Equal(2, file.AesVersion);
        Assert.Equal(ZipMethod.Deflate, file.CompressionMethod);
        ZipEntry link = reader.GetNextEntry()!;
        Assert.Equal(ArchiveEntryKind.Symlink, link.Kind);
        Assert.Equal("f", link.LinkTarget);
    }

    [Fact]
    public void SaltHookMakesOutputReproducibleAndSaltsDiffer()
    {
        Dictionary<string, byte[]> files = EncryptionFixtures.Files();
        ZipWriterOptions Options() => new() { Password = EncryptionFixtures.Password, RandomBytes = EncryptionFixtures.Counter() };
        byte[] a = Write(files, Options(), true);
        byte[] b = Write(files, Options(), true);
        Assert.Equal(a, b);

        byte[] c = Write(files, new ZipWriterOptions { Password = EncryptionFixtures.Password }, true);
        Assert.NotEqual(a, c);
        Assert.Equal(a.Length, c.Length);
    }

    [Fact]
    public void EverySingleByteFlipEndsInATypedError()
    {
        byte[] zip = Write(new Dictionary<string, byte[]> { ["a.txt"] = ZipTestsAccess.Text(300) }, new ZipWriterOptions { Password = EncryptionFixtures.Password, AesStrength = ZipAesStrength.Aes128, RandomBytes = EncryptionFixtures.Counter() }, true);
        int outcomes = 0;
        for (int i = 0; i < zip.Length; i++)
        {
            byte[] bad = (byte[])zip.Clone();
            bad[i] ^= 0x40;
            try
            {
                EncryptionFixtures.ReadZip(bad, EncryptionFixtures.Password);
            }
            catch (Exception ex) when (ex is CompressionException or ArgumentException)
            {
                outcomes++;
            }
        }

        // Flips in the content, salt, verifier, tag and headers must all be noticed; only a few harmless header bytes (time, attributes) may pass.
        Assert.True(outcomes > zip.Length * 7 / 10, $"{outcomes} of {zip.Length} flips detected");
    }

    [Fact]
    public void FlippedCiphertextAndTagFailAuthentication()
    {
        byte[] zip = Write(new Dictionary<string, byte[]> { ["a.txt"] = ZipTestsAccess.Text(500) }, new ZipWriterOptions { Password = EncryptionFixtures.Password, Method = ZipMethod.Store }, true);
        int dataStart = 30 + "a.txt".Length + 11 + 16 + 2; // local header, name, AES field, salt and verifier
        foreach (int at in new[] { dataStart, dataStart + 250, dataStart + 499, dataStart + 500, dataStart + 509 })
        {
            byte[] bad = (byte[])zip.Clone();
            bad[at] ^= 1;
            CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => EncryptionFixtures.ReadZip(bad, EncryptionFixtures.Password));
            Assert.Contains("authentication", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FlippedSaltIsAWrongPassword()
    {
        byte[] zip = Write(new Dictionary<string, byte[]> { ["a.txt"] = ZipTestsAccess.Text(50) }, new ZipWriterOptions { Password = EncryptionFixtures.Password }, true);
        zip[30 + "a.txt".Length + 11] ^= 1;
        ArchivePasswordException ex = Assert.Throws<ArchivePasswordException>(() => EncryptionFixtures.ReadZip(zip, EncryptionFixtures.Password));
        Assert.Equal(ArchivePasswordProblem.Incorrect, ex.Problem);
    }

    [Fact]
    public void TruncatedEncryptedEntriesAreTypedErrors()
    {
        byte[] zip = Write(new Dictionary<string, byte[]> { ["a.txt"] = ZipTestsAccess.Text(400) }, new ZipWriterOptions { Password = EncryptionFixtures.Password }, true);
        for (int len = 0; len < zip.Length; len += 7)
        {
            byte[] cut = zip[..len];
            try
            {
                EncryptionFixtures.ReadZip(cut, EncryptionFixtures.Password, forward: true);
            }
            catch (Exception ex) when (ex is CompressionException or ArgumentException)
            {
            }
        }
    }

    [Fact]
    public void MalformedAesFieldsAreFormatErrors()
    {
        byte[] zip = EncryptionFixtures.Vector("zip-aes256-store.zip");
        // The AES extra field is "01 99 07 00 vv vv 41 45 ss mm mm": damage each part of it in turn.
        int field = IndexOf(zip, [0x01, 0x99, 0x07, 0x00]);
        Assert.True(field > 0);
        foreach ((int offset, byte value) in new (int, byte)[] { (4, 3), (6, (byte)'X'), (8, 0), (8, 9), (9, 99) })
        {
            byte[] bad = (byte[])zip.Clone();
            int count = 0;
            for (int at = IndexOf(zip, [0x01, 0x99, 0x07, 0x00]); at >= 0; at = IndexOf(zip, [0x01, 0x99, 0x07, 0x00], at + 1))
            {
                bad[at + offset] = value;
                count++;
            }

            Assert.True(count >= 2);
            Assert.Throws<CompressionFormatException>(() => EncryptionFixtures.ReadZip(bad, EncryptionFixtures.Password));
        }
    }

    [Fact]
    public void Ae2IgnoresTheCrcFieldButAe1ChecksIt()
    {
        Dictionary<string, byte[]> files = new() { ["a.txt"] = ZipTestsAccess.Text(100) };
        byte[] ae2 = Write(files, new ZipWriterOptions { Password = EncryptionFixtures.Password, Method = ZipMethod.Store }, true);
        byte[] ae1 = Write(files, new ZipWriterOptions { Password = EncryptionFixtures.Password, Method = ZipMethod.Store, AesVersion = ZipAesVersion.Ae1 }, true);
        // Central directory CRC-32 is at offset 16 of its header.
        int Central(byte[] z) => IndexOf(z, [0x50, 0x4B, 0x01, 0x02]);
        int Local(byte[] z) => IndexOf(z, [0x50, 0x4B, 0x03, 0x04]);
        byte[] b2 = (byte[])ae2.Clone();
        b2[Central(b2) + 16] ^= 0xFF;
        b2[Local(b2) + 14] ^= 0xFF;
        EncryptionFixtures.AssertSame(files, EncryptionFixtures.ReadZip(b2, EncryptionFixtures.Password));
        byte[] b1 = (byte[])ae1.Clone();
        b1[Central(b1) + 16] ^= 0xFF;
        b1[Local(b1) + 14] ^= 0xFF;
        Assert.Throws<CompressionFormatException>(() => EncryptionFixtures.ReadZip(b1, EncryptionFixtures.Password));
    }

    [Fact]
    public void ExtractionThroughArchiveUsesThePassword()
    {
        string dir = Path.Combine(Path.GetTempPath(), "enc-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(dir, "out");
        try
        {
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "a.zip");
            File.WriteAllBytes(zip, EncryptionFixtures.Vector("zip-aes256.zip"));
            Assert.Throws<ArchivePasswordException>(() => Archive.Extract(zip, target));
            Archive.Extract(zip, target, new ArchiveExtractOptions { Password = EncryptionFixtures.Password });
            Assert.Equal(EncryptionFixtures.Files()["random.bin"], File.ReadAllBytes(Path.Combine(target, "random.bin")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    internal static int IndexOf(byte[] data, byte[] pattern, int from = 0)
    {
        int i = data.AsSpan(from).IndexOf(pattern);
        return i < 0 ? -1 : i + from;
    }

    internal static byte[] Write(Dictionary<string, byte[]> files, ZipWriterOptions options, bool seekable)
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(seekable ? ms : new WriteOnly(ms), options, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in files)
            {
                w.WriteEntry(new ArchiveEntry { Name = name, Size = data.Length }, data);
            }
        }

        return ms.ToArray();
    }
}

internal static class ZipTestsAccess
{
    public static byte[] Text(int length) => Tests.ZipTests.Text(length, 3);
}

/// <summary>7z: AES-256 read and write, with and without header encryption.</summary>
public class SevenZipEncryptionTests
{
    private static readonly string[] s_vectors = ["7z-aes.7z", "7z-aes-headers.7z", "7z-aes-copy-nonsolid.7z"];

    public static IEnumerable<object[]> Vectors() => s_vectors.Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ReferenceToolVectorsDecode(string name)
    {
        EncryptionFixtures.AssertSame(EncryptionFixtures.Files(), EncryptionFixtures.ReadSevenZip(EncryptionFixtures.Vector(name), EncryptionFixtures.Password));
    }

    [Fact]
    public void EncryptedFilesListWithoutPasswordAndNameTheEntryWhenOpened()
    {
        using var reader = new SevenZipReader(new MemoryStream(EncryptionFixtures.Vector("7z-aes.7z")));
        var names = new List<string>();
        SevenZipEntry? first = null;
        while (reader.GetNextEntry() is { } e)
        {
            names.Add(e.Name);
            first ??= e.Size > 0 ? e : null;
        }

        Assert.Contains("text.txt", names);
        ArchivePasswordException ex = Assert.Throws<ArchivePasswordException>(() => reader.OpenEntryStream(first!));
        Assert.Equal(ArchivePasswordProblem.Required, ex.Problem);
        Assert.Equal(first!.Name, ex.EntryName);
    }

    [Fact]
    public void EncryptedHeaderNeedsThePasswordToList()
    {
        byte[] archive = EncryptionFixtures.Vector("7z-aes-headers.7z");
        using (var reader = new SevenZipReader(new MemoryStream(archive)))
        {
            ArchivePasswordException ex = Assert.Throws<ArchivePasswordException>(() => reader.GetNextEntry());
            Assert.Equal(ArchivePasswordProblem.Required, ex.Problem);
            Assert.Equal(string.Empty, ex.EntryName);
        }

        using var wrong = new SevenZipReader(new MemoryStream(archive), new SevenZipReaderOptions { Password = "wrong-password-2" });
        ArchivePasswordException bad = Assert.Throws<ArchivePasswordException>(() => wrong.GetNextEntry());
        Assert.Equal(ArchivePasswordProblem.Incorrect, bad.Problem);
        Assert.DoesNotContain("wrong-password-2", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongPasswordOnEncryptedDataIsATypedError()
    {
        using var reader = new SevenZipReader(new MemoryStream(EncryptionFixtures.Vector("7z-aes.7z")), new SevenZipReaderOptions { Password = "wrong-password-2" });
        SevenZipEntry? e = reader.GetNextEntry();
        while (e is not null && e.Size == 0)
        {
            e = reader.GetNextEntry();
        }

        ArchivePasswordException ex = Assert.Throws<ArchivePasswordException>(() =>
        {
            using Stream s = reader.OpenEntryStream();
            s.CopyTo(Stream.Null);
        });
        Assert.Equal(ArchivePasswordProblem.Incorrect, ex.Problem);
        Assert.Equal(e!.Name, ex.EntryName);
    }

    [Theory]
    [InlineData(SevenZipMethod.Lzma2, true, true, true)]
    [InlineData(SevenZipMethod.Lzma2, true, false, true)]
    [InlineData(SevenZipMethod.Lzma, false, true, true)]
    [InlineData(SevenZipMethod.Copy, true, true, true)]
    [InlineData(SevenZipMethod.Deflate, true, false, false)]
    [InlineData(SevenZipMethod.Copy, false, true, false)]
    public void RoundTripsThroughOurReaderAndWriter(SevenZipMethod method, bool solid, bool encryptHeader, bool filter)
    {
        Dictionary<string, byte[]> files = EncryptionFixtures.Files();
        files["big.txt"] = ZipTestsAccess.Text(150_000);
        byte[] archive = Write(files, new SevenZipWriterOptions
        {
            Method = method,
            Level = method == SevenZipMethod.Copy ? 5 : 3,
            Solid = solid,
            Password = EncryptionFixtures.Password,
            EncryptHeader = encryptHeader,
            Filter = filter ? SevenZipFilter.Delta : SevenZipFilter.None,
            KeyDerivationPower = 10,
        });
        EncryptionFixtures.AssertSame(files, EncryptionFixtures.ReadSevenZip(archive, EncryptionFixtures.Password));

        using var reader = new SevenZipReader(new MemoryStream(archive), new SevenZipReaderOptions { Password = encryptHeader ? EncryptionFixtures.Password : null });
        Assert.Contains(reader.Folders, f => f.Coders.Any(c => c.MethodId == 0x06F10701));
        Assert.True(reader.IsHeaderEncoded);
        if (!encryptHeader)
        {
            // Listing works without a password; the data does not.
            using var plain = new SevenZipReader(new MemoryStream(archive));
            Assert.NotNull(plain.GetNextEntry());
        }
    }

    [Fact]
    public void IvHookMakesOutputReproducible()
    {
        Dictionary<string, byte[]> files = EncryptionFixtures.Files();
        SevenZipWriterOptions Options() => new() { Password = EncryptionFixtures.Password, KeyDerivationPower = 8, RandomBytes = EncryptionFixtures.Counter() };
        Assert.Equal(Write(files, Options()), Write(files, Options()));
        Assert.NotEqual(Write(files, Options()), Write(files, new SevenZipWriterOptions { Password = EncryptionFixtures.Password, KeyDerivationPower = 8 }));
    }

    [Fact]
    public void KeyDerivationCostIsLimited()
    {
        byte[] archive = Write(EncryptionFixtures.Files(), new SevenZipWriterOptions { Password = EncryptionFixtures.Password, KeyDerivationPower = 12, EncryptHeader = false });
        using var reader = new SevenZipReader(new MemoryStream(archive), new SevenZipReaderOptions { Password = EncryptionFixtures.Password, MaxKeyDerivationPower = 11 });
        SevenZipEntry e = reader.GetNextEntry()!;
        while (e.Size == 0)
        {
            e = reader.GetNextEntry()!;
        }

        Assert.Throws<CompressionLimitException>(() => reader.OpenEntryStream().CopyTo(Stream.Null));
    }

    [Fact]
    public void BitFlipsEndInTypedErrors()
    {
        byte[] archive = Write(new Dictionary<string, byte[]> { ["a.txt"] = ZipTestsAccess.Text(400) }, new SevenZipWriterOptions { Password = EncryptionFixtures.Password, KeyDerivationPower = 6, RandomBytes = EncryptionFixtures.Counter() });
        int detected = 0;
        for (int i = 0; i < archive.Length; i++)
        {
            byte[] bad = (byte[])archive.Clone();
            bad[i] ^= 0x21;
            try
            {
                EncryptionFixtures.ReadSevenZip(bad, EncryptionFixtures.Password);
            }
            catch (Exception ex) when (ex is CompressionException or ArgumentException)
            {
                detected++;
            }
        }

        Assert.True(detected > archive.Length * 8 / 10, $"{detected} of {archive.Length}");
    }

    [Theory]
    [InlineData("a.7z")]
    [InlineData("a.zip")]
    public void HappyPathCreatesAndExtractsEncryptedArchives(string fileName)
    {
        string dir = Path.Combine(Path.GetTempPath(), "enc7-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "in"));
            File.WriteAllBytes(Path.Combine(dir, "in", "t.txt"), ZipTestsAccess.Text(5000));
            string path = Path.Combine(dir, fileName);
            Archive.Create(path, new ArchiveCreateOptions { Password = EncryptionFixtures.Password }).Add(Path.Combine(dir, "in", "t.txt"), "t.txt").Save();
            Assert.Throws<ArchivePasswordException>(() => Archive.Extract(path, Path.Combine(dir, "bad")));
            Archive.Extract(path, Path.Combine(dir, "out"), new ArchiveExtractOptions { Password = EncryptionFixtures.Password });
            Assert.Equal(ZipTestsAccess.Text(5000), File.ReadAllBytes(Path.Combine(dir, "out", "t.txt")));
            Assert.Throws<ArgumentException>(() => Archive.Create(Path.Combine(dir, "x.tar"), new ArchiveCreateOptions { Password = "x" }).Add(Path.Combine(dir, "in", "t.txt"), "t.txt").Save());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    internal static byte[] Write(Dictionary<string, byte[]> files, SevenZipWriterOptions options)
    {
        var ms = new MemoryStream();
        using (var w = new SevenZipWriter(ms, options, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in files)
            {
                w.WriteEntry(new ArchiveEntry { Name = name, Size = data.Length }, data);
            }
        }

        return ms.ToArray();
    }
}

/// <summary>Our archives against 7-Zip and Info-ZIP, run as black boxes. Set ONEARCHIVE_INTEROP=1 and ONEARCHIVE_7Z (default 7zz).</summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class EncryptionInteropTests
{
    private static string SevenZ => Environment.GetEnvironmentVariable("ONEARCHIVE_7Z") ?? "7zz";

    private static void Enabled() => Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");

    private static string Temp(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "enci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData(ZipAesStrength.Aes128, ZipAesVersion.Ae2, ZipMethod.Deflate)]
    [InlineData(ZipAesStrength.Aes192, ZipAesVersion.Ae2, ZipMethod.Deflate)]
    [InlineData(ZipAesStrength.Aes256, ZipAesVersion.Ae2, ZipMethod.Deflate)]
    [InlineData(ZipAesStrength.Aes256, ZipAesVersion.Ae1, ZipMethod.Deflate)]
    [InlineData(ZipAesStrength.Aes128, ZipAesVersion.Ae1, ZipMethod.Store)]
    [InlineData(ZipAesStrength.Aes256, ZipAesVersion.Ae2, ZipMethod.BZip2)]
    public void SevenZipTestsAndExtractsOurAesZip(ZipAesStrength strength, ZipAesVersion version, ZipMethod method)
    {
        Enabled();
        Temp(out string dir);
        try
        {
            Dictionary<string, byte[]> files = EncryptionFixtures.Files();
            files["big.txt"] = ZipTestsAccess.Text(300_000);
            foreach (bool seekable in new[] { true, false })
            {
                string zip = Path.Combine(dir, "ours.zip");
                File.WriteAllBytes(zip, ZipEncryptionTests.Write(files, new ZipWriterOptions { Method = method, Password = EncryptionFixtures.Password, AesStrength = strength, AesVersion = version }, seekable));
                Tools.Run(SevenZ, ["t", "-p" + EncryptionFixtures.Password, zip]);
                string outDir = Path.Combine(dir, "out" + seekable);
                Tools.Run(SevenZ, ["x", "-y", "-p" + EncryptionFixtures.Password, "-o" + outDir, zip]);
                foreach ((string name, byte[] data) in files)
                {
                    Assert.Equal(data, File.ReadAllBytes(Path.Combine(outDir, name)));
                }

                Assert.Throws<InvalidOperationException>(() => Tools.Run(SevenZ, ["t", "-pwrong-password-3", zip]));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(SevenZipMethod.Lzma2, true)]
    [InlineData(SevenZipMethod.Lzma2, false)]
    [InlineData(SevenZipMethod.Copy, true)]
    [InlineData(SevenZipMethod.Deflate, false)]
    public void SevenZipTestsAndExtractsOurAes7z(SevenZipMethod method, bool encryptHeader)
    {
        Enabled();
        Temp(out string dir);
        try
        {
            Dictionary<string, byte[]> files = EncryptionFixtures.Files();
            files["big.txt"] = ZipTestsAccess.Text(300_000);
            string archive = Path.Combine(dir, "ours.7z");
            File.WriteAllBytes(archive, SevenZipEncryptionTests.Write(files, new SevenZipWriterOptions { Method = method, Level = 3, Password = EncryptionFixtures.Password, EncryptHeader = encryptHeader, Filter = SevenZipFilter.Delta }));
            Tools.Run(SevenZ, ["t", "-p" + EncryptionFixtures.Password, archive]);
            string outDir = Path.Combine(dir, "out");
            Tools.Run(SevenZ, ["x", "-y", "-p" + EncryptionFixtures.Password, "-o" + outDir, archive]);
            foreach ((string name, byte[] data) in files)
            {
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(outDir, name)));
            }

            Assert.Throws<InvalidOperationException>(() => Tools.Run(SevenZ, ["t", "-pwrong-password-3", archive]));
            if (encryptHeader)
            {
                // Without the password even the listing fails: the names are encrypted.
                Assert.Throws<InvalidOperationException>(() => Tools.Run(SevenZ, ["l", "-p", archive]));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void OurReaderDecodesFreshlyMadeToolArchives()
    {
        Enabled();
        Temp(out string dir);
        try
        {
            Dictionary<string, byte[]> files = EncryptionFixtures.Files();
            files["big.txt"] = ZipTestsAccess.Text(300_000);
            foreach ((string name, byte[] data) in files)
            {
                File.WriteAllBytes(Path.Combine(dir, name), data);
            }

            string[] names = [.. files.Keys.Order()];
            foreach ((string archive, string[] args) in new (string, string[])[]
            {
                ("a.zip", ["-tzip", "-mem=AES256"]),
                ("b.zip", ["-tzip", "-mem=AES128", "-mm=Copy"]),
                ("c.zip", ["-tzip", "-mem=ZipCrypto"]),
                ("a.7z", ["-t7z"]),
                ("b.7z", ["-t7z", "-mhe=on", "-mx9"]),
                ("c.7z", ["-t7z", "-mhe=on", "-ms=off", "-m0=Copy"]),
            })
            {
                Tools.Run(SevenZ, ["a", "-bd", "-p" + EncryptionFixtures.Password, .. args, archive, .. names], dir);
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, archive));
                Dictionary<string, byte[]> got = archive.EndsWith(".zip", StringComparison.Ordinal)
                    ? EncryptionFixtures.ReadZip(bytes, EncryptionFixtures.Password)
                    : EncryptionFixtures.ReadSevenZip(bytes, EncryptionFixtures.Password);
                EncryptionFixtures.AssertSame(files, got);
            }

            // Info-ZIP's traditional encryption, files written to disk and to a pipe.
            Tools.Run("zip", ["-q", "-X", "-P", EncryptionFixtures.Password, "iz.zip", .. names], dir);
            EncryptionFixtures.AssertSame(files, EncryptionFixtures.ReadZip(File.ReadAllBytes(Path.Combine(dir, "iz.zip")), EncryptionFixtures.Password));
            Tools.Run("bash", ["-c", $"zip -q -X -P {EncryptionFixtures.Password} - {string.Join(' ', names)} > izpipe.zip"], dir);
            EncryptionFixtures.AssertSame(files, EncryptionFixtures.ReadZip(File.ReadAllBytes(Path.Combine(dir, "izpipe.zip")), EncryptionFixtures.Password));
            Tools.Run("unzip", ["-tqq", "-P", EncryptionFixtures.Password, "iz.zip"], dir);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NonAsciiPasswordsAgree()
    {
        Enabled();
        Temp(out string dir);
        try
        {
            const string pw = "p\u00e4ssw\u00f6rd-\u65e5\u672c-\ud83d\ude00";
            string z = Path.Combine(dir, "u.zip");
            File.WriteAllBytes(z, ZipEncryptionTests.Write(EncryptionFixtures.Files(), new ZipWriterOptions { Password = pw }, true));
            Tools.Run(SevenZ, ["t", "-p" + pw, z]);
            string s = Path.Combine(dir, "u.7z");
            File.WriteAllBytes(s, SevenZipEncryptionTests.Write(EncryptionFixtures.Files(), new SevenZipWriterOptions { Password = pw, KeyDerivationPower = 10 }));
            Tools.Run(SevenZ, ["t", "-p" + pw, s]);

            // 7-Zip refuses a non-ASCII password when it creates a zip (it does test ours above, so it reads zip passwords as
            // UTF-8), and takes only basic-plane characters on its command line for 7z.
            const string bmp = "pässwörd-日本";
            File.WriteAllText(Path.Combine(dir, "x.txt"), "x");
            Tools.Run(SevenZ, ["a", "-bd", "-t7z", "-p" + bmp, "t.7z", "x.txt"], dir);
            EncryptionFixtures.AssertSame(new Dictionary<string, byte[]> { ["x.txt"] = "x"u8.ToArray() }, EncryptionFixtures.ReadSevenZip(File.ReadAllBytes(Path.Combine(dir, "t.7z")), bmp));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
