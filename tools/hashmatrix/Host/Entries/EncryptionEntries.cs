using System.Runtime.CompilerServices;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Encrypted zip (WinZip AES, every key size, both versions) and 7z (AES-256, with and without an encrypted header) archives
/// written with the salt and IV hook, so the bytes are reproducible; and the same archives read back, whose output is the
/// decrypted content. The BCL's AES, HMAC-SHA1, PBKDF2 and SHA-256 may run at different speeds on different platforms but
/// must produce the same bytes.
/// </summary>
internal static class EncryptionEntries
{
    private const string Password = "matrix-password";
    private static readonly DateTimeOffset s_base = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [ModuleInitializer]
    internal static void Register()
    {
        RegisterPrimitives();
        foreach (ZipAesStrength strength in new[] { ZipAesStrength.Aes128, ZipAesStrength.Aes192, ZipAesStrength.Aes256 })
        {
            ZipAesStrength s = strength;
            string n = s.ToString().ToLowerInvariant();
            Matrix.Add("zip-encrypt", n + "-ae2-deflate", (input, _) => WriteZip(input, ZipOptions(s, ZipAesVersion.Ae2, ZipMethod.Deflate), seekable: true));
            Matrix.Add("zip-encrypt", n + "-ae1-store", (input, _) => WriteZip(input, ZipOptions(s, ZipAesVersion.Ae1, ZipMethod.Store), seekable: true));
        }

        Matrix.Add("zip-encrypt", "aes256-ae2-deflate-descriptors", (input, _) => WriteZip(input, ZipOptions(ZipAesStrength.Aes256, ZipAesVersion.Ae2, ZipMethod.Deflate), seekable: false));
        Matrix.Add("zip-encrypt", "aes256-ae2-zstd-zip64", (input, _) => WriteZip(input, ZipOptions(ZipAesStrength.Aes256, ZipAesVersion.Ae2, ZipMethod.Zstd, ZipZip64Mode.Always), seekable: true));
        Matrix.Add("zip-encrypt", "read-aes192-ae1", (input, _) => ReadZip(WriteZip(input, ZipOptions(ZipAesStrength.Aes192, ZipAesVersion.Ae1, ZipMethod.Deflate), seekable: true)));
        Matrix.Add("zip-encrypt", "read-aes256-ae2", (input, _) => ReadZip(WriteZip(input, ZipOptions(ZipAesStrength.Aes256, ZipAesVersion.Ae2, ZipMethod.Store), seekable: true)));

        foreach (SevenZipMethod method in new[] { SevenZipMethod.Lzma2, SevenZipMethod.Copy })
        {
            SevenZipMethod m = method;
            string n = m.ToString().ToLowerInvariant();
            Matrix.Add("sevenzip-encrypt", n + "-solid-headers", (input, _) => WriteSevenZip(input, SevenZipOptions(m, headers: true, solid: true)));
            Matrix.Add("sevenzip-encrypt", n + "-nonsolid-plain-header", (input, _) => WriteSevenZip(input, SevenZipOptions(m, headers: false, solid: false)));
        }

        Matrix.Add("sevenzip-encrypt", "delta-lzma2-headers", (input, _) => WriteSevenZip(input, new SevenZipWriterOptions { Password = Password, KeyDerivationPower = 10, RandomBytes = Fill, Filter = SevenZipFilter.Delta, DeltaDistance = 4 }));
        Matrix.Add("sevenzip-encrypt", "read-lzma2-headers", (input, _) => ReadSevenZip(WriteSevenZip(input, SevenZipOptions(SevenZipMethod.Lzma2, headers: true, solid: true))));
        Matrix.Add("sevenzip-encrypt", "read-copy-plain-header", (input, _) => ReadSevenZip(WriteSevenZip(input, SevenZipOptions(SevenZipMethod.Copy, headers: false, solid: false))));
    }

    // One entry per BCL primitive the encryption uses, each on its own: on a platform that lacks one, the entry's digest is
    // ERROR:PlatformNotSupportedException and says which.
    //
    // The two Aes.Create calls are annotated as unsupported on the browser; the entries exist to show what happens there, so
    // the platform-compatibility warning is silenced for this method only.
#pragma warning disable CA1416
    private static void RegisterPrimitives()
    {
        byte[] key = new byte[32];
        Matrix.Add("encrypt-primitive", "aes-ecb", (_, _) => { using var a = System.Security.Cryptography.Aes.Create(); a.SetKey(key); return a.EncryptEcb(new byte[16], System.Security.Cryptography.PaddingMode.None); }, "empty");
        Matrix.Add("encrypt-primitive", "aes-cbc", (_, _) => { using var a = System.Security.Cryptography.Aes.Create(); a.SetKey(key); return a.EncryptCbc(new byte[32], new byte[16], System.Security.Cryptography.PaddingMode.None); }, "empty");
        Matrix.Add("encrypt-primitive", "sha256", (_, _) => System.Security.Cryptography.SHA256.HashData(key), "empty");
        Matrix.Add("encrypt-primitive", "incremental-sha256", (_, _) => { using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256); h.AppendData(key); return h.GetHashAndReset(); }, "empty");
        Matrix.Add("encrypt-primitive", "incremental-hmac-sha1", (_, _) => { using var h = System.Security.Cryptography.IncrementalHash.CreateHMAC(System.Security.Cryptography.HashAlgorithmName.SHA1, key); h.AppendData(key); return h.GetHashAndReset(); }, "empty");
        Matrix.Add("encrypt-primitive", "pbkdf2-sha1", (_, _) => System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(key, key, 1000, System.Security.Cryptography.HashAlgorithmName.SHA1, 34), "empty");
        Matrix.Add("encrypt-primitive", "random-fill", (_, _) => { byte[] b = new byte[16]; System.Security.Cryptography.RandomNumberGenerator.Fill(b); return new byte[1]; }, "empty");
    }

#pragma warning restore CA1416

    // Each call makes a fresh generator, so two writes in a run give the same bytes.
    private static void Fill(Span<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = (byte)(0x30 + (i * 7));
        }
    }

    private static ZipWriterOptions ZipOptions(ZipAesStrength strength, ZipAesVersion version, ZipMethod method, ZipZip64Mode zip64 = ZipZip64Mode.Auto) =>
        new() { Method = method, Zip64 = zip64, Password = Password, AesStrength = strength, AesVersion = version, RandomBytes = Fill };

    private static SevenZipWriterOptions SevenZipOptions(SevenZipMethod method, bool headers, bool solid) =>
        new() { Method = method, Solid = solid, Password = Password, EncryptHeader = headers, KeyDerivationPower = 10, RandomBytes = Fill };

    private sealed class NoSeek(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    private static void Entries(ArchiveWriter w, byte[] data)
    {
        w.WriteEntry(new ArchiveEntry { Name = "root", Kind = ArchiveEntryKind.Directory, ModifiedTime = s_base }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/data.bin", Size = data.Length, ModifiedTime = s_base.AddSeconds(1) }, data);
        w.WriteEntry(new ArchiveEntry { Name = "root/empty", Size = 0, ModifiedTime = s_base.AddSeconds(2) }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "data.bin", ModifiedTime = s_base.AddSeconds(3) }, (Stream?)null);
    }

    private static byte[] WriteZip(ReadOnlySpan<byte> content, ZipWriterOptions options, bool seekable)
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(seekable ? ms : new NoSeek(ms), options, leaveOpen: true))
        {
            Entries(w, content.ToArray());
        }

        return ms.ToArray();
    }

    private static byte[] WriteSevenZip(ReadOnlySpan<byte> content, SevenZipWriterOptions options)
    {
        var ms = new MemoryStream();
        using (var w = new SevenZipWriter(ms, options, leaveOpen: true))
        {
            Entries(w, content.ToArray());
        }

        return ms.ToArray();
    }

    private static byte[] ReadZip(byte[] zip)
    {
        using var reader = new ZipReader(new MemoryStream(zip), new ZipReaderOptions { Password = Password });
        return Content(reader);
    }

    private static byte[] ReadSevenZip(byte[] archive)
    {
        using var reader = new SevenZipReader(new MemoryStream(archive), new SevenZipReaderOptions { Password = Password });
        return Content(reader);
    }

    // The data file's decrypted content followed by the link target.
    private static byte[] Content(ArchiveReader reader)
    {
        var result = new MemoryStream();
        while (reader.GetNextEntry() is { } e)
        {
            if (e.Kind == ArchiveEntryKind.File && e.Size > 0)
            {
                using Stream s = reader.OpenEntryStream();
                s.CopyTo(result);
            }
            else if (e.Kind == ArchiveEntryKind.Symlink)
            {
                result.Write(System.Text.Encoding.UTF8.GetBytes(e.LinkTarget ?? string.Empty));
            }
        }

        return result.ToArray();
    }
}
