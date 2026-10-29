using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneArchive.Tests.Rar;

/// <summary>
/// Assembles RAR 5 archives byte by byte from the technote, independently of the reader, so tests can make the ones the real
/// tool cannot (Windows hosts, hostile headers) and break the ones it can.
/// </summary>
internal sealed class RarTestBuilder
{
    public static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    private readonly List<byte> _out = [];

    public RarTestBuilder(bool signature = true)
    {
        if (signature)
        {
            _out.AddRange(Signature);
        }
    }

    public static byte[] Vint(ulong value)
    {
        var b = new List<byte>();
        do
        {
            byte x = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                x |= 0x80;
            }

            b.Add(x);
        }
        while (value != 0);
        return [.. b];
    }

    public static byte[] U32(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    public static byte[] U64(ulong v)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    public static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>An extra-area record: size, type, payload.</summary>
    public static byte[] Record(ulong type, params byte[][] payload)
    {
        byte[] body = Cat([Vint(type), .. payload]);
        return Cat(Vint((ulong)body.Length), body);
    }

    /// <summary>A header block with its CRC-32 (technote, "General archive block format").</summary>
    public static byte[] Block(int type, ulong flags, byte[] fields, byte[]? extra = null, long dataSize = -1)
    {
        extra ??= [];
        if (extra.Length > 0)
        {
            flags |= 0x1;
        }

        if (dataSize >= 0)
        {
            flags |= 0x2;
        }

        byte[] body = Cat(
            Vint((ulong)type),
            Vint(flags),
            extra.Length > 0 ? Vint((ulong)extra.Length) : [],
            dataSize >= 0 ? Vint((ulong)dataSize) : [],
            fields,
            extra);
        byte[] size = Vint((ulong)body.Length);
        uint crc = Crc32.Update(Crc32.Compute(size), body);
        return Cat(U32(crc), size, body);
    }

    public RarTestBuilder Main(ulong archiveFlags = 0, long? volumeNumber = null, byte[]? extra = null)
    {
        byte[] fields = volumeNumber is { } v ? Cat(Vint(archiveFlags | 0x2), Vint((ulong)v)) : Vint(archiveFlags);
        _out.AddRange(Block(1, 0, fields, extra));
        return this;
    }

    public sealed class FileOptions
    {
        public bool Directory { get; set; }
        public ulong Attributes { get; set; } = 0x81A4;
        public int HostOs { get; set; } = 1;
        public uint? MTime { get; set; } = 1_700_000_000;
        public bool WriteCrc { get; set; } = true;
        public uint? CrcOverride { get; set; }
        public ulong CompressionInfo { get; set; }
        public bool Continued { get; set; }       // data continues from the previous volume
        public bool ContinuesInNext { get; set; } // data continues in the next volume
        public ulong? UnpackedSize { get; set; }  // defaults to the data length
        public long? DataSizeOverride { get; set; }
        public bool UnknownSize { get; set; }
        public byte[]? Extra { get; set; }
        public int Type { get; set; } = 2;
    }

    public RarTestBuilder File(string name, byte[] data, Action<FileOptions>? configure = null)
    {
        var o = new FileOptions();
        configure?.Invoke(o);
        ulong flags = 0;
        if (o.Continued)
        {
            flags |= 0x8;
        }

        if (o.ContinuesInNext)
        {
            flags |= 0x10;
        }

        ulong fileFlags = 0;
        if (o.Directory)
        {
            fileFlags |= 0x1;
        }

        if (o.MTime is not null)
        {
            fileFlags |= 0x2;
        }

        if (o.WriteCrc && !o.Directory)
        {
            fileFlags |= 0x4;
        }

        if (o.UnknownSize)
        {
            fileFlags |= 0x8;
        }

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] fields = Cat(
            Vint(fileFlags),
            Vint(o.UnpackedSize ?? (o.Directory ? 0UL : (ulong)data.Length)),
            Vint(o.Attributes),
            o.MTime is { } t ? U32(t) : [],
            (fileFlags & 0x4) != 0 ? U32(o.CrcOverride ?? Crc32.Compute(data)) : [],
            Vint(o.CompressionInfo),
            Vint((ulong)o.HostOs),
            Vint((ulong)nameBytes.Length),
            nameBytes);
        _out.AddRange(Block(o.Type, flags, fields, o.Extra, o.DataSizeOverride ?? data.Length));
        _out.AddRange(data);
        return this;
    }

    public RarTestBuilder End(bool moreVolumes = false)
    {
        _out.AddRange(Block(5, 0, Vint(moreVolumes ? 1UL : 0UL)));
        return this;
    }

    public RarTestBuilder Raw(byte[] bytes)
    {
        _out.AddRange(bytes);
        return this;
    }

    public byte[] ToArray() => [.. _out];

    /// <summary>Recomputes the header CRC-32 of every block it can walk, so a mutated header gets past the checksum and reaches the field parsers.</summary>
    public static void Resign(byte[] a)
    {
        int p = Signature.Length;
        while (p + 5 <= a.Length)
        {
            int q = p + 4;
            int headerSize = 0;
            int sizeLength = 0;
            while (q < a.Length && sizeLength < 3)
            {
                byte b = a[q++];
                headerSize |= (b & 0x7F) << (7 * sizeLength++);
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            if (headerSize < 2 || q + headerSize > a.Length)
            {
                return;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(p), Crc32.Compute(a.AsSpan(p + 4, sizeLength + headerSize)));
            int pos = q;
            ulong ReadVint()
            {
                ulong v = 0;
                for (int i = 0; i < 10 && pos < q + headerSize; i++)
                {
                    byte b = a[pos++];
                    v |= (ulong)(b & 0x7F) << (7 * i);
                    if ((b & 0x80) == 0)
                    {
                        break;
                    }
                }

                return v;
            }

            ReadVint();
            ulong flags = ReadVint();
            if ((flags & 1) != 0)
            {
                ReadVint();
            }

            ulong data = (flags & 2) != 0 ? ReadVint() : 0;
            if (data > (ulong)a.Length)
            {
                return;
            }

            p = q + headerSize + (int)data;
        }
    }

    public static byte[] Redirection(ulong type, string target, ulong flags = 0)
    {
        byte[] t = Encoding.UTF8.GetBytes(target);
        return Record(5, Vint(type), Vint(flags), Vint((ulong)t.Length), t);
    }
}
