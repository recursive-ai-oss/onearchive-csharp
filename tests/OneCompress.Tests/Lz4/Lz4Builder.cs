using System.Buffers.Binary;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Builds LZ4 blocks and frames by hand, for the cases the lz4 tool will not write.</summary>
internal static class Lz4Builder
{
    /// <summary>One sequence: literals, then a match of <see cref="MatchLength"/> bytes (4 or more) from <see cref="Offset"/> back; offset 0 ends the block after the literals.</summary>
    public readonly record struct Sequence(byte[] Literals, int Offset, int MatchLength);

    /// <summary>Encodes the sequences as a block. The last sequence must have offset 0 (literals only).</summary>
    public static byte[] Block(params Sequence[] sequences)
    {
        var block = new List<byte>();
        foreach (Sequence s in sequences)
        {
            int lit = s.Literals.Length;
            int match = s.Offset == 0 ? 0 : s.MatchLength - 4;
            block.Add((byte)((Math.Min(lit, 15) << 4) | Math.Min(match, 15)));
            Extend(block, lit);
            block.AddRange(s.Literals);
            if (s.Offset != 0)
            {
                block.Add((byte)s.Offset);
                block.Add((byte)(s.Offset >> 8));
                Extend(block, match);
            }
        }

        return [.. block];
    }

    private static void Extend(List<byte> block, int length)
    {
        if (length < 15)
        {
            return;
        }

        length -= 15;
        while (length >= 255)
        {
            block.Add(255);
            length -= 255;
        }

        block.Add((byte)length);
    }

    /// <summary>The bytes a block decodes to, by the simplest possible rule (copy one byte at a time).</summary>
    public static byte[] Expand(byte[] history, params Sequence[] sequences)
    {
        var output = new List<byte>(history);
        foreach (Sequence s in sequences)
        {
            output.AddRange(s.Literals);
            for (int i = 0; i < s.MatchLength && s.Offset != 0; i++)
            {
                output.Add(output[output.Count - s.Offset]);
            }
        }

        return [.. output.Skip(history.Length)];
    }

    public static byte[] Header(int flg, int bd, long? contentSize = null, uint? dictionaryId = null)
    {
        var descriptor = new List<byte> { (byte)flg, (byte)bd };
        if (contentSize is long size)
        {
            Span<byte> eight = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(eight, size);
            descriptor.AddRange(eight.ToArray());
        }

        if (dictionaryId is uint id)
        {
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(four, id);
            descriptor.AddRange(four.ToArray());
        }

        byte hc = (byte)(XxHash32.Compute(descriptor.ToArray()) >> 8);
        return [0x04, 0x22, 0x4D, 0x18, .. descriptor, hc];
    }

    public static byte[] DataBlock(byte[] data, bool uncompressed, bool checksum = false)
    {
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length | (uncompressed ? 0x80000000u : 0));
        byte[] result = [.. size, .. data];
        if (checksum)
        {
            byte[] c = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(c, XxHash32.Compute(data));
            result = [.. result, .. c];
        }

        return result;
    }

    public static byte[] EndMark { get; } = [0, 0, 0, 0];

    public static byte[] ContentChecksum(byte[] content)
    {
        byte[] c = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(c, XxHash32.Compute(content));
        return c;
    }
}
