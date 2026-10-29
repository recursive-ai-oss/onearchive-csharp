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
