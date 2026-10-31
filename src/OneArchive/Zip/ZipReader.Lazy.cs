using System.Buffers.Binary;

namespace RecursiveAI.OneArchive.Zip;

// Lazy directory mode (ZipReaderOptions.LazyDirectory): the central directory (APPNOTE 4.3.12) is read record by record
// instead of being turned into a list of entries, so memory does not grow with the entry count.
//
// The default mode sorts every record by local header offset to give each entry the offset where its data must end and to
// refuse entries that share a header. Here that is done without holding the records:
//  - when the directory lists the entries in increasing offset order (what writers produce), an entry's neighbour is simply
//    the next record, so one pass with one record of look-behind checks everything;
//  - otherwise the offsets alone are kept (8 bytes per entry), sorted, and each entry's neighbour is found by binary search.
// The whole directory is checked before the first entry is returned, so a malformed archive is refused at the same point as in
// the default mode; the entries are then produced by reading the directory a second time.
public sealed partial class ZipReader
{
    private sealed class LazyDirectory(ZipInputBuffer buf, long dirStart, long dirSize, long adjust, ulong total)
    {
        public readonly ZipInputBuffer Buf = buf;
        public readonly long DirStart = dirStart;
        public readonly long DirSize = dirSize;
        public readonly long Adjust = adjust;
        public readonly ulong Total = total;
        public ulong Index;                 // records handed out so far
        public Rec? Ahead;                  // the record after the one being handed out (ordered directories)
        public long[]? Sorted;              // all local header offsets, ascending (unordered directories)
    }

    private async ValueTask StartLazyDirectoryAsync(ulong total, long dirStart, long dirSize, long adjust, bool async, CancellationToken ct)
    {
        long[]? sorted;
        using (var check = new ZipInputBuffer(_stream, _origin, dirStart))
        {
            sorted = await ValidateLazyDirectoryAsync(check, total, dirStart, dirSize, adjust, async, ct).ConfigureAwait(false);
        }

        _lazy = new LazyDirectory(new ZipInputBuffer(_stream, _origin, dirStart), dirStart, dirSize, adjust, total) { Sorted = sorted };
    }

    private async ValueTask<Rec> ReadLazyRecordAsync(ZipInputBuffer buf, long dirStart, long dirSize, long adjust, bool decode, bool async, CancellationToken ct)
    {
        if (buf.Offset - dirStart > dirSize - ZipFormat.CentralHeaderSize)
        {
            throw ZipFormat.Bad(buf.Offset, "the central directory to hold all its entries");
        }

        return await ReadDirectoryRecordAsync(buf, adjust, dirStart, async, ct, decode).ConfigureAwait(false);
    }

    // Reads the whole directory once, keeping only the previous record. Returns the sorted offsets when the directory is not in
    // offset order, otherwise null.
    private async ValueTask<long[]?> ValidateLazyDirectoryAsync(ZipInputBuffer buf, ulong total, long dirStart, long dirSize, long adjust, bool async, CancellationToken ct)
    {
        Rec? prev = null;
        bool ordered = true;
        byte[] sig = new byte[4];
        for (ulong i = 0; i < total; i++)
        {
            Rec r = await ReadLazyRecordAsync(buf, dirStart, dirSize, adjust, decode: false, async, ct).ConfigureAwait(false);
            if (r.RawName.Length == 0)
            {
                throw ZipFormat.Bad(r.LocalOffset, "a non-empty entry name");
            }

            if (_options.Recovery)
            {
                // A directory is only trusted when every local header is where it says (see VerifyLocalSignaturesAsync).
                await ReadAtAsync(r.LocalOffset, sig, async, ct).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt32LittleEndian(sig) != ZipFormat.LocalSignature)
                {
                    throw ZipFormat.Bad(r.LocalOffset, $"a local file header signature (PK\\3\\4) for entry '{Printable(NameOf(r))}', as the central directory says");
                }
            }

            if (ordered && prev is not null)
            {
                if (r.LocalOffset <= prev.LocalOffset)
                {
                    // Not in order (or a shared offset): decided by the sorted pass below.
                    ordered = false;
                }
                else
                {
                    CheckExtent(prev, r.LocalOffset);
                }
            }

            prev = r;
        }

        if (ordered)
        {
            if (prev is not null)
            {
                CheckExtent(prev, dirStart);
            }

            return null;
        }

        // Second pass: the offsets, sorted. A repeated offset means two entries share a local header.
        long[] offsets = new long[(int)total];
        buf.SeekTo(dirStart);
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = (await ReadLazyRecordAsync(buf, dirStart, dirSize, adjust, decode: false, async, ct).ConfigureAwait(false)).LocalOffset;
        }

        Array.Sort(offsets);
        for (int i = 1; i < offsets.Length; i++)
        {
            if (offsets[i] == offsets[i - 1])
            {
                throw ZipFormat.Bad(offsets[i], "entries with different local header offsets (two entries share one, so their data overlaps)");
            }
        }

        // Third pass: every entry must end before the next one in offset order starts.
        buf.SeekTo(dirStart);
        for (int i = 0; i < offsets.Length; i++)
        {
            Rec r = await ReadLazyRecordAsync(buf, dirStart, dirSize, adjust, decode: false, async, ct).ConfigureAwait(false);
            CheckExtent(r, NextOffset(offsets, r.LocalOffset, dirStart));
        }

        return offsets;
    }

    private static long NextOffset(long[] sorted, long offset, long dirStart)
    {
        int at = Array.BinarySearch(sorted, offset);
        return at >= 0 && at + 1 < sorted.Length ? sorted[at + 1] : dirStart;
    }

    // The entry's data has to fit between its local header and the next thing in the archive (same rule as BuildEntries).
    private static void CheckExtent(Rec r, long next)
    {
        long minimum = r.LocalOffset + ZipFormat.LocalHeaderSize + r.RawName.Length;
        if (r.CompressedSize > next - minimum)
        {
            throw ZipFormat.Bad(r.LocalOffset, $"entry '{Printable(NameOf(r))}' to end before the next local header or the central directory (its compressed size of {r.CompressedSize} overlaps what follows)");
        }
    }

    // Records read without decoding carry no name; decode it for a message.
    private static string NameOf(Rec r) => r.Name.Length > 0
        ? r.Name
        : ZipFormat.DecodeName(r.RawName, r.Flags, ZipFormat.Find(r.Extra, ZipFormat.ExtraUnicodePath), null);

    private async ValueTask<ArchiveEntry?> ReadNextLazyAsync(bool async, CancellationToken ct)
    {
        LazyDirectory d = _lazy!;
        if (d.Index >= d.Total)
        {
            return null;
        }

        Rec cur = d.Ahead ?? await ReadLazyRecordAsync(d.Buf, d.DirStart, d.DirSize, d.Adjust, decode: true, async, ct).ConfigureAwait(false);
        d.Ahead = null;
        d.Index++;
        if (d.Sorted is { } sorted)
        {
            cur.Boundary = NextOffset(sorted, cur.LocalOffset, d.DirStart);
        }
        else if (d.Index < d.Total)
        {
            d.Ahead = await ReadLazyRecordAsync(d.Buf, d.DirStart, d.DirSize, d.Adjust, decode: true, async, ct).ConfigureAwait(false);
            cur.Boundary = d.Ahead.LocalOffset;
        }
        else
        {
            cur.Boundary = d.DirStart;
        }

        ZipEntry entry = Make(cur, null, _token);
        if (entry.Kind == ArchiveEntryKind.Symlink)
        {
            entry = await ResolveLinkAsync(entry, async, ct).ConfigureAwait(false);
        }

        if (d.Index == d.Total)
        {
            d.Buf.Dispose();
        }

        return entry;
    }
}
