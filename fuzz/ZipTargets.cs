using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for zip.
/// <list type="bullet">
/// <item><c>zip-read</c>: the zip reader over the input, through the central directory (with entries opened out of order) and
/// forward-only in odd pieces; each must end normally or in one of the library's exceptions, every entry that is read must have
/// the length it declares, and sequential and random-access reads of one entry must agree.</item>
/// <item><c>zip-roundtrip</c>: entries made from the input are written with an option mix (method, zip64, descriptors, seekable or
/// not) and read back both ways unchanged.</item>
/// <item><c>single-stream-read</c>: <c>Archive.Open</c> over compressed single files and everything else, extracting into a
/// scratch directory.</item>
/// </list>
/// </summary>
internal static class ZipTargets
{
    private static readonly ArchiveLimits s_limits = new()
    {
        MaxTotalSize = 1 << 24,
        MaxEntryCount = 2000,
        MaxPathLength = 1024,
        MaxPathDepth = 32,
        MaxExpansionRatio = 200,
        MaxWindowSize = 1 << 22,
    };

    public static FuzzTarget Read { get; } = new("zip-read", static data => CheckRead(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget RoundTrip { get; } = new("zip-roundtrip", static data => CheckRoundTrip(data), static _ => false);

    public static FuzzTarget Single { get; } = new("single-stream-read", static data => CheckSingle(data.ToArray()), static ex => ex is CompressionException);

    private sealed class Pieces(byte[] data) : Stream
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
            int n = Math.Min(Math.Min(count, 1 + ((_at * 11) % 701)), data.Length - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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

    private static string HashOf(Stream s, long expected, string name)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[8192];
        long total = 0;
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            total += n;
        }

        if (expected >= 0 && total != expected)
        {
            throw new InvalidOperationException($"entry '{name}' read {total} bytes but declares {expected}");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void CheckRead(byte[] data)
    {
        // Through the central directory, then each entry again by random access, last to first.
        try
        {
            using var reader = new ZipReader(new MemoryStream(data), new ZipReaderOptions { Limits = s_limits });
            var seen = new List<(ZipEntry Entry, string Hash)>();
            while (reader.GetNextEntry() is { } e)
            {
                if (seen.Count >= 40)
                {
                    break;
                }

                using Stream s = reader.OpenEntryStream();
                seen.Add((e, HashOf(s, e.Kind == ArchiveEntryKind.File ? e.Size : -1, e.Name)));
            }

            for (int i = seen.Count - 1; i >= 0; i--)
            {
                using Stream s = reader.OpenEntryStream(seen[i].Entry);
                if (HashOf(s, -1, seen[i].Entry.Name) != seen[i].Hash)
                {
                    throw new InvalidOperationException("random access and sequential reads of an entry disagree");
                }
            }
        }
        catch (CompressionException)
        {
        }

        // Forward-only.
        try
        {
            using var reader = new ZipReader(new Pieces(data), new ZipReaderOptions { Limits = s_limits });
            int count = 0;
            while (reader.GetNextEntry() is { } e && count++ < 40)
            {
                if ((count & 1) == 0)
                {
                    continue; // every other entry is skipped unread
                }

                using Stream s = reader.OpenEntryStream();
                HashOf(s, e.IsSizeKnown && e.Kind == ArchiveEntryKind.File ? e.Size : -1, e.Name);
            }
        }
        catch (CompressionException)
        {
        }
    }

    private static void CheckRoundTrip(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        byte o = input[0];
        ZipMethod method = ((o >> 5) & 3) switch { 1 => ZipMethod.Deflate, 2 => ZipMethod.Zstd, _ => ZipMethod.Store };
        var options = new ZipWriterOptions
        {
            Method = method,
            Zip64 = (o & 2) != 0 ? ZipZip64Mode.Always : ZipZip64Mode.Auto,
            DataDescriptors = ((o >> 2) & 3) switch { 1 => ZipDataDescriptorMode.Always, _ => ZipDataDescriptorMode.Auto },
            Comment = (o & 16) != 0 ? "fuzz" : null,
        };
        bool seekable = (o & 32) == 0;
        int count = 1 + (input[1] % 6);
        byte[] body = input[2..].ToArray();
        var expected = new List<(string Name, ArchiveEntryKind Kind, byte[] Content)>();
        var ms = new MemoryStream();
        using (var w = new ZipWriter(seekable ? ms : new NoSeekWrite(ms), options, leaveOpen: true))
        {
            int at = 0;
            for (int i = 0; i < count; i++)
            {
                int take = body.Length == 0 ? 0 : Math.Min(body.Length - at, 1 + (body[Math.Min(at, body.Length - 1)] * 13));
                byte[] content = body.AsSpan(at, Math.Max(0, take)).ToArray();
                at += content.Length;
                string name = "e" + i + "/f" + Convert.ToHexString(content.AsSpan(0, Math.Min(3, content.Length)));
                ArchiveEntryKind kind = (i % 5) switch { 3 => ArchiveEntryKind.Directory, 4 => ArchiveEntryKind.Symlink, _ => ArchiveEntryKind.File };
                if (kind == ArchiveEntryKind.Directory)
                {
                    w.WriteEntry(new ArchiveEntry { Name = name, Kind = kind }, (Stream?)null);
                    expected.Add((name, kind, []));
                }
                else if (kind == ArchiveEntryKind.Symlink)
                {
                    string target = "t" + Convert.ToHexString(content.AsSpan(0, Math.Min(4, content.Length)));
                    w.WriteEntry(new ArchiveEntry { Name = name, Kind = kind, LinkTarget = target }, (Stream?)null);
                    expected.Add((name, kind, Encoding.UTF8.GetBytes(target)));
                }
                else
                {
                    w.WriteEntry(new ArchiveEntry { Name = name, Size = content.Length, ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1_600_000_000 + at) }, content);
                    expected.Add((name, kind, content));
                }
            }
        }

        byte[] zip = ms.ToArray();
        foreach (Stream source in new Stream[] { new MemoryStream(zip), new Pieces(zip) })
        {
            using var reader = new ZipReader(source, new ZipReaderOptions { Limits = s_limits });
            bool directory = source is MemoryStream;
            int index = 0;
            while (reader.GetNextEntry() is { } e)
            {
                (string name, ArchiveEntryKind kind, byte[] content) = expected[index++];
                if (e.Name != name.TrimEnd('/'))
                {
                    throw new InvalidOperationException($"entry {index} is '{e.Name}', expected '{name}'");
                }

                if (directory && e.Kind != kind)
                {
                    throw new InvalidOperationException($"entry '{name}' reads back as {e.Kind}, expected {kind}");
                }

                if (kind == ArchiveEntryKind.Directory)
                {
                    continue;
                }

                using Stream s = reader.OpenEntryStream();
                var got = new MemoryStream();
                s.CopyTo(got);
                if (!got.ToArray().AsSpan().SequenceEqual(content))
                {
                    throw new InvalidOperationException($"entry '{name}' reads back different");
                }
            }

            if (index != expected.Count)
            {
                throw new InvalidOperationException("entry count differs");
            }
        }
    }

    private static void CheckSingle(byte[] data)
    {
        string parent = Path.Combine(Path.GetTempPath(), "fuzz-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(parent, "out");
        Directory.CreateDirectory(parent);
        string[] outside;
        try
        {
            try
            {
                Archive.Extract(new MemoryStream(data), target, new ArchiveExtractOptions { Limits = s_limits, FileName = "input.bin", Existing = ExistingFilePolicy.Overwrite });
            }
            catch (ArchiveSafetyException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        finally
        {
            outside = Directory.GetFileSystemEntries(parent).Where(p => p != target).ToArray();
            ArchiveTargets.ForceDelete(parent);
        }

        if (outside.Length > 0)
        {
            throw new InvalidOperationException("extraction created " + outside[0] + " outside the target directory");
        }
    }
}
