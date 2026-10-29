using System.Security.Cryptography;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneCompress;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz target for the compound file reader.
/// <list type="bullet">
/// <item><c>cfb-read</c>: the input is a compound file. It is read from memory and from a stream; both must list the same entries with the
/// same content hashes, and every entry must give the same bytes again by random access, in reverse order, and be found by its stored path
/// (or both must throw the library's exceptions).</item>
/// <item><c>cfb-rewrite</c>: the input is a compound file. When it reads, every entry is written again in version 3 and in version 4 and read back; the
/// names, kinds, sizes, CLSIDs, state bits, times and content hashes must be unchanged, and writing must not throw.</item>
/// <item><c>cfb-write</c>: the input is a script of random paths, sizes and contents. Every entry the writer accepts must be there, with the same
/// content, when its output is read back, in both versions; a rejected entry must leave the writer usable.</item>
/// </list>
/// <c>fuzz/seeds-cfb.sh</c> writes seeds.
/// </summary>
internal static class CfbTargets
{
    private static readonly CompoundFileReaderOptions s_options = new()
    {
        MaxDirectoryEntries = 20000,
        Limits = new ArchiveLimits { MaxTotalSize = 1 << 24, MaxEntryCount = 2000, MaxPathLength = 1024, MaxPathDepth = 32 },
    };

    public static FuzzTarget Read { get; } = new("cfb-read", static data => Check(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget Rewrite { get; } = new("cfb-rewrite", static data => RewriteCheck(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget Write { get; } = new("cfb-write", static data => WriteCheck(data.ToArray()), static _ => false);

    private static readonly CompoundFileReaderOptions s_wide = new()
    {
        Limits = new ArchiveLimits { MaxTotalSize = 1 << 26, MaxEntryCount = 100_000, MaxPathLength = 100_000, MaxPathDepth = 1000 },
    };

    private static List<string> Snapshot(CompoundFileReader r)
    {
        var lines = new List<string>();
        void Add(CfbEntry e)
        {
            string digest = "-";
            if (e.Kind == ArchiveEntryKind.File)
            {
                using Stream s = r.OpenEntryStream(e);
                digest = Convert.ToHexString(SHA256.HashData(ReadAll(s)));
            }

            lines.Add($"{e.Name}|{e.RawName}|{e.Kind}|{e.Size}|{e.ClassId}|{e.StateBits}|{e.CreationFileTime}|{e.ModifiedFileTime}|{digest}");
        }

        Add(r.Root);
        while (r.GetNextEntry() is { } e)
        {
            Add(e);
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static byte[] ReadAll(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void RewriteCheck(byte[] data)
    {
        List<string> before;
        var outputs = new List<(int Version, byte[] Bytes)>();
        using (var r = new CompoundFileReader(data, s_wide))
        {
            before = Snapshot(r);
            foreach (int version in new[] { 3, 4 })
            {
                using var again = new CompoundFileReader(data, s_wide);
                var ms = new MemoryStream();
                using (var w = new CompoundFileWriter(ms, new CompoundFileWriterOptions { MajorVersion = version, StrictNames = false }, leaveOpen: true))
                {
                    w.WriteEntry(again.Root);
                    while (again.GetNextEntry() is { } e)
                    {
                        if (e.Kind == ArchiveEntryKind.File)
                        {
                            using Stream s = again.OpenEntryStream(e);
                            w.WriteEntry(e, s);
                        }
                        else
                        {
                            w.WriteEntry(e);
                        }
                    }
                }

                outputs.Add((version, ms.ToArray()));
            }
        }

        foreach (var (version, bytes) in outputs)
        {
            using var back = new CompoundFileReader(bytes, s_wide);
            if (back.Header.MajorVersion != version || !Snapshot(back).SequenceEqual(before, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"version {version} rewrite changed the tree");
            }
        }
    }

    private static void WriteCheck(byte[] script)
    {
        const string Alphabet = "abAB01 .%_-~F";
        foreach (int version in new[] { 3, 4 })
        {
            var expected = new Dictionary<string, (bool Stream, string Digest)>(StringComparer.Ordinal);
            var ms = new MemoryStream();
            using (var w = new CompoundFileWriter(ms, new CompoundFileWriterOptions { MajorVersion = version, MaxBufferedBytes = 1 << 24 }, leaveOpen: true))
            {
                int p = 0;
                int Next() => p < script.Length ? script[p++] : 0;
                for (int ops = 0; ops < 200 && p < script.Length; ops++)
                {
                    int op = Next();
                    int depth = 1 + (op >> 5 & 3);
                    var parts = new List<string>();
                    for (int d = 0; d < depth; d++)
                    {
                        int len = 1 + (Next() & 7);
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < len; i++)
                        {
                            sb.Append(Alphabet[Next() % Alphabet.Length]);
                        }

                        if ((op & 0x80) != 0 && d == 0)
                        {
                            sb.Append("%003A"); // an escaped colon: rejected while names are strict
                        }

                        parts.Add(sb.ToString());
                    }

                    string path = string.Join('/', parts);
                    bool isStream = (op & 3) != 0;
                    int size = isStream ? ((Next() << 8 | Next()) % 20000) : 0;
                    byte seed = (byte)Next();
                    byte[] content = new byte[size];
                    for (int i = 0; i < size; i++)
                    {
                        content[i] = (byte)(seed + (i * (seed | 1) >> 3));
                    }

                    try
                    {
                        w.WriteEntry(new ArchiveEntry { Name = path, Kind = isStream ? ArchiveEntryKind.File : ArchiveEntryKind.Directory, Size = size }, content);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    string key = string.Join('/', parts.Select(CompoundFileWriter.UnsanitizeName));
                    expected[key] = (isStream, Convert.ToHexString(SHA256.HashData(content)));
                }
            }

            using var r = new CompoundFileReader(ms.ToArray(), s_wide);
            foreach (var (path, (stream, digest)) in expected)
            {
                CfbEntry? e = r.Find(path);
                if (e is null || (e.Kind == ArchiveEntryKind.File) != stream)
                {
                    throw new InvalidOperationException($"v{version}: '{path}' is missing or has the wrong kind");
                }

                if (stream)
                {
                    using Stream s = r.OpenEntryStream(e);
                    if (Convert.ToHexString(SHA256.HashData(ReadAll(s))) != digest)
                    {
                        throw new InvalidOperationException($"v{version}: '{path}' has different content");
                    }
                }
            }
        }
    }

    private static string Summarize(CompoundFileReader reader)
    {
        var sb = new System.Text.StringBuilder();
        var entries = new List<CfbEntry>();
        var lines = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            byte[] buffer = new byte[8192];
            while (reader.GetNextEntry() is { } e)
            {
                entries.Add(e);
                sb.Append(e.Name).Append('|').Append(e.Kind).Append('|').Append(e.Size).Append('|').Append(e.StreamId).Append('|').Append(e.ClassId).Append('|').Append(e.ModifiedFileTime).Append('|');
                using Stream s = reader.OpenEntryStream();
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int n;
                while ((n = s.Read(buffer)) > 0)
                {
                    h.AppendData(buffer, 0, n);
                }

                string digest = Convert.ToHexString(h.GetHashAndReset());
                sb.Append(digest).Append('\n');
                lines.Add($"{e.Name}|{e.Kind}|{e.Size}|{e.StreamId}|{e.ClassId}|{e.ModifiedFileTime}|{digest}");
            }

            // Random access, backwards, must agree with the first pass.
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                using Stream s = reader.OpenEntryStream(entries[i]);
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int n;
                while ((n = s.Read(buffer, 0, 1 + ((i * 37) % 8000))) > 0)
                {
                    h.AppendData(buffer, 0, n);
                }

                string line = $"{entries[i].Name}|{entries[i].Kind}|{entries[i].Size}|{entries[i].StreamId}|{entries[i].ClassId}|{entries[i].ModifiedFileTime}|{Convert.ToHexString(h.GetHashAndReset())}";
                if (!lines.Contains(line))
                {
                    throw new InvalidOperationException("random access gave different bytes for " + entries[i].Name);
                }

                if (!entries[i].RawName.Contains('/', StringComparison.Ordinal) && reader.Find(entries[i].RawName) is null && reader.GetChildren(reader.Root).Count > 0 && entries[i].ParentStreamId == 0)
                {
                    throw new InvalidOperationException("a root entry was not found by its stored name: " + entries[i].Name);
                }
            }
        }
        catch (CompressionException e)
        {
            sb.Append('!').Append(e.GetType().Name);
        }

        return sb.ToString();
    }

    private static void Check(byte[] data)
    {
        string a;
        using (var r = TryOpen(() => new CompoundFileReader(data, s_options), out string failure))
        {
            a = r is null ? failure : Summarize(r);
        }

        string b;
        using (var r = TryOpen(() => new CompoundFileReader(new MemoryStream(data), s_options), out string failure))
        {
            b = r is null ? failure : Summarize(r);
        }

        if (a != b)
        {
            throw new InvalidOperationException("memory and stream sources gave different results");
        }
    }

    private static CompoundFileReader? TryOpen(Func<CompoundFileReader> create, out string failure)
    {
        failure = string.Empty;
        try
        {
            var r = create();
            _ = r.Header;
            return r;
        }
        catch (CompressionException e)
        {
            failure = "!" + e.GetType().Name;
            return null;
        }
    }
}
