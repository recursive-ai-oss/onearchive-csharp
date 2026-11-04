using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.Fuzz;

internal static partial class ZipTargets
{
    public static FuzzTarget RawCopy { get; } = new("zip-raw-copy", static data => CheckRawCopy(data.ToArray()), static _ => false);

    private static byte[] DrainAll(Stream s)
    {
        using (s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    private static void CheckRawCopy(byte[] data)
    {
        for (int mode = 0; mode < 3; mode++)
        {
            var copies = new List<(ZipRawEntry Header, byte[] Raw)>();
            try
            {
                using Stream source = mode == 2 ? new Pieces(data) : new MemoryStream(data);
                using var reader = new ZipReader(source, new ZipReaderOptions { Limits = s_limits, LazyDirectory = mode == 1 });
                var entries = new List<ZipEntry>();
                while (reader.GetNextEntry() is { } e && entries.Count < 40)
                {
                    if (mode == 2)
                    {
                        // Forward-only: the entry has to be opened now, or never.
                        try
                        {
                            AddCopy(copies, e, DrainAll(reader.OpenRawEntryStream(e)));
                        }
                        catch (ArchiveUnsupportedException)
                        {
                        }
                        catch (ArgumentException) when (!e.IsSizeKnown)
                        {
                        }
                    }
                    else
                    {
                        entries.Add(e);
                    }
                }

                // Out of order, last to first.
                var byIndex = new (ZipEntry, byte[])?[entries.Count];
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    byIndex[i] = (entries[i], DrainAll(reader.OpenRawEntryStream(entries[i])));
                }

                foreach ((ZipEntry, byte[])? item in byIndex)
                {
                    if (item is { } x)
                    {
                        AddCopy(copies, x.Item1, x.Item2);
                    }
                }
            }
            catch (CompressionException)
            {
            }

            if (copies.Count == 0)
            {
                continue;
            }

            foreach ((bool seekable, ZipZip64Mode zip64) in new[] { (true, ZipZip64Mode.Auto), (false, ZipZip64Mode.Always) })
            {
                var ms = new MemoryStream();
                using (var w = new ZipWriter(seekable ? ms : new NoSeekWrite(ms), new ZipWriterOptions { Zip64 = zip64 }, leaveOpen: true))
                {
                    foreach ((ZipRawEntry header, byte[] raw) in copies)
                    {
                        w.WriteRawEntry(header, new MemoryStream(raw));
                    }
                }

                using var back = new ZipReader(new MemoryStream(ms.ToArray()), new ZipReaderOptions { Limits = s_limits });
                var listed = new List<ZipEntry>();
                while (back.GetNextEntry() is { } e)
                {
                    listed.Add(e);
                }

                if (listed.Count != copies.Count)
                {
                    throw new InvalidOperationException($"{copies.Count} entries were copied and {listed.Count} read back");
                }

                for (int i = 0; i < listed.Count; i++)
                {
                    ZipRawEntry h = copies[i].Header;
                    ZipEntry y = listed[i];
                    if (!h.RawName.Span.SequenceEqual(y.RawName.Span) || (ushort)h.Method != (ushort)y.Method || h.Flags != y.Flags || h.Crc32 != y.Crc32
                        || h.CompressedSize != y.CompressedSize || h.UncompressedSize != y.UncompressedSize || h.DosTime != y.DosTime || h.DosDate != y.DosDate
                        || h.ExternalAttributes != y.ExternalAttributes || !h.RawComment.Span.SequenceEqual(y.RawComment.Span))
                    {
                        throw new InvalidOperationException($"entry {i} was copied with a different header");
                    }

                    if (!DrainAll(back.OpenRawEntryStream(y)).AsSpan().SequenceEqual(copies[i].Raw))
                    {
                        throw new InvalidOperationException($"entry {i} was copied with different raw bytes");
                    }
                }
            }
        }
    }

    private static void AddCopy(List<(ZipRawEntry, byte[])> copies, ZipEntry e, byte[] raw)
    {
        if (raw.Length != e.CompressedSize)
        {
            throw new InvalidOperationException($"entry '{e.Name}' raw stream is {raw.Length} bytes but its compressed size is {e.CompressedSize}");
        }

        // Names with NUL and oversized extra field sets are refused by the writer by design; leave those entries out.
        if (e.RawName.Span.Contains((byte)0) || e.ExtraFields.Sum(f => f.Data.Length + 4L) > 60_000)
        {
            return;
        }

        copies.Add((ZipRawEntry.FromEntry(e), raw));
    }
}
