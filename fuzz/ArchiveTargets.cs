using System.Security.Cryptography;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Tar;
using RecursiveAI.OneCompress;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for the archive layer.
/// <list type="bullet">
/// <item><c>tar-read</c>: the tar reader over the input, read once from a seekable stream and once from a forward-only one in
/// odd pieces; both must give the same entries and the same content, or both must throw the library's exceptions.</item>
/// <item><c>archive-open</c>: detection, <c>Archive.Open</c>, listing, random access and extraction into a scratch directory
/// (so path and link handling run on whatever the input says), then a check that nothing appeared outside the target.</item>
/// <item><c>tar-roundtrip</c>: entries built from the input are written and read back unchanged.</item>
/// </list>
/// </summary>
internal static class ArchiveTargets
{
    // Small limits keep each execution cheap and still exercise every limit.
    private static readonly ArchiveLimits s_limits = new()
    {
        MaxTotalSize = 1 << 24,
        MaxEntryCount = 2000,
        MaxPathLength = 1024,
        MaxPathDepth = 32,
        MaxExpansionRatio = 200,
        MaxWindowSize = 1 << 22,
    };

    public static FuzzTarget TarRead { get; } = new("tar-read", static data => CheckTarRead(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget Open { get; } = new("archive-open", static data => CheckOpen(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget TarRoundTrip { get; } = new("tar-roundtrip", static data => CheckRoundTrip(data), static _ => false);

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
            int n = Math.Min(Math.Min(count, 1 + ((_at * 7) % 613)), data.Length - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Name, kind, size and a hash of the content of every entry; null when the library's exceptions ended the read.
    private static string? Summarize(Stream source)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            using var reader = new TarReader(source, new TarReaderOptions { Limits = s_limits, MaxSparseRegions = 2000 });
            byte[] buffer = new byte[8192];
            while (reader.GetNextEntry() is { } e)
            {
                sb.Append(e.Name).Append('|').Append(e.Kind).Append('|').Append(e.Size).Append('|').Append(e.LinkTarget).Append('|').Append(e.ModifiedTime?.UtcTicks).Append('|');
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Stream s = reader.OpenEntryStream();
                long total = 0;
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    total += n;
                }

                if (total != (e.Kind == ArchiveEntryKind.File ? e.Size : 0))
                {
                    throw new InvalidOperationException($"entry '{e.Name}' read {total} bytes but declares {e.Size}");
                }

                sb.Append(Convert.ToHexString(hash.GetHashAndReset())).Append('\n');
            }
        }
        catch (CompressionException ex)
        {
            sb.Append("ERR ").Append(ex.GetType().Name);
            return sb.ToString();
        }

        return sb.ToString();
    }

    private static void CheckTarRead(byte[] data)
    {
        string? a = Summarize(new MemoryStream(data));
        string? b = Summarize(new Pieces(data));
        // The two may differ only in where an error is reported, never in what was read before it or whether it happened.
        if (a is null || b is null)
        {
            return;
        }

        int errA = a.IndexOf("ERR ", StringComparison.Ordinal);
        int errB = b.IndexOf("ERR ", StringComparison.Ordinal);
        if ((errA < 0) != (errB < 0) || (errA >= 0 && !a.AsSpan(0, errA).SequenceEqual(b.AsSpan(0, errB))) || (errA < 0 && a != b))
        {
            throw new InvalidOperationException("seekable and forward-only reads disagree");
        }
    }

    private static void CheckOpen(byte[] data)
    {
        string parent = Path.Combine(Path.GetTempPath(), "fuzz-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(parent, "out");
        Directory.CreateDirectory(parent);
        string[] outside = [];
        try
        {
            var open = new ArchiveOpenOptions { Limits = s_limits, FileName = "input.tar" };
            using (var archive = Archive.Open(new MemoryStream(data), open))
            {
                IReadOnlyList<ArchiveEntry> entries = archive.Entries;
                int probe = 0;
                foreach (ArchiveEntry e in entries)
                {
                    if (probe++ % 7 != 0 || e.Kind != ArchiveEntryKind.File)
                    {
                        continue;
                    }

                    using Stream s = archive.OpenEntry(e);
                    long total = 0;
                    byte[] buffer = new byte[4096];
                    int n;
                    while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += n;
                    }

                    if (total != e.Size)
                    {
                        throw new InvalidOperationException("random-access entry has the wrong length");
                    }
                }
            }

            // Extraction with every loosening off: whatever the archive says, nothing may land outside the target.
            try
            {
                Archive.Extract(new MemoryStream(data), target, new ArchiveExtractOptions { Limits = s_limits, FileName = "input.tar", Existing = ExistingFilePolicy.Overwrite });
            }
            catch (ArchiveSafetyException)
            {
            }
            catch (IOException)
            {
                // The file system refused something legal for the archive (for example a name too long for it).
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        finally
        {
            outside = Directory.GetFileSystemEntries(parent).Where(p => p != target).ToArray();
            ForceDelete(parent);
        }

        if (outside.Length > 0)
        {
            throw new InvalidOperationException("extraction created " + outside[0] + " outside the target directory");
        }
    }

    internal static void ForceDelete(string path)
    {
        try
        {
            // Links are not entered: an extracted link may point back up the tree.
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string d in Directory.EnumerateDirectories(path, "*", options))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Builds entries from the input: each 8-byte group describes one entry.
    private static void CheckRoundTrip(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        var format = (data[0] & 1) == 0 ? TarFormat.Pax : TarFormat.Ustar;
        var entries = new List<(ArchiveEntry Entry, byte[] Content)>();
        int at = 1;
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-_. éü日";
        while (at + 8 <= data.Length && entries.Count < 20)
        {
            ReadOnlySpan<byte> g = data.Slice(at, 8);
            at += 8;
            int nameLength = 1 + (g[0] % 160);
            var name = new System.Text.StringBuilder();
            for (int i = 0; i < nameLength; i++)
            {
                char c = Alphabet[(g[(i % 6) + 1] + (i * 13)) % Alphabet.Length];
                name.Append(i > 0 && i % 23 == 0 ? '/' : c);
            }

            string text = name.ToString().Trim('/');
            if (text.Length == 0 || text.EndsWith('.') && text.Length == 1)
            {
                continue;
            }

            var kind = (ArchiveEntryKind)(g[7] % 3 == 0 ? 1 : (g[7] % 3 == 1 ? 0 : 2));
            byte[] content = kind == ArchiveEntryKind.File ? new byte[g[6] * (g[5] % 5)] : [];
            for (int i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(g[4] + (i * 31));
            }

            entries.Add((new ArchiveEntry
            {
                Name = text,
                Kind = kind,
                Size = content.Length,
                ModifiedTime = DateTimeOffset.FromUnixTimeSeconds((g[1] << 20) + g[2]).AddTicks(g[3] * 1000L),
                Mode = (UnixFileMode)(g[4] | (g[5] << 8) & 0xE00),
                Uid = g[2] * 20000L,
                Gid = g[3],
                UserName = g[3] % 2 == 0 ? "user" : null,
                LinkTarget = kind == ArchiveEntryKind.Symlink ? "t" + g[1] : null,
            }, content));
        }

        var output = new MemoryStream();
        try
        {
            using (var writer = new TarWriter(output, new TarWriterOptions { Format = format, RecordSize = 512 }, leaveOpen: true))
            {
                foreach ((ArchiveEntry e, byte[] c) in entries)
                {
                    writer.WriteEntry(e, c);
                }
            }
        }
        catch (ArchiveUnsupportedException) when (format == TarFormat.Ustar)
        {
            return;
        }

        output.Position = 0;
        using var reader = new TarReader(output, new TarReaderOptions { Limits = s_limits });
        foreach ((ArchiveEntry e, byte[] c) in entries)
        {
            ArchiveEntry? got = reader.GetNextEntry();
            if (got is null || got.Name != e.Name || got.Kind != e.Kind || got.Size != e.Size || got.Uid != e.Uid || got.Gid != e.Gid || got.UserName != e.UserName
                || got.LinkTarget != e.LinkTarget || got.Mode != e.Mode || (format == TarFormat.Pax && got.ModifiedTime != e.ModifiedTime)
                || (format == TarFormat.Ustar && got.ModifiedTime!.Value.ToUnixTimeSeconds() != e.ModifiedTime!.Value.ToUnixTimeSeconds()))
            {
                throw new InvalidOperationException($"entry '{e.Name}' did not round-trip");
            }

            var ms = new MemoryStream();
            reader.OpenEntryStream().CopyTo(ms);
            if (!ms.ToArray().AsSpan().SequenceEqual(c))
            {
                throw new InvalidOperationException($"content of '{e.Name}' did not round-trip");
            }
        }

        if (reader.GetNextEntry() is not null)
        {
            throw new InvalidOperationException("extra entries after the round trip");
        }
    }
}
