// Ported from C/Ppmd8Dec.c and C/Ppmd8Enc.c of the LZMA SDK / 7-Zip 26.05 (Igor Pavlov); see PpmdIModel.cs for the model's
// notices. Original notices of these two files, quoted exactly:
//   Ppmd8Dec.c: "Ppmd8Dec.c -- Ppmd8 (PPMdI) Decoder
//                2023-09-07 : Igor Pavlov : Public domain
//                This code is based on:
//                  PPMd var.I (2002): Dmitry Shkarin : Public domain
//                  Carryless rangecoder (1999): Dmitry Subbotin : Public domain"
//   Ppmd8Enc.c: "Ppmd8Enc.c -- Ppmd8 (PPMdI) Encoder
//                2023-09-07 : Igor Pavlov : Public domain
//                This code is based on:
//                  PPMd var.I (2002): Dmitry Shkarin : Public domain
//                  Carryless rangecoder (1999): Dmitry Subbotin : Public domain"

namespace RecursiveAI.OneCompress.Ppmd;

public sealed partial class PpmdIModel
{
    private const uint Top = 1u << 24;
    private const uint Bot = 1u << 15;

    private bool _rangeActive;
    private uint _range;
    private uint _code;
    private uint _low;
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

    // ---- range encoder (carry-less) ----

    /// <summary>Starts encoding. Nothing is written until symbols are coded.</summary>
    public void StartEncoding()
    {
        _low = 0;
        _range = 0xFFFFFFFF;
        _rangeActive = true;
    }

    private void EncNorm()
    {
        while (((_low ^ (_low + _range)) < Top) || (_range < Bot && SetRange((0u - _low) & (Bot - 1))))
        {
            Emit((byte)(_low >> 24));
            _range <<= 8;
            _low <<= 8;
        }
    }

    private bool SetRange(uint value)
    {
        _range = value;
        return true;
    }

    private void Encode(uint start, uint size, uint total)
    {
        _range /= total;
        _low += start * _range;
        _range *= size;
    }

    /// <summary>Writes the four bytes that finish a stream.</summary>
    public void FinishEncoding()
    {
        for (int i = 0; i < 4; i++, _low <<= 8)
        {
            Emit((byte)(_low >> 24));
        }

        _rangeActive = false;
    }

    private void MaskAll()
    {
        int s = U32(_minContext + 4);
        int n = _m[_minContext] + 1;
        for (int i = 0; i < n; i++, s += StateSize)
        {
            _mask[_m[s]] = 0;
        }
    }

    private uint Correct(uint sum) => sum > _range ? _range : sum;

    /// <summary>Encodes one byte, or the end marker when <paramref name="symbol"/> is -1.</summary>
    /// <param name="symbol">A byte value 0 to 255, or -1.</param>
    /// <exception cref="InvalidOperationException">Encoding was not started.</exception>
    /// <exception cref="CompressionException">The model's frequency total passed the range coder's capacity.</exception>
    public void EncodeSymbol(int symbol)
    {
        if (!_rangeActive)
        {
            throw new InvalidOperationException("Call StartEncoding first.");
        }

        int mc = _minContext;
        if (_m[mc] != 0)
        {
            int s = U32(mc + 4);
            uint summFreq = Correct((uint)U16(mc + 2));
            if (summFreq == 0)
            {
                throw CapacityError();
            }

            if (_m[s] == symbol)
            {
                Encode(0, _m[s + 1], summFreq);
                EncNorm();
                _foundState = s;
                Update1_0();
                return;
            }

            _prevSuccess = 0;
            uint sum = _m[s + 1];
            int i = _m[mc];
            do
            {
                s += StateSize;
                if (_m[s] == symbol)
                {
                    Encode(sum, _m[s + 1], summFreq);
                    EncNorm();
                    _foundState = s;
                    Update1();
                    return;
                }

                sum += _m[s + 1];
            }
            while (--i != 0);

            Encode(sum, summFreq - sum, summFreq);
            Array.Fill(_mask, (byte)0xFF);
            MaskAll();
        }
        else
        {
            int idx = BinSummIndex();
            int s = mc + 2;
            uint pr = _binSumm[idx];
            uint bound = (_range >> 14) * pr;
            pr -= (pr + (1u << (PpmdTables.PeriodBits - 2))) >> PpmdTables.PeriodBits;
            if (_m[s] == symbol)
            {
                _binSumm[idx] = (ushort)(pr + (1u << PpmdTables.IntBits));
                _range = bound;
                EncNorm();
                uint freq = _m[s + 1];
                int c = U32(s + 2);
                _foundState = s;
                _prevSuccess = 1;
                _runLength++;
                _m[s + 1] = (byte)(freq + (freq < 196 ? 1u : 0u));
                if (_orderFall == 0 && c >= _unitsStart)
                {
                    _maxContext = _minContext = c;
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
            _range = (_range & ~((uint)PpmdTables.BinScale - 1)) - bound;
            Array.Fill(_mask, (byte)0xFF);
            _mask[_m[s]] = 0;
            _prevSuccess = 0;
        }

        for (;;)
        {
            EncNorm();

            mc = _minContext;
            int numMasked = _m[mc];
            do
            {
                _orderFall++;
                int suffix = U32(mc + 8);
                if (suffix == 0)
                {
                    return; // end marker
                }

                mc = suffix;
            }
            while (_m[mc] == numMasked);

            _minContext = mc;
            int see = MakeEscFreq(numMasked, out uint escFreq);
            int s = U32(mc + 4);
            int n = _m[mc] + 1;
            uint sum = 0;
            for (int i = 0; i < n; i++, s += StateSize)
            {
                if (_m[s] == symbol)
                {
                    uint low = sum;
                    uint freq = _m[s + 1];
                    SeeUpdate(see);
                    _foundState = s;
                    uint total = sum + escFreq;
                    for (int j = i; j < n; j++)
                    {
                        total += (uint)(_m[s + 1] & _mask[_m[s]]);
                        s += StateSize;
                    }

                    total = Correct(total);
                    if (total == 0)
                    {
                        throw CapacityError();
                    }

                    Encode(low, freq, total);
                    EncNorm();
                    Update2();
                    return;
                }

                sum += (uint)(_m[s + 1] & _mask[_m[s]]);
            }

            {
                uint total = sum + escFreq;
                _seeSumm[see] = (ushort)(_seeSumm[see] + total);
                total = Correct(total);
                if (total == 0)
                {
                    throw CapacityError();
                }

                Encode(sum, total - sum, total);
            }

            MaskAll();
        }
    }

    private static CompressionException CapacityError() =>
        new("ppmd: the model's frequency total exceeds the range coder's capacity");

    // ---- rollback ----
    // A symbol that needs input past the end of the buffer must leave the model as it was, so that the caller can present the same
    // bytes again with more after them. Before the model is updated a symbol changes only the range coder, a few scalars and some
    // adaptive probabilities; those are journaled here and restored by Rollback.
    private bool _journaling;
    private uint _jRange;
    private uint _jCode;
    private uint _jLow;
    private int _jPrevSuccess;
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
        _jLow = _low;
        _jPrevSuccess = _prevSuccess;
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
        _low = _jLow;
        _prevSuccess = _jPrevSuccess;
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

    /// <summary>Starts decoding: reads the four header bytes.</summary>
    /// <param name="input">The compressed bytes.</param>
    /// <param name="pos">The read position; advanced past the header (beyond the end of <paramref name="input"/> when it is too short).</param>
    /// <returns>True when the header is valid (code below 0xFFFFFFFF).</returns>
    public bool StartDecoding(ReadOnlySpan<byte> input, ref int pos)
    {
        _code = 0;
        _range = 0xFFFFFFFF;
        _low = 0;
        for (int i = 0; i < 4; i++)
        {
            _code = (_code << 8) | (uint)ReadByte(input, ref pos);
        }

        _rangeActive = true;
        _journaling = true;
        return _code < 0xFFFFFFFF;
    }

    /// <summary>Gets a value indicating whether the decoder has consumed the stream's code exactly.</summary>
    public bool IsFinishedOk => _code == 0;

    private void DecNorm(ReadOnlySpan<byte> input, ref int pos)
    {
        // A valid stream never needs more than four shifts; the cap keeps a corrupt state from spinning.
        for (int guard = 0; guard < 8 && (((_low ^ (_low + _range)) < Top) || (_range < Bot && SetRange((0u - _low) & (Bot - 1)))); guard++)
        {
            _code = (_code << 8) | (uint)ReadByte(input, ref pos);
            _range <<= 8;
            _low <<= 8;
        }
    }

    private void Decode(uint start, uint size)
    {
        start *= _range;
        _low += start;
        _code -= start;
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
        if (!_rangeActive)
        {
            throw new InvalidOperationException("Call StartDecoding first.");
        }

        BeginSymbol(pos);
        int mc = _minContext;
        if (_m[mc] != 0)
        {
            int s = U32(mc + 4);
            uint summFreq = Correct((uint)U16(mc + 2));
            if (summFreq == 0 || _range / summFreq == 0)
            {
                return -2;
            }

            _range /= summFreq;
            uint count = _code / _range;
            uint hiCnt = count;
            uint f = _m[s + 1];

            if ((int)(count -= f) < 0)
            {
                Decode(0, f);
                DecNorm(input, ref pos);
                if (pos > input.Length)
                {
                    return Rollback(ref pos);
                }

                _foundState = s;
                int sym = _m[s];
                Update1_0();
                return sym;
            }

            _prevSuccess = 0;
            int i = _m[mc];
            do
            {
                s += StateSize;
                f = _m[s + 1];
                if ((int)(count -= f) < 0)
                {
                    Decode(hiCnt - count - f, f);
                    DecNorm(input, ref pos);
                    if (pos > input.Length)
                    {
                        return Rollback(ref pos);
                    }

                    _foundState = s;
                    int sym = _m[s];
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
            Array.Fill(_mask, (byte)0xFF);
            MaskAll();
        }
        else
        {
            int idx = BinSummIndex();
            int s = mc + 2;
            uint pr = _binSumm[idx];
            uint size0 = (_range >> 14) * pr;
            _jBinIndex = idx;
            _jBinOld = (ushort)pr;
            pr -= (pr + (1u << (PpmdTables.PeriodBits - 2))) >> PpmdTables.PeriodBits;

            if (_code < size0)
            {
                _binSumm[idx] = (ushort)(pr + (1u << PpmdTables.IntBits));
                _range = size0;
                DecNorm(input, ref pos);
                if (pos > input.Length)
                {
                    return Rollback(ref pos);
                }

                uint freq = _m[s + 1];
                int c = U32(s + 2);
                int sym = _m[s];
                _foundState = s;
                _prevSuccess = 1;
                _runLength++;
                _m[s + 1] = (byte)(freq + (freq < 196 ? 1u : 0u));
                if (_orderFall == 0 && c >= _unitsStart)
                {
                    _maxContext = _minContext = c;
                }
                else
                {
                    UpdateModel();
                }

                return sym;
            }

            _binSumm[idx] = (ushort)pr;
            _initEsc = PpmdTables.ExpEscape[pr >> 10];
            _low += size0;
            _code -= size0;
            _range = (_range & ~((uint)PpmdTables.BinScale - 1)) - size0;
            Array.Fill(_mask, (byte)0xFF);
            _mask[_m[s]] = 0;
            _prevSuccess = 0;
        }

        for (;;)
        {
            DecNorm(input, ref pos);
            if (pos > input.Length)
            {
                return Rollback(ref pos);
            }

            mc = _minContext;
            int numMasked = _m[mc];
            do
            {
                _orderFall++;
                int suffix = U32(mc + 8);
                if (suffix == 0)
                {
                    return -1;
                }

                mc = suffix;
            }
            while (_m[mc] == numMasked);

            int s = U32(mc + 4);
            int num = _m[mc] + 1;
            uint hiCnt = 0;
            for (int i = 0, t = s; i < num; i++, t += StateSize)
            {
                hiCnt += (uint)(_m[t + 1] & _mask[_m[t]]);
            }

            _minContext = mc;
            int see = MakeEscFreq(numMasked, out uint freqSum);
            freqSum += hiCnt;
            uint freqSum2 = Correct(freqSum);
            if (freqSum2 == 0 || _range / freqSum2 == 0)
            {
                return -2;
            }

            _range /= freqSum2;
            uint count = _code / _range;

            if (count < hiCnt)
            {
                hiCnt = count;
                for (;;)
                {
                    count -= (uint)(_m[s + 1] & _mask[_m[s]]);
                    s += StateSize;
                    if ((int)count < 0)
                    {
                        break;
                    }
                }

                s -= StateSize;
                uint f = _m[s + 1];
                Decode(hiCnt - count - f, f);
                DecNorm(input, ref pos);
                if (pos > input.Length)
                {
                    return Rollback(ref pos);
                }

                SeeUpdate(see);
                _foundState = s;
                int sym = _m[s];
                Update2();
                return sym;
            }

            if (count >= freqSum2)
            {
                return -2;
            }

            Decode(hiCnt, freqSum2 - hiCnt);
            _seeSumm[see] = (ushort)(_seeSumm[see] + freqSum);
            MaskAll();
        }
    }
}
