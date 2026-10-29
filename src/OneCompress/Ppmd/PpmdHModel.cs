// Ported from C/Ppmd7.c (the model), C/Ppmd7Dec.c and C/Ppmd7Enc.c (the 7z range coder) of the LZMA SDK / 7-Zip 26.05 (Igor Pavlov).
//
// Original notices, quoted exactly:
//   Ppmd7.c:    "Ppmd7.c -- PPMdH codec
//                2023-09-07 : Igor Pavlov : Public domain
//                This code is based on PPMd var.H (2001): Dmitry Shkarin : Public domain"
//   Ppmd7Dec.c: "Ppmd7Dec.c -- Ppmd7z (PPMdH with 7z Range Coder) Decoder
//                2023-09-07 : Igor Pavlov : Public domain
//                This code is based on:
//                  PPMd var.H (2001): Dmitry Shkarin : Public domain"
//   Ppmd7Enc.c: "Ppmd7Enc.c -- Ppmd7z (PPMdH with 7z Range Coder) Encoder
//                : Igor Pavlov : Public domain
//                This code is based on:
//                  PPMd var.H (2001): Dmitry Shkarin : Public domain"
//
// The model keeps the original's layout: one byte array holds the text area, then 12-byte units
// for symbol lists and contexts, and every link is a 32-bit offset into that array (0 is null).
// Matching the layout exactly is what makes the output byte-identical to PPMd var.H as 7-Zip writes it.

using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>
/// The PPMd variant H model (the 7z "PPMD" coder) together with the 7z flavour of its range coder.
/// This is the raw tier: <see cref="PpmdHEncoder"/> and <see cref="PpmdHDecoder"/> drive it over spans.
/// </summary>
/// <remarks>
/// One instance is either an encoder or a decoder for its whole life, chosen by the first call to
/// <see cref="StartEncoding"/> or <see cref="StartDecoding"/>; <see cref="Reset"/> returns it to the state of a new model.
/// The instance is not thread-safe.
/// </remarks>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed partial class PpmdHModel : IPpmdModel
{
    /// <summary>The smallest model order (the longest context, in symbols). The format allows 2 to 64.</summary>
    public const int MinOrder = 2;

    /// <summary>The largest model order.</summary>
    public const int MaxOrder = 64;

    /// <summary>The smallest model memory size in bytes (2 KiB).</summary>
    public const int MinMemorySize = 1 << 11;

    /// <summary>
    /// The largest model memory size this implementation allocates: 2 GiB minus 256 bytes, the most a managed array holds.
    /// The 7z format itself allows up to 4 GiB minus 36 bytes.
    /// </summary>
    public const int MaxMemorySize = 0x7FFFFF00;

    private const int MaxFreq = PpmdTables.MaxFreq;
    private const int UnitSize = PpmdTables.UnitSize;
    private const int NumIndexes = PpmdTables.NumIndexes;

    // Context record (one unit): NumStats u16 @0, SummFreq u16 @2 (or one inline state @2..7), Stats u32 @4, Suffix u32 @8.
    // State record (6 bytes): Symbol @0, Freq @1, Successor u32 @2.
    private const int StateSize = 6;

    private static readonly byte[] s_ns2BsIndx = BuildNs2BsIndx();
    private static readonly byte[] s_ns2Indx = BuildNs2Indx();

    private readonly byte[] _m;
    private readonly int _size;
    private readonly int _alignOffset;
    private readonly int[] _freeList = new int[NumIndexes];
    private readonly int[] _ps = new int[MaxOrder];
    private readonly byte[] _mask = new byte[256];
    private readonly ushort[] _seeSumm = new ushort[(25 * 16) + 1];
    private readonly byte[] _seeShift = new byte[(25 * 16) + 1];
    private readonly byte[] _seeCount = new byte[(25 * 16) + 1];
    private readonly ushort[] _binSumm = new ushort[128 * 64];

    private int _minContext;
    private int _maxContext;
    private int _foundState;
    private int _orderFall;
    private int _initEsc;
    private int _prevSuccess;
    private int _hiBitsFlag;
    private int _runLength;
    private int _initRl;
    private int _glueCount;
    private int _loUnit;
    private int _hiUnit;
    private int _text;
    private int _unitsStart;

    /// <summary>Creates a model.</summary>
    /// <param name="order">The model order, 2 to 64.</param>
    /// <param name="memorySize">The model memory in bytes, <see cref="MinMemorySize"/> to <see cref="MaxMemorySize"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its range.</exception>
    public PpmdHModel(int order, int memorySize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(order, MinOrder);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(order, MaxOrder);
        ArgumentOutOfRangeException.ThrowIfLessThan(memorySize, MinMemorySize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(memorySize, MaxMemorySize);
        Order = order;
        MemorySize = memorySize;
        _size = memorySize;
        _alignOffset = (4 - memorySize) & 3;
        _m = new byte[_alignOffset + memorySize];
        RestartModel();
    }

    /// <summary>Gets the model order.</summary>
    public int Order { get; }

    /// <summary>Gets the model memory size in bytes.</summary>
    public int MemorySize { get; }

    /// <summary>Returns the model to the state of a newly created one and abandons any coding in progress.</summary>
    public void Reset()
    {
        RestartModel();
        _rangeActive = false;
        _outStart = 0;
        _outLen = 0;
    }

    private static byte[] BuildNs2BsIndx()
    {
        var t = new byte[256];
        t[0] = 0 << 1;
        t[1] = 1 << 1;
        Array.Fill(t, (byte)(2 << 1), 2, 9);
        Array.Fill(t, (byte)(3 << 1), 11, 256 - 11);
        return t;
    }

    private static byte[] BuildNs2Indx()
    {
        var t = new byte[256];
        int i;
        for (i = 0; i < 3; i++)
        {
            t[i] = (byte)i;
        }

        int m = i;
        int k = 1;
        for (; i < 256; i++)
        {
            t[i] = (byte)m;
            if (--k == 0)
            {
                k = ++m - 2;
            }
        }

        return t;
    }

    // ---- raw memory access ----

    // A state is 6 bytes; both parts are read before either is written, so overlapping ranges copy correctly.
    private static void Copy6(Span<byte> mem, int dst, int src)
    {
        int lo = PpmdRaw.U32(mem, src);
        int hi = PpmdRaw.U16(mem, src + 4);
        PpmdRaw.SetU32(mem, dst, lo);
        PpmdRaw.SetU16(mem, dst + 4, hi);
    }

    private static void Swap6(Span<byte> mem, int a, int b) => PpmdRaw.Swap6(mem, a, b);

    private static int U2B(int nu) => nu * UnitSize;

    private static int U2I(int nu) => PpmdTables.Units2Indx[nu - 1];

    private static int I2U(int indx) => PpmdTables.Indx2Units[indx];

    // ---- sub-allocator ----

    private void InsertNode(int node, int indx)
    {
        Span<byte> mem = _m;
        PpmdRaw.SetU32(mem, node, _freeList[indx]);
        _freeList[indx] = node;
    }

    private int RemoveNode(int indx)
    {
        Span<byte> mem = _m;
        int node = _freeList[indx];
        _freeList[indx] = PpmdRaw.U32(mem, node);
        return node;
    }

    private void SplitBlock(int ptr, int oldIndx, int newIndx)
    {
        int nu = I2U(oldIndx) - I2U(newIndx);
        ptr += U2B(I2U(newIndx));
        int i = U2I(nu);
        if (I2U(i) != nu)
        {
            int k = I2U(--i);
            InsertNode(ptr + U2B(k), nu - k - 1);
        }

        InsertNode(ptr, i);
    }

    // A free block is a node: Stamp u16 @0 (0 = free), NU u16 @2, Next u32 @4. While a block sits on a free
    // list its first four bytes hold the list link instead, so the glue pass rewrites them as it walks.
    private void GlueFreeBlocks()
    {
        Span<byte> mem = _m;
        int n = 0;
        _glueCount = 255;

        // A guard node at LoUnit stops a block from gluing into the free gap between LoUnit and HiUnit.
        if (_loUnit != _hiUnit)
        {
            PpmdRaw.SetU16(mem, _loUnit, 1);
        }

        for (int i = 0; i < NumIndexes; i++)
        {
            int nu = I2U(i);
            int next = _freeList[i];
            _freeList[i] = 0;
            while (next != 0)
            {
                int node = next;
                next = PpmdRaw.U32(mem, node);
                PpmdRaw.SetU16(mem, node, 0);
                PpmdRaw.SetU16(mem, node + 2, nu);
                PpmdRaw.SetU32(mem, node + 4, n);
                n = node;
            }
        }

        int head = n;

        // Glue and Fill must walk the list in the same direction.
        int prev = -1; // -1: the list head; otherwise the address of a node's Next field
        while (n != 0)
        {
            int node = n;
            int nu = PpmdRaw.U16(mem, node + 2);
            n = PpmdRaw.U32(mem, node + 4);
            if (nu == 0)
            {
                if (prev < 0)
                {
                    head = n;
                }
                else
                {
                    PpmdRaw.SetU32(mem, prev, n);
                }

                continue;
            }

            prev = node + 4;
            for (;;)
            {
                int node2 = node + (nu * UnitSize);
                nu += PpmdRaw.U16(mem, node2 + 2);
                if (PpmdRaw.U16(mem, node2) != 0 || nu >= 0x10000)
                {
                    break;
                }

                PpmdRaw.SetU16(mem, node + 2, nu);
                PpmdRaw.SetU16(mem, node2 + 2, 0);
            }
        }

        for (n = head; n != 0;)
        {
            int node = n;
            int nu = PpmdRaw.U16(mem, node + 2);
            n = PpmdRaw.U32(mem, node + 4);
            if (nu == 0)
            {
                continue;
            }

            for (; nu > 128; nu -= 128, node += 128 * UnitSize)
            {
                InsertNode(node, NumIndexes - 1);
            }

            int i = U2I(nu);
            if (I2U(i) != nu)
            {
                int k = I2U(--i);
                InsertNode(node + (k * UnitSize), nu - k - 1);
            }

            InsertNode(node, i);
        }
    }

    private int AllocUnitsRare(int indx)
    {
        if (_glueCount == 0)
        {
            GlueFreeBlocks();
            if (_freeList[indx] != 0)
            {
                return RemoveNode(indx);
            }
        }

        int i = indx;
        do
        {
            if (++i == NumIndexes)
            {
                int numBytes = U2B(I2U(indx));
                int us = _unitsStart;
                _glueCount--;
                if (us - _text > numBytes)
                {
                    _unitsStart = us - numBytes;
                    return _unitsStart;
                }

                return 0;
            }
        }
        while (_freeList[i] == 0);

        int block = RemoveNode(i);
        SplitBlock(block, i, indx);
        return block;
    }

    private int AllocUnits(int indx)
    {
        if (_freeList[indx] != 0)
        {
            return RemoveNode(indx);
        }

        int numBytes = U2B(I2U(indx));
        int lo = _loUnit;
        if (_hiUnit - lo >= numBytes)
        {
            _loUnit = lo + numBytes;
            return lo;
        }

        return AllocUnitsRare(indx);
    }

    private void Mem12Copy(int dest, int src, int numUnits) => Buffer.BlockCopy(_m, src, _m, dest, numUnits * UnitSize);

    // ---- model ----

    private void RestartModel()
    {
        Span<byte> mem = _m;
        Array.Clear(_freeList);
        _text = _alignOffset;
        _hiUnit = _text + _size;
        _loUnit = _unitsStart = _hiUnit - (_size / 8 / UnitSize * 7 * UnitSize);
        _glueCount = 0;

        _orderFall = Order;
        _runLength = _initRl = -(Order < 12 ? Order : 12) - 1;
        _prevSuccess = 0;

        _hiUnit -= UnitSize;
        int mc = _hiUnit;
        int s = _loUnit;
        _loUnit += U2B(256 / 2);
        _maxContext = _minContext = mc;
        _foundState = s;

        PpmdRaw.SetU16(mem, mc, 256);
        PpmdRaw.SetU16(mem, mc + 2, 256 + 1);
        PpmdRaw.SetU32(mem, mc + 4, s);
        PpmdRaw.SetU32(mem, mc + 8, 0);

        for (int i = 0; i < 256; i++, s += StateSize)
        {
            PpmdRaw.At(mem, s) = (byte)i;
            PpmdRaw.At(mem, s + 1) = 1;
            PpmdRaw.SetU32(mem, s + 2, 0);
        }

        for (int i = 0; i < 128; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                ushort val = (ushort)(PpmdTables.BinScale - (PpmdTables.InitBinEsc[k] / (i + 2)));
                for (int m = 0; m < 64; m += 8)
                {
                    _binSumm[(i * 64) + k + m] = val;
                }
            }
        }

        for (int i = 0; i < 25; i++)
        {
            int summ = (5 * i + 10) << (PpmdTables.PeriodBits - 4);
            for (int k = 0; k < 16; k++)
            {
                int idx = (i * 16) + k;
                _seeSumm[idx] = (ushort)summ;
                _seeShift[idx] = PpmdTables.PeriodBits - 4;
                _seeCount[idx] = 4;
            }
        }

        int dummy = 25 * 16;
        _seeSumm[dummy] = 0;
        _seeShift[dummy] = PpmdTables.PeriodBits;
        _seeCount[dummy] = 64;
    }

    // Called when the found state's successor is a raw link into the text: creates the context records for it,
    // and for the identical raw links in the suffix contexts. Returns 0 when memory runs out.
    private int CreateSuccessors()
    {
        Span<byte> mem = _m;
        int c = _minContext;
        int upBranch = PpmdRaw.U32(mem, _foundState + 2);
        int numPs = 0;

        if (_orderFall != 0)
        {
            _ps[numPs++] = _foundState;
        }

        while (PpmdRaw.U32(mem, c + 8) != 0)
        {
            c = PpmdRaw.U32(mem, c + 8);
            int s;
            if (PpmdRaw.U16(mem, c) != 1)
            {
                byte sym = PpmdRaw.At(mem, _foundState);
                s = PpmdRaw.U32(mem, c + 4);
                while (PpmdRaw.At(mem, s) != sym)
                {
                    s += StateSize;
                }
            }
            else
            {
                s = c + 2;
            }

            int successor = PpmdRaw.U32(mem, s + 2);
            if (successor != upBranch)
            {
                c = successor;
                if (numPs == 0)
                {
                    return c;
                }

                break;
            }

            _ps[numPs++] = s;
        }

        byte newSym = PpmdRaw.At(mem, upBranch);
        upBranch++;

        byte newFreq;
        if (PpmdRaw.U16(mem, c) == 1)
        {
            newFreq = PpmdRaw.At(mem, c + 3);
        }
        else
        {
            int s = PpmdRaw.U32(mem, c + 4);
            while (PpmdRaw.At(mem, s) != newSym)
            {
                s += StateSize;
            }

            uint cf = (uint)PpmdRaw.At(mem, s + 1) - 1;
            uint s0 = (uint)PpmdRaw.U16(mem, c + 2) - (uint)PpmdRaw.U16(mem, c) - cf;
            newFreq = (byte)(1 + ((2 * cf <= s0) ? (5 * cf > s0 ? 1u : 0u) : (((2 * cf) + s0 - 1) / (2 * s0)) + 1));
        }

        do
        {
            int c1;
            if (_hiUnit != _loUnit)
            {
                _hiUnit -= UnitSize;
                c1 = _hiUnit;
            }
            else if (_freeList[0] != 0)
            {
                c1 = RemoveNode(0);
            }
            else
            {
                c1 = AllocUnitsRare(0);
                if (c1 == 0)
                {
                    return 0;
                }
            }

            PpmdRaw.SetU16(mem, c1, 1);
            PpmdRaw.At(mem, c1 + 2) = newSym;
            PpmdRaw.At(mem, c1 + 3) = newFreq;
            PpmdRaw.SetU32(mem, c1 + 4, upBranch);
            PpmdRaw.SetU32(mem, c1 + 8, c);
            PpmdRaw.SetU32(mem, _ps[--numPs] + 2, c1);
            c = c1;
        }
        while (numPs != 0);

        return c;
    }

    private void UpdateModel()
    {
        Span<byte> mem = _m;
        int fs = _foundState;
        int fSym = PpmdRaw.At(mem, fs);
        int fFreq = PpmdRaw.At(mem, fs + 1);
        int maxSuccessor;
        int minSuccessor;

        if (fFreq < MaxFreq / 4 && PpmdRaw.U32(mem, _minContext + 8) != 0)
        {
            int c = PpmdRaw.U32(mem, _minContext + 8);
            if (PpmdRaw.U16(mem, c) == 1)
            {
                int s = c + 2;
                if (PpmdRaw.At(mem, s + 1) < 32)
                {
                    PpmdRaw.At(mem, s + 1)++;
                }
            }
            else
            {
                int s = PpmdRaw.U32(mem, c + 4);
                if (PpmdRaw.At(mem, s) != fSym)
                {
                    do
                    {
                        s += StateSize;
                    }
                    while (PpmdRaw.At(mem, s) != fSym);

                    if (PpmdRaw.At(mem, s + 1) >= PpmdRaw.At(mem, s - StateSize + 1))
                    {
                        Swap6(mem, s, s - StateSize);
                        s -= StateSize;
                    }
                }

                if (PpmdRaw.At(mem, s + 1) < MaxFreq - 9)
                {
                    PpmdRaw.At(mem, s + 1) += 2;
                    PpmdRaw.SetU16(mem, c + 2, PpmdRaw.U16(mem, c + 2) + 2);
                }
            }
        }

        if (_orderFall == 0)
        {
            // Maximum order context: the found state's successor is a raw link.
            _maxContext = _minContext = CreateSuccessors();
            if (_minContext == 0)
            {
                RestartModel();
                return;
            }

            PpmdRaw.SetU32(mem, _foundState + 2, _minContext);
            return;
        }

        {
            int text = _text;
            PpmdRaw.At(mem, text++) = PpmdRaw.At(mem, _foundState);
            _text = text;
            if (text >= _unitsStart)
            {
                RestartModel();
                return;
            }

            maxSuccessor = text;
        }

        minSuccessor = PpmdRaw.U32(mem, _foundState + 2);

        if (minSuccessor != 0)
        {
            if (minSuccessor <= maxSuccessor)
            {
                int cs = CreateSuccessors();
                if (cs == 0)
                {
                    RestartModel();
                    return;
                }

                minSuccessor = cs;
            }

            if (--_orderFall == 0)
            {
                maxSuccessor = minSuccessor;
                if (_maxContext != _minContext)
                {
                    _text--;
                }
            }
        }
        else
        {
            // Only the root context holds null successors; the next context is the root again.
            PpmdRaw.SetU32(mem, _foundState + 2, maxSuccessor);
            minSuccessor = _minContext;
        }

        int mc = _minContext;
        int cc = _maxContext;

        _maxContext = _minContext = minSuccessor;
        PpmdRaw.PrefetchStats(mem, minSuccessor);

        if (cc == mc)
        {
            return;
        }

        int ns = PpmdRaw.U16(mem, mc);
        uint s0 = (uint)PpmdRaw.U16(mem, mc + 2) - (uint)ns - ((uint)PpmdRaw.At(mem, _foundState + 1) - 1);

        do
        {
            int ns1 = PpmdRaw.U16(mem, cc);
            uint sum;

            if (ns1 != 1)
            {
                if ((ns1 & 1) == 0)
                {
                    // The symbol list is full: grow it by one unit.
                    int oldNU = ns1 >> 1;
                    int i = U2I(oldNU);
                    if (i != U2I(oldNU + 1))
                    {
                        int ptr = AllocUnits(i + 1);
                        if (ptr == 0)
                        {
                            RestartModel();
                            return;
                        }

                        int oldPtr = PpmdRaw.U32(mem, cc + 4);
                        Mem12Copy(ptr, oldPtr, oldNU);
                        InsertNode(oldPtr, i);
                        PpmdRaw.SetU32(mem, cc + 4, ptr);
                    }
                }

                sum = (uint)PpmdRaw.U16(mem, cc + 2);
                sum += (uint)((2 * ns1 < ns ? 1 : 0) + (2 * ((4 * ns1 <= ns ? 1 : 0) & (sum <= (uint)(8 * ns1) ? 1 : 0))));
            }
            else
            {
                // Turn a one-symbol context into a two-symbol one.
                int s = AllocUnits(0);
                if (s == 0)
                {
                    RestartModel();
                    return;
                }

                uint freq = PpmdRaw.At(mem, cc + 3);
                PpmdRaw.At(mem, s) = PpmdRaw.At(mem, cc + 2);
                PpmdRaw.SetU32(mem, s + 2, PpmdRaw.U32(mem, cc + 4));
                PpmdRaw.SetU32(mem, cc + 4, s);
                if (freq < (MaxFreq / 4) - 1)
                {
                    freq <<= 1;
                }
                else
                {
                    freq = MaxFreq - 4;
                }

                PpmdRaw.At(mem, s + 1) = (byte)freq;
                sum = freq + (uint)_initEsc + (ns > 3 ? 1u : 0u);
            }

            {
                int s = PpmdRaw.U32(mem, cc + 4) + (ns1 * StateSize);
                uint cf = 2 * (sum + 6) * PpmdRaw.At(mem, _foundState + 1);
                uint sf = s0 + sum;
                PpmdRaw.At(mem, s) = PpmdRaw.At(mem, _foundState);
                PpmdRaw.SetU16(mem, cc, ns1 + 1);
                PpmdRaw.SetU32(mem, s + 2, maxSuccessor);

                if (cf < 6 * sf)
                {
                    cf = 1u + (cf > sf ? 1u : 0u) + (cf >= 4 * sf ? 1u : 0u);
                    sum += 3;
                }
                else
                {
                    cf = 4u + (cf >= 9 * sf ? 1u : 0u) + (cf >= 12 * sf ? 1u : 0u) + (cf >= 15 * sf ? 1u : 0u);
                    sum += cf;
                }

                PpmdRaw.SetU16(mem, cc + 2, (int)(sum & 0xFFFF));
                PpmdRaw.At(mem, s + 1) = (byte)cf;
            }

            cc = PpmdRaw.U32(mem, cc + 8);
        }
        while (cc != mc);
    }

    private void Rescale()
    {
        Span<byte> mem = _m;
        int mc = _minContext;
        int stats = PpmdRaw.U32(mem, mc + 4);
        int s = _foundState;

        // Move the found state to the front.
        if (s != stats)
        {
            byte tSym = PpmdRaw.At(mem, s);
            byte tFreq = PpmdRaw.At(mem, s + 1);
            int tSucc = PpmdRaw.U32(mem, s + 2);
            do
            {
                Copy6(mem, s, s - StateSize);
                s -= StateSize;
            }
            while (s != stats);
            PpmdRaw.At(mem, s) = tSym;
            PpmdRaw.At(mem, s + 1) = tFreq;
            PpmdRaw.SetU32(mem, s + 2, tSucc);
        }

        uint sumFreq = PpmdRaw.At(mem, s + 1);
        uint escFreq = (uint)PpmdRaw.U16(mem, mc + 2) - sumFreq;

        // Symbols may be dropped from a maximum-order context only.
        uint adder = _orderFall != 0 ? 1u : 0u;

        sumFreq = (sumFreq + 4 + adder) >> 1;
        int i = PpmdRaw.U16(mem, mc) - 1;
        PpmdRaw.At(mem, s + 1) = (byte)sumFreq;

        do
        {
            s += StateSize;
            uint freq = PpmdRaw.At(mem, s + 1);
            escFreq -= freq;
            freq = (freq + adder) >> 1;
            sumFreq += freq;
            PpmdRaw.At(mem, s + 1) = (byte)freq;
            if (freq > PpmdRaw.At(mem, s - StateSize + 1))
            {
                byte tSym = PpmdRaw.At(mem, s);
                int tSucc = PpmdRaw.U32(mem, s + 2);
                int s1 = s;
                do
                {
                    Copy6(mem, s1, s1 - StateSize);
                    s1 -= StateSize;
                }
                while (s1 != stats && freq > PpmdRaw.At(mem, s1 - StateSize + 1));

                PpmdRaw.At(mem, s1) = tSym;
                PpmdRaw.At(mem, s1 + 1) = (byte)freq;
                PpmdRaw.SetU32(mem, s1 + 2, tSucc);
            }
        }
        while (--i != 0);

        if (PpmdRaw.At(mem, s + 1) == 0)
        {
            // Remove every symbol whose frequency fell to zero.
            i = 0;
            do
            {
                i++;
                s -= StateSize;
            }
            while (PpmdRaw.At(mem, s + 1) == 0);

            escFreq += (uint)i;
            int numStats = PpmdRaw.U16(mem, mc);
            int numStatsNew = numStats - i;
            PpmdRaw.SetU16(mem, mc, numStatsNew);
            int n0 = (numStats + 1) >> 1;

            if (numStatsNew == 1)
            {
                uint freq = PpmdRaw.At(mem, stats + 1);
                do
                {
                    escFreq >>= 1;
                    freq = (freq + 1) >> 1;
                }
                while (escFreq > 1);

                s = mc + 2;
                Copy6(mem, s, stats);
                PpmdRaw.At(mem, s + 1) = (byte)freq;
                _foundState = s;
                InsertNode(stats, U2I(n0));
                return;
            }

            int n1 = (numStatsNew + 1) >> 1;
            if (n0 != n1)
            {
                int i0 = U2I(n0);
                int i1 = U2I(n1);
                if (i0 != i1)
                {
                    if (_freeList[i1] != 0)
                    {
                        int ptr = RemoveNode(i1);
                        PpmdRaw.SetU32(mem, mc + 4, ptr);
                        Mem12Copy(ptr, stats, n1);
                        InsertNode(stats, i0);
                    }
                    else
                    {
                        SplitBlock(stats, i0, i1);
                    }
                }
            }
        }

        PpmdRaw.SetU16(mem, mc + 2, (int)((sumFreq + escFreq - (escFreq >> 1)) & 0xFFFF));
        _foundState = PpmdRaw.U32(mem, mc + 4);
    }

    // Picks the secondary-estimation slot for an escape from the current context and returns its index;
    // escFreq receives the escape frequency to code.
    private int MakeEscFreq(int numMasked, out uint escFreq)
    {
        Span<byte> mem = _m;
        int mc = _minContext;
        uint numStats = (uint)PpmdRaw.U16(mem, mc);
        if (numStats != 256)
        {
            uint nonMasked = numStats - (uint)numMasked;
            int see = (s_ns2Indx[(int)nonMasked - 1] * 16)
                + (nonMasked < (uint)PpmdRaw.U16(mem, PpmdRaw.U32(mem, mc + 8)) - numStats ? 1 : 0)
                + (2 * (PpmdRaw.U16(mem, mc + 2) < 11 * numStats ? 1 : 0))
                + (4 * ((uint)numMasked > nonMasked ? 1 : 0))
                + _hiBitsFlag;
            if (_journaling)
            {
                JournalSee(see);
            }

            uint summ = _seeSumm[see];
            uint r = summ >> _seeShift[see];
            _seeSumm[see] = (ushort)(summ - r);
            escFreq = r + (r == 0 ? 1u : 0u);
            return see;
        }

        escFreq = 1;
        return 25 * 16;
    }

    private void SeeUpdate(int see)
    {
        if (_seeShift[see] < PpmdTables.PeriodBits && --_seeCount[see] == 0)
        {
            _seeSumm[see] = (ushort)(_seeSumm[see] << 1);
            _seeCount[see] = (byte)(3 << _seeShift[see]++);
        }
    }

    private void NextContext()
    {
        Span<byte> mem = _m;
        int c = PpmdRaw.U32(mem, _foundState + 2);
        if (_orderFall == 0 && c > _text)
        {
            _maxContext = _minContext = c;
            PpmdRaw.PrefetchStats(mem, c);
        }
        else
        {
            UpdateModel();
        }
    }

    private void Update1()
    {
        Span<byte> mem = _m;
        int s = _foundState;
        int mc = _minContext;
        uint freq = (uint)PpmdRaw.At(mem, s + 1) + 4;
        PpmdRaw.SetU16(mem, mc + 2, PpmdRaw.U16(mem, mc + 2) + 4);
        PpmdRaw.At(mem, s + 1) = (byte)freq;
        if (freq > PpmdRaw.At(mem, s - StateSize + 1))
        {
            Swap6(mem, s, s - StateSize);
            _foundState = s -= StateSize;
            if (freq > MaxFreq)
            {
                Rescale();
            }
        }

        NextContext();
    }

    private void Update1_0()
    {
        Span<byte> mem = _m;
        int s = _foundState;
        int mc = _minContext;
        uint freq = PpmdRaw.At(mem, s + 1);
        uint summFreq = (uint)PpmdRaw.U16(mem, mc + 2);
        _prevSuccess = 2 * freq > summFreq ? 1 : 0;
        _runLength += _prevSuccess;
        PpmdRaw.SetU16(mem, mc + 2, (int)((summFreq + 4) & 0xFFFF));
        freq += 4;
        PpmdRaw.At(mem, s + 1) = (byte)freq;
        if (freq > MaxFreq)
        {
            Rescale();
        }

        NextContext();
    }

    private void Update2()
    {
        Span<byte> mem = _m;
        int s = _foundState;
        int mc = _minContext;
        uint freq = (uint)PpmdRaw.At(mem, s + 1) + 4;
        _runLength = _initRl;
        PpmdRaw.SetU16(mem, mc + 2, PpmdRaw.U16(mem, mc + 2) + 4);
        PpmdRaw.At(mem, s + 1) = (byte)freq;
        if (freq > MaxFreq)
        {
            Rescale();
        }

        UpdateModel();
    }

    private static int HiBitsFlag3(int sym) => ((sym + 0xC0) >> (8 - 3)) & (1 << 3);

    private static int HiBitsFlag4(int sym) => ((sym + 0xC0) >> (8 - 4)) & (1 << 4);

    // Index of the binary-context probability for the current one-symbol context; also sets the high-bits flag.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private int BinSummIndex()
    {
        Span<byte> mem = _m;
        int mc = _minContext;
        int one = mc + 2;
        _hiBitsFlag = HiBitsFlag3(PpmdRaw.At(mem, _foundState));
        return ((PpmdRaw.At(mem, one + 1) - 1) * 64)
            + _prevSuccess + ((_runLength >> 26) & 0x20)
            + s_ns2BsIndx[PpmdRaw.U16(mem, PpmdRaw.U32(mem, mc + 8)) - 1]
            + HiBitsFlag4(PpmdRaw.At(mem, one))
            + _hiBitsFlag;
    }
}
