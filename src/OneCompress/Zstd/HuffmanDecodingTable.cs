using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A Huffman decoding table for Zstandard literals (RFC 8878 §4.2): a single-level lookup indexed by the
/// next <see cref="MaxBits"/> bits of the stream. An instance can be rebuilt many times without allocating.
/// </summary>
public sealed class HuffmanDecodingTable
{
    /// <summary>The longest code length the format allows.</summary>
    public const int MaxBits = ZstdFormat.HuffmanMaxBits;

    private readonly ushort[] _table = new ushort[1 << MaxBits]; // (symbol << 8) | code length
    private readonly byte[] _weights = new byte[260]; // 255 weights plus room for the final writes of the two-state loop
    private FseDecodingTable? _weightTable;
    private int _tableBits;
    private int _weightCount;

    /// <summary>The explicit weights of the last description read (the last symbol's weight is implied).</summary>
    internal ReadOnlySpan<byte> Weights => _weights.AsSpan(0, _weightCount);

    /// <summary>Gets the length in bits of the longest code (the table has <c>1 &lt;&lt; TableBits</c> entries).</summary>
    public int TableBits => _tableBits;

    /// <summary>
    /// Reads a Huffman tree description (RFC 8878 §4.2.1) from the start of <paramref name="source"/> and builds the table.
    /// </summary>
    /// <param name="source">The bytes that start with the tree description; other data may follow it.</param>
    /// <param name="streamOffset">Offset of <paramref name="source"/> in the compressed input, for error messages.</param>
    /// <returns>The number of bytes the description occupies.</returns>
    /// <exception cref="CompressionFormatException">The description is truncated or does not describe a valid prefix code.</exception>
    public int ReadDescription(ReadOnlySpan<byte> source, long streamOffset = 0) => ReadDescription(source, streamOffset, buildTable: true);

    // With buildTable false only the weights are read (see Weights); the encoder uses that to check a description it wrote.
    internal int ReadDescription(ReadOnlySpan<byte> source, long streamOffset, bool buildTable)
    {
        if (source.Length == 0)
        {
            ZstdFormat.ThrowTruncated(streamOffset, "a Huffman tree description");
        }

        int header = source[0];
        int weightCount;
        int consumed;
        if (header >= 128)
        {
            // RFC 8878 §4.2.1.1: direct representation, two 4-bit weights per byte, high nibble first.
            weightCount = header - 127;
            int bytes = (weightCount + 1) / 2;
            if (source.Length < 1 + bytes)
            {
                ZstdFormat.ThrowTruncated(streamOffset + source.Length, $"{bytes} bytes of Huffman weights");
            }

            for (int i = 0; i < weightCount; i += 2)
            {
                byte b = source[1 + (i / 2)];
                _weights[i] = (byte)(b >> 4);
                if (i + 1 < weightCount)
                {
                    _weights[i + 1] = (byte)(b & 15);
                }
            }

            consumed = 1 + bytes;
        }
        else
        {
            // RFC 8878 §4.2.1.2: the weights are an FSE-compressed stream of `header` bytes.
            if (header == 0)
            {
                ZstdFormat.ThrowCorrupt(streamOffset, "a nonzero size for FSE-compressed Huffman weights");
            }

            if (source.Length < 1 + header)
            {
                ZstdFormat.ThrowTruncated(streamOffset + source.Length, $"{header} bytes of compressed Huffman weights");
            }

            weightCount = DecodeCompressedWeights(source.Slice(1, header), streamOffset + 1);
            consumed = 1 + header;
        }

        _weightCount = weightCount;
        if (buildTable)
        {
            BuildFromWeights(_weights.AsSpan(0, weightCount), streamOffset);
        }

        return consumed;
    }

    private int DecodeCompressedWeights(ReadOnlySpan<byte> stream, long streamOffset)
    {
        FseDecodingTable table = _weightTable ??= new FseDecodingTable();
        int descriptionSize = table.ReadDescription(stream, 255, ZstdFormat.HuffmanWeightMaxAccuracyLog, streamOffset);
        if (descriptionSize >= stream.Length)
        {
            ZstdFormat.ThrowTruncated(streamOffset + stream.Length, "a bitstream after the Huffman weight table");
        }

        var bits = new ZstdReverseBitReader(stream[descriptionSize..], streamOffset + descriptionSize);
        ReadOnlySpan<FseEntry> entries = table.Entries;
        int accuracyLog = table.AccuracyLog;
        bits.Reload();
        uint state1 = bits.ReadBitsNoReload(accuracyLog);
        uint state2 = bits.ReadBitsNoReload(accuracyLog);
        if (bits.IsOverflowed)
        {
            ZstdFormat.ThrowCorrupt(streamOffset + descriptionSize, "two initial FSE states for the Huffman weights");
        }

        byte[] weights = _weights;
        int n = 0;

        // RFC 8878 §4.2.1.2: two states take turns; when an update would need more bits than remain, the
        // missing bits are zero and the other state's symbol is the last one.
        while (true)
        {
            if (n >= 255)
            {
                ZstdFormat.ThrowCorrupt(streamOffset, "at most 255 Huffman weights");
            }

            FseEntry e1 = entries[(int)state1];
            weights[n++] = e1.Symbol;
            bits.Reload();
            state1 = e1.Baseline + bits.ReadBitsNoReload(e1.NumBits);
            if (bits.IsOverflowed)
            {
                weights[n++] = entries[(int)state2].Symbol;
                break;
            }

            FseEntry e2 = entries[(int)state2];
            weights[n++] = e2.Symbol;
            bits.Reload();
            state2 = e2.Baseline + bits.ReadBitsNoReload(e2.NumBits);
            if (bits.IsOverflowed)
            {
                weights[n++] = entries[(int)state1].Symbol;
                break;
            }
        }

        if (n > 255)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "at most 255 Huffman weights");
        }

        return n;
    }

    /// <summary>
    /// Builds the table from the weights of symbols 0 up to the last present symbol, exclusive of that last
    /// symbol, whose weight is implied (RFC 8878 §4.2.1 and §4.2.1.3).
    /// </summary>
    /// <param name="weights">The weight of each symbol from 0, each 0 to 11; at most 255 entries.</param>
    /// <param name="streamOffset">Offset of the description in the compressed input, for error messages.</param>
    /// <exception cref="CompressionFormatException">The weights do not describe a valid prefix code.</exception>
    public void BuildFromWeights(ReadOnlySpan<byte> weights, long streamOffset = 0)
    {
        if (weights.Length > 255)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "at most 255 explicit Huffman weights");
        }

        // Rank counts by weight, and the total of 2^(weight-1) over the listed symbols.
        Span<int> rankCount = stackalloc int[ZstdFormat.HuffmanMaxBits + 2];
        uint total = 0;
        foreach (byte w in weights)
        {
            if (w > ZstdFormat.HuffmanMaxBits)
            {
                ZstdFormat.ThrowCorrupt(streamOffset, $"Huffman weights of at most {ZstdFormat.HuffmanMaxBits}, found {w}");
            }

            rankCount[w]++;
            if (w != 0)
            {
                total += 1u << (w - 1);
            }
        }

        if (total == 0)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "at least one nonzero Huffman weight before the last symbol");
        }

        // The implied last weight completes the total to a power of two.
        int maxBits = BitOperations.Log2(total) + 1;
        if (maxBits > ZstdFormat.HuffmanMaxBits)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, $"a Huffman tree depth of at most {ZstdFormat.HuffmanMaxBits}, found {maxBits}");
        }

        uint rest = (1u << maxBits) - total;
        if (!BitOperations.IsPow2(rest))
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "Huffman weights that complete to a power of two");
        }

        int lastWeight = BitOperations.Log2(rest) + 1;
        rankCount[lastWeight]++;
        if (rankCount[1] == 0)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "at least one Huffman symbol of weight 1");
        }

        // Codes are assigned from the lowest weight (longest code) upward, symbols in natural order within a
        // weight, so each weight owns a consecutive run of table rows of 2^(weight-1) rows per symbol.
        Span<int> rankStart = stackalloc int[ZstdFormat.HuffmanMaxBits + 2];
        int next = 0;
        for (int w = 1; w <= maxBits; w++)
        {
            rankStart[w] = next;
            next += rankCount[w] << (w - 1);
        }

        ushort[] table = _table;
        for (int s = 0; s <= weights.Length; s++)
        {
            int w = s < weights.Length ? weights[s] : lastWeight;
            if (w == 0)
            {
                continue;
            }

            int length = 1 << (w - 1);
            int start = rankStart[w];
            rankStart[w] += length;
            ushort entry = (ushort)((s << 8) | (maxBits + 1 - w));
            table.AsSpan(start, length).Fill(entry);
        }

        _tableBits = maxBits;
    }

    /// <summary>Decodes a single Huffman-coded stream (RFC 8878 §4.2.2) into <paramref name="destination"/>.</summary>
    /// <param name="stream">The stream's bytes, ending with the byte that holds the marker bit.</param>
    /// <param name="destination">Receives exactly <c>destination.Length</c> symbols.</param>
    /// <param name="streamOffset">Offset of <paramref name="stream"/> in the compressed input, for error messages.</param>
    /// <exception cref="CompressionFormatException">The stream does not hold exactly that many symbols.</exception>
    public void DecodeStream(ReadOnlySpan<byte> stream, Span<byte> destination, long streamOffset = 0)
    {
        if (_tableBits == 0)
        {
            throw new InvalidOperationException("The table has not been built.");
        }

        var bits = new ZstdReverseBitReader(stream, streamOffset);
        DecodeSymbols(ref bits, destination);
        if (!bits.IsFinished)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "a Huffman stream that is consumed exactly by its symbols");
        }
    }

    /// <summary>
    /// Decodes four Huffman-coded streams that share one table (RFC 8878 §3.1.1.3.1.6 and §4.2.2) into
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">The 6-byte jump table of three 16-bit stream sizes followed by the four streams.</param>
    /// <param name="destination">
    /// Receives all symbols. Each of the first three streams holds <c>(destination.Length + 3) / 4</c> of them
    /// and the last holds the rest.
    /// </param>
    /// <param name="streamOffset">Offset of <paramref name="source"/> in the compressed input, for error messages.</param>
    /// <exception cref="CompressionFormatException">The jump table or a stream is invalid.</exception>
    public void DecodeFourStreams(ReadOnlySpan<byte> source, Span<byte> destination, long streamOffset = 0)
    {
        if (_tableBits == 0)
        {
            throw new InvalidOperationException("The table has not been built.");
        }

        if (source.Length < 6)
        {
            ZstdFormat.ThrowTruncated(streamOffset + source.Length, "a 6-byte jump table");
        }

        int size1 = BinaryPrimitives.ReadUInt16LittleEndian(source);
        int size2 = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
        int size3 = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        int size4 = source.Length - 6 - size1 - size2 - size3;
        if (size4 < 1)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "jump table sizes that leave at least one byte for the fourth stream");
        }

        int part = (destination.Length + 3) / 4;
        int lastPart = destination.Length - (3 * part);
        if (lastPart < 0)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "at least 4 literals in a four-stream block");
        }

        int p = 6;
        ReadOnlySpan<byte> stream1 = source.Slice(p, size1);
        long offset1 = streamOffset + p;
        p += size1;
        ReadOnlySpan<byte> stream2 = source.Slice(p, size2);
        long offset2 = streamOffset + p;
        p += size2;
        ReadOnlySpan<byte> stream3 = source.Slice(p, size3);
        long offset3 = streamOffset + p;
        p += size3;
        ReadOnlySpan<byte> stream4 = source.Slice(p, size4);
        long offset4 = streamOffset + p;

        var bits1 = new ZstdReverseBitReader(stream1, offset1);
        var bits2 = new ZstdReverseBitReader(stream2, offset2);
        var bits3 = new ZstdReverseBitReader(stream3, offset3);
        var bits4 = new ZstdReverseBitReader(stream4, offset4);
        Span<byte> out1 = destination[..part];
        Span<byte> out2 = destination.Slice(part, part);
        Span<byte> out3 = destination.Slice(2 * part, part);
        Span<byte> out4 = destination[(3 * part)..];

        // The four streams are independent, so decoding them in step lets the processor overlap their table
        // lookups. The fourth stream is the shortest, so the joint loop runs as far as it has whole groups of four.
        ushort[] table = _table;
        int tableBits = _tableBits;
        int i = 0;

        // The fast loop keeps each stream's register in local variables. It runs while every stream still has at least
        // eight unread bytes, so a refill is always "step back by the whole bytes consumed" (at most 6 after four
        // symbols of at most 11 bits, plus the 7 spare bits), and stops short of the streams' ends; the general
        // code below finishes them.
        ulong c1 = bits1.Container, c2 = bits2.Container, c3 = bits3.Container, c4 = bits4.Container;
        int k1 = bits1.Consumed, k2 = bits2.Consumed, k3 = bits3.Consumed, k4 = bits4.Consumed;
        int pos1 = bits1.Pointer, pos2 = bits2.Pointer, pos3 = bits3.Pointer, pos4 = bits4.Pointer;
        int shift = 64 - tableBits;
        int joint = lastPart & ~3;
        while (i < joint && pos1 >= 8 && pos2 >= 8 && pos3 >= 8 && pos4 >= 8)
        {
            {
                ushort e1 = Raw.Get(table, (int)((c1 << k1) >> shift)); // the index has tableBits bits; the table is longer
                k1 += e1 & 0xFF;
                Raw.Set(out1, i, (byte)(e1 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e2 = Raw.Get(table, (int)((c2 << k2) >> shift)); // the index has tableBits bits; the table is longer
                k2 += e2 & 0xFF;
                Raw.Set(out2, i, (byte)(e2 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e3 = Raw.Get(table, (int)((c3 << k3) >> shift)); // the index has tableBits bits; the table is longer
                k3 += e3 & 0xFF;
                Raw.Set(out3, i, (byte)(e3 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e4 = Raw.Get(table, (int)((c4 << k4) >> shift)); // the index has tableBits bits; the table is longer
                k4 += e4 & 0xFF;
                Raw.Set(out4, i, (byte)(e4 >> 8)); // i + 3 < joint <= lastPart <= every output's length
            }

            {
                ushort e1 = Raw.Get(table, (int)((c1 << k1) >> shift)); // the index has tableBits bits; the table is longer
                k1 += e1 & 0xFF;
                Raw.Set(out1, i + 1, (byte)(e1 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e2 = Raw.Get(table, (int)((c2 << k2) >> shift)); // the index has tableBits bits; the table is longer
                k2 += e2 & 0xFF;
                Raw.Set(out2, i + 1, (byte)(e2 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e3 = Raw.Get(table, (int)((c3 << k3) >> shift)); // the index has tableBits bits; the table is longer
                k3 += e3 & 0xFF;
                Raw.Set(out3, i + 1, (byte)(e3 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e4 = Raw.Get(table, (int)((c4 << k4) >> shift)); // the index has tableBits bits; the table is longer
                k4 += e4 & 0xFF;
                Raw.Set(out4, i + 1, (byte)(e4 >> 8)); // i + 3 < joint <= lastPart <= every output's length
            }

            {
                ushort e1 = Raw.Get(table, (int)((c1 << k1) >> shift)); // the index has tableBits bits; the table is longer
                k1 += e1 & 0xFF;
                Raw.Set(out1, i + 2, (byte)(e1 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e2 = Raw.Get(table, (int)((c2 << k2) >> shift)); // the index has tableBits bits; the table is longer
                k2 += e2 & 0xFF;
                Raw.Set(out2, i + 2, (byte)(e2 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e3 = Raw.Get(table, (int)((c3 << k3) >> shift)); // the index has tableBits bits; the table is longer
                k3 += e3 & 0xFF;
                Raw.Set(out3, i + 2, (byte)(e3 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e4 = Raw.Get(table, (int)((c4 << k4) >> shift)); // the index has tableBits bits; the table is longer
                k4 += e4 & 0xFF;
                Raw.Set(out4, i + 2, (byte)(e4 >> 8)); // i + 3 < joint <= lastPart <= every output's length
            }

            {
                ushort e1 = Raw.Get(table, (int)((c1 << k1) >> shift)); // the index has tableBits bits; the table is longer
                k1 += e1 & 0xFF;
                Raw.Set(out1, i + 3, (byte)(e1 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e2 = Raw.Get(table, (int)((c2 << k2) >> shift)); // the index has tableBits bits; the table is longer
                k2 += e2 & 0xFF;
                Raw.Set(out2, i + 3, (byte)(e2 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e3 = Raw.Get(table, (int)((c3 << k3) >> shift)); // the index has tableBits bits; the table is longer
                k3 += e3 & 0xFF;
                Raw.Set(out3, i + 3, (byte)(e3 >> 8)); // i + 3 < joint <= lastPart <= every output's length
                ushort e4 = Raw.Get(table, (int)((c4 << k4) >> shift)); // the index has tableBits bits; the table is longer
                k4 += e4 & 0xFF;
                Raw.Set(out4, i + 3, (byte)(e4 >> 8)); // i + 3 < joint <= lastPart <= every output's length
            }

            // Refill: the register starts at the lowest byte not yet consumed; q + 8 <= stream length holds because q
            // only moves down from length - 8.
            pos1 -= k1 >> 3;
            k1 &= 7;
            c1 = Raw.ReadUInt64(stream1, pos1);
            pos2 -= k2 >> 3;
            k2 &= 7;
            c2 = Raw.ReadUInt64(stream2, pos2);
            pos3 -= k3 >> 3;
            k3 &= 7;
            c3 = Raw.ReadUInt64(stream3, pos3);
            pos4 -= k4 >> 3;
            k4 &= 7;
            c4 = Raw.ReadUInt64(stream4, pos4);
            i += 4;
        }

        bits1.Restore(c1, k1, pos1);
        bits2.Restore(c2, k2, pos2);
        bits3.Restore(c3, k3, pos3);
        bits4.Restore(c4, k4, pos4);

        DecodeSymbols(ref bits1, out1[i..]);
        DecodeSymbols(ref bits2, out2[i..]);
        DecodeSymbols(ref bits3, out3[i..]);
        DecodeSymbols(ref bits4, out4[i..]);
        if (!bits1.IsFinished)
        {
            ZstdFormat.ThrowCorrupt(offset1, "a Huffman stream that is consumed exactly by its symbols");
        }

        if (!bits2.IsFinished)
        {
            ZstdFormat.ThrowCorrupt(offset2, "a Huffman stream that is consumed exactly by its symbols");
        }

        if (!bits3.IsFinished)
        {
            ZstdFormat.ThrowCorrupt(offset3, "a Huffman stream that is consumed exactly by its symbols");
        }

        if (!bits4.IsFinished)
        {
            ZstdFormat.ThrowCorrupt(offset4, "a Huffman stream that is consumed exactly by its symbols");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DecodeSymbols(ref ZstdReverseBitReader bits, Span<byte> destination)
    {
        ushort[] table = _table;
        int tableBits = _tableBits;
        int i = 0;

        // After a refill at least 56 bits are available, so four symbols of at most 11 bits fit.
        while (destination.Length - i >= 4)
        {
            bits.Reload();
            ushort e0 = Raw.Get(table, (int)bits.PeekBitsNoReload(tableBits));
            bits.Skip(e0 & 0xFF);
            ushort e1 = Raw.Get(table, (int)bits.PeekBitsNoReload(tableBits));
            bits.Skip(e1 & 0xFF);
            ushort e2 = Raw.Get(table, (int)bits.PeekBitsNoReload(tableBits));
            bits.Skip(e2 & 0xFF);
            ushort e3 = Raw.Get(table, (int)bits.PeekBitsNoReload(tableBits));
            bits.Skip(e3 & 0xFF);
            Raw.Set(destination, i, (byte)(e0 >> 8)); // i + 3 < destination.Length
            Raw.Set(destination, i + 1, (byte)(e1 >> 8));
            Raw.Set(destination, i + 2, (byte)(e2 >> 8));
            Raw.Set(destination, i + 3, (byte)(e3 >> 8));
            i += 4;
        }

        for (; i < destination.Length; i++)
        {
            bits.Reload();
            ushort e = Raw.Get(table, (int)bits.PeekBitsNoReload(tableBits));
            bits.Skip(e & 0xFF);
            destination[i] = (byte)(e >> 8);
        }
    }
}
