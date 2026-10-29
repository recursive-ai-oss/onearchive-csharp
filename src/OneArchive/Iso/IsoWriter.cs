using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RecursiveAI.OneArchive.Iso;

/// <summary>
/// Writes ISO 9660 images (ECMA-119) with a Joliet hierarchy and Rock Ridge entries.
/// </summary>
/// <remarks>
/// <para>Entries may come in any order and parents are implied: writing <c>a/b/c.txt</c> creates the directories <c>a</c> and
/// <c>a/b</c> with the default mode (0755) and <see cref="IsoWriterOptions.DefaultTime"/>. The image is laid out when
/// <see cref="ArchiveWriter.Finish()"/> runs, because directory sizes depend on every name, so file content is spooled (in memory
/// up to <see cref="IsoWriterOptions.MaxMemoryBuffer"/>, then in a temporary file).</para>
/// <para>The layout is: 16 sectors of zeros, the volume descriptors (primary, Joliet, terminator), the path tables, the directories
/// of the primary hierarchy, the SUSP continuation areas (they belong to the primary hierarchy), the directories of the Joliet
/// hierarchy, then the file data in the order the files were
/// written, each starting on a sector. Directory records are sorted as clause 9.3 asks, path tables as clause 6.9.1 asks. Names that
/// collide after cutting to the interchange level get a three-digit counter, in the order the entries were written. The image
/// depends only on the entries and options.</para>
/// <para>Rock Ridge (IEEE P1282 1.12 with the 44-byte <c>PX</c>) writes <c>PX</c>, <c>TF</c> (modification, and access and change
/// when the entry has them), <c>NM</c>, <c>SL</c> and <c>PN</c>; the root's own record carries <c>SP</c> and <c>ER</c>. Whatever
/// does not fit a record goes to continuation areas (<c>CE</c>). Deep hierarchies are written as they are (more than eight levels,
/// as common tools do); no directory is relocated. Modes keep the twelve permission bits, owners are numeric.</para>
/// <para>Files of 4 GiB or more need level 3 and are written in several extents of 4 GiB minus 2048 bytes. Hard links share the
/// data of the file they name, which must have been written earlier. Symbolic links, devices and FIFOs need Rock Ridge.
/// Boot catalogs and hybrid partition tables are not written.</para>
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class IsoWriter : ArchiveWriter
{
    private const long ChunkSize = 0xFFFFF800L;
    private const int Sector = IsoFormat.SectorSize;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly IsoWriterOptions _options;
    private readonly Node _root = new(string.Empty, null, ArchiveEntryKind.Directory) { Serial = 1 };
    private readonly List<Node> _nodes = [];
    private Stream _spool = new MemoryStream();
    private int _serial = 1;
    private bool _finished;
    private bool _faulted;
    private bool _disposed;

    /// <summary>Creates a writer.</summary>
    /// <param name="stream">A writable stream. It need not seek.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the writer is disposed.</param>
    /// <exception cref="ArgumentException">The stream is not writable or an option is out of range.</exception>
    public IsoWriter(Stream stream, IsoWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _options = options ?? IsoWriterOptions.Default;
        if (_options.Level is < 1 or > 3)
        {
            throw new ArgumentException("The interchange level must be 1, 2 or 3.", nameof(options));
        }

        if (_options.VolumeId.Length > 32 || _options.SystemId.Length > 32 || _options.VolumeSetId.Length > 128 || _options.PublisherId.Length > 128
            || _options.PreparerId.Length > 128 || _options.ApplicationId.Length > 128)
        {
            throw new ArgumentException("An identifier is longer than its field (32 characters for the volume and system identifier, 128 for the others).", nameof(options));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public override ArchiveFormat Format => ArchiveFormat.Iso;

    // ---- the tree ----

    private sealed class TreeData
    {
        public List<Node> Sorted = [];
        public Dictionary<Node, Plan> Plans = [];
        public Plan? Dot;
        public Plan? DotDot;
        public int Number;
        public long Extent;
        public long Length;
    }

    private sealed class Node(string name, Node? parent, ArchiveEntryKind kind)
    {
        public string Name = name;
        public Node? Parent = parent;
        public ArchiveEntryKind Kind = kind;
        public ArchiveEntry? Entry;
        public List<Node> Children = [];
        public Dictionary<string, Node> Map = new(StringComparer.Ordinal);
        public long SpoolOffset;
        public long Size;
        public Node? LinkTo;
        public int Links = 1;
        public int Serial;
        public long DataExtent;
        public bool IsDirectory => Kind == ArchiveEntryKind.Directory;
        public TreeData[] Trees = [new(), new()];
        public string[] Ident = ["", ""];
    }

    // One directory record's layout: its identifier, the Rock Ridge entries that sit in it and in continuation areas.
    private sealed class Plan
    {
        public byte[] Ident = [];
        public List<byte[]> Inline = [];
        public List<(int ZoneOffset, List<byte[]> Entries)> Areas = [];
        public int Length;
    }

    /// <inheritdoc />
    protected override async ValueTask WriteEntryAsync(ArchiveEntry entry, Stream? content, bool async, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The image is already finished.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await AddAsync(entry, content, async, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not (ArgumentException or ArchiveUnsupportedException or InvalidOperationException or OperationCanceledException))
        {
            _faulted = true;
            throw;
        }
    }

    private async ValueTask AddAsync(ArchiveEntry entry, Stream? content, bool async, CancellationToken ct)
    {
        string[] parts = SplitName(entry.Name);
        ArchiveEntryKind kind = entry.Kind;
        bool special = kind is ArchiveEntryKind.CharacterDevice or ArchiveEntryKind.BlockDevice or ArchiveEntryKind.Fifo or ArchiveEntryKind.Symlink;
        if (special && !_options.RockRidge)
        {
            throw new ArchiveUnsupportedException(IsoReader.Name, $"{kind} entries without Rock Ridge (entry '{entry.Name}')");
        }

        string? target = null;
        if (kind is ArchiveEntryKind.Symlink or ArchiveEntryKind.HardLink)
        {
            target = entry.LinkTarget;
            if (string.IsNullOrEmpty(target) || target.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("A link needs a non-empty link target without NUL.", nameof(entry));
            }
        }

        if (entry.Size < 0)
        {
            throw new ArgumentException("The entry size is negative.", nameof(entry));
        }

        long size = kind == ArchiveEntryKind.File ? entry.Size : 0;
        if (size > 0 && content is null)
        {
            throw new ArgumentException("A file entry with a size needs content.", nameof(entry));
        }

        if (size >= ChunkSize && _options.Level < 3)
        {
            throw new ArchiveUnsupportedException(IsoReader.Name, $"files of 4 GiB or more below interchange level 3 (entry '{entry.Name}')");
        }

        Node? linkTarget = null;
        if (kind == ArchiveEntryKind.HardLink)
        {
            linkTarget = Find(SplitName(target!)) ?? throw new ArgumentException($"The hard link target '{target}' was not written before the link.", nameof(entry));
            if (linkTarget.Kind != ArchiveEntryKind.File)
            {
                throw new ArgumentException("A hard link must name a regular file.", nameof(entry));
            }
        }

        // Parents are created as needed; a name that is already a file cannot get children.
        Node dir = _root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!dir.Map.TryGetValue(parts[i], out Node? next))
            {
                next = NewNode(parts[i], dir, ArchiveEntryKind.Directory);
            }
            else if (!next.IsDirectory)
            {
                throw new ArgumentException($"'{entry.Name}' lies under '{parts[i]}', which is not a directory.", nameof(entry));
            }

            dir = next;
        }

        string leaf = parts[^1];
        if (dir.Map.TryGetValue(leaf, out Node? existing))
        {
            if (!(kind == ArchiveEntryKind.Directory && existing.IsDirectory && existing.Entry is null))
            {
                throw new ArgumentException($"The name '{entry.Name}' is used twice.", nameof(entry));
            }

            existing.Entry = entry;
            return;
        }

        Node node = NewNode(leaf, dir, kind == ArchiveEntryKind.HardLink ? ArchiveEntryKind.File : kind);
        node.Entry = entry;
        if (linkTarget is not null)
        {
            node.LinkTo = linkTarget;
            node.Serial = linkTarget.Serial;
            node.Size = linkTarget.Size;
            _serial--;
            linkTarget.Links++;
            return;
        }

        if (size > 0)
        {
            node.Size = size;
            node.SpoolOffset = _spool.Length;
            try
            {
                await SpoolAsync(content!, size, async, ct).ConfigureAwait(false);
            }
            catch
            {
                // The entry is half in the spool: the image cannot be finished.
                _faulted = true;
                throw;
            }
        }
    }

    private Node NewNode(string name, Node parent, ArchiveEntryKind kind)
    {
        var node = new Node(name, parent, kind) { Serial = ++_serial };
        parent.Children.Add(node);
        parent.Map[name] = node;
        _nodes.Add(node);
        return node;
    }

    private Node? Find(string[] parts)
    {
        Node cur = _root;
        foreach (string p in parts)
        {
            if (!cur.Map.TryGetValue(p, out Node? next))
            {
                return null;
            }

            cur = next;
        }

        return cur;
    }

    private static string[] SplitName(string name)
    {
        string[] parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("The entry name must not be empty.", nameof(name));
        }

        foreach (string p in parts)
        {
            if (p is "." or ".." || p.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"The entry name '{name}' contains '.', '..' or NUL as a component.", nameof(name));
            }

            if (Encoding.UTF8.GetByteCount(p) > 255)
            {
                throw new ArchiveUnsupportedException(IsoReader.Name, $"a name component longer than 255 bytes ('{p[..Math.Min(p.Length, 40)]}...')");
            }
        }

        return parts;
    }

    private async ValueTask SpoolAsync(Stream content, long size, bool async, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long left = size;
            while (left > 0)
            {
                int want = (int)Math.Min(buffer.Length, left);
                int n = async
                    ? await content.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false)
                    : content.Read(buffer, 0, want);
                if (n == 0)
                {
                    throw new ArgumentException($"The content ended {left} bytes before the entry's declared size of {size}.", nameof(content));
                }

                if (_spool is MemoryStream ms && ms.Length + n > _options.MaxMemoryBuffer)
                {
                    _spool = MoveToFile(ms);
                    await ms.DisposeAsync().ConfigureAwait(false);
                }

                if (async)
                {
                    await _spool.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                else
                {
                    _spool.Write(buffer, 0, n);
                }

                left -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileStream MoveToFile(MemoryStream ms)
    {
        FileStream file;
        try
        {
            file = new FileStream(Path.Combine(Path.GetTempPath(), "onearchive-iso-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            throw new ArchiveUnsupportedException(IsoReader.Name, "spooling more than IsoWriterOptions.MaxMemoryBuffer bytes on a platform without temporary files", e);
        }

        ms.Position = 0;
        ms.CopyTo(file);
        return file;
    }

    // ---- layout ----

    private bool HasJoliet => _options.Joliet;

    private int TreeCount => HasJoliet ? 2 : 1;

    /// <inheritdoc />
    protected override async ValueTask FinishAsync(bool async, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            return;
        }

        if (_faulted)
        {
            throw new InvalidOperationException("An earlier write failed; the image cannot be finished.");
        }

        _finished = true;
        try
        {
            await WriteImageAsync(async, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private static long Sectors(long bytes) => (bytes + Sector - 1) / Sector;

    private async ValueTask WriteImageAsync(bool async, CancellationToken ct)
    {
        // 1. Identifiers and the order of every directory's records.
        var dirs = new List<Node>[2] { [], [] };
        for (int t = 0; t < TreeCount; t++)
        {
            NameDirectories(t);
            dirs[t] = BreadthFirst(t);
        }

        // 2. Record plans (Rock Ridge entries, continuation areas) and directory sizes.
        int zone = 0;
        for (int t = 0; t < TreeCount; t++)
        {
            foreach (Node d in dirs[t])
            {
                PlanDirectory(d, t, ref zone);
            }
        }

        // 3. Sector assignment.
        long next = IsoFormat.FirstDescriptorSector + 1 + (HasJoliet ? 1 : 0) + 1;
        long[] ptL = new long[2];
        long[] ptM = new long[2];
        long[] ptSize = new long[2];
        for (int t = 0; t < TreeCount; t++)
        {
            ptSize[t] = PathTableSize(dirs[t], t);
            ptL[t] = next;
            next += Sectors(ptSize[t]);
            ptM[t] = next;
            next += Sectors(ptSize[t]);
        }

        long ceBase = 0;
        for (int t = 0; t < TreeCount; t++)
        {
            foreach (Node d in dirs[t])
            {
                d.Trees[t].Extent = next;
                next += d.Trees[t].Length / Sector;
            }

            if (t == 0)
            {
                // Only the primary hierarchy carries Rock Ridge. Its continuation areas follow its directories directly: a reader
                // that looks ahead only a little past the directory it is parsing finds them there.
                ceBase = next;
                next += Sectors(zone);
            }
        }

        var files = new List<Node>();
        foreach (Node n in _nodes)
        {
            if (n.Kind == ArchiveEntryKind.File && n.LinkTo is null && n.Size > 0)
            {
                n.DataExtent = next;
                next += Sectors(n.Size);
                files.Add(n);
            }
        }

        foreach (Node n in _nodes)
        {
            if (n.LinkTo is { } t)
            {
                n.DataExtent = t.DataExtent;
            }
        }

        long totalSectors = next;

        // 4. Write.
        await WriteAsync(new byte[IsoFormat.FirstDescriptorSector * Sector], async, ct).ConfigureAwait(false);
        await WriteAsync(BuildDescriptor(0, totalSectors, ptSize[0], ptL[0], ptM[0]), async, ct).ConfigureAwait(false);
        if (HasJoliet)
        {
            await WriteAsync(BuildDescriptor(1, totalSectors, ptSize[1], ptL[1], ptM[1]), async, ct).ConfigureAwait(false);
        }

        byte[] terminator = new byte[Sector];
        terminator[0] = 255;
        "CD001"u8.CopyTo(terminator.AsSpan(1));
        terminator[6] = 1;
        await WriteAsync(terminator, async, ct).ConfigureAwait(false);

        for (int t = 0; t < TreeCount; t++)
        {
            await WriteAsync(BuildPathTable(dirs[t], t, bigEndian: false, ptSize[t]), async, ct).ConfigureAwait(false);
            await WriteAsync(BuildPathTable(dirs[t], t, bigEndian: true, ptSize[t]), async, ct).ConfigureAwait(false);
        }

        for (int t = 0; t < TreeCount; t++)
        {
            foreach (Node d in dirs[t])
            {
                await WriteAsync(BuildDirectory(d, t, ceBase), async, ct).ConfigureAwait(false);
            }

            if (t == 0)
            {
                await WriteAsync(BuildZone(dirs, zone, ceBase), async, ct).ConfigureAwait(false);
            }
        }

        await CopyFilesAsync(files, async, ct).ConfigureAwait(false);
        if (async)
        {
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        else
        {
            _stream.Flush();
        }
    }

    private async ValueTask WriteAsync(byte[] data, bool async, CancellationToken ct)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (async)
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        else
        {
            _stream.Write(data, 0, data.Length);
        }
    }

    private async ValueTask CopyFilesAsync(List<Node> files, bool async, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (Node f in files)
            {
                _spool.Position = f.SpoolOffset;
                long left = f.Size;
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, left);
                    int n = async
                        ? await _spool.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false)
                        : _spool.Read(buffer, 0, want);
                    if (n == 0)
                    {
                        throw new IOException("The spool ended early.");
                    }

                    if (async)
                    {
                        await _stream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        _stream.Write(buffer, 0, n);
                    }

                    left -= n;
                }

                int pad = (int)((Sector - (f.Size % Sector)) % Sector);
                if (pad > 0)
                {
                    await WriteAsync(new byte[pad], async, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- identifiers ----

    private void NameDirectories(int t)
    {
        NameChildren(_root, t);
        foreach (Node n in _nodes)
        {
            if (n.IsDirectory)
            {
                NameChildren(n, t);
            }
        }
    }

    private void NameChildren(Node dir, int t)
    {
        var used = new HashSet<string>(t == 0 ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (Node c in dir.Children)
        {
            for (int counter = 0; ; counter++)
            {
                string id = t == 0 ? IsoNaming.IsoName(c.Name, c.IsDirectory, _options.Level, counter) : IsoNaming.JolietName(c.Name, counter);
                if (used.Add(id))
                {
                    c.Ident[t] = id;
                    break;
                }
            }
        }

        var sorted = new List<Node>(dir.Children);
        if (t == 0)
        {
            sorted.Sort(static (a, b) => IsoNaming.CompareIso(a.Ident[0], b.Ident[0]));
        }
        else
        {
            sorted.Sort(static (a, b) => string.CompareOrdinal(a.Ident[1], b.Ident[1]));
        }

        dir.Trees[t].Sorted = sorted;
    }

    // Clause 6.9.1: level by level, within a level by the parent's number, then by identifier: a queue gives exactly that.
    private List<Node> BreadthFirst(int t)
    {
        var order = new List<Node> { _root };
        for (int i = 0; i < order.Count; i++)
        {
            order[i].Trees[t].Number = i + 1;
            foreach (Node c in order[i].Trees[t].Sorted)
            {
                if (c.IsDirectory)
                {
                    order.Add(c);
                }
            }
        }

        return order;
    }

    private static byte[] IdentBytes(Node n, int t) => t == 0 ? Encoding.ASCII.GetBytes(n.Ident[0]) : Encoding.BigEndianUnicode.GetBytes(n.Ident[1]);

    // ---- directory records and Rock Ridge ----

    private static Plan MakePlan(byte[] ident, List<byte[]>? rr, ref int zone)
    {
        var plan = new Plan { Ident = ident };
        int sysStart = IsoFormat.RecordFixedLength + ident.Length + ((ident.Length & 1) == 0 ? 1 : 0);
        if (rr is null || rr.Count == 0)
        {
            plan.Length = sysStart;
            return plan;
        }

        int avail = 254 - sysStart;
        int total = 0;
        foreach (byte[] e in rr)
        {
            total += e.Length;
        }

        if (total <= avail)
        {
            plan.Inline.AddRange(rr);
            plan.Length = sysStart + total + (total & 1);
            return plan;
        }

        int used = 0;
        int k = 0;
        while (k < rr.Count && used + rr[k].Length + 28 <= avail)
        {
            used += rr[k].Length;
            plan.Inline.Add(rr[k]);
            k++;
        }

        // RRIP 4.1.4: a name may be cut into several NM entries, so the first one fills what is left of the record.
        if (k < rr.Count && rr[k][0] == (byte)'N' && rr[k][1] == (byte)'M')
        {
            int room = avail - 28 - used - 5;
            if (room > 0)
            {
                byte[] whole = rr[k];
                byte[] first = new byte[5 + room];
                first[0] = (byte)'N';
                first[1] = (byte)'M';
                first[2] = (byte)first.Length;
                first[3] = 1;
                first[4] = (byte)(whole[4] | 1);
                Array.Copy(whole, 5, first, 5, room);
                byte[] rest = new byte[whole.Length - room];
                rest[0] = (byte)'N';
                rest[1] = (byte)'M';
                rest[2] = (byte)rest.Length;
                rest[3] = 1;
                rest[4] = whole[4];
                Array.Copy(whole, 5 + room, rest, 5, whole.Length - 5 - room);
                plan.Inline.Add(first);
                used += first.Length;
                rr = [.. rr];
                rr[k] = rest;
            }
        }

        plan.Length = sysStart + used + 28 + ((used + 28) & 1);
        int index = k;
        while (index < rr.Count)
        {
            // A continuation area stays inside one sector (SUSP 5.1 gives it a block and an offset); one that cannot start here
            // moves to the next sector. A chain continues in a further area through a trailing CE entry.
            int room = Sector - (zone % Sector);
            int remaining = 0;
            for (int j = index; j < rr.Count; j++)
            {
                remaining += rr[j].Length;
            }

            int start = zone;
            var entries = new List<byte[]>();
            int fill = 0;
            if (remaining <= room)
            {
                while (index < rr.Count)
                {
                    entries.Add(rr[index]);
                    fill += rr[index++].Length;
                }
            }
            else
            {
                while (index < rr.Count && fill + rr[index].Length + 28 <= room)
                {
                    entries.Add(rr[index]);
                    fill += rr[index++].Length;
                }

                fill += 28;
            }

            if (entries.Count == 0)
            {
                zone += room;
                continue;
            }

            plan.Areas.Add((start, entries));
            zone = start + fill;
        }

        return plan;
    }

    private void PlanDirectory(Node dir, int t, ref int zone)
    {
        TreeData td = dir.Trees[t];
        bool rr = t == 0 && _options.RockRidge;
        Node parent = dir.Parent ?? dir;
        td.Dot = MakePlan([0], rr ? RockRidgeEntries(dir, dir == _root ? Special.RootDot : Special.Dot, null) : null, ref zone);
        td.DotDot = MakePlan([1], rr ? RockRidgeEntries(parent, Special.DotDot, null) : null, ref zone);
        long used = 0;
        used = Place(used, td.Dot.Length);
        used = Place(used, td.DotDot.Length);
        foreach (Node c in td.Sorted)
        {
            byte[] ident = IdentBytes(c, t);
            Plan plan = MakePlan(ident, rr ? RockRidgeEntries(c, Special.None, c.Name) : null, ref zone);
            td.Plans[c] = plan;
            int records = c.Kind == ArchiveEntryKind.File && c.Size > ChunkSize ? (int)((c.Size + ChunkSize - 1) / ChunkSize) : 1;
            used = Place(used, plan.Length);
            for (int i = 1; i < records; i++)
            {
                used = Place(used, PlainLength(ident));
            }
        }

        td.Length = Sectors(used) * Sector;
    }

    private static int PlainLength(byte[] ident) => IsoFormat.RecordFixedLength + ident.Length + ((ident.Length & 1) == 0 ? 1 : 0);

    // Records never cross a sector boundary (clause 9.1).
    private static long Place(long used, int length)
    {
        long inSector = used % Sector;
        if (inSector + length > Sector)
        {
            used += Sector - inSector;
        }

        return used + length;
    }

    private enum Special
    {
        None,
        Dot,
        DotDot,
        RootDot,
    }

    private static byte[] Entry(string sig, int version, ReadOnlySpan<byte> body)
    {
        byte[] e = new byte[4 + body.Length];
        e[0] = (byte)sig[0];
        e[1] = (byte)sig[1];
        e[2] = (byte)e.Length;
        e[3] = (byte)version;
        body.CopyTo(e.AsSpan(4));
        return e;
    }

    private static uint ModeBits(Node n) =>
        n.Kind switch
        {
            ArchiveEntryKind.Directory => 0x4000u,
            ArchiveEntryKind.Symlink => 0xA000u,
            ArchiveEntryKind.CharacterDevice => 0x2000u,
            ArchiveEntryKind.BlockDevice => 0x6000u,
            ArchiveEntryKind.Fifo => 0x1000u,
            _ => 0x8000u,
        };

    private static uint Permissions(Node n)
    {
        if (n.Entry?.Mode is { } m)
        {
            return (uint)m & 0xFFF;
        }

        return n.Kind switch
        {
            ArchiveEntryKind.Directory => 0x1EDu,
            ArchiveEntryKind.Symlink => 0x1FFu,
            _ => 0x1A4u,
        };
    }

    private DateTimeOffset TimeOf(Node n) => n.Entry?.ModifiedTime ?? _options.DefaultTime;

    private List<byte[]> RockRidgeEntries(Node n, Special special, string? name)
    {
        var list = new List<byte[]>();
        if (special == Special.RootDot)
        {
            list.Add(Entry("SP", 1, [0xBE, 0xEF, 0x00]));
        }

        Node source = n.LinkTo ?? n;
        byte[] px = new byte[40];
        IsoFormat.WriteBoth32(px.AsSpan(0), ModeBits(source) | Permissions(n));
        long links = source.IsDirectory ? 2 + source.Children.Count(static c => c.IsDirectory) : source.Links;
        IsoFormat.WriteBoth32(px.AsSpan(8), (uint)links);
        IsoFormat.WriteBoth32(px.AsSpan(16), (uint)(n.Entry?.Uid ?? 0));
        IsoFormat.WriteBoth32(px.AsSpan(24), (uint)(n.Entry?.Gid ?? 0));
        IsoFormat.WriteBoth32(px.AsSpan(32), (uint)source.Serial);
        list.Add(Entry("PX", 1, px));

        ArchiveEntry? e = n.Entry;
        int flags = 0x02;
        if (e?.AccessTime is not null)
        {
            flags |= 0x04;
        }

        if (e?.ChangeTime is not null)
        {
            flags |= 0x08;
        }

        byte[] tf = new byte[1 + (7 * BitOperationsPopCount(flags))];
        tf[0] = (byte)flags;
        int at = 1;
        IsoFormat.WriteDate7(tf.AsSpan(at), TimeOf(n));
        at += 7;
        if (e?.AccessTime is { } access)
        {
            IsoFormat.WriteDate7(tf.AsSpan(at), access);
            at += 7;
        }

        if (e?.ChangeTime is { } change)
        {
            IsoFormat.WriteDate7(tf.AsSpan(at), change);
        }

        list.Add(Entry("TF", 1, tf));

        if (special == Special.None && name is not null)
        {
            // RRIP 4.1.4: the real name, in pieces of at most 250 bytes that continue each other.
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            for (int off = 0; off < bytes.Length; off += 250)
            {
                int len = Math.Min(250, bytes.Length - off);
                byte[] body = new byte[1 + len];
                body[0] = (byte)(off + len < bytes.Length ? 1 : 0);
                bytes.AsSpan(off, len).CopyTo(body.AsSpan(1));
                list.Add(Entry("NM", 1, body));
            }
        }

        if (n.Kind == ArchiveEntryKind.Symlink && special == Special.None)
        {
            AddSymlink(list, n.Entry!.LinkTarget!);
        }

        if (n.Kind is ArchiveEntryKind.CharacterDevice or ArchiveEntryKind.BlockDevice && special == Special.None)
        {
            ulong major = (ulong)(n.Entry!.DeviceMajor);
            ulong minor = (ulong)(n.Entry.DeviceMinor);
            ulong dev = ((major & 0xFFFFF000UL) << 32) | ((major & 0xFFFUL) << 8) | ((minor & 0xFFFFFF00UL) << 12) | (minor & 0xFFUL);
            byte[] pn = new byte[16];
            IsoFormat.WriteBoth32(pn.AsSpan(0), (uint)(dev >> 32));
            IsoFormat.WriteBoth32(pn.AsSpan(8), (uint)dev);
            list.Add(Entry("PN", 1, pn));
        }

        if (special == Special.RootDot)
        {
            // SUSP 5.5 and RRIP 4: identify the Rock Ridge protocol.
            byte[] id = "RRIP_1991A"u8.ToArray();
            byte[] des = "THE ROCK RIDGE INTERCHANGE PROTOCOL PROVIDES SUPPORT FOR POSIX FILE SYSTEM SEMANTICS"u8.ToArray();
            byte[] src = "PLEASE CONTACT DISC PUBLISHER FOR SPECIFICATION SOURCE.  SEE PUBLISHER IDENTIFIER IN PRIMARY VOLUME DESCRIPTOR FOR CONTACT INFORMATION."u8.ToArray();
            byte[] body = new byte[4 + id.Length + des.Length + src.Length];
            body[0] = (byte)id.Length;
            body[1] = (byte)des.Length;
            body[2] = (byte)src.Length;
            body[3] = 1;
            id.CopyTo(body, 4);
            des.CopyTo(body, 4 + id.Length);
            src.CopyTo(body, 4 + id.Length + des.Length);
            list.Add(Entry("ER", 1, body));
        }

        return list;
    }

    private static int BitOperationsPopCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);

    // RRIP 4.1.3: component records of "/"-separated parts; a long part continues in more records, and a full SL entry continues in the next.
    private static void AddSymlink(List<byte[]> list, string target)
    {
        var comps = new List<byte[]>();
        int start = 0;
        if (target.StartsWith('/'))
        {
            comps.Add([0x08, 0]);
            start = 1;
        }

        foreach (string part in target[start..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                comps.Add([0x02, 0]);
            }
            else if (part == "..")
            {
                comps.Add([0x04, 0]);
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(part);
                for (int off = 0; off < bytes.Length; off += 248)
                {
                    int len = Math.Min(248, bytes.Length - off);
                    byte[] rec = new byte[2 + len];
                    rec[0] = (byte)(off + len < bytes.Length ? 1 : 0);
                    rec[1] = (byte)len;
                    bytes.AsSpan(off, len).CopyTo(rec.AsSpan(2));
                    comps.Add(rec);
                }
            }
        }

        if (comps.Count == 0)
        {
            comps.Add([0x02, 0]);
        }

        var area = new List<byte>();
        var groups = new List<List<byte>>();
        foreach (byte[] c in comps)
        {
            if (area.Count + c.Length > 250)
            {
                groups.Add(area);
                area = [];
            }

            area.AddRange(c);
        }

        groups.Add(area);
        for (int i = 0; i < groups.Count; i++)
        {
            byte[] body = new byte[1 + groups[i].Count];
            body[0] = (byte)(i + 1 < groups.Count ? 1 : 0);
            groups[i].CopyTo(body, 1);
            list.Add(Entry("SL", 1, body));
        }
    }

    // ---- emission ----

    private static byte[] BuildRecord(Plan plan, int length, long extent, long dataLength, byte flags, DateTimeOffset time, long ceBase, bool withSystemUse)
    {
        byte[] r = new byte[length];
        r[0] = (byte)length;
        IsoFormat.WriteBoth32(r.AsSpan(2), (uint)extent);
        IsoFormat.WriteBoth32(r.AsSpan(10), (uint)dataLength);
        IsoFormat.WriteDate7(r.AsSpan(18), time);
        r[25] = flags;
        IsoFormat.WriteBoth16(r.AsSpan(28), 1);
        r[32] = (byte)plan.Ident.Length;
        plan.Ident.CopyTo(r, 33);
        if (!withSystemUse)
        {
            return r;
        }

        int at = IsoFormat.RecordFixedLength + plan.Ident.Length + ((plan.Ident.Length & 1) == 0 ? 1 : 0);
        foreach (byte[] e in plan.Inline)
        {
            e.CopyTo(r, at);
            at += e.Length;
        }

        if (plan.Areas.Count > 0)
        {
            WriteCe(r.AsSpan(at), plan.Areas[0], plan.Areas.Count > 1, ceBase);
        }

        return r;
    }

    // The length covers the area's own trailing CE entry when the chain goes on.
    private static void WriteCe(Span<byte> dest, (int ZoneOffset, List<byte[]> Entries) area, bool chained, long ceBase)
    {
        long block = ceBase + (area.ZoneOffset / Sector);
        int offset = area.ZoneOffset % Sector;
        int length = chained ? 28 : 0;
        foreach (byte[] e in area.Entries)
        {
            length += e.Length;
        }

        dest[0] = (byte)'C';
        dest[1] = (byte)'E';
        dest[2] = 28;
        dest[3] = 1;
        IsoFormat.WriteBoth32(dest[4..], (uint)block);
        IsoFormat.WriteBoth32(dest[12..], (uint)offset);
        IsoFormat.WriteBoth32(dest[20..], (uint)length);
    }

    private byte[] BuildDirectory(Node dir, int t, long ceBase)
    {
        TreeData td = dir.Trees[t];
        bool rr = t == 0 && _options.RockRidge;
        Node parent = dir.Parent ?? dir;
        byte[] buffer = new byte[td.Length];
        long used = 0;

        void Put(byte[] record)
        {
            used = Place(used, record.Length);
            record.CopyTo(buffer, used - record.Length);
        }

        Put(BuildRecord(td.Dot!, td.Dot!.Length, td.Extent, td.Length, 0x02, TimeOf(dir), ceBase, rr));
        Put(BuildRecord(td.DotDot!, td.DotDot!.Length, parent.Trees[t].Extent, parent.Trees[t].Length, 0x02, TimeOf(parent), ceBase, rr));
        foreach (Node c in td.Sorted)
        {
            Plan plan = td.Plans[c];
            DateTimeOffset time = TimeOf(c);
            if (c.IsDirectory)
            {
                Put(BuildRecord(plan, plan.Length, c.Trees[t].Extent, c.Trees[t].Length, 0x02, time, ceBase, rr));
                continue;
            }

            if (c.Kind != ArchiveEntryKind.File || c.Size == 0)
            {
                Put(BuildRecord(plan, plan.Length, 0, 0, 0, time, ceBase, rr));
                continue;
            }

            long done = 0;
            bool firstRecord = true;
            while (done < c.Size)
            {
                long len = Math.Min(ChunkSize, c.Size - done);
                bool more = done + len < c.Size;
                long extent = c.DataExtent + (done / Sector);
                Put(firstRecord
                    ? BuildRecord(plan, plan.Length, extent, len, (byte)(more ? 0x80 : 0), time, ceBase, rr)
                    : BuildRecord(plan, PlainLength(plan.Ident), extent, len, (byte)(more ? 0x80 : 0), time, ceBase, false));
                firstRecord = false;
                done += len;
            }
        }

        return buffer;
    }

    // The continuation areas of every record in the order the plans were made.
    private static byte[] BuildZone(List<Node>[] dirs, int zone, long ceBase)
    {
        byte[] bytes = new byte[Sectors(zone) * Sector];
        foreach (Node d in dirs[0])
        {
            TreeData td = d.Trees[0];
            FillZone(bytes, td.Dot!, ceBase);
            FillZone(bytes, td.DotDot!, ceBase);
            foreach (Node c in td.Sorted)
            {
                FillZone(bytes, td.Plans[c], ceBase);
            }
        }

        return bytes;
    }

    private static void FillZone(byte[] bytes, Plan plan, long ceBase)
    {
        for (int i = 0; i < plan.Areas.Count; i++)
        {
            (int offset, List<byte[]> entries) = plan.Areas[i];
            int at = offset;
            foreach (byte[] e in entries)
            {
                e.CopyTo(bytes, at);
                at += e.Length;
            }

            if (i + 1 < plan.Areas.Count)
            {
                WriteCe(bytes.AsSpan(at), plan.Areas[i + 1], i + 2 < plan.Areas.Count, ceBase);
            }
        }
    }

    // ---- path tables (clause 9.4) ----

    private long PathTableSize(List<Node> dirs, int t)
    {
        long size = 0;
        foreach (Node d in dirs)
        {
            int idLen = d == _root ? 1 : IdentBytes(d, t).Length;
            size += 8 + idLen + (idLen & 1);
        }

        return size;
    }

    private byte[] BuildPathTable(List<Node> dirs, int t, bool bigEndian, long size)
    {
        byte[] bytes = new byte[Sectors(size) * Sector];
        int at = 0;
        foreach (Node d in dirs)
        {
            byte[] id = d == _root ? [0] : IdentBytes(d, t);
            bytes[at] = (byte)id.Length;
            Span<byte> s = bytes.AsSpan(at + 2);
            uint extent = (uint)d.Trees[t].Extent;
            ushort parent = (ushort)(d.Parent ?? d).Trees[t].Number;
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(s, extent);
                BinaryPrimitives.WriteUInt16BigEndian(s[4..], parent);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s, extent);
                BinaryPrimitives.WriteUInt16LittleEndian(s[4..], parent);
            }

            id.CopyTo(bytes, at + 8);
            at += 8 + id.Length + (id.Length & 1);
        }

        return bytes;
    }

    // ---- volume descriptors (clause 8) ----

    private byte[] BuildDescriptor(int t, long totalSectors, long ptSize, long ptL, long ptM)
    {
        byte[] d = new byte[Sector];
        bool joliet = t == 1;
        d[0] = (byte)(joliet ? 2 : 1);
        "CD001"u8.CopyTo(d.AsSpan(1));
        d[6] = 1;
        void Text(int at, int len, string text)
        {
            if (joliet)
            {
                IsoFormat.WriteJolietText(d.AsSpan(at, len), text);
            }
            else
            {
                IsoFormat.WriteText(d.AsSpan(at, len), text);
            }
        }

        Text(8, 32, _options.SystemId);
        Text(40, 32, _options.VolumeId);
        if (joliet)
        {
            "%/E"u8.CopyTo(d.AsSpan(88));
        }

        IsoFormat.WriteBoth32(d.AsSpan(80), (uint)totalSectors);
        IsoFormat.WriteBoth16(d.AsSpan(120), 1);
        IsoFormat.WriteBoth16(d.AsSpan(124), 1);
        IsoFormat.WriteBoth16(d.AsSpan(128), Sector);
        IsoFormat.WriteBoth32(d.AsSpan(132), (uint)ptSize);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(140), (uint)ptL);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(148), (uint)ptM);

        // The root record: 34 bytes, identifier 0x00.
        TreeData root = _root.Trees[t];
        Span<byte> r = d.AsSpan(156, 34);
        r[0] = 34;
        IsoFormat.WriteBoth32(r[2..], (uint)root.Extent);
        IsoFormat.WriteBoth32(r[10..], (uint)root.Length);
        IsoFormat.WriteDate7(r[18..], _options.DefaultTime);
        r[25] = 0x02;
        IsoFormat.WriteBoth16(r[28..], 1);
        r[32] = 1;

        Text(190, 128, _options.VolumeSetId);
        Text(318, 128, _options.PublisherId);
        Text(446, 128, _options.PreparerId);
        Text(574, 128, _options.ApplicationId);
        Text(702, 37, string.Empty);
        Text(739, 37, string.Empty);
        Text(776, 37, string.Empty);
        IsoFormat.WriteDate17(d.AsSpan(813, 17), _options.VolumeTime);
        IsoFormat.WriteDate17(d.AsSpan(830, 17), _options.VolumeTime);
        IsoFormat.WriteDate17(d.AsSpan(847, 17), null);
        IsoFormat.WriteDate17(d.AsSpan(864, 17), null);
        d[881] = 1;
        return d;
    }

    // ---- disposal ----

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
            _spool.Dispose();
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
                await FinishAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
            await _spool.DisposeAsync().ConfigureAwait(false);
            if (!_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
