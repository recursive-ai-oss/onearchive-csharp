using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Deflate64;

public sealed partial class Deflate64Encoder
{
    // Block types (RFC 1951 section 3.2.3).
    private const int FixedType = 1;
    private const int DynamicType = 2;

    private static readonly byte[] s_distanceSlots = BuildDistanceSlots();
    private static readonly byte[] s_fixedLiteralLengths = BuildFixedLiteralLengths();
    private static readonly ushort[] s_fixedLiteralCodes = BuildFixedCodes(s_fixedLiteralLengths);
    private static readonly byte[] s_fixedDistanceLengths = BuildFixedDistanceLengths();
    private static readonly ushort[] s_fixedDistanceCodes = BuildFixedCodes(s_fixedDistanceLengths);

    private readonly uint[] _literalFrequencies = new uint[Deflate64Tables.LiteralLengthSymbolCount];
    private readonly uint[] _distanceFrequencies = new uint[Deflate64Tables.DistanceSymbolCount];
    private readonly uint[] _codeLengthFrequencies = new uint[DeflateTables.CodeLengthSymbolCount];
    private readonly byte[] _literalLengths = new byte[Deflate64Tables.LiteralLengthSymbolCount];
    private readonly byte[] _distanceLengths = new byte[Deflate64Tables.DistanceSymbolCount];
    private readonly byte[] _codeLengthLengths = new byte[DeflateTables.CodeLengthSymbolCount];
    private readonly ushort[] _literalCodes = new ushort[Deflate64Tables.LiteralLengthSymbolCount];
    private readonly ushort[] _distanceCodes = new ushort[Deflate64Tables.DistanceSymbolCount];
    private readonly ushort[] _codeLengthCodes = new ushort[DeflateTables.CodeLengthSymbolCount];
    private readonly byte[] _allLengths = new byte[Deflate64Tables.LiteralLengthSymbolCount + Deflate64Tables.DistanceSymbolCount];
    private readonly byte[] _runSymbols = new byte[Deflate64Tables.LiteralLengthSymbolCount + Deflate64Tables.DistanceSymbolCount];
    private readonly byte[] _runExtras = new byte[Deflate64Tables.LiteralLengthSymbolCount + Deflate64Tables.DistanceSymbolCount];

    // distance 1..256 index directly; above that every slot covers whole multiples of 128, so (distance - 1) >> 7 finds it.
    private static byte[] BuildDistanceSlots()
    {
        var table = new byte[256 + (Deflate64Tables.WindowSize >> 7)];
        ReadOnlySpan<ushort> bases = Deflate64Tables.DistanceBase;
        for (int distance = 1; distance <= 256; distance++)
        {
            table[distance - 1] = (byte)SlotOf(bases, distance);
        }

        for (int i = 2; i < Deflate64Tables.WindowSize >> 7; i++)
        {
            table[256 + i] = (byte)SlotOf(bases, (i << 7) + 1);
        }

        return table;
    }

    private static int SlotOf(ReadOnlySpan<ushort> bases, int distance)
    {
        int slot = bases.Length - 1;
        while (bases[slot] > distance)
        {
            slot--;
        }

        return slot;
    }

    private static byte[] BuildFixedLiteralLengths()
    {
        var lengths = new byte[Deflate64Tables.LiteralLengthSymbolCount];
        DeflateTables.GetFixedLiteralLengthCodeLengths(lengths);
        return lengths;
    }

    private static byte[] BuildFixedDistanceLengths()
    {
        var lengths = new byte[Deflate64Tables.DistanceSymbolCount];
        DeflateTables.GetFixedDistanceCodeLengths(lengths);
        return lengths;
    }

    private static ushort[] BuildFixedCodes(byte[] lengths)
    {
        var codes = new ushort[lengths.Length];
        DeflateHuffmanTable.GetCanonicalCodes(lengths, codes);
        return codes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DistanceSlot(int distance) =>
        distance <= 256 ? s_distanceSlots[distance - 1] : s_distanceSlots[256 + ((distance - 1) >> 7)];

    // 0..28: the index of the length symbol minus 257 (258 and above use symbol 285).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LengthSlot(int length) => length >= 258 ? 28 : DeflateSymbols.LengthSlot[length];

    // Writes the tokens collected since the last block as one block, the cheapest of stored, fixed and dynamic (a tie prefers
    // fixed, then dynamic, then stored), and starts a new one.
    private void CloseBlock(bool final)
    {
        if (_tokenCount == 0)
        {
            if (final)
            {
                WriteEmptyFinal();
            }

            return;
        }

        Array.Clear(_literalFrequencies);
        Array.Clear(_distanceFrequencies);
        long extraBits = 0;
        int[] lengthTokens = _tokenLength;
        int[] valueTokens = _tokenValue;
        for (int i = 0; i < _tokenCount; i++)
        {
            int length = lengthTokens[i];
            if (length == 0)
            {
                _literalFrequencies[valueTokens[i]]++;
            }
            else
            {
                int slot = LengthSlot(length);
                int dslot = DistanceSlot(valueTokens[i]);
                _literalFrequencies[257 + slot]++;
                _distanceFrequencies[dslot]++;
                extraBits += Deflate64Tables.LengthExtraBits[slot] + Deflate64Tables.DistanceExtraBits[dslot];
            }
        }

        _literalFrequencies[DeflateTables.EndOfBlockSymbol] = 1;
        EnsureTwoSymbols(_literalFrequencies);
        EnsureTwoSymbols(_distanceFrequencies);

        // Dynamic code: lengths of at most 15 bits (RFC 1951 section 3.2.7); symbols 286 and 287 are never used.
        _builder.BuildLengths(_literalFrequencies.AsSpan(0, 286), DeflateTables.MaxCodeLength, _literalLengths);
        _builder.BuildLengths(_distanceFrequencies, DeflateTables.MaxCodeLength, _distanceLengths);
        int hlit = 286;
        while (hlit > 257 && _literalLengths[hlit - 1] == 0)
        {
            hlit--;
        }

        int hdist = Deflate64Tables.DistanceSymbolCount;
        while (hdist > 1 && _distanceLengths[hdist - 1] == 0)
        {
            hdist--;
        }

        int runCount = BuildRuns(hlit, hdist);
        Array.Clear(_codeLengthFrequencies);
        for (int i = 0; i < runCount; i++)
        {
            _codeLengthFrequencies[_runSymbols[i]]++;
        }

        EnsureTwoSymbols(_codeLengthFrequencies);
        _builder.BuildLengths(_codeLengthFrequencies, 7, _codeLengthLengths);
        int hclen = DeflateTables.CodeLengthSymbolCount;
        ReadOnlySpan<byte> order = DeflateTables.CodeLengthOrder;
        while (hclen > 4 && _codeLengthLengths[order[hclen - 1]] == 0)
        {
            hclen--;
        }

        long payloadDynamic = extraBits;
        long payloadFixed = extraBits;
        for (int s = 0; s < 286; s++)
        {
            payloadDynamic += (long)_literalFrequencies[s] * _literalLengths[s];
            payloadFixed += (long)_literalFrequencies[s] * s_fixedLiteralLengths[s];
        }

        for (int s = 0; s < Deflate64Tables.DistanceSymbolCount; s++)
        {
            payloadDynamic += (long)_distanceFrequencies[s] * _distanceLengths[s];
            payloadFixed += (long)_distanceFrequencies[s] * 5;
        }

        long headerDynamic = 3 + 14 + (3L * hclen);
        for (int i = 0; i < runCount; i++)
        {
            headerDynamic += _codeLengthLengths[_runSymbols[i]] + (_runSymbols[i] == 16 ? 2 : _runSymbols[i] == 17 ? 3 : _runSymbols[i] == 18 ? 7 : 0);
        }

        long costFixed = 3 + payloadFixed;
        long costDynamic = headerDynamic + payloadDynamic;
        long pieces = (_blockBytes + 65534) / 65535;
        long pad = (8 - ((_writer.PartialBitCount + 3) & 7)) & 7;
        long costStored = 3 + pad + 32 + (8L * _blockBytes) + ((pieces - 1) * 40);
        int kind = FixedType;
        long best = costFixed;
        if (costDynamic < best)
        {
            kind = DynamicType;
            best = costDynamic;
        }

        if (costStored < best)
        {
            kind = 0;
        }

        if (kind == 0)
        {
            _writer.WriteStoredBlocks(_window.AsSpan(_blockStart, _blockBytes), final);
        }
        else
        {
            _writer.WriteBits((uint)((final ? 1 : 0) | (kind << 1)), 3);
            ReadOnlySpan<byte> literalLengths;
            ReadOnlySpan<byte> distanceLengths;
            ReadOnlySpan<ushort> literalCodes;
            ReadOnlySpan<ushort> distanceCodes;
            if (kind == DynamicType)
            {
                WriteDynamicHeader(hlit, hdist, hclen, runCount);
                DeflateHuffmanTable.GetCanonicalCodes(_literalLengths, _literalCodes);
                DeflateHuffmanTable.GetCanonicalCodes(_distanceLengths, _distanceCodes);
                literalLengths = _literalLengths;
                distanceLengths = _distanceLengths;
                literalCodes = _literalCodes;
                distanceCodes = _distanceCodes;
            }
            else
            {
                literalLengths = s_fixedLiteralLengths;
                distanceLengths = s_fixedDistanceLengths;
                literalCodes = s_fixedLiteralCodes;
                distanceCodes = s_fixedDistanceCodes;
            }

            WriteTokens(literalLengths, literalCodes, distanceLengths, distanceCodes);
        }

        _blockStart += _blockBytes;
        _blockBytes = 0;
        _tokenCount = 0;
        ResetSplit();
    }

    private void WriteTokens(ReadOnlySpan<byte> literalLengths, ReadOnlySpan<ushort> literalCodes, ReadOnlySpan<byte> distanceLengths, ReadOnlySpan<ushort> distanceCodes)
    {
        int[] lengthTokens = _tokenLength;
        int[] valueTokens = _tokenValue;
        ReadOnlySpan<ushort> lengthBase = Deflate64Tables.LengthBase;
        ReadOnlySpan<ushort> distanceBase = Deflate64Tables.DistanceBase;
        ReadOnlySpan<byte> lengthExtra = Deflate64Tables.LengthExtraBits;
        ReadOnlySpan<byte> distanceExtra = Deflate64Tables.DistanceExtraBits;
        for (int i = 0; i < _tokenCount; i++)
        {
            int length = lengthTokens[i];
            if (length == 0)
            {
                int v = valueTokens[i];
                _writer.WriteBits(literalCodes[v], literalLengths[v]);
                continue;
            }

            int slot = LengthSlot(length);
            int symbol = 257 + slot;
            int codeLength = literalLengths[symbol];
            _writer.WriteBits(literalCodes[symbol] | ((uint)(length - lengthBase[slot]) << codeLength), codeLength + lengthExtra[slot]);
            int distance = valueTokens[i];
            int dslot = DistanceSlot(distance);
            int dcodeLength = distanceLengths[dslot];
            _writer.WriteBits(distanceCodes[dslot] | ((uint)(distance - distanceBase[dslot]) << dcodeLength), dcodeLength + distanceExtra[dslot]);
        }

        _writer.WriteBits(literalCodes[DeflateTables.EndOfBlockSymbol], literalLengths[DeflateTables.EndOfBlockSymbol]);
    }

    // RFC 1951 section 3.2.7: the code lengths of both alphabets as one sequence, run-length coded with symbols 16 (repeat the
    // previous length 3 to 6 times), 17 (3 to 10 zeros) and 18 (11 to 138 zeros), chosen greedily.
    private int BuildRuns(int hlit, int hdist)
    {
        _literalLengths.AsSpan(0, hlit).CopyTo(_allLengths);
        _distanceLengths.AsSpan(0, hdist).CopyTo(_allLengths.AsSpan(hlit));
        int total = hlit + hdist;
        int count = 0;
        int i = 0;
        while (i < total)
        {
            byte value = _allLengths[i];
            int run = 1;
            while (i + run < total && _allLengths[i + run] == value)
            {
                run++;
            }

            if (value == 0 && run >= 3)
            {
                int take = Math.Min(run, 138);
                if (take >= 11)
                {
                    _runSymbols[count] = 18;
                    _runExtras[count++] = (byte)(take - 11);
                }
                else
                {
                    _runSymbols[count] = 17;
                    _runExtras[count++] = (byte)(take - 3);
                }

                i += take;
                continue;
            }

            // A first copy of the length, then repeats of it while at least three remain.
            _runSymbols[count] = value;
            _runExtras[count++] = 0;
            i++;
            run--;
            while (value != 0 && run >= 3)
            {
                int take = Math.Min(run, 6);
                _runSymbols[count] = 16;
                _runExtras[count++] = (byte)(take - 3);
                i += take;
                run -= take;
            }

            // Fewer than three left (or a short run of zeros): plain lengths.
            while (run > 0 && (value != 0 || run < 3))
            {
                _runSymbols[count] = value;
                _runExtras[count++] = 0;
                i++;
                run--;
            }
        }

        return count;
    }

    private void WriteDynamicHeader(int hlit, int hdist, int hclen, int runCount)
    {
        _writer.WriteBits((uint)(hlit - 257), 5);
        _writer.WriteBits((uint)(hdist - 1), 5);
        _writer.WriteBits((uint)(hclen - 4), 4);
        ReadOnlySpan<byte> order = DeflateTables.CodeLengthOrder;
        for (int i = 0; i < hclen; i++)
        {
            _writer.WriteBits(_codeLengthLengths[order[i]], 3);
        }

        DeflateHuffmanTable.GetCanonicalCodes(_codeLengthLengths, _codeLengthCodes);
        for (int i = 0; i < runCount; i++)
        {
            int symbol = _runSymbols[i];
            _writer.WriteBits(_codeLengthCodes[symbol], _codeLengthLengths[symbol]);
            if (symbol >= 16)
            {
                _writer.WriteBits(_runExtras[i], symbol == 16 ? 2 : symbol == 17 ? 3 : 7);
            }
        }
    }

    // A code with a single symbol would be incomplete; two used symbols always give a complete code.
    private static void EnsureTwoSymbols(uint[] frequencies)
    {
        int used = 0;
        foreach (uint f in frequencies)
        {
            if (f != 0 && ++used == 2)
            {
                return;
            }
        }

        for (int i = 0; used < 2; i++)
        {
            if (frequencies[i] == 0)
            {
                frequencies[i] = 1;
                used++;
            }
        }
    }
}
