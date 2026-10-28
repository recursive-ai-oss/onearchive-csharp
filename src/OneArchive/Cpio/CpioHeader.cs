using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Cpio;

// Field layouts: the GNU cpio manual ("new ASCII" and "crc" formats, 110-byte headers of 8-digit hexadecimal fields)
// and POSIX.1 pax's cpio interchange format ("odc", 76-byte headers of octal fields).
internal static class CpioHeader
{
    public const string FormatName = "cpio";
    public const int NewcHeaderLength = 110;
    public const int OdcHeaderLength = 76;

    public const uint TypeMask = 0xF000;
    public const uint TypeFile = 0x8000;
    public const uint TypeDirectory = 0x4000;
    public const uint TypeSymlink = 0xA000;
    public const uint TypeChar = 0x2000;
    public const uint TypeBlock = 0x6000;
    public const uint TypeFifo = 0x1000;
    public const uint TypeSocket = 0xC000;

    public static ReadOnlySpan<byte> TrailerName => "TRAILER!!!"u8;

    public static string Magic(CpioFormat f) => f switch
    {
        CpioFormat.Newc => "070701",
        CpioFormat.Crc => "070702",
        _ => "070707",
    };

    public static long Pad4(long n) => (n + 3) & ~3L;

    /// <summary>Returns the stored length of the data of a member: padded to 4 bytes in newc and crc, as is in odc.</summary>
    public static long PadData(CpioFormat f, long size) => f == CpioFormat.Odc ? size : (size > long.MaxValue - 4 ? long.MaxValue : Pad4(size));

    public static uint ByteSum(ReadOnlySpan<byte> data)
    {
        uint s = 0;
        foreach (byte b in data)
        {
            s += b;
        }

        return s;
    }

    public static long Hex(ReadOnlySpan<byte> field, long offset, string what)
    {
        long v = 0;
        foreach (byte c in field)
        {
            int d = c is >= (byte)'0' and <= (byte)'9' ? c - '0'
                : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10
                : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10
                : -1;
            if (d < 0)
            {
                throw new CompressionFormatException(FormatName, offset, $"the {what} as {field.Length} hexadecimal digits");
            }

            v = (v << 4) | (uint)d;
        }

        return v;
    }

    public static long Octal(ReadOnlySpan<byte> field, long offset, string what)
    {
        long v = 0;
        foreach (byte c in field)
        {
            if (c is < (byte)'0' or > (byte)'7')
            {
                throw new CompressionFormatException(FormatName, offset, $"the {what} as {field.Length} octal digits");
            }

            v = (v << 3) | (uint)(c - '0');
        }

        return v;
    }

    /// <summary>Writes a value as fixed-width upper-case hexadecimal; false if it does not fit.</summary>
    public static bool PutHex(Span<byte> field, long value)
    {
        if (value < 0 || (field.Length < 16 && value >> (4 * field.Length) != 0))
        {
            return false;
        }

        for (int i = field.Length - 1; i >= 0; i--, value >>= 4)
        {
            field[i] = (byte)"0123456789ABCDEF"[(int)(value & 15)];
        }

        return true;
    }

    /// <summary>Writes a value as fixed-width octal; false if it does not fit.</summary>
    public static bool PutOctal(Span<byte> field, long value)
    {
        if (value < 0 || (field.Length < 21 && value >> (3 * field.Length) != 0))
        {
            return false;
        }

        for (int i = field.Length - 1; i >= 0; i--, value >>= 3)
        {
            field[i] = (byte)('0' + (int)(value & 7));
        }

        return true;
    }
}
