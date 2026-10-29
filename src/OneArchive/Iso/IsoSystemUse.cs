using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneArchive.Internal;

namespace RecursiveAI.OneArchive.Iso;

/// <summary>What the System Use entries of one directory record say (IEEE P1281 SUSP and IEEE P1282 Rock Ridge).</summary>
internal sealed class SystemUseInfo
{
    public bool HasSp;
    public int SkipLength;
    public bool HasPx;
    public uint Mode;
    public long Links;
    public long Uid;
    public long Gid;
    public long Serial;
    public ulong? Device;
    public byte[]? Name;
    public bool NameIsCurrent;
    public bool NameIsParent;
    public string? LinkTarget;
    public DateTimeOffset? Creation;
    public DateTimeOffset? Modify;
    public DateTimeOffset? Access;
    public DateTimeOffset? Attributes;
    public DateTimeOffset? Backup;
    public DateTimeOffset? Expiration;
    public DateTimeOffset? Effective;
    public bool Relocated;
    public long? ChildLink;
    public long? ParentLink;
    public long? ZisofsSize;
    public List<string>? ExtensionIds;
    public bool HasRockRidgeEntries;
}

/// <summary>Reads a continuation area (a "CE" target); supplied by the reader, which bounds it by the image and by the total budget.</summary>
internal delegate byte[] ContinuationReader(long block, long offset, long length, long recordOffset);

internal static class SystemUseParser
{
    /// <summary>
    /// Parses the System Use field of a directory record. The first entry starts at <paramref name="skip"/> (SUSP 4, "LEN_SKP"), except
    /// in the root directory's own "." record, where the "SP" entry comes first and <paramref name="skip"/> is zero.
    /// </summary>
    public static SystemUseInfo Parse(ReadOnlySpan<byte> field, int skip, long recordOffset, int maxTotal, ContinuationReader readContinuation)
    {
        var info = new SystemUseInfo();
        byte[]? nameBuf = null;
        bool nameDone = false;
        List<byte>? slBuf = null;
        bool slDone = false;
        int total = field.Length;
        if (skip > field.Length)
        {
            return info;
        }

        byte[] area = field[skip..].ToArray();
        long areaOffset = recordOffset;
        var seen = new List<(long Block, long Offset, long Length)>();
        while (true)
        {
            (long Block, long Offset, long Length)? next = null;
            int i = 0;
            while (area.Length - i >= 4)
            {
                byte s0 = area[i];
                byte s1 = area[i + 1];
                int len = area[i + 2];
                if (s0 == 0 && s1 == 0)
                {
                    break;
                }

                if (len < 4 || i + len > area.Length)
                {
                    throw IsoFormat.Bad(areaOffset + i, $"a System Use entry '{(char)s0}{(char)s1}' of at least 4 bytes that fits the field (length {len}, {area.Length - i} left)");
                }

                ReadOnlySpan<byte> e = area.AsSpan(i, len);
                long at = areaOffset + i;
                string sig = string.Create(2, (s0, s1), static (span, v) => { span[0] = (char)v.s0; span[1] = (char)v.s1; });
                switch (sig)
                {
                    case "SP":
                        // SUSP 5.3: 0xBE 0xEF check bytes, then LEN_SKP.
                        if (len >= 7 && e[4] == 0xBE && e[5] == 0xEF)
                        {
                            info.HasSp = true;
                            info.SkipLength = e[6];
                        }

                        break;
                    case "ST":
                        i = area.Length;
                        continue;
                    case "CE":
                        if (len < 28)
                        {
                            throw IsoFormat.Bad(at, $"a 28-byte CE entry (length {len})");
                        }

                        next = (IsoFormat.ReadBoth32(e[4..], at + 4, "the continuation block"), IsoFormat.ReadBoth32(e[12..], at + 12, "the continuation offset"), IsoFormat.ReadBoth32(e[20..], at + 20, "the continuation length"));
                        break;
                    case "ER":
                        if (len >= 8)
                        {
                            int idLen = e[4];
                            int desLen = e[5];
                            int srcLen = e[6];
                            if (8 + idLen + desLen + srcLen > len)
                            {
                                throw IsoFormat.Bad(at, "an ER entry whose identifier, descriptor and source fit its length");
                            }

                            (info.ExtensionIds ??= []).Add(Encoding.Latin1.GetString(e.Slice(8, idLen)));
                        }

                        break;
                    case "PX":
                        // RRIP 4.1.1: mode, links, uid, gid (and serial number in the 44-byte form), each in both byte orders.
                        if (len >= 36)
                        {
                            info.HasPx = true;
                            info.HasRockRidgeEntries = true;
                            info.Mode = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                            info.Links = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                            info.Uid = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
                            info.Gid = BinaryPrimitives.ReadUInt32LittleEndian(e[28..]);
                            if (len >= 44)
                            {
                                info.Serial = BinaryPrimitives.ReadUInt32LittleEndian(e[36..]);
                            }
                        }

                        break;
                    case "PN":
                        if (len >= 20)
                        {
                            info.HasRockRidgeEntries = true;
                            info.Device = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                        }

                        break;
                    case "NM":
                        if (len >= 5 && !nameDone)
                        {
                            info.HasRockRidgeEntries = true;
                            int flags = e[4];
                            if ((flags & 0x02) != 0)
                            {
                                info.NameIsCurrent = true;
                            }
                            else if ((flags & 0x04) != 0)
                            {
                                info.NameIsParent = true;
                            }
                            else
                            {
                                nameBuf = nameBuf is null ? e[5..].ToArray() : [.. nameBuf, .. e[5..]];
                            }

                            nameDone = (flags & 0x01) == 0;
                        }

                        break;
                    case "SL":
                        if (len >= 5 && !slDone)
                        {
                            info.HasRockRidgeEntries = true;
                            slBuf ??= [];
                            slBuf.AddRange(e[5..].ToArray());
                            slDone = (e[4] & 0x01) == 0;
                        }

                        break;
                    case "TF":
                        if (len >= 5)
                        {
                            info.HasRockRidgeEntries = true;
                            ParseTf(e, at, info);
                        }

                        break;
                    case "RE":
                        info.HasRockRidgeEntries = true;
                        info.Relocated = true;
                        break;
                    case "CL":
                        if (len >= 12)
                        {
                            info.HasRockRidgeEntries = true;
                            info.ChildLink = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                        }

                        break;
                    case "PL":
                        if (len >= 12)
                        {
                            info.HasRockRidgeEntries = true;
                            info.ParentLink = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                        }

                        break;
                    case "ZF":
                        // The zisofs extension: algorithm "pz", then header size, block size and the uncompressed size.
                        if (len >= 16 && e[4] == (byte)'p' && e[5] == (byte)'z')
                        {
                            info.ZisofsSize = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                        }

                        break;
                    default:
                        // PD, SF, ES and entries of other extensions are skipped.
                        break;
                }

                i += len;
            }

            if (next is not { } ce)
            {
                break;
            }

            // A continuation must not revisit or overlap an area of this record: that is how a loop is written.
            foreach ((long b, long o, long l) in seen)
            {
                if (b == ce.Block && ((o < ce.Offset + ce.Length && ce.Offset < o + l) || o == ce.Offset))
                {
                    throw IsoFormat.Bad(areaOffset, $"continuation areas that do not overlap (block {ce.Block} offset {ce.Offset} was already used by this record)");
                }
            }

            if (ce.Length == 0 || ce.Length > maxTotal || total + ce.Length > maxTotal)
            {
                throw new RecursiveAI.OneCompress.CompressionLimitException(IsoFormat.Name, nameof(IsoReaderOptions.MaxSystemUseSize), maxTotal, total + ce.Length);
            }

            seen.Add(ce);
            total += (int)ce.Length;
            area = readContinuation(ce.Block, ce.Offset, ce.Length, recordOffset);
            areaOffset = (ce.Block * IsoFormat.SectorSize) + ce.Offset;
        }

        info.Name = nameBuf;
        if (slBuf is not null)
        {
            info.LinkTarget = BuildLink(slBuf, recordOffset);
        }

        return info;
    }

    // RRIP 4.1.3: component records, each with flags and a length; a component may continue in the next record.
    private static string BuildLink(List<byte> area, long offset)
    {
        var parts = new List<string>();
        bool absolute = false;
        var current = new StringBuilder();
        bool continued = false;
        int i = 0;
        while (i < area.Count)
        {
            if (i + 2 > area.Count)
            {
                throw IsoFormat.Bad(offset, "a symbolic link component record with flags and a length");
            }

            int flags = area[i];
            int len = area[i + 1];
            if (i + 2 + len > area.Count)
            {
                throw IsoFormat.Bad(offset, "a symbolic link component record that fits the SL entries");
            }

            string text = PathRules.DecodeName(area.GetRange(i + 2, len).ToArray());
            i += 2 + len;
            if (!continued)
            {
                current.Clear();
            }

            if ((flags & 0x02) != 0)
            {
                current.Append('.');
            }
            else if ((flags & 0x04) != 0)
            {
                current.Append("..");
            }
            else if ((flags & 0x08) != 0)
            {
                absolute = true;
            }
            else
            {
                current.Append(text);
            }

            continued = (flags & 0x01) != 0;
            if (!continued && (current.Length > 0))
            {
                parts.Add(current.ToString());
            }
        }

        if (continued && current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        string joined = string.Join('/', parts);
        return absolute ? "/" + joined : joined;
    }

    // RRIP 4.1.6: a flag byte, then one time stamp per set bit among creation, modify, access, attributes, backup, expiration, effective.
    private static void ParseTf(ReadOnlySpan<byte> e, long at, SystemUseInfo info)
    {
        int flags = e[4];
        bool longForm = (flags & 0x80) != 0;
        int size = longForm ? 17 : 7;
        int p = 5;
        for (int bit = 0; bit < 7; bit++)
        {
            if ((flags & (1 << bit)) == 0)
            {
                continue;
            }

            if (p + size > e.Length)
            {
                throw IsoFormat.Bad(at, "a TF entry long enough for the time stamps its flags announce");
            }

            ReadOnlySpan<byte> t = e.Slice(p, size);
            DateTimeOffset? value = longForm ? IsoFormat.ParseDate17(t) : IsoFormat.ParseDate7(t);
            p += size;
            switch (bit)
            {
                case 0: info.Creation = value; break;
                case 1: info.Modify = value; break;
                case 2: info.Access = value; break;
                case 3: info.Attributes = value; break;
                case 4: info.Backup = value; break;
                case 5: info.Expiration = value; break;
                default: info.Effective = value; break;
            }
        }
    }
}
