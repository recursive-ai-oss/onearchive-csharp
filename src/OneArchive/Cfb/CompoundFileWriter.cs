using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Cfb;

/// <summary>Options for <see cref="CompoundFileWriter"/>.</summary>
public sealed class CompoundFileWriterOptions
{
    /// <summary>The default for <see cref="MaxBufferedBytes"/>: 1 GiB.</summary>
    public const long DefaultMaxBufferedBytes = 1L << 30;

    /// <summary>The options with every default.</summary>
    public static CompoundFileWriterOptions Default { get; } = new();

    /// <summary>Gets the major version to write: 3 (512-byte sectors, the default) or 4 (4096-byte sectors).</summary>
    public int MajorVersion { get; init; } = 3;

    /// <summary>
    /// Gets a value indicating whether names that contain <c>/ \ : !</c> are rejected ([MS-CFB] 2.6.1 forbids them). Default true.
    /// Set it to false to store such names as given, which the reader of this library accepts.
    /// </summary>
    public bool StrictNames { get; init; } = true;

    /// <summary>
    /// Gets the most stream content the writer holds in memory until <c>Finish</c> (the sector layout depends on every size).
    /// Exceeding it raises <see cref="CompressionLimitException"/>. Default 1 GiB.
    /// </summary>
    public long MaxBufferedBytes { get; init; } = DefaultMaxBufferedBytes;
}

/// <summary>
/// Writes a compound file ([MS-CFB] 13.0), version 3 or 4, from storages and streams.
/// </summary>
/// <remarks>
/// <para>Entry names are paths of stored names separated by <c>/</c>, in the form <see cref="CompoundFileReader"/> produces (see
/// <see cref="UnsanitizeName"/>). A directory entry makes a storage and a file entry a stream; missing parent storages are created.
/// An entry with an empty name and kind directory describes the root storage (CLSID, state bits and times). Other kinds raise
/// <see cref="ArchiveUnsupportedException"/>. A <see cref="CfbEntry"/> carries a CLSID, state bits and raw FILETIMEs; for any other
/// entry the CLSID and state bits are zero and <see cref="ArchiveEntry.ModifiedTime"/> / <see cref="ArchiveEntry.ChangeTime"/> are the modified and
/// creation times (the clock is never read; no time means zero, "not recorded").</para>
/// <para>The sector layout needs every size, so stream content is held in memory until <see cref="ArchiveWriter.Finish()"/>
/// (<see cref="CompoundFileWriterOptions.MaxBufferedBytes"/>). The file is then written in one forward pass: header, directory, mini FAT, mini stream,
/// stream data in directory order, FAT, DIFAT. Siblings form a red-black tree in the [MS-CFB] 2.6.4 order. The same entries and options give the same bytes.</para>
/// <para>Version 3 streams are limited to 2 GiB - 1 ([MS-CFB] 2.2). A name is 1 to 31 UTF-16 code units, without NUL, and unique within its storage ignoring case (2.6.4).</para>
/// </remarks>
public sealed class CompoundFileWriter : ArchiveWriter
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;
    private const uint FatSect = 0xFFFFFFFD;
    private const uint DifSect = 0xFFFFFFFC;
    private const uint NoStream = 0xFFFFFFFF;
    private const uint MaxRegularSector = 0xFFFFFFF9;
    private const long FileTimeToDateTimeTicks = 504911232000000000L;
    private const int MiniSectorSize = 64;
    private const int MiniCutoff = 4096;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly CompoundFileWriterOptions _options;
    private readonly Node _root = new("Root Entry", isStream: false);
    private long _buffered;
    private int _count = 1;
    private bool _finished;
    private bool _faulted;
    private bool _disposed;

    private sealed class Node
    {
        public Node(string name, bool isStream)
        {
            Name = name;
            IsStream = isStream;
        }

        public string Name;
        public readonly bool IsStream;
        public bool Explicit;
        public byte[] Data = [];
        public readonly Dictionary<string, Node> ByKey = new(StringComparer.Ordinal);
        public List<Node> Sorted = [];
        public Guid ClassId;
        public uint State;
        public ulong Created;
        public ulong Modified;
        public int Id;
        public uint Left = NoStream, Right = NoStream, Child = NoStream;
        public byte Color = 1;
        public uint Start;
    }

    /// <summary>Creates a writer.</summary>
    /// <param name="stream">A writable stream. Nothing is written until <c>Finish</c>.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the writer is disposed.</param>
    /// <exception cref="ArgumentException">The stream is not writable, or the major version is not 3 or 4.</exception>
    public CompoundFileWriter(Stream stream, CompoundFileWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _options = options ?? CompoundFileWriterOptions.Default;
        if (_options.MajorVersion is not (3 or 4))
        {
            throw new ArgumentException("The major version must be 3 or 4.", nameof(options));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public override ArchiveFormat Format => ArchiveFormat.CompoundFile;

    /// <summary>
    /// The inverse of <see cref="CompoundFileReader.SanitizeName(string)"/>: each <c>%</c> followed by four uppercase hex digits becomes that UTF-16 code unit.
    /// Any other <c>%</c> is kept as it is, so a name the reader produced always maps back to the stored name.
    /// </summary>
    /// <param name="name">One path component.</param>
    /// <returns>The stored name.</returns>
    public static string UnsanitizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '%' && i + 5 <= name.Length && TryHex4(name.AsSpan(i + 1, 4), out int unit))
            {
                sb.Append((char)unit);
                i += 4;
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool TryHex4(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        foreach (char c in s)
        {
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0)
            {
                return false;
            }

            value = (value << 4) | d;
        }

        return true;
    }

    private static string KeyOf(string name)
    {
        // [MS-CFB] 2.6.4: names are equal when equal after simple upper-casing of each UTF-16 code unit.
        return string.Create(name.Length, name, static (span, n) =>
        {
            for (int i = 0; i < n.Length; i++)
            {
                span[i] = char.ToUpperInvariant(n[i]);
            }
        });
    }

    private static ulong ToFileTime(DateTimeOffset? t, string what, string entry)
    {
        if (t is not { } v)
        {
            return 0;
        }

        long ticks = v.UtcTicks - FileTimeToDateTimeTicks;
        if (ticks < 0)
        {
            throw new ArgumentException($"The {what} of '{entry}' is before 1601-01-01, which a FILETIME cannot hold.", nameof(entry));
        }

        return (ulong)ticks;
    }

    private string CheckName(string stored, string path)
    {
        if (stored.Length is 0 or > 31)
        {
            throw new ArgumentException($"The name '{CompoundFileReader.SanitizeName(stored)}' in '{path}' must have 1 to 31 UTF-16 code units ([MS-CFB] 2.6.1).");
        }

        if (stored.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"A name in '{path}' contains NUL.");
        }

        if (_options.StrictNames && stored.AsSpan().IndexOfAny("/\\:!") >= 0)
        {
            throw new ArgumentException($"The name '{CompoundFileReader.SanitizeName(stored)}' in '{path}' contains one of / \\ : !, which [MS-CFB] 2.6.1 forbids (see CompoundFileWriterOptions.StrictNames).");
        }

        return stored;
    }

    /// <inheritdoc />
    protected override async ValueTask WriteEntryAsync(ArchiveEntry entry, Stream? content, bool async, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The compound file is already finished.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory))
        {
            throw new ArchiveUnsupportedException(CompoundFileReader.Name, $"{entry.Kind} entries (entry '{entry.Name}'); a compound file holds storages and streams only");
        }

        bool isStream = entry.Kind == ArchiveEntryKind.File;
        string path = entry.Name ?? throw new ArgumentException("The entry has no name.", nameof(entry));
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 && isStream)
        {
            throw new ArgumentException("A stream needs a non-empty name.", nameof(entry));
        }

        long size = entry.Size;
        if (isStream)
        {
            if (size < 0)
            {
                throw new ArgumentException("The entry size is negative.", nameof(entry));
            }

            if (_options.MajorVersion == 3 && size > int.MaxValue)
            {
                throw new ArchiveUnsupportedException(CompoundFileReader.Name, $"a stream of {size} bytes (entry '{entry.Name}'); version 3 streams are limited to 2 GiB - 1");
            }

            if (size > 0 && content is null)
            {
                throw new ArgumentException("A file entry with a size needs content.", nameof(entry));
            }

            if (size > _options.MaxBufferedBytes - _buffered)
            {
                throw new CompressionLimitException(CompoundFileReader.Name, nameof(CompoundFileWriterOptions.MaxBufferedBytes), _options.MaxBufferedBytes, _buffered + size);
            }
        }

        // Resolve and validate everything before touching the tree, so a rejected entry leaves the writer usable.
        var stored = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "." or "..")
            {
                throw new ArgumentException($"The entry name '{path}' has a '{parts[i]}' component.", nameof(entry));
            }

            stored[i] = CheckName(UnsanitizeName(parts[i]), path);
        }

        byte[] data = [];
        if (isStream && size > 0)
        {
            data = new byte[size];
            int got = 0;
            while (got < data.Length)
            {
                int n = async
                    ? await content!.ReadAsync(data.AsMemory(got), cancellationToken).ConfigureAwait(false)
                    : content!.Read(data, got, data.Length - got);
                if (n <= 0)
                {
                    throw new ArgumentException($"The content of '{path}' ended after {got} bytes; the entry size is {size}.", nameof(content));
                }

                got += n;
            }
        }

        var cfb = entry as CfbEntry;
        ulong created = cfb is { CreationFileTime: not 0 } ? cfb.CreationFileTime : ToFileTime(entry.ChangeTime, "change time", path);
        ulong modified = cfb is { ModifiedFileTime: not 0 } ? cfb.ModifiedFileTime : ToFileTime(entry.ModifiedTime, "modified time", path);
        Guid clsid = cfb?.ClassId ?? default;
        uint state = cfb?.StateBits ?? 0;

        Node parent = _root;
        var made = new List<(Node Parent, string Key)>();
        try
        {
            for (int i = 0; i < stored.Length; i++)
            {
                bool last = i == stored.Length - 1;
                string key = KeyOf(stored[i]);
                if (parent.ByKey.TryGetValue(key, out Node? existing))
                {
                    if (last)
                    {
                        if (isStream || existing.IsStream || existing.Explicit)
                        {
                            throw new ArgumentException($"The entry '{path}' duplicates an earlier entry (names are compared ignoring case).", nameof(entry));
                        }
                    }
                    else if (existing.IsStream)
                    {
                        throw new ArgumentException($"The entry '{path}' lies below the stream '{existing.Name}'.", nameof(entry));
                    }

                    parent = existing;
                    continue;
                }

                if (_count + 1 > CompoundFileReaderOptions.DefaultMaxDirectoryEntries)
                {
                    throw new CompressionLimitException(CompoundFileReader.Name, "directory entries", CompoundFileReaderOptions.DefaultMaxDirectoryEntries, _count + 1L);
                }

                var node = new Node(stored[i], isStream && last);
                parent.ByKey.Add(key, node);
                made.Add((parent, key));
                _count++;
                parent = node;
            }
        }
        catch
        {
            foreach (var (p, k) in made)
            {
                p.ByKey.Remove(k);
                _count--;
            }

            throw;
        }

        parent.Explicit = true;
        parent.ClassId = clsid;
        parent.State = state;
        parent.Created = created;
        parent.Modified = modified;
        parent.Data = data;
        _buffered += data.Length;
    }

    /// <inheritdoc />
    protected override async ValueTask FinishAsync(bool async, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            await WriteFileAsync(async, cancellationToken).ConfigureAwait(false);
            if (async)
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _stream.Flush();
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    // [MS-CFB] 2.6.4: left is less than right; a shorter name is less, then compare upper-cased code units.
    private static int Compare(Node a, Node b) => CompoundFileReader.CompareNames(a.Name, b.Name);

    private int _next;

    private static uint Link(List<Node> kids, int lo, int hi, int depth, int maxDepth, bool perfect)
    {
        if (lo >= hi)
        {
            return NoStream;
        }

        int mid = lo + ((hi - lo) / 2);
        Node k = kids[mid];
        k.Color = (byte)(depth == maxDepth && !perfect ? 0 : 1);
        k.Left = Link(kids, lo, mid, depth + 1, maxDepth, perfect);
        k.Right = Link(kids, mid + 1, hi, depth + 1, maxDepth, perfect);
        return (uint)k.Id;
    }

    private async ValueTask WriteFileAsync(bool async, CancellationToken ct)
    {
        int version = _options.MajorVersion;
        int ss = version == 3 ? 512 : 4096;
        int per = ss / 4;

        // Nesting can be as deep as the entry count allows, so the tree is walked with an explicit stack, never recursion.
        var all = new List<Node>(_count);
        NumberIteratively(all);

        // ---- mini stream and mini FAT ([MS-CFB] 2.5) ----
        var miniFat = new List<uint>();
        long miniBytes = 0;
        foreach (Node n in all)
        {
            if (n.IsStream && n.Data.Length is > 0 and < MiniCutoff)
            {
                int cnt = (n.Data.Length + MiniSectorSize - 1) / MiniSectorSize;
                n.Start = (uint)miniFat.Count;
                for (int i = 0; i < cnt; i++)
                {
                    miniFat.Add(i < cnt - 1 ? (uint)miniFat.Count + 1 : EndOfChain);
                }

                miniBytes += (long)cnt * MiniSectorSize;
            }
        }

        long miniSectors = (miniBytes + ss - 1) / ss;
        long miniFatSectors = (miniFat.Count + per - 1) / per;
        long dirSectors = ((long)all.Count * 128 + ss - 1) / ss;

        // ---- sector allocation: directory, mini FAT, mini stream, big streams, FAT, DIFAT ----
        long next = 0;
        uint dirStart = (uint)next;
        next += dirSectors;
        long miniFatStart = next;
        next += miniFatSectors;
        long miniStart = next;
        next += miniSectors;
        long dataStart = next;
        foreach (Node n in all)
        {
            if (n.IsStream && n.Data.Length >= MiniCutoff)
            {
                n.Start = (uint)next;
                next += (n.Data.Length + ss - 1) / ss;
            }
        }

        long other = next;
        long nfat = 0, ndif = 0;
        while (true)
        {
            long total = other + nfat + ndif;
            long nf = (total + per - 1) / per;
            long nd = Math.Max(0, nf - 109) == 0 ? 0 : (nf - 109 + per - 2) / (per - 1);
            if (nf == nfat && nd == ndif)
            {
                break;
            }

            nfat = nf;
            ndif = nd;
        }

        long fatStart = other;
        long difStart = fatStart + nfat;
        long totalSectors = difStart + ndif;
        if (totalSectors > MaxRegularSector)
        {
            throw new CompressionLimitException(CompoundFileReader.Name, "sectors", MaxRegularSector, totalSectors);
        }

        // ---- the FAT ----
        var fat = new uint[nfat * per];
        Array.Fill(fat, FreeSect);
        void Chain(long first, long count)
        {
            for (long i = 0; i < count; i++)
            {
                fat[first + i] = i + 1 < count ? (uint)(first + i + 1) : EndOfChain;
            }
        }

        Chain(0, dirSectors);
        Chain(miniFatStart, miniFatSectors);
        Chain(miniStart, miniSectors);
        foreach (Node n in all)
        {
            if (n.IsStream && n.Data.Length >= MiniCutoff)
            {
                Chain(n.Start, (n.Data.Length + ss - 1) / ss);
            }
        }

        for (long i = 0; i < nfat; i++)
        {
            fat[fatStart + i] = FatSect;
        }

        for (long i = 0; i < ndif; i++)
        {
            fat[difStart + i] = DifSect;
        }

        // ---- header ([MS-CFB] 2.2) ----
        byte[] sector = new byte[ss];
        Span<byte> h = sector;
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[24..], 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)version);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(h[30..], (ushort)(version == 3 ? 9 : 12));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], version == 4 ? (uint)dirSectors : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[44..], (uint)nfat);
        BinaryPrimitives.WriteUInt32LittleEndian(h[48..], dirStart);
        BinaryPrimitives.WriteUInt32LittleEndian(h[56..], MiniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(h[60..], miniFatSectors > 0 ? (uint)miniFatStart : EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(h[64..], (uint)miniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(h[68..], ndif > 0 ? (uint)difStart : EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(h[72..], (uint)ndif);
        for (int i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[(76 + (i * 4))..], i < nfat ? (uint)(fatStart + i) : FreeSect);
        }

        await WriteAsync(sector, async, ct).ConfigureAwait(false);

        // ---- directory ([MS-CFB] 2.6) ----
        _root.Start = miniSectors > 0 ? (uint)miniStart : EndOfChain;
        var dir = new byte[dirSectors * ss];
        for (int i = 0; i < dir.Length / 128; i++)
        {
            Span<byte> rec = dir.AsSpan(i * 128, 128);
            if (i >= all.Count)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(rec[68..], NoStream);
                BinaryPrimitives.WriteUInt32LittleEndian(rec[72..], NoStream);
                BinaryPrimitives.WriteUInt32LittleEndian(rec[76..], NoStream);
                continue;
            }

            Node n = all[i];
            string name = ReferenceEquals(n, _root) ? "Root Entry" : n.Name;
            for (int k = 0; k < name.Length; k++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(rec[(k * 2)..], name[k]);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(rec[64..], (ushort)((name.Length + 1) * 2));
            rec[66] = ReferenceEquals(n, _root) ? (byte)5 : n.IsStream ? (byte)2 : (byte)1;
            rec[67] = n.Color;
            BinaryPrimitives.WriteUInt32LittleEndian(rec[68..], n.Left);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[72..], n.Right);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[76..], n.Child);
            n.ClassId.TryWriteBytes(rec[80..], bigEndian: false, out _);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[96..], n.State);
            BinaryPrimitives.WriteUInt64LittleEndian(rec[100..], n.Created);
            BinaryPrimitives.WriteUInt64LittleEndian(rec[108..], n.Modified);
            uint start;
            ulong sz;
            if (ReferenceEquals(n, _root))
            {
                start = _root.Start;
                sz = (ulong)miniBytes;
            }
            else if (n.IsStream)
            {
                start = n.Data.Length == 0 ? EndOfChain : n.Start;
                sz = (ulong)n.Data.Length;
            }
            else
            {
                start = 0;
                sz = 0;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(rec[116..], start);
            BinaryPrimitives.WriteUInt64LittleEndian(rec[120..], sz);
        }

        await WriteAsync(dir, async, ct).ConfigureAwait(false);

        // ---- mini FAT ----
        var mf = new byte[miniFatSectors * ss];
        for (int i = 0; i < mf.Length / 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(mf.AsSpan(i * 4), i < miniFat.Count ? miniFat[i] : FreeSect);
        }

        await WriteAsync(mf, async, ct).ConfigureAwait(false);

        // ---- mini stream ----
        byte[] pad = new byte[MiniSectorSize];
        long written = 0;
        foreach (Node n in all)
        {
            if (n.IsStream && n.Data.Length is > 0 and < MiniCutoff)
            {
                await WriteAsync(n.Data, async, ct).ConfigureAwait(false);
                int fill = (MiniSectorSize - (n.Data.Length % MiniSectorSize)) % MiniSectorSize;
                await WriteAsync(pad.AsMemory(0, fill), async, ct).ConfigureAwait(false);
                written += n.Data.Length + fill;
            }
        }

        await WriteAsync(new byte[(miniSectors * ss) - written], async, ct).ConfigureAwait(false);

        // ---- big streams ----
        foreach (Node n in all)
        {
            if (n.IsStream && n.Data.Length >= MiniCutoff)
            {
                await WriteAsync(n.Data, async, ct).ConfigureAwait(false);
                int fill = (ss - (n.Data.Length % ss)) % ss;
                await WriteAsync(new byte[fill], async, ct).ConfigureAwait(false);
            }
        }

        // ---- FAT ----
        byte[] fatBytes = new byte[nfat * ss];
        for (int i = 0; i < fat.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), fat[i]);
        }

        await WriteAsync(fatBytes, async, ct).ConfigureAwait(false);

        // ---- DIFAT ([MS-CFB] 2.4): FAT sector numbers beyond the header's 109, then the next DIFAT sector ----
        byte[] difBytes = new byte[ndif * ss];
        for (long d = 0; d < ndif; d++)
        {
            Span<byte> s = difBytes.AsSpan((int)(d * ss), ss);
            for (int i = 0; i < per - 1; i++)
            {
                long f = 109 + (d * (per - 1)) + i;
                BinaryPrimitives.WriteUInt32LittleEndian(s[(i * 4)..], f < nfat ? (uint)(fatStart + f) : FreeSect);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(s[((per - 1) * 4)..], d + 1 < ndif ? (uint)(difStart + d + 1) : EndOfChain);
        }

        await WriteAsync(difBytes, async, ct).ConfigureAwait(false);
    }

    private void NumberIteratively(List<Node> all)
    {
        // Preorder numbering with an explicit stack of (storage, next child index); the red-black links are set when a storage's children are known.
        _next = 0;
        var stack = new Stack<(Node Node, int Index)>();
        _root.Id = _next++;
        all.Add(_root);
        SortChildren(_root);
        stack.Push((_root, 0));
        while (stack.Count > 0)
        {
            var (node, index) = stack.Pop();
            if (index >= node.Sorted.Count)
            {
                continue;
            }

            stack.Push((node, index + 1));
            Node kid = node.Sorted[index];
            kid.Id = _next++;
            all.Add(kid);
            if (!kid.IsStream)
            {
                SortChildren(kid);
                stack.Push((kid, 0));
            }
        }

        // Links need the children's final ids, which exist only now.
        foreach (Node n in all)
        {
            if (!n.IsStream && n.Sorted.Count > 0)
            {
                int maxDepth = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)n.Sorted.Count);
                bool perfect = ((n.Sorted.Count + 1) & n.Sorted.Count) == 0;
                n.Child = Link(n.Sorted, 0, n.Sorted.Count, 1, maxDepth, perfect);
            }
        }
    }

    private static void SortChildren(Node n)
    {
        var kids = new List<Node>(n.ByKey.Values);
        kids.Sort(Compare);
        n.Sorted = kids;
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> data, bool async, CancellationToken ct)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (async)
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        else
        {
            _stream.Write(data.Span);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }

        try
        {
            if (!_finished && !_faulted)
            {
                Finish();
            }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_finished && !_faulted)
            {
                await FinishAsync(true, default).ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
