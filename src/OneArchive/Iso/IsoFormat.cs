using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Iso;

/// <summary>Field encodings of ECMA-119 shared by the reader and the writer.</summary>
internal static class IsoFormat
{
    public const string Name = "iso9660";

    /// <summary>The size of a logical sector and, in the only block size supported, of a logical block (ECMA-119 clause 6.1.2).</summary>
    public const int SectorSize = 2048;

    /// <summary>Volume descriptors start at logical sector 16 (clause 6.2.1).</summary>
    public const int FirstDescriptorSector = 16;

    /// <summary>The offset at which the standard identifier "CD001" of the first descriptor sits (clause 8.4.2).</summary>
    public const int IdentifierOffset = FirstDescriptorSector * SectorSize + 1;

    /// <summary>The number of bytes ArchiveDetector needs to see the identifier.</summary>
    public const int DetectLength = IdentifierOffset + 6;

    public const int RecordFixedLength = 33;

    public static CompressionFormatException Bad(long offset, string expected, bool truncated = false) => new(Name, offset, expected, truncated);

    // ---- both-byte-order numbers (clauses 7.2.3, 7.3.3) ----

    public static uint ReadBoth32(ReadOnlySpan<byte> b, long offset, string what)
    {
        uint le = BinaryPrimitives.ReadUInt32LittleEndian(b);
        uint be = BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
        if (le != be)
        {
            throw Bad(offset, $"{what} recorded the same in both byte orders (found {le} and {be})");
        }

        return le;
    }

    public static void WriteBoth32(Span<byte> b, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        BinaryPrimitives.WriteUInt32BigEndian(b[4..], value);
    }

    public static void WriteBoth16(Span<byte> b, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        BinaryPrimitives.WriteUInt16BigEndian(b[2..], value);
    }

    // ---- dates ----

    /// <summary>Clause 9.1.5: seven bytes, years since 1900, month, day, hour, minute, second, and the offset from GMT in 15-minute steps. Null if it is not a valid date.</summary>
    public static DateTimeOffset? ParseDate7(ReadOnlySpan<byte> b)
    {
        int year = 1900 + b[0];
        int month = b[1];
        int day = b[2];
        int hour = b[3];
        int minute = b[4];
        int second = b[5];
        int offset = (sbyte)b[6];
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59 || offset is < -48 or > 52)
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offset * 15));
    }

    /// <summary>Clause 8.4.26.1: sixteen digits (year, month, day, hour, minute, second, hundredths) and an offset byte. All zeros means "not specified".</summary>
    public static DateTimeOffset? ParseDate17(ReadOnlySpan<byte> b)
    {
        static int Num(ReadOnlySpan<byte> d)
        {
            int v = 0;
            foreach (byte c in d)
            {
                if (c is < (byte)'0' or > (byte)'9')
                {
                    return -1;
                }

                v = (v * 10) + (c - '0');
            }

            return v;
        }

        int year = Num(b[..4]);
        int month = Num(b.Slice(4, 2));
        int day = Num(b.Slice(6, 2));
        int hour = Num(b.Slice(8, 2));
        int minute = Num(b.Slice(10, 2));
        int second = Num(b.Slice(12, 2));
        int centi = Num(b.Slice(14, 2));
        int offset = (sbyte)b[16];
        if (year < 1 || year > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour is < 0 or > 23
            || minute is < 0 or > 59 || second is < 0 or > 59 || centi < 0 || offset is < -48 or > 52)
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offset * 15)).AddMilliseconds(centi * 10);
    }

    public static void WriteDate7(Span<byte> b, DateTimeOffset time)
    {
        DateTime utc = time.UtcDateTime;
        int year = Math.Clamp(utc.Year, 1900, 2155);
        b[0] = (byte)(year - 1900);
        b[1] = (byte)utc.Month;
        b[2] = (byte)utc.Day;
        b[3] = (byte)utc.Hour;
        b[4] = (byte)utc.Minute;
        b[5] = (byte)utc.Second;
        b[6] = 0;
    }

    public static void WriteDate17(Span<byte> b, DateTimeOffset? time)
    {
        if (time is not { } t)
        {
            b[..16].Fill((byte)'0');
            b[16] = 0;
            return;
        }

        DateTime u = t.UtcDateTime;
        Encoding.ASCII.GetBytes($"{u.Year:D4}{u.Month:D2}{u.Day:D2}{u.Hour:D2}{u.Minute:D2}{u.Second:D2}{u.Millisecond / 10:D2}", b);
        b[16] = 0;
    }

    // ---- strings ----

    /// <summary>Reads a space-padded a-character or d-character field.</summary>
    public static string ReadText(ReadOnlySpan<byte> field) => Encoding.Latin1.GetString(field).TrimEnd(' ', '\0');

    /// <summary>Reads a space-padded Joliet field (UCS-2, big endian).</summary>
    public static string ReadJolietText(ReadOnlySpan<byte> field)
    {
        if ((field.Length & 1) != 0)
        {
            field = field[..^1];
        }

        return Encoding.BigEndianUnicode.GetString(field).TrimEnd(' ', '\0');
    }

    public static void WriteText(Span<byte> field, string text)
    {
        field.Fill((byte)' ');
        int n = Math.Min(field.Length, text.Length);
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            field[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'_';
        }
    }

    public static void WriteJolietText(Span<byte> field, string text)
    {
        for (int i = 0; i + 1 < field.Length; i += 2)
        {
            field[i] = 0;
            field[i + 1] = (byte)' ';
        }

        int n = Math.Min(field.Length / 2, text.Length);
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(field[(i * 2)..], text[i]);
        }
    }

    /// <summary>Strips the ";version" suffix (clause 7.5.1) and, for files, the dot left when the extension is empty.</summary>
    public static string StripVersion(string identifier, bool isDirectory)
    {
        if (isDirectory)
        {
            return identifier;
        }

        int semi = identifier.LastIndexOf(';');
        if (semi >= 0)
        {
            bool digits = semi + 1 < identifier.Length;
            for (int i = semi + 1; i < identifier.Length; i++)
            {
                if (identifier[i] is < '0' or > '9')
                {
                    digits = false;
                    break;
                }
            }

            if (digits)
            {
                identifier = identifier[..semi];
            }
        }

        if (identifier.Length > 1 && identifier[^1] == '.')
        {
            identifier = identifier[..^1];
        }

        return identifier;
    }
}
