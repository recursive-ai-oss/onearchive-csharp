using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.SevenZip;

namespace RecursiveAI.Fuzz;

/// <summary>
/// <c>sevenzip-roundtrip</c>: entries made from the input are written with an option mix (method, level, solid limits, filter,
/// header encoding, seekable or not output) and read back through the reader unchanged, with a second write giving the same bytes.
/// </summary>
internal static class SevenZipWriterTargets
{
    public static FuzzTarget RoundTrip { get; } = new("sevenzip-roundtrip", static data => Check(data), static _ => false);

    private sealed class NoSeekWrite(Stream inner) : Stream
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

    private static void Check(ReadOnlySpan<byte> input)
    {
        if (input.Length < 4)
        {
            return;
        }

        byte o = input[0];
        var options = new SevenZipWriterOptions
        {
            Method = (o & 7) switch { 5 => SevenZipMethod.Deflate64, 1 => SevenZipMethod.Lzma, 2 => SevenZipMethod.Copy, 3 => SevenZipMethod.Deflate, 4 => SevenZipMethod.Zstd, _ => SevenZipMethod.Lzma2 },
            Level = 1 + (input[1] % 9),
            Solid = (o & 8) == 0,
            SolidFileCount = (o & 16) != 0 ? 2 : 0,
            SolidBlockSize = (o & 32) != 0 ? 1000 : SevenZipWriterOptions.DefaultSolidBlockSize,
            EncodeHeader = (o & 64) == 0,
            Filter = (SevenZipFilter)(input[2] % 11),
            DeltaDistance = 1 + (input[2] % 7),
        };
        bool seekable = (o & 128) == 0;
        int count = 1 + (input[3] % 6);
        byte[] body = input[4..].ToArray();
        byte[] first = Write(options, body, count, seekable, out List<(string Name, ArchiveEntryKind Kind, byte[] Content)> expected);
        byte[] second = Write(options, body, count, seekable, out _);
        if (!first.AsSpan().SequenceEqual(second))
        {
            throw new InvalidOperationException("two writes of the same entries differ");
        }

        using var reader = new SevenZipReader(new MemoryStream(first));
        if (reader.Items.Count != expected.Count)
        {
            throw new InvalidOperationException($"{reader.Items.Count} entries read, {expected.Count} written");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            SevenZipEntry e = reader.Items[i];
            (string name, ArchiveEntryKind kind, byte[] content) = expected[i];
            if (e.Name != name || e.Kind != kind)
            {
                throw new InvalidOperationException($"entry {i} is '{e.Name}' {e.Kind}, expected '{name}' {kind}");
            }

            if (kind == ArchiveEntryKind.Directory)
            {
                continue;
            }

            if (kind == ArchiveEntryKind.Symlink)
            {
                // The reader reports a link's target as LinkTarget; its content is that same text.
                if (e.LinkTarget != Encoding.UTF8.GetString(content))
                {
                    throw new InvalidOperationException($"link '{name}' reads back target '{e.LinkTarget}'");
                }

                continue;
            }

            using Stream s = reader.OpenEntryStream(e);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            if (!ms.ToArray().AsSpan().SequenceEqual(content))
            {
                throw new InvalidOperationException($"entry '{name}' reads back different content");
            }
        }
    }

    private static byte[] Write(SevenZipWriterOptions options, byte[] body, int count, bool seekable, out List<(string Name, ArchiveEntryKind Kind, byte[] Content)> expected)
    {
        expected = [];
        var ms = new MemoryStream();
        using (var w = new SevenZipWriter(seekable ? ms : new NoSeekWrite(ms), options, leaveOpen: true))
        {
            int at = 0;
            for (int i = 0; i < count; i++)
            {
                int take = body.Length == 0 ? 0 : Math.Min(body.Length - at, 1 + (body[Math.Min(at, body.Length - 1)] * 13));
                byte[] content = body.AsSpan(at, Math.Max(0, take)).ToArray();
                at += content.Length;
                string name = "e" + i + "/f" + Convert.ToHexString(content.AsSpan(0, Math.Min(3, content.Length)));
                if (i % 5 == 3)
                {
                    w.WriteEntry(new ArchiveEntry { Name = name, Kind = ArchiveEntryKind.Directory }, (Stream?)null);
                    expected.Add((name, ArchiveEntryKind.Directory, []));
                }
                else if (i % 5 == 4)
                {
                    string target = "t" + Convert.ToHexString(content.AsSpan(0, Math.Min(4, content.Length)));
                    w.WriteEntry(new ArchiveEntry { Name = name, Kind = ArchiveEntryKind.Symlink, LinkTarget = target }, (Stream?)null);
                    expected.Add((name, ArchiveEntryKind.Symlink, Encoding.UTF8.GetBytes(target)));
                }
                else
                {
                    w.WriteEntry(new ArchiveEntry { Name = name, Size = content.Length }, content);
                    expected.Add((name, ArchiveEntryKind.File, content));
                }
            }
        }

        return ms.ToArray();
    }
}
