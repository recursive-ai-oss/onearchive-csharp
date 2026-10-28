using RecursiveAI.OneCompress.BZip2;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Writes bits most significant first, as bzip2 does.</summary>
internal sealed class BZip2BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _acc;
    private int _count;

    public void Write(long value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            _acc = (_acc << 1) | (int)((value >> i) & 1);
            if (++_count == 8)
            {
                _bytes.Add((byte)_acc);
                _acc = 0;
                _count = 0;
            }
        }
    }

    public byte[] ToArray()
    {
        var copy = new List<byte>(_bytes);
        if (_count > 0)
        {
            copy.Add((byte)(_acc << (8 - _count)));
        }

        return copy.ToArray();
    }
}

/// <summary>
/// One block as the file holds it, field by field, so tests can write valid blocks and then break exactly one thing.
/// <see cref="BZip2TestBuilder.Model"/> fills it in for real data with simple, honest algorithms (a naive sort, plain Huffman).
/// </summary>
internal sealed class BZip2BlockModel
{
    public uint Crc;
    public bool Randomized;
    public int OriginalRow;
    public byte[] UsedBytes = [];
    public int TableCount = 2;
    public List<int> Selectors = [];
    public byte[][] Lengths = [];
    public List<int> Symbols = [];

    // Written as given, and not derived from the model: tests set these to write fields the format forbids.
    public int? SelectorCountField;

    public int AlphabetSize => UsedBytes.Length + 2;

    public void Write(BZip2BitWriter w)
    {
        w.Write(0x314159265359, 48);
        w.Write(Crc, 32);
        w.Write(Randomized ? 1 : 0, 1);
        w.Write(OriginalRow, 24);

        int groups = 0;
        var perGroup = new int[16];
        foreach (byte b in UsedBytes)
        {
            groups |= 0x8000 >> (b >> 4);
            perGroup[b >> 4] |= 0x8000 >> (b & 15);
        }

        w.Write(groups, 16);
        for (int g = 0; g < 16; g++)
        {
            if ((groups & (0x8000 >> g)) != 0)
            {
                w.Write(perGroup[g], 16);
            }
        }

        w.Write(TableCount, 3);
        w.Write(SelectorCountField ?? Selectors.Count, 15);
        var mtf = Enumerable.Range(0, Math.Max(TableCount, 1)).ToList();
        foreach (int s in Selectors)
        {
            int index = mtf.IndexOf(s);
            if (index < 0)
            {
                // A selector the table count cannot hold: written as its unary form directly.
                w.Write(((1L << s) - 1) << 1, s + 1);
                continue;
            }

            mtf.RemoveAt(index);
            mtf.Insert(0, s);
            w.Write(((1L << index) - 1) << 1, index + 1);
        }

        foreach (byte[] lengths in Lengths)
        {
            int cur = lengths[0];
            w.Write(cur, 5);
            foreach (byte l in lengths)
            {
                while (cur < l)
                {
                    w.Write(0b10, 2);
                    cur++;
                }

                while (cur > l)
                {
                    w.Write(0b11, 2);
                    cur--;
                }

                w.Write(0, 1);
            }
        }

        var tables = new BZip2HuffmanTable[Lengths.Length];
        for (int t = 0; t < tables.Length; t++)
        {
            tables[t] = new BZip2HuffmanTable();
            if (!tables[t].TryBuild(Lengths[t]))
            {
                return; // a table the format forbids has no codes to write the symbols with
            }
        }

        int n = 0;
        foreach (int sym in Symbols)
        {
            BZip2HuffmanTable table = tables[Selectors[Math.Min(n / 50, Selectors.Count - 1)] % tables.Length];
            w.Write(table.GetCode(sym), table.GetLength(sym));
            n++;
        }
    }
}

internal static class BZip2TestBuilder
{
    public static void WriteStreamHeader(BZip2BitWriter w, int level)
    {
        w.Write('B', 8);
        w.Write('Z', 8);
        w.Write('h', 8);
        w.Write('0' + level, 8);
    }

    public static void WriteEnd(BZip2BitWriter w, uint combinedCrc)
    {
        w.Write(0x177245385090, 48);
        w.Write(combinedCrc, 32);
    }

    /// <summary>A whole stream of the given data, in blocks of at most <paramref name="blockInput"/> input bytes.</summary>
    public static byte[] Stream(byte[] data, int level = 9, int blockInput = int.MaxValue, Action<BZip2BlockModel, int>? tamper = null, uint? combinedCrcOverride = null)
    {
        var w = new BZip2BitWriter();
        WriteStreamHeader(w, level);
        uint combined = 0;
        int index = 0;
        for (int at = 0; at < data.Length; at += blockInput)
        {
            byte[] part = data.AsSpan(at, Math.Min(blockInput, data.Length - at)).ToArray();
            BZip2BlockModel m = Model(part);
            combined = BZip2Crc.Combine(combined, m.Crc);
            tamper?.Invoke(m, index++);
            m.Write(w);
        }

        WriteEnd(w, combinedCrcOverride ?? combined);
        return w.ToArray();
    }

    public static byte[] RunLengthEncode(byte[] data)
    {
        var o = new List<byte>();
        int i = 0;
        while (i < data.Length)
        {
            int run = 1;
            while (i + run < data.Length && data[i + run] == data[i] && run < 255 + 4)
            {
                run++;
            }

            if (run >= 4)
            {
                o.AddRange([data[i], data[i], data[i], data[i], (byte)(run - 4)]);
            }
            else
            {
                for (int k = 0; k < run; k++)
                {
                    o.Add(data[i]);
                }
            }

            i += run;
        }

        return o.ToArray();
    }

    public static (byte[] Last, int Row) Bwt(byte[] block)
    {
        int n = block.Length;
        int[] rotations = Enumerable.Range(0, n).ToArray();
        Array.Sort(rotations, (a, b) =>
        {
            for (int k = 0; k < n; k++)
            {
                int d = block[(a + k) % n] - block[(b + k) % n];
                if (d != 0)
                {
                    return d;
                }
            }

            return a.CompareTo(b);
        });
        var last = new byte[n];
        int row = 0;
        for (int i = 0; i < n; i++)
        {
            last[i] = block[(rotations[i] + n - 1) % n];
            if (rotations[i] == 0)
            {
                row = i;
            }
        }

        return (last, row);
    }

    public static BZip2BlockModel Model(byte[] original) => ModelFromRunLengthEncoded(RunLengthEncode(original), BZip2Crc.Compute(original));

    /// <summary>A block whose first-stage bytes are given as they are, so tests can write what an encoder never would.</summary>
    public static BZip2BlockModel ModelFromRunLengthEncoded(byte[] rle, uint crc)
    {
        (byte[] last, int row) = Bwt(rle);
        byte[] used = last.Distinct().OrderBy(static b => b).ToArray();
        var mtf = new List<byte>(used);
        var symbols = new List<int>();
        int zeros = 0;
        void FlushZeros()
        {
            // Bijective base 2, least significant digit first: RUNA = 1, RUNB = 2.
            for (int z = zeros; z > 0; z = (z - 1) >> 1)
            {
                symbols.Add((z & 1) == 1 ? 0 : 1);
            }

            zeros = 0;
        }

        foreach (byte b in last)
        {
            int index = mtf.IndexOf(b);
            if (index == 0)
            {
                zeros++;
                continue;
            }

            FlushZeros();
            mtf.RemoveAt(index);
            mtf.Insert(0, b);
            symbols.Add(index + 1);
        }

        FlushZeros();
        int alphabet = used.Length + 2;
        symbols.Add(alphabet - 1);

        var frequency = new long[alphabet];
        foreach (int s in symbols)
        {
            frequency[s]++;
        }

        byte[] lengths = HuffmanLengths(frequency);
        int groups = (symbols.Count + 49) / 50;
        return new BZip2BlockModel
        {
            Crc = crc,
            OriginalRow = row,
            UsedBytes = used,
            TableCount = 2,
            Selectors = Enumerable.Range(0, groups).Select(static g => g % 2).ToList(),
            Lengths = [lengths, lengths],
            Symbols = symbols,
        };
    }

    // Plain Huffman lengths over frequencies raised to at least one; the test inputs are small enough to stay below 20 bits.
    public static byte[] HuffmanLengths(long[] frequency)
    {
        int n = frequency.Length;
        var nodes = new List<(long Weight, List<int> Leaves)>();
        for (int i = 0; i < n; i++)
        {
            nodes.Add((frequency[i] + 1, [i]));
        }

        var lengths = new byte[n];
        while (nodes.Count > 1)
        {
            nodes.Sort(static (a, b) => a.Weight.CompareTo(b.Weight));
            var a = nodes[0];
            var b = nodes[1];
            nodes.RemoveRange(0, 2);
            foreach (int leaf in a.Leaves)
            {
                lengths[leaf]++;
            }

            foreach (int leaf in b.Leaves)
            {
                lengths[leaf]++;
            }

            a.Leaves.AddRange(b.Leaves);
            nodes.Add((a.Weight + b.Weight, a.Leaves));
        }

        return lengths;
    }
}
