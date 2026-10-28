using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>Property IDs and constants of the 7z header (7zFormat.txt, "Property IDs").</summary>
internal static class SevenZipFormat
{
    public const string Name = "7z";

    public const int SignatureHeaderSize = 32;

    public const byte End = 0x00;
    public const byte Header = 0x01;
    public const byte ArchiveProperties = 0x02;
    public const byte AdditionalStreamsInfo = 0x03;
    public const byte MainStreamsInfo = 0x04;
    public const byte FilesInfo = 0x05;
    public const byte PackInfo = 0x06;
    public const byte UnpackInfo = 0x07;
    public const byte SubStreamsInfo = 0x08;
    public const byte Size = 0x09;
    public const byte Crc = 0x0A;
    public const byte Folder = 0x0B;
    public const byte CodersUnpackSize = 0x0C;
    public const byte NumUnpackStream = 0x0D;
    public const byte EmptyStream = 0x0E;
    public const byte EmptyFile = 0x0F;
    public const byte Anti = 0x10;
    public const byte Names = 0x11;
    public const byte CTime = 0x12;
    public const byte ATime = 0x13;
    public const byte MTime = 0x14;
    public const byte WinAttributes = 0x15;
    public const byte EncodedHeader = 0x17;
    public const byte StartPos = 0x18;
    public const byte Dummy = 0x19;

    /// <summary>Methods.txt: method IDs.</summary>
    public const ulong MethodCopy = 0x00;
    public const ulong MethodDelta = 0x03;
    public const ulong MethodBcjX86 = 0x04;
    public const ulong MethodDeflate = 0x040108;
    public const ulong MethodDeflate64 = 0x040109;
    public const ulong MethodBZip2 = 0x040202;
    public const ulong MethodZstd = 0xF71101;
    public const ulong MethodLzma = 0x030101;
    public const ulong MethodLzma2 = 0x21;
    public const ulong MethodBcj2 = 0x0303011B;
    public const ulong MethodPpmd = 0x030401;
    public const ulong MethodAes = 0x06F10701;

    public static string MethodName(ulong id) => id switch
    {
        MethodCopy => "Copy",
        MethodDelta => "Delta",
        MethodBcjX86 or 0x03030103 => "BCJ",
        0x05 or 0x03030205 => "PPC",
        0x06 or 0x03030401 => "IA64",
        0x07 or 0x03030501 => "ARM",
        0x08 or 0x03030701 => "ARMT",
        0x09 or 0x03030805 => "SPARC",
        0x0A => "ARM64",
        0x0B => "RISCV",
        MethodLzma2 => "LZMA2",
        MethodLzma => "LZMA",
        MethodBcj2 => "BCJ2",
        MethodPpmd => "PPMd",
        MethodDeflate => "Deflate",
        MethodDeflate64 => "Deflate64",
        MethodBZip2 => "BZip2",
        MethodAes => "7zAES",
        MethodZstd => "Zstandard",
        _ => "unknown",
    };

    public static ArchiveUnsupportedException UnsupportedMethod(ulong id) =>
        new(Name, $"the coder with method ID 0x{id:X} ({MethodName(id)})");

    public const long FileTimeEpochTicks = 504911232000000000L; // 1601-01-01 in DateTime ticks.

    public static DateTimeOffset? FromFileTime(ulong value)
    {
        // A 7z time is 100 ns ticks since 1601-01-01 UTC. Values outside DateTime's range are treated as absent.
        if (value > (ulong)(DateTime.MaxValue.Ticks - FileTimeEpochTicks))
        {
            return null;
        }

        return new DateTimeOffset(new DateTime(FileTimeEpochTicks + (long)value, DateTimeKind.Utc));
    }
}

/// <summary>A cursor over header bytes with the 7z number encodings; every failure names the offset.</summary>
internal sealed class SevenZipByteReader
{
    private readonly byte[] _buf;
    private readonly int _end;
    private readonly long _baseOffset;
    private readonly bool _decoded;

    public SevenZipByteReader(byte[] buffer, int start, int end, long baseOffset, bool decoded)
    {
        _buf = buffer;
        Pos = start;
        _end = end;
        _baseOffset = baseOffset;
        _decoded = decoded;
    }

    public int Pos { get; private set; }

    public int Remaining => _end - Pos;

    public CompressionFormatException Bad(string expected, bool truncated = false) =>
        new(SevenZipFormat.Name, _baseOffset + Pos, _decoded ? expected + " (in the decoded header)" : expected, truncated);

    public byte ReadByte()
    {
        if (Pos >= _end)
        {
            throw Bad("more header bytes", true);
        }

        return _buf[Pos++];
    }

    public ReadOnlySpan<byte> ReadBytes(long count)
    {
        if (count < 0 || count > Remaining)
        {
            throw Bad($"{count} more header bytes", true);
        }

        ReadOnlySpan<byte> s = _buf.AsSpan(Pos, (int)count);
        Pos += (int)count;
        return s;
    }

    public void Skip(long count) => ReadBytes(count);

    public ReadOnlySpan<byte> Peek() => _buf.AsSpan(Pos, Remaining);

    public void Rewind(int count) => Pos -= count;

    /// <summary>A reader over the next <paramref name="size"/> bytes; this reader does not move.</summary>
    public SevenZipByteReader Slice(long size) => new(_buf, Pos, Pos + (int)size, _baseOffset, _decoded);

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));

    /// <summary>7zFormat.txt "Notes about Notation and encoding": the UINT64 variable-length number.</summary>
    public ulong ReadNumber()
    {
        byte first = ReadByte();
        byte mask = 0x80;
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((first & mask) == 0)
            {
                ulong high = (ulong)(first & (mask - 1));
                return value + (high << (8 * i));
            }

            value |= (ulong)ReadByte() << (8 * i);
            mask >>= 1;
        }

        return value;
    }

    /// <summary>Reads a number that must fit in 62 bits (sizes and offsets).</summary>
    public long ReadSize(string what)
    {
        ulong v = ReadNumber();
        if (v > (1UL << 62))
        {
            Pos--;
            throw Bad($"{what} below 2^62");
        }

        return (long)v;
    }

    /// <summary>Reads a count of items that each need at least one header byte, or fail before anything is allocated.</summary>
    public int ReadCount(string what, long max)
    {
        ulong v = ReadNumber();
        if (v > (ulong)max)
        {
            throw Bad($"{what} of at most {max}");
        }

        return (int)v;
    }

    public bool[] ReadBitVector(int count)
    {
        var bits = new bool[count];
        byte b = 0;
        byte mask = 0;
        for (int i = 0; i < count; i++)
        {
            if (mask == 0)
            {
                b = ReadByte();
                mask = 0x80;
            }

            bits[i] = (b & mask) != 0;
            mask >>= 1;
        }

        return bits;
    }

    /// <summary>A byte "AllAreDefined", then a bit vector when it is zero.</summary>
    public bool[] ReadOptionalBitVector(int count)
    {
        if (ReadByte() == 0)
        {
            return ReadBitVector(count);
        }

        var all = new bool[count];
        Array.Fill(all, true);
        return all;
    }

    /// <summary>7zFormat.txt "Digests".</summary>
    public uint?[] ReadDigests(int count)
    {
        bool[] defined = ReadOptionalBitVector(count);
        var crcs = new uint?[count];
        for (int i = 0; i < count; i++)
        {
            if (defined[i])
            {
                crcs[i] = ReadUInt32();
            }
        }

        return crcs;
    }
}

/// <summary>What the header says about the packed streams and folders.</summary>
internal sealed class SevenZipStreamsInfo
{
    public long PackPos;
    public long[] PackSizes = [];
    public SevenZipFolder[] Folders = [];
    public int TotalSubStreams;
}

/// <summary>A raw file record, before it becomes an entry.</summary>
internal sealed class SevenZipFileRecord
{
    public string Name = string.Empty;
    public bool EmptyStream;
    public bool EmptyFile;
    public bool Anti;
    public DateTimeOffset? CTime;
    public DateTimeOffset? ATime;
    public DateTimeOffset? MTime;
    public uint? Attributes;
}

/// <summary>Parses the 7z header structures (7zFormat.txt) with every count and size checked before use.</summary>
internal sealed class SevenZipHeaderParser(SevenZipReaderOptions options)
{
    private readonly int _maxFiles = options.Limits.MaxEntryCount;

    /// <summary>Parses a plain header, starting at the kHeader byte (7zFormat.txt "Header").</summary>
    public (SevenZipStreamsInfo Streams, List<SevenZipFileRecord> Files) ParseHeader(SevenZipByteReader r)
    {
        if (r.ReadByte() != SevenZipFormat.Header)
        {
            r.Rewind(1);
            throw r.Bad("the header marker (property 0x01) or an encoded header (0x17)");
        }

        SevenZipStreamsInfo streams = new();
        List<SevenZipFileRecord> files = [];
        byte id = r.ReadByte();
        if (id == SevenZipFormat.ArchiveProperties)
        {
            while (true)
            {
                byte type = r.ReadByte();
                if (type == 0)
                {
                    break;
                }

                r.Skip(r.ReadSize("archive property size"));
            }

            id = r.ReadByte();
        }

        if (id == SevenZipFormat.AdditionalStreamsInfo)
        {
            // Only names, times and attributes can live there and every one of them is marked external; none is supported.
            ParseStreamsInfo(r);
            id = r.ReadByte();
        }

        if (id == SevenZipFormat.MainStreamsInfo)
        {
            streams = ParseStreamsInfo(r);
            id = r.ReadByte();
        }

        if (id == SevenZipFormat.FilesInfo)
        {
            files = ParseFilesInfo(r, streams);
            id = r.ReadByte();
        }
        else if (streams.TotalSubStreams != 0)
        {
            throw r.Bad("a file list for the streams");
        }

        if (id != SevenZipFormat.End)
        {
            r.Rewind(1);
            throw r.Bad("the end of the header (property 0x00)");
        }

        return (streams, files);
    }

    public SevenZipStreamsInfo ParseStreamsInfo(SevenZipByteReader r)
    {
        var info = new SevenZipStreamsInfo();
        byte id = r.ReadByte();
        if (id == SevenZipFormat.PackInfo)
        {
            ParsePackInfo(r, info);
            id = r.ReadByte();
        }

        if (id == SevenZipFormat.UnpackInfo)
        {
            ParseUnpackInfo(r, info);
            id = r.ReadByte();
        }

        // Folders with no SubStreamsInfo hold one file each.
        bool haveSub = false;
        if (id == SevenZipFormat.SubStreamsInfo)
        {
            ParseSubStreamsInfo(r, info);
            haveSub = true;
            id = r.ReadByte();
        }

        if (!haveSub)
        {
            foreach (SevenZipFolder f in info.Folders)
            {
                f.SubStreamSizes = [f.UnpackSize];
                f.SubStreamCrcs = [f.Crc];
            }

            info.TotalSubStreams = info.Folders.Length;
        }

        if (id != SevenZipFormat.End)
        {
            throw r.Bad("the end of the streams info (property 0x00)");
        }

        int packNeeded = 0;
        foreach (SevenZipFolder f in info.Folders)
        {
            f.FirstPackStream = packNeeded;
            packNeeded += f.PackedStreams.Count;
        }

        if (packNeeded > info.PackSizes.Length)
        {
            throw r.Bad($"at least {packNeeded} packed streams for the folders, found {info.PackSizes.Length}");
        }

        return info;
    }

    private static void ParsePackInfo(SevenZipByteReader r, SevenZipStreamsInfo info)
    {
        info.PackPos = r.ReadSize("pack position");
        int n = r.ReadCount("packed stream count", r.Remaining);
        info.PackSizes = new long[n];
        byte id = r.ReadByte();
        while (id != SevenZipFormat.End)
        {
            if (id == SevenZipFormat.Size)
            {
                for (int i = 0; i < n; i++)
                {
                    info.PackSizes[i] = r.ReadSize("packed stream size");
                }
            }
            else if (id == SevenZipFormat.Crc)
            {
                r.ReadDigests(n); // Packed-stream CRCs are not used: the folder and file CRCs cover the data.
            }
            else
            {
                throw r.Bad("a packed-stream size list (0x09), digests (0x0A) or the end (0x00)");
            }

            id = r.ReadByte();
        }
    }

    private void ParseUnpackInfo(SevenZipByteReader r, SevenZipStreamsInfo info)
    {
        if (r.ReadByte() != SevenZipFormat.Folder)
        {
            throw r.Bad("the folder list (property 0x0B)");
        }

        // Every folder takes at least two header bytes (a coder count and one coder flag byte).
        int n = r.ReadCount("folder count", r.Remaining / 2);
        if (r.ReadByte() != 0)
        {
            throw new ArchiveUnsupportedException(SevenZipFormat.Name, "folders stored in additional header streams");
        }

        var folders = new SevenZipFolder[n];
        for (int i = 0; i < n; i++)
        {
            folders[i] = ParseFolder(r);
        }

        if (r.ReadByte() != SevenZipFormat.CodersUnpackSize)
        {
            throw r.Bad("the coder unpack sizes (property 0x0C)");
        }

        foreach (SevenZipFolder f in folders)
        {
            int outs = 0;
            foreach (SevenZipCoder c in f.Coders)
            {
                outs += c.NumOutStreams;
            }

            var sizes = new long[outs];
            for (int i = 0; i < outs; i++)
            {
                sizes[i] = r.ReadSize("coder unpack size");
            }

            f.UnpackSizes = sizes;
        }

        byte id = r.ReadByte();
        if (id == SevenZipFormat.Crc)
        {
            uint?[] crcs = r.ReadDigests(n);
            for (int i = 0; i < n; i++)
            {
                folders[i].Crc = crcs[i];
            }

            id = r.ReadByte();
        }

        if (id != SevenZipFormat.End)
        {
            throw r.Bad("the folder CRCs (0x0A) or the end (0x00)");
        }

        info.Folders = folders;
    }

    private SevenZipFolder ParseFolder(SevenZipByteReader r)
    {
        int numCoders = r.ReadCount("coder count", options.MaxCodersPerFolder);
        if (numCoders == 0)
        {
            throw r.Bad("at least one coder in a folder");
        }

        var coders = new SevenZipCoder[numCoders];
        int totalIn = 0;
        int totalOut = 0;
        for (int i = 0; i < numCoders; i++)
        {
            byte flags = r.ReadByte();
            int idSize = flags & 0x0F;
            if ((flags & 0xC0) != 0 || idSize > 8)
            {
                throw r.Bad("a coder flag byte with a method ID of at most 8 bytes and the reserved bits clear");
            }

            ulong method = 0;
            foreach (byte b in r.ReadBytes(idSize))
            {
                method = (method << 8) | b;
            }

            int numIn = 1;
            int numOut = 1;
            if ((flags & 0x10) != 0)
            {
                numIn = r.ReadCount("coder input stream count", 32);
                numOut = r.ReadCount("coder output stream count", 32);
                if (numIn == 0 || numOut == 0)
                {
                    // 7zFormat.txt: NumInStreams and NumOutStreams are counts of streams a coder reads and writes; a coder
                    // with none cannot take part in a bond, and the bond indices below are bounded by the totals.
                    throw r.Bad("a coder with at least one input and one output stream");
                }
            }

            byte[] props = [];
            if ((flags & 0x20) != 0)
            {
                int size = r.ReadCount("coder properties size", r.Remaining);
                props = r.ReadBytes(size).ToArray();
            }

            coders[i] = new SevenZipCoder(method, numIn, numOut, props);
            totalIn += numIn;
            totalOut += numOut;
            if (totalIn > 64 || totalOut > 64)
            {
                throw r.Bad("a coder graph of at most 64 streams");
            }
        }

        if (totalOut == 0)
        {
            throw r.Bad("a coder graph with an output stream");
        }

        // NumBindPairs = NumOutStreamsTotal - 1: every output but the folder's result feeds some input.
        int numBind = totalOut - 1;
        var binds = new SevenZipBindPair[numBind];
        var inUsed = new bool[totalIn];
        var outUsed = new bool[totalOut];
        for (int i = 0; i < numBind; i++)
        {
            int inIndex = r.ReadCount("bond input index", totalIn - 1);
            int outIndex = r.ReadCount("bond output index", totalOut - 1);
            if (inUsed[inIndex] || outUsed[outIndex])
            {
                throw r.Bad("each coder stream bound at most once");
            }

            inUsed[inIndex] = true;
            outUsed[outIndex] = true;
            binds[i] = new SevenZipBindPair(inIndex, outIndex);
        }

        int numPacked = totalIn - numBind;
        if (numPacked < 1)
        {
            throw r.Bad("at least one packed stream in a folder");
        }

        var packed = new int[numPacked];
        if (numPacked == 1)
        {
            int k = Array.IndexOf(inUsed, false);
            packed[0] = k;
        }
        else
        {
            for (int i = 0; i < numPacked; i++)
            {
                int idx = r.ReadCount("packed stream index", totalIn - 1);
                if (inUsed[idx])
                {
                    throw r.Bad("each coder input fed by exactly one packed stream or bond");
                }

                inUsed[idx] = true;
                packed[i] = idx;
            }
        }

        int main = Array.IndexOf(outUsed, false);
        var folder = new SevenZipFolder(coders, binds, packed, main);
        ValidateGraph(r, folder);
        return folder;
    }

    // Every coder must be reachable from the result exactly once; a bond that loops back is refused.
    private static void ValidateGraph(SevenZipByteReader r, SevenZipFolder f)
    {
        int n = f.Coders.Count;
        var inFirst = new int[n];
        var outFirst = new int[n];
        int ai = 0;
        int ao = 0;
        for (int i = 0; i < n; i++)
        {
            inFirst[i] = ai;
            outFirst[i] = ao;
            ai += f.Coders[i].NumInStreams;
            ao += f.Coders[i].NumOutStreams;
        }

        int CoderOfOut(int outIndex)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                if (outIndex >= outFirst[i])
                {
                    return i;
                }
            }

            return 0;
        }

        var visited = new bool[n];
        int visitedCount = 0;
        var stack = new Stack<int>();
        stack.Push(CoderOfOut(f.MainOutStream));
        while (stack.Count > 0)
        {
            int c = stack.Pop();
            if (visited[c])
            {
                throw r.Bad("a coder graph without cycles");
            }

            visited[c] = true;
            visitedCount++;
            for (int j = 0; j < f.Coders[c].NumInStreams; j++)
            {
                int inIdx = inFirst[c] + j;
                foreach (SevenZipBindPair b in f.BindPairs)
                {
                    if (b.InIndex == inIdx)
                    {
                        stack.Push(CoderOfOut(b.OutIndex));
                    }
                }
            }
        }

        if (visitedCount != n)
        {
            throw r.Bad("every coder of a folder connected to the folder's result");
        }
    }

    private void ParseSubStreamsInfo(SevenZipByteReader r, SevenZipStreamsInfo info)
    {
        SevenZipFolder[] folders = info.Folders;
        var counts = new int[folders.Length];
        Array.Fill(counts, 1);
        byte id = r.ReadByte();
        long total = folders.Length;
        if (id == SevenZipFormat.NumUnpackStream)
        {
            total = 0;
            for (int i = 0; i < folders.Length; i++)
            {
                counts[i] = r.ReadCount("files in a folder", _maxFiles);
                total += counts[i];
                if (total > _maxFiles)
                {
                    throw new CompressionLimitException(SevenZipFormat.Name, nameof(ArchiveLimits.MaxEntryCount), _maxFiles, total);
                }
            }

            id = r.ReadByte();
        }

        // Sizes: all but the last file of each folder are stored; the last is what the folder has left.
        var sizes = new long[folders.Length][];
        bool haveSizes = id == SevenZipFormat.Size;
        for (int i = 0; i < folders.Length; i++)
        {
            int n = counts[i];
            sizes[i] = new long[n];
            if (n == 0)
            {
                continue;
            }

            if (n > 1 && !haveSizes)
            {
                throw r.Bad("file sizes (property 0x09) for a folder with several files");
            }

            long sum = 0;
            for (int j = 0; j < n - 1; j++)
            {
                long s = r.ReadSize("file size");
                sizes[i][j] = s;
                sum += s;
                if (sum > folders[i].UnpackSize)
                {
                    throw r.Bad("file sizes that add up to at most the folder's unpacked size");
                }
            }

            sizes[i][n - 1] = folders[i].UnpackSize - sum;
        }

        if (haveSizes)
        {
            id = r.ReadByte();
        }

        // Digests are stored only for files whose CRC the folder does not already give.
        var crcs = new uint?[folders.Length][];
        int unknown = 0;
        for (int i = 0; i < folders.Length; i++)
        {
            crcs[i] = new uint?[counts[i]];
            if (counts[i] == 1 && folders[i].Crc is { } fc)
            {
                crcs[i][0] = fc;
            }
            else
            {
                unknown += counts[i];
            }
        }

        while (id != SevenZipFormat.End)
        {
            if (id != SevenZipFormat.Crc)
            {
                throw r.Bad("file CRCs (property 0x0A) or the end (0x00)");
            }

            uint?[] digests = r.ReadDigests(unknown);
            int k = 0;
            for (int i = 0; i < folders.Length; i++)
            {
                if (counts[i] == 1 && folders[i].Crc is not null)
                {
                    continue;
                }

                for (int j = 0; j < counts[i]; j++)
                {
                    crcs[i][j] = digests[k++];
                }
            }

            id = r.ReadByte();
        }

        for (int i = 0; i < folders.Length; i++)
        {
            folders[i].SubStreamSizes = sizes[i];
            folders[i].SubStreamCrcs = crcs[i];
        }

        info.TotalSubStreams = (int)total;
    }

    public List<SevenZipFileRecord> ParseFilesInfo(SevenZipByteReader r, SevenZipStreamsInfo streams)
    {
        // Every file takes at least two bytes of names (the terminator), so the count is also bounded by the header.
        ulong declared = r.ReadNumber();
        if (declared > (ulong)_maxFiles)
        {
            throw new CompressionLimitException(SevenZipFormat.Name, nameof(ArchiveLimits.MaxEntryCount), _maxFiles, (long)Math.Min(declared, long.MaxValue));
        }

        if (declared > (ulong)r.Remaining)
        {
            throw r.Bad($"a header with room for {declared} files", true);
        }

        int n = (int)declared;
        var files = new List<SevenZipFileRecord>(n);
        for (int i = 0; i < n; i++)
        {
            files.Add(new SevenZipFileRecord());
        }

        bool[]? emptyStream = null;
        bool[]? emptyFile = null;
        bool[]? anti = null;
        int numEmpty = 0;
        while (true)
        {
            byte type = r.ReadByte();
            if (type == SevenZipFormat.End)
            {
                break;
            }

            long size = r.ReadSize("property size");
            if (size > r.Remaining)
            {
                throw r.Bad($"a property of {size} bytes inside the header", true);
            }

            SevenZipByteReader sub = r.Slice(size);
            switch (type)
            {
                case SevenZipFormat.EmptyStream:
                    emptyStream = sub.ReadBitVector(n);
                    if (emptyFile is not null || anti is not null)
                    {
                        throw r.Bad("a single empty-stream vector (property 0x0E) ahead of the vectors that depend on it");
                    }

                    numEmpty = 0;
                    foreach (bool e in emptyStream)
                    {
                        if (e)
                        {
                            numEmpty++;
                        }
                    }

                    break;
                case SevenZipFormat.EmptyFile:
                    // 7zFormat.txt: EmptyFile and Anti have one bit per empty stream, so the EmptyStream vector (0x0E) comes first.
                    if (emptyStream is null)
                    {
                        throw r.Bad("the empty-stream vector (property 0x0E) before the empty-file vector (0x0F)");
                    }

                    emptyFile = sub.ReadBitVector(numEmpty);
                    break;
                case SevenZipFormat.Anti:
                    if (emptyStream is null)
                    {
                        throw r.Bad("the empty-stream vector (property 0x0E) before the anti-item vector (0x10)");
                    }

                    anti = sub.ReadBitVector(numEmpty);
                    break;
                case SevenZipFormat.Names:
                    ParseNames(sub, files);
                    break;
                case SevenZipFormat.CTime:
                case SevenZipFormat.ATime:
                case SevenZipFormat.MTime:
                    ParseTimes(sub, files, type);
                    break;
                case SevenZipFormat.WinAttributes:
                    ParseAttributes(sub, files);
                    break;
                default:
                    // kDummy (padding), kStartPos, kComment and anything unknown: skipped by their size.
                    break;
            }

            r.Skip(size);
        }

        if (emptyStream is not null)
        {
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (!emptyStream[i])
                {
                    continue;
                }

                files[i].EmptyStream = true;
                files[i].EmptyFile = emptyFile is not null && emptyFile[k];
                files[i].Anti = anti is not null && anti[k];
                k++;
            }
        }

        int withData = n - numEmpty;
        if (withData != streams.TotalSubStreams)
        {
            throw r.Bad($"{streams.TotalSubStreams} files with data, the file list has {withData}");
        }

        return files;
    }

    private static void ParseNames(SevenZipByteReader r, List<SevenZipFileRecord> files)
    {
        if (r.ReadByte() != 0)
        {
            throw new ArchiveUnsupportedException(SevenZipFormat.Name, "file names stored in additional header streams");
        }

        foreach (SevenZipFileRecord f in files)
        {
            ReadOnlySpan<byte> rest = r.Peek();
            int len = -1;
            for (int i = 0; i + 1 < rest.Length; i += 2)
            {
                if (rest[i] == 0 && rest[i + 1] == 0)
                {
                    len = i;
                    break;
                }
            }

            if (len < 0)
            {
                throw r.Bad("a zero-terminated UTF-16 name for every file", true);
            }

            f.Name = DecodeName(rest[..len]);
            r.Skip(len + 2);
        }
    }

    // Names are UTF-16LE; lone surrogates are kept as they are (a hostile name is not an error, extraction judges it).
    private static string DecodeName(ReadOnlySpan<byte> utf16) => Encoding.Unicode.GetString(utf16);

    private static void ParseTimes(SevenZipByteReader r, List<SevenZipFileRecord> files, byte type)
    {
        bool[] defined = r.ReadOptionalBitVector(files.Count);
        if (r.ReadByte() != 0)
        {
            throw new ArchiveUnsupportedException(SevenZipFormat.Name, "times stored in additional header streams");
        }

        for (int i = 0; i < files.Count; i++)
        {
            if (!defined[i])
            {
                continue;
            }

            DateTimeOffset? t = SevenZipFormat.FromFileTime(r.ReadUInt64());
            switch (type)
            {
                case SevenZipFormat.CTime: files[i].CTime = t; break;
                case SevenZipFormat.ATime: files[i].ATime = t; break;
                default: files[i].MTime = t; break;
            }
        }
    }

    private static void ParseAttributes(SevenZipByteReader r, List<SevenZipFileRecord> files)
    {
        bool[] defined = r.ReadOptionalBitVector(files.Count);
        if (r.ReadByte() != 0)
        {
            throw new ArchiveUnsupportedException(SevenZipFormat.Name, "attributes stored in additional header streams");
        }

        for (int i = 0; i < files.Count; i++)
        {
            if (defined[i])
            {
                files[i].Attributes = r.ReadUInt32();
            }
        }
    }
}
