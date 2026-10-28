using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The zip writer over an entry mix (directory, file, empty file, symbolic link, Unicode name, sub-second times) with store and
/// zstd, local headers patched or data descriptors, zip64 forced or automatic; the reader's view of one of them, forward-only
/// and through the directory; compressed single files written through <c>Archive</c>; and gzip read from hand-made members
/// (stored blocks, so no encoder is involved). Nothing here depends on the platform.
/// </summary>
internal static class ZipEntries
{
    private static readonly DateTimeOffset s_base = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [ModuleInitializer]
    internal static void Register()
    {
        foreach (ZipMethod method in new[] { ZipMethod.Store, ZipMethod.Deflate, ZipMethod.Zstd })
        {
            ZipMethod m = method;
            string n = m.ToString().ToLowerInvariant();
            Matrix.Add("zip-write", n + "-seekable", (input, _) => Write(input, new ZipWriterOptions { Method = m }, seekable: true));
            Matrix.Add("zip-write", n + "-descriptors", (input, _) => Write(input, new ZipWriterOptions { Method = m }, seekable: false));
            Matrix.Add("zip-write", n + "-descriptors-seekable", (input, _) => Write(input, new ZipWriterOptions { Method = m, DataDescriptors = ZipDataDescriptorMode.Always }, seekable: true));
            Matrix.Add("zip-write", n + "-zip64", (input, _) => Write(input, new ZipWriterOptions { Method = m, Zip64 = ZipZip64Mode.Always }, seekable: true));
            Matrix.Add("zip-write", n + "-zip64-descriptors", (input, _) => Write(input, new ZipWriterOptions { Method = m, Zip64 = ZipZip64Mode.Always }, seekable: false));
        }

        Matrix.Add("zip-write", "zstd-level9-comment", (input, _) => Write(input, new ZipWriterOptions { Method = ZipMethod.Zstd, Level = 9, Comment = "matrix" }, seekable: true));
        Matrix.Add("zip-write", "deflate-level1", (input, _) => Write(input, new ZipWriterOptions { Method = ZipMethod.Deflate, Level = 1 }, seekable: true));
        Matrix.Add("zip-write", "deflate-level12-comment", (input, _) => Write(input, new ZipWriterOptions { Method = ZipMethod.Deflate, Level = 12, Comment = "matrix" }, seekable: true));
        Matrix.Add("zip-write", "many-empty-entries", (_, _) => ManyEntries(), "empty");
        Matrix.Add("zip-read", "store-summary", (input, _) => Summary(input));
        Matrix.Add("zip-single-stream", "zst", (input, _) => Single(input, ArchiveCompression.Zstd, null));
        Matrix.Add("zip-single-stream", "zst-level9", (input, _) => Single(input, ArchiveCompression.Zstd, 9));
        Matrix.Add("zip-single-stream", "lz4", (input, _) => Single(input, ArchiveCompression.Lz4, null));
        Matrix.Add("zip-single-stream", "gz", (input, _) => Single(input, ArchiveCompression.Gzip, null));
        Matrix.Add("zip-single-stream", "gz-level1", (input, _) => Single(input, ArchiveCompression.Gzip, 1));
        Matrix.Add("zip-single-stream", "gz-level9", (input, _) => Single(input, ArchiveCompression.Gzip, 9));
        Matrix.Add("zip-single-stream", "tar-gz", (input, _) => TarGz(input));
        Matrix.Add("zip-single-stream", "gzip-read", (input, _) => GzipRead(input));
    }

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

    private sealed class Forward(byte[] data) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, 997), data.Length - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void Mix(ZipWriter w, byte[] content)
    {
        w.WriteEntry(new ArchiveEntry { Name = "root", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)0b111_101_101, ModifiedTime = s_base }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/data.bin", Size = content.Length, Mode = (UnixFileMode)0b110_100_100, ModifiedTime = s_base.AddSeconds(1) }, content);
        w.WriteEntry(new ArchiveEntry { Name = "root/empty", Size = 0, ModifiedTime = s_base.AddSeconds(2) }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "data.bin", ModifiedTime = s_base.AddSeconds(3) }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "root/ünïcödé-日本語.txt", Size = 2, ModifiedTime = s_base.AddTicks(1234567), AccessTime = s_base.AddSeconds(10), ChangeTime = s_base.AddSeconds(20) }, "hi"u8.ToArray());
        w.WriteEntry(new ArchiveEntry { Name = "root/text.txt", Size = 5, ModifiedTime = new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero) }, "hello"u8.ToArray());
        w.WriteEntry(new ArchiveEntry { Name = "root/old.txt", Size = 1, ModifiedTime = new DateTimeOffset(1975, 1, 1, 0, 0, 0, TimeSpan.Zero) }, new byte[] { 1 });
        w.WriteEntry(new ArchiveEntry { Name = "root/x-" + new string('n', 200), Size = 1 }, new byte[] { 7 });
    }

    private static byte[] Write(ReadOnlySpan<byte> content, ZipWriterOptions options, bool seekable)
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(seekable ? ms : new NoSeek(ms), options, leaveOpen: true))
        {
            Mix(w, content.ToArray());
        }

        return ms.ToArray();
    }

    private static byte[] ManyEntries()
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, new ZipWriterOptions { Method = ZipMethod.Store }, leaveOpen: true))
        {
            for (int i = 0; i < 66_000; i++)
            {
                w.WriteEntry(new ArchiveEntry { Name = "n/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) }, (Stream?)null);
            }
        }

        return ms.ToArray();
    }

    private static byte[] Summary(ReadOnlySpan<byte> content)
    {
        byte[] zip = Write(content, new ZipWriterOptions { Method = ZipMethod.Store, Comment = "c" }, seekable: true);
        var sb = new StringBuilder();
        Describe(sb, new MemoryStream(zip));
        sb.Append("--\n");
        Describe(sb, new Forward(zip));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Describe(StringBuilder sb, Stream source)
    {
        using var reader = new ZipReader(source);
        byte[] buffer = new byte[8192];
        while (reader.GetNextEntry() is { } e)
        {
            sb.Append(e.Name).Append('|').Append(e.Kind).Append('|').Append(e.Size).Append('|').Append(e.Method).Append('|').Append(e.Mode).Append('|')
                .Append(e.ModifiedTime?.UtcTicks).Append('|').Append(e.AccessTime?.UtcTicks).Append('|').Append(e.LinkTarget).Append('|').Append(e.Crc32).Append('|').Append(e.LocalHeaderOffset).Append('|');
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using Stream s = reader.OpenEntryStream();
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, n);
            }

            sb.Append(Convert.ToHexString(hash.GetHashAndReset())).Append('\n');
        }
    }

    private static byte[] Single(ReadOnlySpan<byte> content, ArchiveCompression compression, int? level)
    {
        var ms = new MemoryStream();
        Archive.Create(ms, new ArchiveCreateOptions { Format = ArchiveFormat.SingleStream, Compression = compression, CompressionLevel = level, LeaveOpen = true })
            .Add(new ArchiveEntry { Name = "data.bin", Size = content.Length }, content.ToArray())
            .Save();
        return ms.ToArray();
    }

    private static byte[] TarGz(ReadOnlySpan<byte> content)
    {
        var ms = new MemoryStream();
        Archive.Create(ms, new ArchiveCreateOptions { Format = ArchiveFormat.Tar, Compression = ArchiveCompression.Gzip, LeaveOpen = true })
            .Add(new ArchiveEntry { Name = "data.bin", Size = content.Length }, content.ToArray())
            .Save();
        return ms.ToArray();
    }

    // Two gzip members made of stored blocks (RFC 1951 3.2.4) with header names and times, read back through Archive.
    private static byte[] GzipRead(ReadOnlySpan<byte> content)
    {
        var gz = new MemoryStream();
        Member(gz, content.ToArray(), "first.bin", 1_700_000_000);
        Member(gz, "tail"u8.ToArray(), null, 0);
        using Archive archive = Archive.Open(new MemoryStream(gz.ToArray()), new ArchiveOpenOptions { FileName = "x.gz" });
        var e = Assert(archive.Entries);
        using Stream s = archive.OpenEntry(e);
        var outp = new MemoryStream();
        outp.Write(Encoding.UTF8.GetBytes(e.Name + "|" + e.ModifiedTime?.UtcTicks + "|"));
        s.CopyTo(outp);
        return outp.ToArray();

        static ArchiveEntry Assert(IReadOnlyList<ArchiveEntry> entries) => entries.Count == 1 ? entries[0] : throw new InvalidOperationException("expected one entry");
    }

    private static void Member(Stream to, byte[] data, string? name, uint mtime)
    {
        to.Write([0x1F, 0x8B, 8, (byte)(name is null ? 0 : 8)]);
        Span<byte> t = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(t, mtime);
        to.Write(t);
        to.Write([0, 255]);
        if (name is not null)
        {
            to.Write(Encoding.Latin1.GetBytes(name));
            to.WriteByte(0);
        }

        int at = 0;
        Span<byte> len = stackalloc byte[4];
        do
        {
            int n = Math.Min(65535, data.Length - at);
            bool last = at + n >= data.Length;
            to.WriteByte((byte)(last ? 1 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)n);
            BinaryPrimitives.WriteUInt16LittleEndian(len[2..], (ushort)~n);
            to.Write(len);
            to.Write(data.AsSpan(at, n));
            at += n;
        }
        while (at < data.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(t, RecursiveAI.OneCompress.Checksums.Crc32.Compute(data));
        to.Write(t);
        BinaryPrimitives.WriteUInt32LittleEndian(t, (uint)data.Length);
        to.Write(t);
    }
}
