using System.Globalization;
using System.Text;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tar;

/// <summary>Field layout and codecs of the 512-byte tar header (POSIX.1-2001 pax, "ustar interchange format"; GNU tar manual, "Basic Tar Format").</summary>
internal static class TarHeader
{
    public const int BlockSize = 512;
    public const string FormatName = "tar";

    // Offsets and sizes of the ustar fields.
    public const int NameAt = 0, NameLen = 100;
    public const int ModeAt = 100, ModeLen = 8;
    public const int UidAt = 108, UidLen = 8;
    public const int GidAt = 116, GidLen = 8;
    public const int SizeAt = 124, SizeLen = 12;
    public const int MtimeAt = 136, MtimeLen = 12;
    public const int ChecksumAt = 148, ChecksumLen = 8;
    public const int TypeAt = 156;
    public const int LinkAt = 157, LinkLen = 100;
    public const int MagicAt = 257, MagicLen = 6;
    public const int VersionAt = 263, VersionLen = 2;
    public const int UnameAt = 265, UnameLen = 32;
    public const int GnameAt = 297, GnameLen = 32;
    public const int DevMajorAt = 329, DevMajorLen = 8;
    public const int DevMinorAt = 337, DevMinorLen = 8;
    public const int PrefixAt = 345, PrefixLen = 155;

    // GNU tar's use of the bytes after devminor (old GNU header).
    public const int GnuAtimeAt = 345, GnuCtimeAt = 357, GnuTimeLen = 12;
    public const int GnuSparseAt = 386, GnuSparseCount = 4, GnuSparseEntryLen = 24;
    public const int GnuIsExtendedAt = 482;
    public const int GnuRealSizeAt = 483, GnuRealSizeLen = 12;

    // An extension block of an old GNU sparse file: 21 entries and the "more follows" flag.
    public const int GnuExtSparseCount = 21, GnuExtIsExtendedAt = 504;

    /// <summary>The largest value an 11-digit octal size or time field holds.</summary>
    public const long MaxOctal11 = 8589934591;

    /// <summary>The largest value a 7-digit octal field holds (mode, uid, gid, device numbers).</summary>
    public const long MaxOctal7 = 2097151;

    public static bool IsZero(ReadOnlySpan<byte> block) => !block.ContainsAnyExcept((byte)0);

    public static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        return nul < 0 ? field : field[..nul];
    }

    public static uint ComputeChecksum(ReadOnlySpan<byte> block, bool signed)
    {
        long sum = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            byte b = i >= ChecksumAt && i < ChecksumAt + ChecksumLen ? (byte)' ' : block[i];
            sum += signed ? (sbyte)b : b;
        }

        return unchecked((uint)sum);
    }

    /// <summary>
    /// Reads a numeric field: octal digits padded with spaces or NULs, or GNU's base-256 form (high bit of the first
    /// byte set; 0xFF as first byte means negative). Throws a format error naming the field otherwise.
    /// </summary>
    public static long ParseNumber(ReadOnlySpan<byte> field, long offset, string what)
    {
        if (field.Length > 0 && (field[0] & 0x80) != 0)
        {
            bool negative = field[0] == 0xFF;
            ulong value = negative ? ulong.MaxValue : (ulong)(field[0] & 0x7F);
            for (int i = 1; i < field.Length; i++)
            {
                if (!negative && value > (ulong)long.MaxValue >> 8)
                {
                    throw Bad(offset, $"{what} that fits in 63 bits");
                }

                value = (value << 8) | field[i];
            }

            long result = unchecked((long)value);
            if (negative ? result >= 0 : result < 0)
            {
                throw Bad(offset, $"{what} that fits in 63 bits");
            }

            return result;
        }

        int p = 0;
        while (p < field.Length && (field[p] == ' ' || field[p] == 0))
        {
            p++;
        }

        long n = 0;
        int digits = 0;
        while (p < field.Length && field[p] >= '0' && field[p] <= '7')
        {
            if (n > (long.MaxValue >> 3))
            {
                throw Bad(offset, $"{what} that fits in 63 bits");
            }

            n = (n << 3) | (long)(field[p] - '0');
            p++;
            digits++;
        }

        // What follows the digits must be padding.
        while (p < field.Length)
        {
            if (field[p] != ' ' && field[p] != 0)
            {
                throw Bad(offset, $"{what} as octal digits");
            }

            p++;
        }

        return digits == 0 ? 0 : n;
    }

    public static CompressionFormatException Bad(long offset, string expected, bool truncated = false) =>
        new(FormatName, offset, expected, truncated);

    // ---- pax records (POSIX.1-2001 pax "Extended Header", the 'length key=value\n' lines) ----

    /// <summary>Parses extended-header data into its records, in order. Duplicate keys are kept.</summary>
    public static List<KeyValuePair<string, string>> ParsePax(ReadOnlySpan<byte> data, long offset)
    {
        var list = new List<KeyValuePair<string, string>>();
        int at = 0;
        while (at < data.Length)
        {
            // length: decimal digits, a space, then key=value and a newline; the length counts the whole record.
            int p = at;
            long length = 0;
            while (p < data.Length && data[p] >= '0' && data[p] <= '9')
            {
                length = (length * 10) + (data[p] - '0');
                if (length > data.Length)
                {
                    throw Bad(offset + at, "a pax record length within the extended header");
                }

                p++;
            }

            if (p == at || p >= data.Length || data[p] != ' ')
            {
                throw Bad(offset + at, "a pax record starting with its decimal length and a space");
            }

            if (length < (p - at) + 1 + 3 || at + length > data.Length || data[at + (int)length - 1] != (byte)'\n')
            {
                throw Bad(offset + at, "a pax record of 'length key=value' ending in a newline");
            }

            ReadOnlySpan<byte> body = data.Slice(p + 1, (int)length - (p - at) - 2);
            int eq = body.IndexOf((byte)'=');
            if (eq <= 0)
            {
                throw Bad(offset + at, "a pax record with a key followed by '='");
            }

            list.Add(new KeyValuePair<string, string>(Internal.PathRules.DecodeName(body[..eq]), Internal.PathRules.DecodeName(body[(eq + 1)..])));
            at += (int)length;
        }

        return list;
    }

    /// <summary>Writes pax records, each as <c>length key=value\n</c> with the length counting itself.</summary>
    public static void AppendPaxRecord(MemoryStream output, string key, string value)
    {
        int payload = 1 + Encoding.UTF8.GetByteCount(key) + 1 + Encoding.UTF8.GetByteCount(value) + 1;
        int digits = payload.ToString(CultureInfo.InvariantCulture).Length;
        int total = payload + digits;
        if (total.ToString(CultureInfo.InvariantCulture).Length != digits)
        {
            total++;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(total.ToString(CultureInfo.InvariantCulture) + " " + key + "=" + value + "\n");
        output.Write(bytes);
    }

    // ---- times ----

    private const long TicksPerSecond = 10_000_000;
    private const long MinSeconds = -62135596800;
    private const long MaxSeconds = 253402300799;

    public static DateTimeOffset FromUnixSeconds(long seconds, long offset, string what)
    {
        if (seconds < MinSeconds || seconds > MaxSeconds)
        {
            throw Bad(offset, $"{what} between the years 1 and 9999");
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    /// <summary>Parses a pax time: optional minus, digits, optional fraction. Digits beyond 100 ns are dropped.</summary>
    public static DateTimeOffset ParsePaxTime(string text, long offset, string key)
    {
        int p = 0;
        bool negative = false;
        if (text.Length > 0 && text[0] == '-')
        {
            negative = true;
            p = 1;
        }

        long seconds = 0;
        int digits = 0;
        while (p < text.Length && text[p] >= '0' && text[p] <= '9')
        {
            seconds = (seconds * 10) + (text[p] - '0');
            if (seconds > MaxSeconds + 1)
            {
                throw Bad(offset, $"pax {key} between the years 1 and 9999");
            }

            p++;
            digits++;
        }

        long fraction = 0;
        if (p < text.Length && text[p] == '.')
        {
            p++;
            int f = 0;
            while (p < text.Length && text[p] >= '0' && text[p] <= '9')
            {
                if (f < 7)
                {
                    fraction = (fraction * 10) + (text[p] - '0');
                    f++;
                }

                p++;
                digits++;
            }

            while (f < 7)
            {
                fraction *= 10;
                f++;
            }
        }

        if (digits == 0 || p != text.Length)
        {
            throw Bad(offset, $"pax {key} as seconds with an optional fraction");
        }

        long ticks = (seconds * TicksPerSecond) + fraction;
        if (negative)
        {
            ticks = -ticks;
        }

        long total = DateTimeOffset.UnixEpoch.UtcTicks + ticks;
        if (total < DateTimeOffset.MinValue.UtcTicks || total > DateTimeOffset.MaxValue.UtcTicks)
        {
            throw Bad(offset, $"pax {key} between the years 1 and 9999");
        }

        return new DateTimeOffset(total, TimeSpan.Zero);
    }

    /// <summary>Formats a time for a pax record: seconds, with a fraction (trailing zeros dropped) only when there is one.</summary>
    public static string FormatPaxTime(DateTimeOffset time)
    {
        long ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        bool negative = ticks < 0;
        long abs = negative ? -ticks : ticks;
        long seconds = abs / TicksPerSecond;
        long fraction = abs % TicksPerSecond;
        string text = (negative ? "-" : string.Empty) + seconds.ToString(CultureInfo.InvariantCulture);
        if (fraction != 0)
        {
            text += "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return text;
    }

    /// <summary>Seconds since the epoch, rounded towards negative infinity, and whether the time had a fraction.</summary>
    public static long FloorSeconds(DateTimeOffset time, out bool hasFraction)
    {
        long ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        long seconds = ticks / TicksPerSecond;
        long rem = ticks % TicksPerSecond;
        if (rem < 0)
        {
            seconds--;
        }

        hasFraction = rem != 0;
        return seconds;
    }

    // ---- writing numeric fields ----

    /// <summary>Writes <paramref name="value"/> as zero-padded octal with a NUL terminator; false if it does not fit or is negative.</summary>
    public static bool TryWriteOctal(Span<byte> field, long value)
    {
        if (value < 0)
        {
            return false;
        }

        int digits = field.Length - 1;
        field[digits] = 0;
        long v = value;
        for (int i = digits - 1; i >= 0; i--)
        {
            field[i] = (byte)('0' + (int)(v & 7));
            v >>= 3;
        }

        return v == 0;
    }
}
