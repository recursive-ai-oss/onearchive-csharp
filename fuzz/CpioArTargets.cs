using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Ar;
using RecursiveAI.OneArchive.Cpio;
using RecursiveAI.OneCompress;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for the cpio and ar readers.
/// <list type="bullet">
/// <item><c>cpio-read</c>, <c>ar-read</c>: the reader over the input from a seekable stream and from a forward-only one in odd
/// pieces; both must list the same entries with the same content, or both must throw the library's exceptions. The seekable run
/// resolves cpio hard-link stubs only in a second pass, whose entries are checked against random access.</item>
/// <item><c>cpio-roundtrip</c>, <c>ar-roundtrip</c>: entries built from the input are written in every variant and read back unchanged.</item>
/// </list>
/// </summary>
internal static class CpioArTargets
{
    private static readonly ArchiveLimits s_limits = new()
    {
        MaxTotalSize = 1 << 24,
        MaxEntryCount = 2000,
        MaxPathLength = 1024,
        MaxPathDepth = 32,
    };

    public static FuzzTarget CpioRead { get; } = new("cpio-read", static data => CheckCpio(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget ArRead { get; } = new("ar-read", static data => CheckAr(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget CpioRoundTrip { get; } = new("cpio-roundtrip", static data => RoundTripCpio(data), static _ => false);

    public static FuzzTarget ArRoundTrip { get; } = new("ar-roundtrip", static data => RoundTripAr(data), static _ => false);

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

    // Reads every entry and summarizes name, kind, size and a hash of the content; an error ends the summary with its type.
    private static string Summarize(ArchiveReader reader, bool randomAccessToo)
    {
        var sb = new StringBuilder();
        var entries = new List<ArchiveEntry>();
        try
        {
            byte[] buffer = new byte[8192];
            while (reader.GetNextEntry() is { } e)
            {
                entries.Add(e);
                sb.Append(e.Name).Append('|').Append(e.Kind).Append('|').Append(e.Size).Append('|').Append(e.LinkTarget).Append('|').Append(e.ModifiedTime?.UtcTicks).Append('|').Append((int?)e.Mode).Append('|').Append(e.DeviceMajor).Append(',').Append(e.DeviceMinor).Append('|');
                if (e.Kind == ArchiveEntryKind.File)
                {
                    sb.Append(Hash(reader.OpenEntryStream(), buffer, e));
                }

                sb.Append('\n');
            }
        }
        catch (CompressionException ex)
        {
            sb.Append("ERR ").Append(ex.GetType().Name);
            return sb.ToString();
        }

        if (randomAccessToo && reader.SupportsRandomAccess)
        {
            // Opening an entry again by random access must give the same bytes as the sequential read did.
            byte[] buffer = new byte[8192];
            int i = 0;
            foreach (ArchiveEntry e in entries)
            {
                if (e.Kind == ArchiveEntryKind.File && i++ % 3 == 0)
                {
                    string again = Hash(reader.OpenEntryStream(e), buffer, e);
                    if (!sb.ToString().Contains(again, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"random access to '{e.Name}' gives other content");
                    }
                }
            }
        }

        return sb.ToString();
    }

    private static string Hash(Stream s, byte[] buffer, ArchiveEntry e)
    {
        using (s)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                total += n;
            }

            if (total != e.Size)
            {
                throw new InvalidOperationException($"entry '{e.Name}' read {total} bytes but declares {e.Size}");
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }

    private static void Agree(string a, string b)
    {
        // The two may differ only in where an error is reported, never in what was read before it or whether it happened.
        int errA = a.IndexOf("ERR ", StringComparison.Ordinal);
        int errB = b.IndexOf("ERR ", StringComparison.Ordinal);
        if ((errA < 0) != (errB < 0) || (errA >= 0 && !a.AsSpan(0, errA).SequenceEqual(b.AsSpan(0, errB))) || (errA < 0 && a != b))
        {
            throw new InvalidOperationException("seekable and forward-only reads disagree");
        }
    }

    private static void CheckCpio(byte[] data)
    {
        // Stub resolution is a seekable-only feature: compare with it off, then run it on its own.
        var plain = new CpioReaderOptions { Limits = s_limits, ResolveLinkStubs = false };
        string a, b;
        using (var r = new CpioReader(new MemoryStream(data), plain))
        {
            a = Summarize(r, randomAccessToo: true);
        }

        using (var r = new CpioReader(new Pieces(data), plain))
        {
            b = Summarize(r, randomAccessToo: false);
        }

        Agree(a, b);
        using var resolving = new CpioReader(new MemoryStream(data), new CpioReaderOptions { Limits = s_limits });
        Summarize(resolving, randomAccessToo: true);
    }

    private static void CheckAr(byte[] data)
    {
        foreach (bool special in new[] { false, true })
        {
            var options = new ArReaderOptions { Limits = s_limits, IncludeSpecialMembers = special, MaxNameTableSize = 1 << 20 };
            string a, b;
            using (var r = new ArReader(new MemoryStream(data), options))
            {
                a = Summarize(r, randomAccessToo: true);
            }

            using (var r = new ArReader(new Pieces(data), options))
            {
                b = Summarize(r, randomAccessToo: false);
            }

            Agree(a, b);
        }
    }

    // Entries from the input: byte 0 picks the variant, then groups of (name length, size, mode) with names and content taken from the rest.
    private static void RoundTripCpio(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        var format = (CpioFormat)(input[0] % 3);
        var entries = Build(input[1..], withDirectories: true);
        var ms = new MemoryStream();
        using (var w = new CpioWriter(ms, new CpioWriterOptions { Format = format, BlockSize = 1 + (input[0] >> 2) }, leaveOpen: true))
        {
            foreach (var (e, content) in entries)
            {
                w.WriteEntry(e, content);
            }
        }

        using var r = new CpioReader(new MemoryStream(ms.ToArray()), new CpioReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = long.MaxValue, MaxEntryCount = int.MaxValue } });
        Compare(r, entries);
    }

    private static void RoundTripAr(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        var format = (ArFormat)(input[0] % 3);
        var entries = Build(input[1..], withDirectories: false);
        if (format == ArFormat.Common && entries.Any(e => Encoding.UTF8.GetByteCount(e.Entry.Name) > 15))
        {
            return;
        }

        var ms = new MemoryStream();
        using (var w = new ArWriter(ms, new ArWriterOptions { Format = format, MaxMemoryBuffer = (input[0] & 4) != 0 ? 64 : ArWriterOptions.DefaultMaxMemoryBuffer }, leaveOpen: true))
        {
            foreach (var (e, content) in entries)
            {
                w.WriteEntry(e, content);
            }
        }

        using var r = new ArReader(new MemoryStream(ms.ToArray()), new ArReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = long.MaxValue, MaxEntryCount = int.MaxValue } });
        Compare(r, entries);
    }

    private static List<(ArchiveEntry Entry, byte[] Content)> Build(ReadOnlySpan<byte> input, bool withDirectories)
    {
        var list = new List<(ArchiveEntry, byte[])>();
        int at = 0;
        int index = 0;
        while (at + 4 <= input.Length && list.Count < 40)
        {
            int nameLen = 1 + (input[at] % 40);
            int size = input[at + 1] * (1 + (input[at + 2] % 4));
            int kind = withDirectories ? input[at + 3] % 3 : 0;
            at += 4;
            var name = new StringBuilder();
            for (int i = 0; i < nameLen && at < input.Length; i++, at++)
            {
                // Printable ASCII, a few multi-byte characters, but never NUL, a line feed or a leading/trailing separator.
                byte b = input[at];
                name.Append(b % 11 == 0 ? '\u00e9' : b % 13 == 0 ? '/' : (char)('!' + (b % 90)));
            }

            name.Append(index++.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string n = name.ToString().Trim('/');
            if (n.Length == 0 || n.EndsWith(' '))
            {
                n += "x";
            }

            n = n.Replace("//", "/", StringComparison.Ordinal);
            byte[] content = new byte[size];
            for (int i = 0; i < size; i++)
            {
                content[i] = (byte)(i * 31 + size);
            }

            ArchiveEntry e = kind switch
            {
                1 => new ArchiveEntry { Name = n, Kind = ArchiveEntryKind.Directory },
                2 => new ArchiveEntry { Name = n, Kind = ArchiveEntryKind.Symlink, LinkTarget = "t" + n },
                _ => new ArchiveEntry { Name = n, Size = size, Mode = (UnixFileMode)(0x1A4 + (size & 0x49)), ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(size * 1000L) },
            };
            list.Add((e, kind == 0 ? content : []));
        }

        return list;
    }

    private static void Compare(ArchiveReader reader, List<(ArchiveEntry Entry, byte[] Content)> expected)
    {
        foreach (var (e, content) in expected)
        {
            ArchiveEntry got = reader.GetNextEntry() ?? throw new InvalidOperationException("an entry is missing");
            if (got.Name != e.Name || got.Kind != e.Kind || got.LinkTarget != e.LinkTarget || got.Size != e.Size)
            {
                throw new InvalidOperationException($"entry '{e.Name}' came back as '{got.Name}' ({got.Kind}, {got.Size})");
            }

            if (e.Kind == ArchiveEntryKind.File)
            {
                if (got.Mode != e.Mode || got.ModifiedTime != e.ModifiedTime)
                {
                    throw new InvalidOperationException($"entry '{e.Name}' lost its mode or time");
                }

                using var ms = new MemoryStream();
                using (Stream s = reader.OpenEntryStream())
                {
                    s.CopyTo(ms);
                }

                if (!ms.ToArray().AsSpan().SequenceEqual(content))
                {
                    throw new InvalidOperationException($"entry '{e.Name}' came back with other content");
                }
            }
        }

        if (reader.GetNextEntry() is not null)
        {
            throw new InvalidOperationException("extra entries");
        }
    }
}
