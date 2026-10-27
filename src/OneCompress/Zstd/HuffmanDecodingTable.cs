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

    /// <summary>Gets the length in bits of the longest code (the table has <c>1 &lt;&lt; TableBits</c> entries).</summary>
    public int TableBits => _tableBits;

    /// <summary>
    /// Reads a Huffman tree description (RFC 8878 §4.2.1) from the start of <paramref name="source"/> and builds the table.
    /// </summary>
    /// <param name="source">The bytes that start with the tree description; other data may follow it.</param>
    /// <param name="streamOffset">Offset of <paramref name="source"/> in the compressed input, for error messages.</param>
    /// <returns>The number of bytes the description occupies.</returns>
    /// <exception cref="CompressionFormatException">The description is truncated or does not describe a valid prefix code.</exception>
    public int ReadDescription(ReadOnlySpan<byte> source, long streamOffset = 0)
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

        BuildFromWeights(_weights.AsSpan(0, weightCount), streamOffset);
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
        DecodeStream(source.Slice(p, size1), destination[..part], streamOffset + p);
        p += size1;
        DecodeStream(source.Slice(p, size2), destination.Slice(part, part), streamOffset + p);
        p += size2;
        DecodeStream(source.Slice(p, size3), destination.Slice(2 * part, part), streamOffset + p);
        p += size3;
        DecodeStream(source.Slice(p, size4), destination[(3 * part)..], streamOffset + p);
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
            ushort e0 = table[bits.PeekBitsNoReload(tableBits)];
            bits.Skip(e0 & 0xFF);
            ushort e1 = table[bits.PeekBitsNoReload(tableBits)];
            bits.Skip(e1 & 0xFF);
            ushort e2 = table[bits.PeekBitsNoReload(tableBits)];
            bits.Skip(e2 & 0xFF);
            ushort e3 = table[bits.PeekBitsNoReload(tableBits)];
            bits.Skip(e3 & 0xFF);
            destination[i] = (byte)(e0 >> 8);
            destination[i + 1] = (byte)(e1 >> 8);
            destination[i + 2] = (byte)(e2 >> 8);
            destination[i + 3] = (byte)(e3 >> 8);
            i += 4;
        }

        for (; i < destination.Length; i++)
        {
            bits.Reload();
            ushort e = table[bits.PeekBitsNoReload(tableBits)];
            bits.Skip(e & 0xFF);
            destination[i] = (byte)(e >> 8);
        }
    }
}
