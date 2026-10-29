using System.Text;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Rar;

/// <summary>
/// A header block of a RAR 5 archive as read from the stream: the common fields are parsed, the rest of the body is kept as
/// bytes for the type-specific parser (RAR 5.0 technote, "General archive block format").
/// </summary>
internal sealed class RarBlock
{
    public long Offset;          // from the signature, in this volume
    public int Type;
    public ulong Flags;
    public byte[] Body = [];     // from the Header type field to the end of the extra area
    public int FieldsStart;      // first byte after the common fields
    public int ExtraStart;       // start of the extra area (== Body.Length without one)
    public long BodyOffset;      // where Body[0] sits in the volume
    public ulong DataSize;
    public long DataOffset;      // where the data area starts

    public bool HasExtra => (Flags & 0x1) != 0;

    public bool HasData => (Flags & 0x2) != 0;

    public bool ContinuedFromPrevious => (Flags & 0x8) != 0;

    public bool ContinuesInNext => (Flags & 0x10) != 0;
}

/// <summary>Reads the vints and fixed-size fields of a header body, naming the offset when the body is too short or a vint is malformed.</summary>
internal ref struct RarFieldReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly long _baseOffset;
    private int _pos;

    public RarFieldReader(ReadOnlySpan<byte> data, long baseOffset, int start = 0)
    {
        _data = data;
        _baseOffset = baseOffset;
        _pos = start;
    }

    public readonly int Position => _pos;

    public readonly int Remaining => _data.Length - _pos;

    public readonly long OffsetHere => _baseOffset + _pos;

    /// <summary>Reads a vint (technote, "vint"): seven bits per byte, low group first, high bit means more. Padding (0x80) bytes are allowed.</summary>
    public ulong ReadVint(string what)
    {
        ulong value = 0;
        for (int i = 0; i < 10; i++)
        {
            if (_pos >= _data.Length)
            {
                throw new CompressionFormatException(RarReader.Name, OffsetHere, $"{what}: a vint that ends inside the header", true);
            }

            byte b = _data[_pos++];
            if (i == 9 && b > 1)
            {
                throw new CompressionFormatException(RarReader.Name, OffsetHere - 1, $"{what}: a vint that fits in 64 bits");
            }

            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new CompressionFormatException(RarReader.Name, OffsetHere, $"{what}: a vint of at most 10 bytes");
    }

    /// <summary>Reads a vint that must be at most <paramref name="max"/>.</summary>
    public int ReadSmall(string what, int max = int.MaxValue)
    {
        long at = OffsetHere;
        ulong v = ReadVint(what);
        if (v > (ulong)max)
        {
            throw new CompressionFormatException(RarReader.Name, at, $"{what} of at most {max} (found {v})");
        }

        return (int)v;
    }

    public uint ReadUInt32(string what)
    {
        ReadOnlySpan<byte> s = Take(4, what);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s);
    }

    public ulong ReadUInt64(string what)
    {
        ReadOnlySpan<byte> s = Take(8, what);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s);
    }

    public byte ReadByte(string what) => Take(1, what)[0];

    public ReadOnlySpan<byte> Take(int count, string what)
    {
        if ((uint)count > (uint)Remaining)
        {
            throw new CompressionFormatException(RarReader.Name, OffsetHere, $"{what}: {count} bytes, but only {Remaining} are left in the header", true);
        }

        ReadOnlySpan<byte> s = _data.Slice(_pos, count);
        _pos += count;
        return s;
    }

    public void Skip(int count, string what) => Take(count, what);
}

/// <summary>Decodes names and times the way the technote defines them.</summary>
internal static class RarText
{
    private static readonly UTF8Encoding s_utf8 = new(false, false);

    // 100-ns ticks between 0001-01-01 and 1601-01-01 (the Windows FILETIME epoch).
    private const long FileTimeEpochTicks = 504_911_232_000_000_000L;

    public static string Utf8(ReadOnlySpan<byte> bytes) => s_utf8.GetString(bytes);

    /// <summary>A name for an error message: names come from hostile input, so control characters and long names are cut.</summary>
    public static string Printable(string name)
    {
        char[] chars = name.Length > 200 ? name[..200].ToCharArray() : name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }

    /// <summary>Windows FILETIME to a time; a value outside the representable range reads as <see langword="null"/>.</summary>
    public static DateTimeOffset? FromFileTime(ulong filetime)
    {
        if (filetime > (ulong)(DateTimeOffset.MaxValue.UtcTicks - FileTimeEpochTicks))
        {
            return null;
        }

        return new DateTimeOffset(FileTimeEpochTicks + (long)filetime, TimeSpan.Zero);
    }

    /// <summary>Unix seconds plus nanoseconds, truncated to 100 ns; out of range reads as <see langword="null"/>.</summary>
    public static DateTimeOffset? FromUnix(ulong seconds, uint nanoseconds)
    {
        if (seconds > 253_402_300_799UL || nanoseconds >= 1_000_000_000)
        {
            return null;
        }

        return DateTimeOffset.UnixEpoch.AddSeconds((long)seconds).AddTicks(nanoseconds / 100);
    }
}
