using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// Turns a sorted block into its bits: the symbol map, move-to-front and zero-run coding (RUNA and RUNB), the tables and selectors from
/// <see cref="BZip2TableChooser"/>, and the Huffman-coded symbols (derivation note, facts F4 to F9; layout in docs/bzip2-decoder.md).
/// </summary>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BZip2BlockWriter
{
    private const long BlockMagic = 0x314159265359;

    private readonly BZip2MoveToFront _mtf = new();
    private readonly BZip2TableChooser _chooser = new();
    private readonly BZip2HuffmanTable[] _tables = [new(), new(), new(), new(), new(), new()];
    private readonly bool[] _used = new bool[256];
    private readonly byte[] _usedList = new byte[256];
    private readonly BZip2MoveToFront _selectorMtf = new();
    private readonly uint[] _entries = new uint[6 * BZip2HuffmanTable.MaxAlphabetSize];
    private ushort[] _symbols = new ushort[1024];

    /// <summary>Writes one block.</summary>
    /// <param name="writer">Receives the block's bits.</param>
    /// <param name="lastColumn">The last column from <see cref="BZip2BlockSorter.Sort"/>.</param>
    /// <param name="originalRow">The row that <see cref="BZip2BlockSorter.Sort"/> returned.</param>
    /// <param name="blockCrc">The block's CRC from <see cref="BZip2RunLengthEncoder.Finish"/>.</param>
    /// <param name="maxCodeLength">The longest code length allowed, 9 to 20.</param>
    /// <exception cref="ArgumentException">The last column is empty or <paramref name="originalRow"/> is outside it.</exception>
    public void Write(BZip2BitWriter writer, ReadOnlySpan<byte> lastColumn, int originalRow, uint blockCrc, int maxCodeLength = BZip2HuffmanBuilder.DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (lastColumn.IsEmpty || (uint)originalRow >= (uint)lastColumn.Length)
        {
            throw new ArgumentException("The block is empty or the original row is outside it.");
        }

        // Symbol map.
        Array.Clear(_used);
        foreach (byte b in lastColumn)
        {
            _used[b] = true;
        }

        int usedCount = 0;
        for (int v = 0; v < 256; v++)
        {
            if (_used[v])
            {
                _usedList[usedCount++] = (byte)v;
            }
        }

        int alphabet = usedCount + 2;
        int count = MoveToFrontSymbols(lastColumn, usedCount);
        ReadOnlySpan<ushort> symbols = _symbols.AsSpan(0, count);
        int tableCount = _chooser.Choose(symbols, alphabet, maxCodeLength);

        // Block header (docs/bzip2-decoder.md, "Stream layout").
        writer.Write((uint)(BlockMagic >> 24), 24);
        writer.Write((uint)(BlockMagic & 0xFFFFFF), 24);
        writer.Write(blockCrc, 32);
        writer.Write(0, 1); // never randomised
        writer.Write((uint)originalRow, 24);

        uint groupBits = 0;
        for (int g = 0; g < 16; g++)
        {
            for (int k = 0; k < 16; k++)
            {
                if (_used[(g * 16) + k])
                {
                    groupBits |= 0x8000u >> g;
                    break;
                }
            }
        }

        writer.Write(groupBits, 16);
        for (int g = 0; g < 16; g++)
        {
            if ((groupBits & (0x8000u >> g)) == 0)
            {
                continue;
            }

            uint bits = 0;
            for (int k = 0; k < 16; k++)
            {
                if (_used[(g * 16) + k])
                {
                    bits |= 0x8000u >> k;
                }
            }

            writer.Write(bits, 16);
        }

        ReadOnlySpan<byte> selectors = _chooser.Selectors;
        writer.Write((uint)tableCount, 3);
        writer.Write((uint)selectors.Length, 15);
        _selectorMtf.Reset(tableCount);
        foreach (byte s in selectors)
        {
            int index = _selectorMtf.Encode(s);
            for (int i = 0; i < index; i++)
            {
                writer.Write(1, 1);
            }

            writer.Write(0, 1);
        }

        // Tables: a 5-bit start length, then for each symbol steps of +1 ("10") or -1 ("11") and a closing 0.
        for (int t = 0; t < tableCount; t++)
        {
            ReadOnlySpan<byte> lengths = _chooser.GetLengths(t);
            int current = lengths[0];
            writer.Write((uint)current, 5);
            foreach (byte target in lengths)
            {
                while (current < target)
                {
                    writer.Write(0b10, 2);
                    current++;
                }

                while (current > target)
                {
                    writer.Write(0b11, 2);
                    current--;
                }

                writer.Write(0, 1);
            }

            _tables[t].Build(lengths);
        }

        // Symbols.
        int stride = BZip2HuffmanTable.MaxAlphabetSize;
        for (int t = 0; t < tableCount; t++)
        {
            for (int s = 0; s < alphabet; s++)
            {
                _entries[(t * stride) + s] = (_tables[t].GetCode(s) << 5) | (uint)_tables[t].GetLength(s);
            }
        }

        writer.WriteSymbols(symbols, selectors, _entries, stride, BZip2TableChooser.GroupSize);
    }

    // Move-to-front, then runs of the front value (position 0) as bijective base 2 digits RUNA (0) and RUNB (1), least significant first.
    private int MoveToFrontSymbols(ReadOnlySpan<byte> lastColumn, int usedCount)
    {
        if (_symbols.Length < lastColumn.Length + 1)
        {
            _symbols = new ushort[lastColumn.Length + 1];
        }

        ushort[] symbols = _symbols;
        _mtf.Reset(_usedList.AsSpan(0, usedCount));
        int count = _mtf.EncodeSymbols(lastColumn, symbols);
        symbols[count++] = (ushort)(usedCount + 1);
        return count;
    }
}
