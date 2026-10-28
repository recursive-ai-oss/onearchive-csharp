using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.Fuzz;

/// <summary>
/// <c>fuzz seeds-encrypted DIR</c>: writes small encrypted archives (password <c>fuzz-password</c>, key derivation kept cheap)
/// into DIR/zip-read-encrypted and DIR/sevenzip-read-encrypted, and a few byte strings into the two round-trip targets' folders.
/// </summary>
internal static class EncryptedSeeds
{
    public static void Write(string dir)
    {
        string zipDir = Directory.CreateDirectory(Path.Combine(dir, "zip-read-encrypted")).FullName;
        string szDir = Directory.CreateDirectory(Path.Combine(dir, "sevenzip-read-encrypted")).FullName;
        byte[] text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("seed text for the encrypted readers. ", 30)));
        byte[] noise = new byte[300];
        new Random(3).NextBytes(noise);
        var files = new (string Name, byte[] Data)[] { ("a.txt", text), ("b.bin", noise), ("empty", []) };

        int i = 0;
        foreach (ZipAesStrength strength in new[] { ZipAesStrength.Aes128, ZipAesStrength.Aes192, ZipAesStrength.Aes256 })
        {
            foreach (ZipMethod method in new[] { ZipMethod.Store, ZipMethod.Deflate })
            {
                var options = new ZipWriterOptions
                {
                    Method = method,
                    Password = ZipTargets.Password,
                    AesStrength = strength,
                    AesVersion = i % 2 == 0 ? ZipAesVersion.Ae2 : ZipAesVersion.Ae1,
                    RandomBytes = static s => s.Fill(0x5A),
                };
                var ms = new MemoryStream();
                using (var w = new ZipWriter(ms, options, leaveOpen: true))
                {
                    foreach ((string name, byte[] data) in files)
                    {
                        w.WriteEntry(new ArchiveEntry { Name = name, Size = data.Length }, data);
                    }
                }

                File.WriteAllBytes(Path.Combine(zipDir, $"aes-{i++}.zip"), ms.ToArray());
            }
        }

        i = 0;
        foreach (bool headers in new[] { false, true })
        {
            foreach (SevenZipMethod method in new[] { SevenZipMethod.Copy, SevenZipMethod.Lzma2 })
            {
                var ms = new MemoryStream();
                using (var w = new SevenZipWriter(ms, new SevenZipWriterOptions
                {
                    Method = method,
                    Level = 3,
                    Password = SevenZipTargets.Password,
                    EncryptHeader = headers,
                    KeyDerivationPower = 6,
                    RandomBytes = static s => s.Fill(0xA5),
                }, leaveOpen: true))
                {
                    foreach ((string name, byte[] data) in files)
                    {
                        w.WriteEntry(new ArchiveEntry { Name = name, Size = data.Length }, data);
                    }
                }

                File.WriteAllBytes(Path.Combine(szDir, $"aes-{i++}.7z"), ms.ToArray());
            }
        }

        foreach (string target in new[] { "zip-roundtrip-encrypted", "sevenzip-roundtrip-encrypted" })
        {
            string d = Directory.CreateDirectory(Path.Combine(dir, target)).FullName;
            for (int k = 0; k < 6; k++)
            {
                byte[] seed = new byte[40 + (k * 30)];
                new Random(k + 11).NextBytes(seed);
                File.WriteAllBytes(Path.Combine(d, $"s{k}"), seed);
            }
        }
    }
}
