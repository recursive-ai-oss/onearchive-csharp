using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Checksum entries. A checksum's value is part of the bytes every container writes, so each
/// algorithm must give the same value whichever vector, instruction or scalar path runs. Each
/// entry also feeds the input in uneven pieces, and the CRCs combine two halves, so the
/// incremental and combine paths are covered too.
/// </summary>
internal static class ChecksumEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        Matrix.Add("checksum", "crc32", (input, _) => Pack32(Crc32.Compute(input), Pieces(input, 0u, Crc32.Update), Crc32.Combine(Crc32.Compute(First(input)), Crc32.Compute(Second(input)), Second(input).Length)));
        Matrix.Add("checksum", "crc32c", (input, _) => Pack32(Crc32C.Compute(input), Pieces(input, 0u, Crc32C.Update), Crc32C.Combine(Crc32C.Compute(First(input)), Crc32C.Compute(Second(input)), Second(input).Length)));
        Matrix.Add("checksum", "crc64", (input, _) => Pack64(Crc64.Compute(input), Pieces(input, 0UL, Crc64.Update), Crc64.Combine(Crc64.Compute(First(input)), Crc64.Compute(Second(input)), Second(input).Length)));
        Matrix.Add("checksum", "adler32", (input, _) => Pack32(Adler32.Compute(input), Pieces(input, 1u, Adler32.Update), 0));
        Matrix.Add("checksum", "xxhash32", (input, _) => Pack32(XxHash32.Compute(input), XxHash32.Compute(input, 0x9E3779B1u), XxHash32Pieces(input)));
        Matrix.Add("checksum", "xxhash64", (input, _) => Pack64(XxHash64.Compute(input), XxHash64.Compute(input, 0x9E3779B97F4A7C15UL), XxHash64Pieces(input)));
    }

    private delegate T Update<T>(T value, ReadOnlySpan<byte> data);

    private static ReadOnlySpan<byte> First(ReadOnlySpan<byte> input) => input[..(input.Length / 2)];

    private static ReadOnlySpan<byte> Second(ReadOnlySpan<byte> input) => input[(input.Length / 2)..];

    // Pieces of 1, 2, 3, ... 97 bytes, repeating, so piece boundaries land at many different offsets.
    private static T Pieces<T>(ReadOnlySpan<byte> input, T start, Update<T> update)
    {
        T value = start;
        for (int i = 0, n = 1; i < input.Length; i += n, n = (n % 97) + 1)
        {
            value = update(value, input.Slice(i, Math.Min(n, input.Length - i)));
        }

        return value;
    }

    private static uint XxHash32Pieces(ReadOnlySpan<byte> input)
    {
        XxHash32 state = default;
        for (int i = 0, n = 1; i < input.Length; i += n, n = (n % 97) + 1)
        {
            state.Append(input.Slice(i, Math.Min(n, input.Length - i)));
        }

        return state.GetCurrentValue();
    }

    private static ulong XxHash64Pieces(ReadOnlySpan<byte> input)
    {
        XxHash64 state = default;
        for (int i = 0, n = 1; i < input.Length; i += n, n = (n % 97) + 1)
        {
            state.Append(input.Slice(i, Math.Min(n, input.Length - i)));
        }

        return state.GetCurrentValue();
    }

    private static byte[] Pack32(uint a, uint b, uint c)
    {
        byte[] r = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(r, a);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(8), c);
        return r;
    }

    private static byte[] Pack64(ulong a, ulong b, ulong c)
    {
        byte[] r = new byte[24];
        BinaryPrimitives.WriteUInt64BigEndian(r, a);
        BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(8), b);
        BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(16), c);
        return r;
    }
}
