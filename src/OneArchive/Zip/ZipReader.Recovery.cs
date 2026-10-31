using System.Buffers.Binary;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Zip;

// Recovery mode (ZipReaderOptions.Recovery): finding the next local header after damage, and reporting what was lost.
public sealed partial class ZipReader
{
    // APPNOTE 4.3.7: "PK\3\4", the local file header signature, as it appears in the byte stream.
    private static readonly byte[] s_localSignature = [0x50, 0x4B, 0x03, 0x04];

    // A candidate header is only accepted when its name and extra field fit the input buffer together with the 30 fixed
    // bytes, and its name is at most this long. The bounds keep the check per candidate small, so that a scan over data
    // full of signatures costs time proportional to its length.
    private const int MaxCandidateName = 4096;

    private readonly List<ZipDamage> _damage = [];
    private long _recoveryBytes;
    private long _dataStart;            // archive offset of the current entry's data (forward reading)
    private long _rewindTo = -1;        // where to look again if the bytes after the current entry are not a header; -1: nowhere
    private bool _entryFailed;
    private Stream? _wrapper;

    /// <summary>
    /// Gets what recovery skipped or found damaged, in the order it was found. Always empty unless <see cref="ZipReaderOptions.Recovery"/> is set,
    /// and empty after a complete read of an undamaged archive. The list grows as entries are read: check it after the last
    /// <see cref="GetNextEntry"/>, and after reading an entry's content to learn whether that entry is damaged.
    /// </summary>
    public IReadOnlyList<ZipDamage> Damage => _damage;

    /// <summary>Gets a value indicating whether recovery has skipped or found anything damaged so far. See <see cref="Damage"/>.</summary>
    public bool IsRecovered => _damage.Count > 0;

    private void AddDamage(ZipDamageKind kind, long offset, long length, string? name, string reason)
    {
        if (_damage.Count >= Limits.MaxEntryCount)
        {
            throw new CompressionLimitException(ZipFormat.Name, nameof(ArchiveLimits.MaxEntryCount), Limits.MaxEntryCount, _damage.Count + 1L);
        }

        _damage.Add(new ZipDamage(kind, offset, Math.Max(0, length), name, reason));
    }

    private void Charge(long bytes)
    {
        _recoveryBytes += bytes;
        if (_recoveryBytes > _options.MaxRecoveryBytes)
        {
            throw new CompressionLimitException(ZipFormat.Name, nameof(ZipReaderOptions.MaxRecoveryBytes), _options.MaxRecoveryBytes, _recoveryBytes);
        }
    }

    // The end record or central directory could not be used: read the local headers instead.
    private void FallBackToForward(CompressionFormatException ex)
    {
        _entries = null;
        _byOffset = null;
        _comment = string.Empty;
        _useDirectory = false;
        _buf = new ZipInputBuffer(_stream, _origin, 0);
        AddDamage(ZipDamageKind.DirectoryUnusable, ex.Offset, 0, null, ex.Message);
    }

    // In recovery mode a directory is only trusted when every entry's local header is where it says: garbage inserted or
    // removed in the body leaves a well-formed directory whose offsets are wrong.
    private async ValueTask VerifyLocalSignaturesAsync(bool async, CancellationToken ct)
    {
        byte[] sig = new byte[4];
        foreach (ZipEntry e in _entries!)
        {
            await ReadAtAsync(e.LocalHeaderOffset, sig, async, ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(sig) != ZipFormat.LocalSignature)
            {
                throw ZipFormat.Bad(e.LocalHeaderOffset, $"a local file header signature (PK\\3\\4) for entry '{Printable(e.Name)}', as the central directory says");
            }
        }
    }

    private ZipRecoveryEntryStream Wrap(ZipEntry entry, Stream inner, bool forward) => new ZipRecoveryEntryStream(this, entry, inner, forward);

    // Called by the wrapper around an entry's content stream.
    internal void EntryFailed(ZipEntry entry, CompressionFormatException ex, bool forward)
    {
        if (forward)
        {
            if (_entryFailed)
            {
                return;
            }

            _entryFailed = true;
        }

        NoteEntryDamage(entry.Name, entry.LocalHeaderOffset, ex);
    }

    internal void EntryCompleted(bool forward)
    {
        if (forward)
        {
            _rewindTo = -1; // the entry decoded and checked out, so the bytes after it are not part of it
        }
    }

    private void NoteEntryDamage(string name, long headerOffset, CompressionFormatException ex) =>
        AddDamage(ex.IsTruncated ? ZipDamageKind.Truncated : ZipDamageKind.DamagedEntry, headerOffset, ex.Offset - headerOffset, name, ex.Message);

    // After an entry failed: put the input where the entry should have ended, when its size says so.
    private async ValueTask MoveToDeclaredEndAsync(Rec cur, bool async, CancellationToken ct)
    {
        ZipInputBuffer buf = _buf!;
        if (!cur.SizeKnown || (cur.Flags & ZipFormat.FlagDescriptor) != 0)
        {
            return;
        }

        long end = _dataStart + cur.CompressedSize;
        if (buf.CanSeek)
        {
            buf.SeekTo(Math.Min(end, _stream.Length - _origin));
        }
        else if (end > buf.Offset)
        {
            await buf.SkipAsync(end - buf.Offset, async, ct).ConfigureAwait(false);
        }
    }

    private void ReportSkippedToEnd(long at, long rewound, long end)
    {
        long from = at <= end ? at : (rewound >= 0 ? rewound : end);
        if (end > from)
        {
            AddDamage(ZipDamageKind.SkippedBytes, from, end - from, null, "no further local file header (PK\\3\\4) before the end of the archive");
        }
    }

    // The input is at `at`, which is not the start of a plausible local header (or is the end of the input). Finds the next
    // one. Returns false when the input ends first. Reports the bytes passed over.
    private async ValueTask<bool> RecoverAsync(ZipInputBuffer buf, long at, bool skipFirst, bool async, CancellationToken ct)
    {
        long rewound = -1;
        if (_rewindTo >= 0 && _rewindTo < at && buf.CanSeek)
        {
            // The entry before did not end at a header: its declared size may be wrong. Look through its data too.
            rewound = _rewindTo;
            Charge(at - rewound);
            buf.SeekTo(rewound);
            skipFirst = false;
        }

        _rewindTo = -1;
        if (skipFirst)
        {
            buf.Consume(1);
            Charge(1);
        }

        while (true)
        {
            if (buf.Available == 0 && !await buf.FillAsync(async, ct).ConfigureAwait(false))
            {
                ReportSkippedToEnd(at, rewound, buf.Offset);
                return false;
            }

            int i = buf.Span.IndexOf(s_localSignature);
            if (i < 0)
            {
                // Keep the last three bytes: a signature may straddle the next read.
                int pass = Math.Max(0, buf.Available - 3);
                buf.Consume(pass);
                Charge(pass);
                if (!await buf.FillAsync(async, ct).ConfigureAwait(false))
                {
                    int rest = buf.Available;
                    buf.Consume(rest);
                    Charge(rest);
                    ReportSkippedToEnd(at, rewound, buf.Offset);
                    return false;
                }

                continue;
            }

            buf.Consume(i);
            Charge(i);
            if (await LooksLikeLocalHeaderAsync(buf, async, ct).ConfigureAwait(false))
            {
                long found = buf.Offset;
                if (rewound >= 0 && found < at)
                {
                    AddDamage(ZipDamageKind.SkippedBytes, rewound, found - rewound, null, "the data of the preceding entry ended before its declared size; resumed at the next local file header");
                }
                else
                {
                    AddDamage(ZipDamageKind.SkippedBytes, at, found - at, null, "bytes that are not a local file header; resumed at the next one");
                }

                return true;
            }

            buf.Consume(1);
            Charge(1);
        }
    }

    // Decides whether the bytes at the input's position, which start with a local header signature, are a header. The
    // fields are checked against what writers produce (APPNOTE 4.4), so that a signature that happens to occur inside
    // compressed data is rejected. Nothing is consumed.
    private async ValueTask<bool> LooksLikeLocalHeaderAsync(ZipInputBuffer buf, bool async, CancellationToken ct)
    {
        if (!await buf.EnsureAsync(ZipFormat.LocalHeaderSize, async, ct).ConfigureAwait(false))
        {
            return false;
        }

        ReadOnlySpan<byte> h = buf.Span;
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
        ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]);
        ushort date = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
        uint comp32 = BinaryPrimitives.ReadUInt32LittleEndian(h[18..]);
        uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(h[22..]);
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[26..]);
        int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);

        // 4.4.3: the version is a number such as 20 or 45 (2.0, 4.5). 4.4.4: bits 7 to 10 are unused, 14 and 15 reserved.
        if (version > 99 || (flags & 0xC780) != 0 || !KnownMethod(method) || nameLen == 0 || nameLen > MaxCandidateName)
        {
            return false;
        }

        // 4.4.6: a zero date is common (no date known); otherwise month and day must exist.
        if (date != 0 && (((date >> 5) & 15) is < 1 or > 12 || (date & 31) is < 1))
        {
            return false;
        }

        // A stored entry that is not encrypted and has no data descriptor has equal sizes.
        if (method == 0 && (flags & (ZipFormat.FlagDescriptor | 1)) == 0 && comp32 != size32)
        {
            return false;
        }

        int need = ZipFormat.LocalHeaderSize + nameLen + extraLen;
        Charge(nameLen + extraLen); // the checks below read this much; charging it bounds the total work on any input
        if (need > ZipInputBuffer.Size || !await buf.EnsureAsync(need, async, ct).ConfigureAwait(false))
        {
            return false;
        }

        h = buf.Span;
        ReadOnlySpan<byte> name = h.Slice(ZipFormat.LocalHeaderSize, nameLen);
        foreach (byte b in name)
        {
            if (b < 0x20 || b == 0x7F)
            {
                return false;
            }
        }

        if ((flags & 0x800) != 0 && !System.Text.Unicode.Utf8.IsValid(name))
        {
            return false;
        }

        // 4.5.1: the extra field is a chain of (id, size, data) records, and 4.5.3 puts the sizes of a header that
        // holds 0xFFFFFFFF in the zip64 record.
        ReadOnlySpan<byte> extra = h.Slice(ZipFormat.LocalHeaderSize + nameLen, extraLen);
        bool needZip64 = size32 == ZipFormat.Max32 || comp32 == ZipFormat.Max32;
        bool haveZip64 = false;
        int p = 0;
        while (extra.Length - p >= 4)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra[p..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(p + 2)..]);
            if (size > extra.Length - p - 4)
            {
                return false;
            }

            if (id == ZipFormat.ExtraZip64 && size >= 16)
            {
                haveZip64 = true;
            }

            p += 4 + size;
        }

        return !needZip64 || haveZip64;
    }

    private static bool KnownMethod(ushort method) => method is (>= 0 and <= 6) or 8 or 9 or 10 or 12 or 14 or 18 or 19 or 20 or (>= 93 and <= 99);
}

/// <summary>Wraps an entry's content stream in recovery mode: a failure while reading is recorded in the reader's damage list and still raised.</summary>
internal sealed class ZipRecoveryEntryStream(ZipReader owner, ZipEntry entry, Stream inner, bool forward) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Note(() => inner.Read(buffer, offset, count), count);

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return Done(inner.Read(buffer), buffer.Length);
        }
        catch (CompressionFormatException ex)
        {
            owner.EntryFailed(entry, ex, forward);
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return Done(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);
        }
        catch (CompressionFormatException ex)
        {
            owner.EntryFailed(entry, ex, forward);
            throw;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Note(Func<int> read, int requested)
    {
        try
        {
            return Done(read(), requested);
        }
        catch (CompressionFormatException ex)
        {
            owner.EntryFailed(entry, ex, forward);
            throw;
        }
    }

    private int Done(int n, int requested)
    {
        if (n == 0 && requested > 0)
        {
            owner.EntryCompleted(forward);
        }

        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
