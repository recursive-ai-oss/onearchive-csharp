using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Iso;
using RecursiveAI.OneCompress;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for ISO 9660.
/// <list type="bullet">
/// <item><c>iso-read</c>: the reader over the input in each hierarchy (primary with and without Rock Ridge, Joliet): every entry
/// is listed and its content read and hashed. Only the library's exceptions may leave; an entry whose content is shorter than
/// its declared size is a bug.</item>
/// <item><c>iso-roundtrip</c>: entries built from the input are written with options taken from its first byte and read back unchanged.</item>
/// </list>
/// </summary>
internal static class IsoTargets
{
    private static readonly ArchiveLimits s_limits = new()
    {
        MaxTotalSize = 1 << 24,
        MaxEntryCount = 1000,
        MaxPathLength = 1024,
        MaxPathDepth = 32,
    };

    public static FuzzTarget Read { get; } = new("iso-read", static data => CheckRead(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget RoundTrip { get; } = new("iso-roundtrip", static data => RoundTripImage(data), static _ => false);

    private static void CheckRead(byte[] data)
    {
        foreach (IsoTree tree in new[] { IsoTree.Auto, IsoTree.Primary, IsoTree.Joliet })
        {
            foreach (bool rr in new[] { true, false })
            {
                if (!rr && tree == IsoTree.Joliet)
                {
                    continue;
                }

                try
                {
                    ReadEverything(data, new IsoReaderOptions
                    {
                        Limits = s_limits,
                        Tree = tree,
                        UseRockRidge = rr,
                        MaxDirectorySize = 1 << 20,
                        MaxContinuationBytes = 1 << 22,
                    });
                }
                catch (CompressionException)
                {
                    // A typed refusal is the expected answer to a bad image.
                }
            }
        }
    }

    private static void ReadEverything(byte[] data, IsoReaderOptions options)
    {
        using var reader = new IsoReader(new MemoryStream(data), options);
        byte[] buffer = new byte[16384];
        long budget = 48L << 20;
        while (reader.GetNextEntry() is { } e)
        {
            // Touch the fields the entry model promises.
            _ = e.Name.Length + e.IsoPath.Length + e.Identifier.Length + e.Extents.Count;
            if (e.Kind != ArchiveEntryKind.File || budget <= 0)
            {
                continue;
            }

            long total = 0;
            using (Stream s = reader.OpenEntryStream())
            {
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                }
            }

            budget -= total;
            if (total != e.Size)
            {
                throw new InvalidOperationException($"entry '{e.Name}' read {total} bytes but declares {e.Size}");
            }
        }
    }

    // Byte 0 picks the options, then groups of (name length, size, directory) with names and content taken from the rest.
    private static void RoundTripImage(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        byte o = input[0];
        var options = new IsoWriterOptions
        {
            Level = 1 + (o % 3),
            Joliet = (o & 4) != 0,
            RockRidge = (o & 8) != 0 || (o & 4) == 0,
            MaxMemoryBuffer = (o & 16) != 0 ? 64 : IsoWriterOptions.Default.MaxMemoryBuffer,
        };
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var ms = new MemoryStream();
        using (var w = new IsoWriter(ms, options, leaveOpen: true))
        {
            ReadOnlySpan<byte> rest = input[1..];
            int at = 0;
            int index = 0;
            while (at + 3 <= rest.Length && index < 30)
            {
                int nameLen = 1 + (rest[at] % 24);
                int size = rest[at + 1] * (1 + (rest[at + 2] % 8));
                at += 3;
                var name = new StringBuilder();
                for (int i = 0; i < nameLen && at < rest.Length; i++, at++)
                {
                    byte b = rest[at];
                    name.Append(b % 17 == 0 ? '/' : (char)('a' + (b % 26)));
                }

                string n = string.Join('/', name.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries)) + "-" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                byte[] content = new byte[size];
                for (int i = 0; i < size; i++)
                {
                    content[i] = (byte)((i * 31) + size);
                }

                // A name may not be both a file and a directory of another entry; a prefix clash is skipped.
                if (expected.Keys.Any(k => k.StartsWith(n + "/", StringComparison.Ordinal) || n.StartsWith(k + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                w.WriteEntry(new ArchiveEntry { Name = n, Kind = ArchiveEntryKind.File, Size = size, Mode = (UnixFileMode)0x1A4, ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1_000_000 + size) }, content);
                expected[n] = content;
            }
        }

        // Without Rock Ridge and Joliet the names are cut to the interchange level, so only the contents can be compared.
        bool namesKept = options.RockRidge || options.Joliet;
        var unlimited = new ArchiveLimits { MaxTotalSize = long.MaxValue, MaxEntryCount = int.MaxValue };
        using var r = new IsoReader(new MemoryStream(ms.ToArray()), new IsoReaderOptions { Limits = unlimited });
        int files = 0;
        while (r.GetNextEntry() is { } e)
        {
            if (e.Kind != ArchiveEntryKind.File)
            {
                continue;
            }

            files++;
            using var content = new MemoryStream();
            using (Stream s = r.OpenEntryStream())
            {
                s.CopyTo(content);
            }

            if (namesKept)
            {
                if (!expected.TryGetValue(e.Name, out byte[]? want) || !want.AsSpan().SequenceEqual(content.ToArray()))
                {
                    throw new InvalidOperationException($"entry '{e.Name}' came back changed or unexpected");
                }
            }
            else if (content.Length != e.Size)
            {
                throw new InvalidOperationException($"entry '{e.Name}' has the wrong content length");
            }
        }

        if (files != expected.Count)
        {
            throw new InvalidOperationException($"{expected.Count} files written, {files} read");
        }
    }
}
