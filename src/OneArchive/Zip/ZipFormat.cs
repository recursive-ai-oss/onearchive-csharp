using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Zip;

/// <summary>Constants and small helpers for the zip structures (PKWARE APPNOTE 6.3.10, section numbers in comments).</summary>
internal static class ZipFormat
{
    public const string Name = "zip";

    // APPNOTE 4.3.7, 4.3.12, 4.3.16, 4.3.9.3, 4.3.14, 4.3.15: record signatures.
    public const uint LocalSignature = 0x04034b50;
    public const uint CentralSignature = 0x02014b50;
    public const uint EndSignature = 0x06054b50;
    public const uint Zip64EndSignature = 0x06064b50;
    public const uint Zip64LocatorSignature = 0x07064b50;
    public const uint DescriptorSignature = 0x08074b50;
    public const uint SpanMarker = 0x30304b50;

    public const int LocalHeaderSize = 30;
    public const int CentralHeaderSize = 46;
    public const int EndRecordSize = 22;
    public const int Zip64EndRecordSize = 56;
    public const int Zip64LocatorSize = 20;

    // APPNOTE 4.4.4: general purpose bit flag.
    public const ushort FlagEncrypted = 1;
    public const ushort FlagDescriptor = 8;
    public const ushort FlagUtf8 = 0x800;
    public const ushort FlagStrongEncryption = 0x40;

    // APPNOTE 4.5.2 and later: extra field header ids.
    public const ushort ExtraZip64 = 0x0001;
    public const ushort ExtraNtfs = 0x000a;
    public const ushort ExtraExtendedTime = 0x5455;
    public const ushort ExtraUnicodePath = 0x7075;

    public const uint Max32 = 0xFFFFFFFF;
    public const ushort Max16 = 0xFFFF;

    // The largest entry (uncompressed) a writer announces without a zip64 extra in the local header. Output of any
    // supported method is at most 0.1% larger than its input, so the compressed size stays below 4 GiB too.
    public const long LocalZip64Threshold = 0xF000_0000L;

    public static CompressionFormatException Bad(long offset, string expected, bool truncated = false) =>
        new(Name, offset, expected, truncated);

    public static string MethodName(int method) => method switch
    {
        0 => "store",
        1 => "shrink",
        6 => "implode",
        8 => "deflate",
        9 => "deflate64",
        12 => "bzip2",
        14 => "lzma",
        93 => "zstd",
        95 => "xz",
        98 => "ppmd",
        99 => "AES encryption",
        _ => "unknown",
    };

    // ---- DOS date and time (APPNOTE 4.4.6). The fields carry no time zone: they are read and written as UTC. ----

    public static DateTimeOffset? FromDos(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9);
        int month = (date >> 5) & 15;
        int day = date & 31;
        int hour = time >> 11;
        int minute = (time >> 5) & 63;
        int second = (time & 31) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
    }

    public static (ushort Date, ushort Time) ToDos(DateTimeOffset? value)
    {
        if (value is not { } t)
        {
            return (0x0021, 0);
        }

        DateTime utc = t.UtcDateTime;
        if (utc.Year < 1980)
        {
            return (0x0021, 0);
        }

        if (utc.Year > 2107)
        {
            return ((ushort)(((2107 - 1980) << 9) | (12 << 5) | 31), (ushort)((23 << 11) | (59 << 5) | 29));
        }

        return ((ushort)(((utc.Year - 1980) << 9) | (utc.Month << 5) | utc.Day), (ushort)((utc.Hour << 11) | (utc.Minute << 5) | (utc.Second / 2)));
    }

    // Windows FILETIME: 100 ns ticks since 1601-01-01.
    private const long FileTimeEpochTicks = 504_911_232_000_000_000L;

    public static DateTimeOffset? FromFileTime(ulong value)
    {
        if (value == 0 || value > (ulong)(DateTime.MaxValue.Ticks - FileTimeEpochTicks))
        {
            return null;
        }

        return new DateTimeOffset((long)value + FileTimeEpochTicks, TimeSpan.Zero);
    }

    public static ulong ToFileTime(DateTimeOffset value) => (ulong)Math.Max(0, value.UtcTicks - FileTimeEpochTicks);

    // ---- Names ----

    private const string Cp437High =
        "\u00C7\u00FC\u00E9\u00E2\u00E4\u00E0\u00E5\u00E7\u00EA\u00EB\u00E8\u00EF\u00EE\u00EC\u00C4\u00C5" +
        "\u00C9\u00E6\u00C6\u00F4\u00F6\u00F2\u00FB\u00F9\u00FF\u00D6\u00DC\u00A2\u00A3\u00A5\u20A7\u0192" +
        "\u00E1\u00ED\u00F3\u00FA\u00F1\u00D1\u00AA\u00BA\u00BF\u2310\u00AC\u00BD\u00BC\u00A1\u00AB\u00BB" +
        "\u2591\u2592\u2593\u2502\u2524\u2561\u2562\u2556\u2555\u2563\u2551\u2557\u255D\u255C\u255B\u2510" +
        "\u2514\u2534\u252C\u251C\u2500\u253C\u255E\u255F\u255A\u2554\u2569\u2566\u2560\u2550\u256C\u2567" +
        "\u2568\u2564\u2565\u2559\u2558\u2552\u2553\u256B\u256A\u2518\u250C\u2588\u2584\u258C\u2590\u2580" +
        "\u03B1\u00DF\u0393\u03C0\u03A3\u03C3\u00B5\u03C4\u03A6\u0398\u03A9\u03B4\u221E\u03C6\u03B5\u2229" +
        "\u2261\u00B1\u2265\u2264\u2320\u2321\u00F7\u2248\u00B0\u2219\u00B7\u221A\u207F\u00B2\u25A0\u00A0";

    /// <summary>Decodes IBM code page 437, the encoding APPNOTE 4.4.17 names for entries without the UTF-8 flag.</summary>
    public static string DecodeCp437(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes.ToArray(), static (chars, b) =>
        {
            for (int i = 0; i < b.Length; i++)
            {
                chars[i] = b[i] < 0x80 ? (char)b[i] : Cp437High[b[i] - 0x80];
            }
        });
    }

    public static string DecodeName(ReadOnlySpan<byte> raw, ushort flags, ReadOnlySpan<byte> unicodeExtra, Encoding? legacy)
    {
        if ((flags & FlagUtf8) != 0)
        {
            return Encoding.UTF8.GetString(raw);
        }

        // Info-ZIP Unicode Path Extra Field: version 1, CRC-32 of the header's name, then the UTF-8 name. The name is
        // used only if the CRC shows the field belongs to this name.
        if (unicodeExtra.Length >= 5 && unicodeExtra[0] == 1
            && BinaryPrimitives.ReadUInt32LittleEndian(unicodeExtra[1..]) == RecursiveAI.OneCompress.Checksums.Crc32.Compute(raw)
            && Utf8.IsValid(unicodeExtra[5..]))
        {
            return Encoding.UTF8.GetString(unicodeExtra[5..]);
        }

        return legacy is null ? DecodeCp437(raw) : legacy.GetString(raw);
    }

    private static class Utf8
    {
        public static bool IsValid(ReadOnlySpan<byte> bytes) => System.Text.Unicode.Utf8.IsValid(bytes);
    }

    // ---- Extra fields (APPNOTE 4.3.7 and 4.5.1: id, size, data, repeated) ----

    /// <summary>Splits an extra field area into its records. Fewer than four bytes at the end are padding.</summary>
    public static List<ZipExtraField> ParseExtra(ReadOnlySpan<byte> extra, long offset)
    {
        var list = new List<ZipExtraField>();
        int p = 0;
        while (extra.Length - p >= 4)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra[p..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(p + 2)..]);
            if (size > extra.Length - p - 4)
            {
                throw Bad(offset + p, $"an extra field (id 0x{id:X4}) of {size} bytes inside the extra data");
            }

            list.Add(new ZipExtraField(id, extra.Slice(p + 4, size).ToArray()));
            p += 4 + size;
        }

        return list;
    }

    public static ReadOnlySpan<byte> Find(List<ZipExtraField> fields, ushort id)
    {
        foreach (ZipExtraField f in fields)
        {
            if (f.Id == id)
            {
                return f.Data.Span;
            }
        }

        return default;
    }

    public static bool Has(List<ZipExtraField> fields, ushort id)
    {
        foreach (ZipExtraField f in fields)
        {
            if (f.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the modification, access and change times from the extended timestamp field (0x5455) and the NTFS field
    /// (0x000a). NTFS values win: they have 100 ns precision.
    /// </summary>
    public static void ReadTimes(List<ZipExtraField> fields, ref DateTimeOffset? modified, ref DateTimeOffset? accessed, ref DateTimeOffset? changed)
    {
        ReadOnlySpan<byte> ut = Find(fields, ExtraExtendedTime);
        if (ut.Length >= 1)
        {
            // Bit 0: modification, bit 1: access, bit 2: creation, each a 32-bit Unix time in that order. The central
            // directory copy may carry the modification time alone, whatever the flags say.
            int flags = ut[0];
            int p = 1;
            if ((flags & 1) != 0 && ut.Length - p >= 4)
            {
                modified = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt32LittleEndian(ut[p..]));
                p += 4;
            }

            if ((flags & 2) != 0 && ut.Length - p >= 4)
            {
                accessed = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt32LittleEndian(ut[p..]));
                p += 4;
            }

            if ((flags & 4) != 0 && ut.Length - p >= 4)
            {
                changed = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt32LittleEndian(ut[p..]));
            }
        }

        ReadOnlySpan<byte> ntfs = Find(fields, ExtraNtfs);
        if (ntfs.Length >= 4)
        {
            // 4 reserved bytes, then attributes: tag (2), size (2), data. Tag 1 holds three FILETIMEs.
            int p = 4;
            while (ntfs.Length - p >= 4)
            {
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(ntfs[p..]);
                int size = BinaryPrimitives.ReadUInt16LittleEndian(ntfs[(p + 2)..]);
                p += 4;
                if (size > ntfs.Length - p)
                {
                    break;
                }

                if (tag == 1 && size >= 24)
                {
                    modified = FromFileTime(BinaryPrimitives.ReadUInt64LittleEndian(ntfs[p..])) ?? modified;
                    accessed = FromFileTime(BinaryPrimitives.ReadUInt64LittleEndian(ntfs[(p + 8)..])) ?? accessed;
                    changed = FromFileTime(BinaryPrimitives.ReadUInt64LittleEndian(ntfs[(p + 16)..])) ?? changed;
                }

                p += size;
            }
        }
    }

    /// <summary>Unix file type bits (st_mode &amp; S_IFMT) mapped to an entry kind.</summary>
    public static ArchiveEntryKind KindFromUnixMode(uint mode, ArchiveEntryKind fallback) => (mode & 0xF000) switch
    {
        0xA000 => ArchiveEntryKind.Symlink,
        0x4000 => ArchiveEntryKind.Directory,
        0x2000 => ArchiveEntryKind.CharacterDevice,
        0x6000 => ArchiveEntryKind.BlockDevice,
        0x1000 or 0xC000 => ArchiveEntryKind.Fifo,
        _ => fallback,
    };
}
