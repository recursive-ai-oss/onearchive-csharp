using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>The tier 3 parts: Huffman tables, canonical codes, the bit reader.</summary>
public class DeflateHuffmanTests
{
    // A random complete prefix code over `count` symbols with no code longer than 15 bits: split leaves of a binary tree.
    private static byte[] RandomCompleteLengths(Random rng, int count, int alphabetSize)
    {
        var leaves = new List<int> { 0 };
        while (leaves.Count < count)
        {
            int i = rng.Next(leaves.Count);
            if (leaves[i] >= 15)
            {
                continue;
            }

            int depth = leaves[i] + 1;
            leaves[i] = depth;
            leaves.Add(depth);
        }

        byte[] lengths = new byte[alphabetSize];
        int[] symbols = Enumerable.Range(0, alphabetSize).OrderBy(_ => rng.Next()).Take(count).ToArray();
        for (int i = 0; i < count; i++)
        {
            lengths[symbols[i]] = (byte)(leaves[i] == 0 ? 1 : leaves[i]);
        }

        return lengths;
    }

    [Fact]
    public void LookupAgreesWithCanonicalCodes()
    {
        var rng = new Random(3);
        for (int round = 0; round < 60; round++)
        {
            int count = 2 + rng.Next(round < 30 ? 30 : 280);
            byte[] lengths = RandomCompleteLengths(rng, count, 288);
            var table = new DeflateHuffmanTable(DeflateAlphabet.LiteralLength);
            Assert.True(table.TryBuild(lengths, out string? problem), problem);
            ushort[] codes = new ushort[288];
            DeflateHuffmanTable.GetCanonicalCodes(lengths, codes);
            for (int s = 0; s < 288; s++)
            {
                if (lengths[s] == 0)
                {
                    continue;
                }

                for (int k = 0; k < 20; k++)
                {
                    uint bits = codes[s] | ((uint)rng.Next(1 << 15) << lengths[s]);
                    DeflateHuffmanEntry e = table.Lookup(bits & 0x7FFF);
                    Assert.Equal(lengths[s], e.CodeLength);
                    if (s < 256)
                    {
                        Assert.Equal(DeflateSymbolKind.Literal, e.Kind);
                        Assert.Equal(s, e.Value);
                    }
                    else if (s == 256)
                    {
                        Assert.Equal(DeflateSymbolKind.EndOfBlock, e.Kind);
                    }
                    else if (s <= 285)
                    {
                        Assert.Equal(DeflateSymbolKind.Length, e.Kind);
                        Assert.Equal(DeflateTables.LengthBase[s - 257], e.Value);
                        Assert.Equal(DeflateTables.LengthExtraBits[s - 257], e.ExtraBits);
                    }
                    else
                    {
                        Assert.Equal(DeflateSymbolKind.Invalid, e.Kind);
                    }
                }
            }
        }
    }

    [Fact]
    public void CanonicalCodesFollowTheRfcExample()
    {
        // RFC 1951 section 3.2.2: lengths (3,3,3,3,3,2,4,4) give A=010 B=011 C=100 D=101 E=110 F=00 G=1110 H=1111.
        byte[] lengths = [3, 3, 3, 3, 3, 2, 4, 4];
        ushort[] codes = new ushort[8];
        DeflateHuffmanTable.GetCanonicalCodes(lengths, codes);
        uint[] expected = [0b010, 0b011, 0b100, 0b101, 0b110, 0b00, 0b1110, 0b1111];
        for (int i = 0; i < 8; i++)
        {
            uint reversed = 0;
            for (int b = 0; b < lengths[i]; b++)
            {
                reversed |= ((expected[i] >> b) & 1) << (lengths[i] - 1 - b);
            }

            Assert.Equal(reversed, codes[i]);
        }
    }

    [Fact]
    public void FixedCodesAreComplete()
    {
        byte[] lengths = new byte[288];
        DeflateTables.GetFixedLiteralLengthCodeLengths(lengths);
        var table = new DeflateHuffmanTable(DeflateAlphabet.LiteralLength);
        Assert.True(table.TryBuild(lengths, out _));
        DeflateTables.GetFixedDistanceCodeLengths(lengths);
        var distance = new DeflateHuffmanTable(DeflateAlphabet.Distance);
        Assert.True(distance.TryBuild(lengths.AsSpan(0, 32), out _));
        Assert.Equal(DeflateSymbolKind.Distance, distance.Lookup(0).Kind);
        Assert.Equal(1, distance.Lookup(0).Value);
    }

    [Fact]
    public void BadLengthSetsAreRefusedAndOddOnesAccepted()
    {
        var table = new DeflateHuffmanTable(DeflateAlphabet.LiteralLength);
        Assert.False(table.TryBuild([1, 1, 1], out string? p));
        Assert.Contains("over-subscribed", p, StringComparison.Ordinal);
        Assert.False(table.TryBuild([1, 2], out p)); // incomplete
        Assert.Contains("complete", p, StringComparison.Ordinal);
        Assert.False(table.TryBuild([16, 1], out _));
        Assert.False(table.TryBuild([2, 2, 2], out _)); // incomplete: one code of length 2 unused
        Assert.True(table.TryBuild([2, 2, 2, 2], out _));

        // One code of length 1 is accepted; the other bit pattern is invalid.
        Assert.True(table.TryBuild([0, 1], out _));
        Assert.Equal(DeflateSymbolKind.Literal, table.Lookup(0).Kind);
        Assert.Equal(1, table.Lookup(0).Value);
        Assert.Equal(DeflateSymbolKind.Invalid, table.Lookup(1).Kind);
        Assert.True(table.Lookup(1).CodeLength >= 1);

        // No codes at all: accepted, every lookup invalid.
        Assert.True(table.TryBuild([], out _));
        Assert.Equal(DeflateSymbolKind.Invalid, table.Lookup(0x1234).Kind);
        Assert.True(table.TryBuild(new byte[288], out _));

        // A failed build leaves nothing behind.
        Assert.True(table.TryBuild([1, 1], out _));
        Assert.False(table.TryBuild([1, 1, 1], out _));
        Assert.Equal(DeflateSymbolKind.Invalid, table.Lookup(0).Kind);

        Assert.Throws<ArgumentException>(() => table.TryBuild(new byte[289], out _));
    }

    [Fact]
    public void BitReaderReadsLeastSignificantBitFirst()
    {
        byte[] data = [0b1011_0101, 0b0000_1111, 0xAA, 0x55, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var r = new DeflateBitReader(data);
        Assert.True(r.TryReadBits(3, out uint v));
        Assert.Equal(0b101u, v);
        Assert.True(r.TryReadBits(7, out v));
        Assert.Equal(0b111_0110u, v); // bits 3..9: 10110 from the first byte, then the low 2 bits of the second
        r.AlignToByte();
        Assert.True(r.TryReadBits(16, out v));
        Assert.Equal(0x55AAu, v);
        Assert.Equal(0, new DeflateBitReader(data).BitCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeflateBitReader(data, 0, 64));
    }

    [Fact]
    public void BitReaderResumesFromItsState()
    {
        byte[] data = DeflateTestData.RandomBytes(1, 100);
        var whole = new DeflateBitReader(data);
        var expected = new List<uint>();
        var rng = new Random(2);
        var sizes = new List<int>();
        for (int i = 0; i < 40; i++)
        {
            int n = rng.Next(1, 20);
            Assert.True(whole.TryReadBits(n, out uint v));
            expected.Add(v);
            sizes.Add(n);
        }

        // Read the same fields while handing the input over in pieces of 3 bytes, carrying the reader's state.
        ulong bits = 0;
        int count = 0;
        int fed = 0;
        var got = new List<uint>();
        int size = 0;
        while (got.Count < sizes.Count)
        {
            var r = new DeflateBitReader(data.AsSpan(fed, Math.Min(3, data.Length - fed)), bits, count);
            while (got.Count < sizes.Count && r.TryReadBits(sizes[size], out uint v))
            {
                got.Add(v);
                size++;
            }

            fed += r.Position;
            bits = r.BitBuffer;
            count = r.BitCount;
        }

        Assert.Equal(expected, got);
    }

    [Fact]
    public void RefillAndReturnWholeBytes()
    {
        byte[] data = DeflateTestData.RandomBytes(4, 40);
        var r = new DeflateBitReader(data);
        r.Refill();
        Assert.InRange(r.BitCount, 56, 63);
        r.TrimBuffer();
        r.DropBits(11);
        int back = r.ReturnWholeBytes();
        Assert.Equal(5, back);
        Assert.Equal(2, r.Position);
        Assert.Equal(5, r.BitCount);
        // After giving the bytes back, reading continues exactly where the dropped bits ended.
        Assert.True(r.TryReadBits(5, out uint v));
        Assert.Equal((uint)(data[1] >> 3) & 31, v);
    }

    [Fact]
    public void PeekSymbolNeedsOnlyTheBitsOfTheCode()
    {
        byte[] lengths = new byte[288];
        DeflateTables.GetFixedLiteralLengthCodeLengths(lengths);
        var table = new DeflateHuffmanTable(DeflateAlphabet.LiteralLength);
        Assert.True(table.TryBuild(lengths, out _));
        // End-of-block is the 7-bit code 0000000; one byte of zero bits holds it, an empty source does not.
        var r = new DeflateBitReader([0]);
        Assert.True(r.TryPeekSymbol(table, out DeflateHuffmanEntry e));
        Assert.Equal(DeflateSymbolKind.EndOfBlock, e.Kind);
        Assert.Equal(7, e.CodeLength);
        Assert.Equal(1, r.Position);
        var empty = new DeflateBitReader([]);
        Assert.False(empty.TryPeekSymbol(table, out _));
    }
}
