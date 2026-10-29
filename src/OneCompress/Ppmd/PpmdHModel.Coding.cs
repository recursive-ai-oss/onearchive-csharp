// Ported from C/Ppmd7Dec.c and C/Ppmd7Enc.c of the LZMA SDK / 7-Zip 26.05 (Igor Pavlov); see PpmdHModel.cs for the
// original notices, quoted exactly. This file holds the 7z range coder and the symbol coding loops.

namespace RecursiveAI.OneCompress.Ppmd;

public sealed partial class PpmdHModel
{
    private const uint TopValue = 1u << 24;

    private bool _rangeActive;
    private bool _journaling;

    // Input bytes that must remain before a symbol for it to be decoded without a journal: well above 2 * (MaxOrder + 2) + 4.
    private const int JournalMargin = 512;
    private uint _range;
    private uint _code;
    private ulong _low;
    private byte _cache;
    private ulong _cacheSize;
    private byte[] _out = new byte[256];
    private int _outStart;
    private int _outLen;

    /// <summary>Gets the compressed bytes the encoder has produced and the caller has not yet taken.</summary>
    public ReadOnlySpan<byte> PendingOutput => _out.AsSpan(_outStart, _outLen - _outStart);

    /// <summary>Marks <paramref name="count"/> bytes of <see cref="PendingOutput"/> as taken.</summary>
    /// <param name="count">The number of bytes taken, at most the length of <see cref="PendingOutput"/>.</param>
    public void ConsumeOutput(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _outLen - _outStart);
        _outStart += count;
        if (_outStart == _outLen)
        {
            _outStart = 0;
            _outLen = 0;
        }
    }

    // ---- range encoder (7z flavour: carry propagation through a cache byte) ----

    /// <summary>Starts encoding: writes nothing yet (the first output byte is produced by the first shift).</summary>
    public void StartEncoding()
    {
        _low = 0;
        _range = 0xFFFFFFFF;
        _cache = 0;
        _cacheSize = 1;
        _rangeActive = true;
    }

    private void Emit(byte b)
    {
        if (_outLen == _out.Length)
        {
            if (_outStart > 0)
            {
                Buffer.BlockCopy(_out, _outStart, _out, 0, _outLen - _outStart);
                _outLen -= _outStart;
                _outStart = 0;
            }
            else
            {
                Array.Resize(ref _out, _out.Length * 2);
            }
        }

        _out[_outLen++] = b;
    }

    private void ShiftLow()
    {
        if ((uint)_low < 0xFF000000u || (uint)(_low >> 32) != 0)
        {
            byte temp = _cache;
            do
            {
                Emit((byte)(temp + (byte)(_low >> 32)));
                temp = 0xFF;
            }
            while (--_cacheSize != 0);

            _cache = (byte)((uint)_low >> 24);
        }

        _cacheSize++;
        _low = (uint)_low << 8;
    }

    private void EncNorm1()
    {
        if (_range < TopValue)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    private void EncNorm2()
    {
        EncNorm1();
        EncNorm1();
    }

    private void Encode(uint start, uint size)
    {
        _low += start * _range;
        _range *= size;
    }

    /// <summary>Writes the five bytes that finish a 7z PPMd stream.</summary>
    public void FinishEncoding()
    {
        for (int i = 0; i < 5; i++)
        {
            ShiftLow();
        }

        _rangeActive = false;
    }

    private void SetRangeMaskAll()
    {
        Span<byte> mem = _m;
        int s = PpmdRaw.U32(mem, _minContext + 4);
        int n = PpmdRaw.U16(mem, _minContext);
        for (int i = 0; i < n; i++, s += StateSize)
        {
            PpmdRaw.Slot(_mask, PpmdRaw.At(mem, s)) = 0;
        }
    }

    int IPpmdModel.EncodeBlock(ReadOnlySpan<byte> source, int maxPending)
    {
        int i = 0;
        while (i < source.Length && _outLen - _outStart < maxPending)
        {
            EncodeSymbol(source[i++]);
        }

        return i;
    }

    int IPpmdModel.DecodeBlock(ReadOnlySpan<byte> input, ref int pos, Span<byte> destination, out int count)
    {
        int n = 0;
        while (n < destination.Length)
        {
            int sym = DecodeSymbol(input, ref pos);
            if (sym < 0)
            {
                count = n;
                return sym;
            }

            destination[n++] = (byte)sym;
        }

        count = n;
        return 0;
    }

    /// <summary>Encodes one byte, or the end marker when <paramref name="symbol"/> is -1 (7z streams do not use the marker).</summary>
    /// <param name="symbol">A byte value 0 to 255, or -1.</param>
    /// <exception cref="InvalidOperationException">Encoding was not started.</exception>
    /// <exception cref="CompressionException">The model's frequency total passed the range coder's capacity.</exception>
    public void EncodeSymbol(int symbol)
    {
        Span<byte> mem = _m;
        if (!_rangeActive)
        {
            throw new InvalidOperationException("Call StartEncoding first.");
        }

        int mc = _minContext;
        if (PpmdRaw.U16(mem, mc) != 1)
        {
            int s = PpmdRaw.U32(mem, mc + 4);
            PpmdRaw.Prefetch(mem, PpmdRaw.U32(mem, s + 2));
            uint summFreq = (uint)PpmdRaw.U16(mem, mc + 2);
            if (summFreq == 0 || _range / summFreq == 0)
            {
                throw CapacityError();
            }

            _range /= summFreq;

            if (PpmdRaw.At(mem, s) == symbol)
            {
                Encode(0, PpmdRaw.At(mem, s + 1));
                EncNorm2();
                _foundState = s;
                Update1_0();
                return;
            }

            _prevSuccess = 0;
            uint sum = PpmdRaw.At(mem, s + 1);
            int i = PpmdRaw.U16(mem, mc) - 1;
            do
            {
                s += StateSize;
                if (PpmdRaw.At(mem, s) == symbol)
                {
                    Encode(sum, PpmdRaw.At(mem, s + 1));
                    EncNorm2();
                    _foundState = s;
                    Update1();
                    return;
                }

                sum += PpmdRaw.At(mem, s + 1);
            }
            while (--i != 0);

            Encode(sum, summFreq - sum);
            _hiBitsFlag = HiBitsFlag3(PpmdRaw.At(mem, _foundState));
            Array.Fill(_mask, (byte)0xFF);
            SetRangeMaskAll();
        }
        else
        {
            int idx = BinSummIndex();
            int s = mc + 2;
            PpmdRaw.Prefetch(mem, PpmdRaw.U32(mem, mc + 4));
            uint pr = _binSumm[idx];
            uint bound = (_range >> 14) * pr;
            pr -= (pr + (1u << (PpmdTables.PeriodBits - 2))) >> PpmdTables.PeriodBits;
            if (PpmdRaw.At(mem, s) == symbol)
            {
                _binSumm[idx] = (ushort)(pr + (1u << PpmdTables.IntBits));
                _range = bound;
                EncNorm1();
                uint freq = PpmdRaw.At(mem, s + 1);
                int c = PpmdRaw.U32(mem, s + 2);
                _foundState = s;
                _prevSuccess = 1;
                _runLength++;
                PpmdRaw.At(mem, s + 1) = (byte)(freq + (freq < 128 ? 1u : 0u));
                if (_orderFall == 0 && c > _text)
                {
                    _maxContext = _minContext = c;
                    PpmdRaw.PrefetchStats(mem, c);
                }
                else
                {
                    UpdateModel();
                }

                return;
            }

            _binSumm[idx] = (ushort)pr;
            _initEsc = PpmdTables.ExpEscape[pr >> 10];
            _low += bound;
            _range -= bound;
            Array.Fill(_mask, (byte)0xFF);
            PpmdRaw.Slot(_mask, PpmdRaw.At(mem, s)) = 0;
            _prevSuccess = 0;
        }

        for (;;)
        {
            EncNorm2();

            mc = _minContext;
            int numMasked = PpmdRaw.U16(mem, mc);
            do
            {
                _orderFall++;
                int suffix = PpmdRaw.U32(mem, mc + 8);
                if (suffix == 0)
                {
                    return; // end marker
                }

                mc = suffix;
            }
            while (PpmdRaw.U16(mem, mc) == numMasked);

            _minContext = mc;
            int see = MakeEscFreq(numMasked, out uint escFreq);
            int s = PpmdRaw.U32(mem, mc + 4);
            int n = PpmdRaw.U16(mem, mc);
            uint sum = 0;
            for (int i = 0; i < n; i++, s += StateSize)
            {
                if (PpmdRaw.At(mem, s) == symbol)
                {
                    uint low = sum;
                    uint freq = PpmdRaw.At(mem, s + 1);
                    SeeUpdate(see);
                    _foundState = s;
                    uint total = sum + escFreq;
                    for (int j = i; j < n; j++)
                    {
                        total += (uint)(PpmdRaw.At(mem, s + 1) & PpmdRaw.Slot(_mask, PpmdRaw.At(mem, s)));
                        s += StateSize;
                    }

                    if (total == 0 || _range / total == 0)
                    {
                        throw CapacityError();
                    }

                    _range /= total;
                    Encode(low, freq);
                    EncNorm2();
                    Update2();
                    return;
                }

                sum += (uint)(PpmdRaw.At(mem, s + 1) & PpmdRaw.Slot(_mask, PpmdRaw.At(mem, s)));
            }

            {
                uint total = sum + escFreq;
                _seeSumm[see] = (ushort)(_seeSumm[see] + total);
                if (_range / total == 0)
                {
                    throw CapacityError();
                }

                _range /= total;
                Encode(sum, escFreq);
            }

            SetRangeMaskAll();
        }
    }

    private static CompressionException CapacityError() =>
        new("ppmd: the model's frequency total exceeds the range coder's capacity");

    // ---- rollback ----
    // A symbol that needs input past the end of the buffer must leave the model as it was, so that the caller can present the same
    // bytes again with more after them. Before the model is updated a symbol changes only the range coder, a few scalars and some
    // adaptive probabilities; those are journaled here and restored by Rollback.
    private uint _jRange;
    private uint _jCode;
    private int _jPrevSuccess;
    private int _jHiBitsFlag;
    private int _jInitEsc;
    private int _jOrderFall;
    private int _jMinContext;
    private int _jStartPos;
    private int _jBinIndex;
    private ushort _jBinOld;
    private int _jSeeCount;
    private readonly int[] _jSeeIndex = new int[2 * (MaxOrder + 2)];
    private readonly ushort[] _jSeeOld = new ushort[2 * (MaxOrder + 2)];

    private void BeginSymbol(int pos)
    {
        _jRange = _range;
        _jCode = _code;
        _jPrevSuccess = _prevSuccess;
        _jHiBitsFlag = _hiBitsFlag;
        _jInitEsc = _initEsc;
        _jOrderFall = _orderFall;
        _jMinContext = _minContext;
        _jStartPos = pos;
        _jBinIndex = -1;
        _jSeeCount = 0;
    }

    private void JournalSee(int see)
    {
        _jSeeIndex[_jSeeCount] = see;
        _jSeeOld[_jSeeCount++] = _seeSumm[see];
    }

    private int Rollback(ref int pos)
    {
        while (_jSeeCount > 0)
        {
            _jSeeCount--;
            _seeSumm[_jSeeIndex[_jSeeCount]] = _jSeeOld[_jSeeCount];
        }

        if (_jBinIndex >= 0)
        {
            _binSumm[_jBinIndex] = _jBinOld;
        }

        _range = _jRange;
        _code = _jCode;
        _prevSuccess = _jPrevSuccess;
        _hiBitsFlag = _jHiBitsFlag;
        _initEsc = _jInitEsc;
        _orderFall = _jOrderFall;
        _minContext = _jMinContext;
        pos = _jStartPos;
        return -3;
    }

    // ---- range decoder ----

    private static int ReadByte(ReadOnlySpan<byte> input, ref int pos)
    {
        int p = pos++;
        return (uint)p < (uint)input.Length ? input[p] : 0;
    }

    /// <summary>Starts decoding: reads the five header bytes of a 7z PPMd stream.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="pos">The read position; advanced past the header (beyond <paramref name="input"/>'s end when it is too short).</param>
    /// <returns>True when the header is valid (first byte zero, code below 0xFFFFFFFF).</returns>
    public bool StartDecoding(ReadOnlySpan<byte> input, ref int pos)
    {
        _code = 0;
        _range = 0xFFFFFFFF;
        bool ok = ReadByte(input, ref pos) == 0;
        for (int i = 0; i < 4; i++)
        {
            _code = (_code << 8) | (uint)ReadByte(input, ref pos);
        }

        _rangeActive = true;
        _journaling = true;
        return ok && _code < 0xFFFFFFFF;
    }

    /// <summary>Gets a value indicating whether the decoder has consumed the stream's code exactly (the check 7-Zip makes at the end).</summary>
    public bool IsFinishedOk => _code == 0;

    private void DecNorm1(ReadOnlySpan<byte> input, ref int pos)
    {
        if (_range < TopValue)
        {
            _code = (_code << 8) | (uint)ReadByte(input, ref pos);
            _range <<= 8;
        }
    }

    private void DecNorm2(ReadOnlySpan<byte> input, ref int pos)
    {
        DecNorm1(input, ref pos);
        DecNorm1(input, ref pos);
    }

    private void Decode(uint start, uint size)
    {
        _code -= start * _range;
        _range *= size;
    }

    /// <summary>Decodes one symbol.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="pos">The read position; advanced past the bytes the symbol used.</param>
    /// <returns>
    /// A byte value, -1 for the end marker, -2 when the data is invalid, or -3 when the symbol needs bytes past the end of <paramref name="input"/>:
    /// the model is then unchanged and <paramref name="pos"/> is where the symbol began, so the call can be repeated with the same bytes and more after them.
    /// </returns>
    /// <exception cref="InvalidOperationException">Decoding was not started.</exception>
    public int DecodeSymbol(ReadOnlySpan<byte> input, ref int pos)
    {
        Span<byte> mem = _m;
        if (!_rangeActive)
        {
            throw new InvalidOperationException("Call StartDecoding first.");
        }

        // A symbol reads at most two bytes per escape level and a model is at most MaxOrder + 1 levels deep, so with this much input
        // left it cannot run past the end and needs no journal to roll back.
        bool journal = input.Length - pos < JournalMargin;
        _journaling = journal;
        if (journal)
        {
            BeginSymbol(pos);
        }

        int mc = _minContext;
        if (PpmdRaw.U16(mem, mc) != 1)
        {
            int s = PpmdRaw.U32(mem, mc + 4);
            PpmdRaw.Prefetch(mem, PpmdRaw.U32(mem, s + 2));
            uint summFreq = (uint)PpmdRaw.U16(mem, mc + 2);
            if (summFreq == 0 || _range / summFreq == 0)
            {
                return -2;
            }

            _range /= summFreq;
            uint count = _code / _range;
            uint hiCnt = count;
            uint f = PpmdRaw.At(mem, s + 1);

            if ((int)(count -= f) < 0)
            {
                Decode(0, f);
                DecNorm2(input, ref pos);
                if (pos > input.Length)
                {
                    return Rollback(ref pos);
                }

                _foundState = s;

                int sym = PpmdRaw.At(mem, s);
                Update1_0();
                return sym;
            }

            _prevSuccess = 0;
            int i = PpmdRaw.U16(mem, mc) - 1;
            do
            {
                s += StateSize;
                f = PpmdRaw.At(mem, s + 1);
                if ((int)(count -= f) < 0)
                {
                    Decode(hiCnt - count - f, f);
                    DecNorm2(input, ref pos);
                    if (pos > input.Length)
                    {
                        return Rollback(ref pos);
                    }

                    _foundState = s;

                    int sym = PpmdRaw.At(mem, s);
                    Update1();
                    return sym;
                }
            }
            while (--i != 0);

            if (hiCnt >= summFreq)
            {
                return -2;
            }

            hiCnt -= count;
            Decode(hiCnt, summFreq - hiCnt);
            _hiBitsFlag = HiBitsFlag3(PpmdRaw.At(mem, _foundState));
            Array.Fill(_mask, (byte)0xFF);
            SetRangeMaskAll();
        }
        else
        {
            int idx = BinSummIndex();
            int s = mc + 2;
            PpmdRaw.Prefetch(mem, PpmdRaw.U32(mem, mc + 4));
            uint pr = _binSumm[idx];
            uint size0 = (_range >> 14) * pr;
            if (journal)
            {
                _jBinIndex = idx;
                _jBinOld = (ushort)pr;
            }

            pr -= (pr + (1u << (PpmdTables.PeriodBits - 2))) >> PpmdTables.PeriodBits;

            if (_code < size0)
            {
                _binSumm[idx] = (ushort)(pr + (1u << PpmdTables.IntBits));
                _range = size0;
                DecNorm1(input, ref pos);
                if (pos > input.Length)
                {
                    return Rollback(ref pos);
                }

                uint freq = PpmdRaw.At(mem, s + 1);
                int c = PpmdRaw.U32(mem, s + 2);
                int sym = PpmdRaw.At(mem, s);
                _foundState = s;
                _prevSuccess = 1;
                _runLength++;
                PpmdRaw.At(mem, s + 1) = (byte)(freq + (freq < 128 ? 1u : 0u));
                if (_orderFall == 0 && c > _text)
                {
                    _maxContext = _minContext = c;
                    PpmdRaw.PrefetchStats(mem, c);
                }
                else
                {
                    UpdateModel();
                }

                return sym;
            }

            _binSumm[idx] = (ushort)pr;
            _initEsc = PpmdTables.ExpEscape[pr >> 10];
            _code -= size0;
            _range -= size0;
            Array.Fill(_mask, (byte)0xFF);
            PpmdRaw.Slot(_mask, PpmdRaw.At(mem, s)) = 0;
            _prevSuccess = 0;
        }

        for (;;)
        {
            DecNorm2(input, ref pos);
            if (pos > input.Length)
            {
                return Rollback(ref pos);
            }

            mc = _minContext;
            int numMasked = PpmdRaw.U16(mem, mc);
            do
            {
                _orderFall++;
                int suffix = PpmdRaw.U32(mem, mc + 8);
                if (suffix == 0)
                {
                    return -1;
                }

                mc = suffix;
            }
            while (PpmdRaw.U16(mem, mc) == numMasked);

            int s = PpmdRaw.U32(mem, mc + 4);
            int num = PpmdRaw.U16(mem, mc);
            uint hiCnt = 0;
            for (int i = 0, t = s; i < num; i++, t += StateSize)
            {
                hiCnt += (uint)(PpmdRaw.At(mem, t + 1) & PpmdRaw.Slot(_mask, PpmdRaw.At(mem, t)));
            }

            _minContext = mc;
            int see = MakeEscFreq(numMasked, out uint freqSum);
            freqSum += hiCnt;
            if (_range / freqSum == 0)
            {
                return -2;
            }

            _range /= freqSum;
            uint count = _code / _range;

            if (count < hiCnt)
            {
                hiCnt = count;
                for (;;)
                {
                    count -= (uint)(PpmdRaw.At(mem, s + 1) & PpmdRaw.Slot(_mask, PpmdRaw.At(mem, s)));
                    s += StateSize;
                    if ((int)count < 0)
                    {
                        break;
                    }
                }

                s -= StateSize;
                uint f = PpmdRaw.At(mem, s + 1);
                Decode(hiCnt - count - f, f);
                DecNorm2(input, ref pos);
                if (pos > input.Length)
                {
                    return Rollback(ref pos);
                }

                SeeUpdate(see);
                _foundState = s;
                int sym = PpmdRaw.At(mem, s);
                Update2();
                return sym;
            }

            if (count >= freqSum)
            {
                return -2;
            }

            Decode(hiCnt, freqSum - hiCnt);
            _seeSumm[see] = (ushort)(_seeSumm[see] + freqSum);
            SetRangeMaskAll();
        }
    }
}
