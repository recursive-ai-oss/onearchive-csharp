// Ported from C/Ppmd8.c (the model) of the LZMA SDK / 7-Zip 26.05 (Igor Pavlov).
//
// Original notices, quoted exactly:
//   Ppmd8.c: "Ppmd8.c -- PPMdI codec
//             : Igor Pavlov : Public domain
//             This code is based on PPMd var.I (2002): Dmitry Shkarin : Public domain"
//   Ppmd.h:  "Ppmd.h -- PPMD codec common code
//             2023-03-05 : Igor Pavlov : Public domain
//             This code is based on PPMd var.H (2001): Dmitry Shkarin : Public domain"
//
// Same layout idea as PpmdHModel: one byte array, 12-byte units, 32-bit offsets, 0 as null. A context's symbol
// count is stored minus one in a byte, so 256 symbols fit. Variant I adds the cut-off restore method.

using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>How the variant I model recovers when its memory is full.</summary>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum PpmdRestoreMethod
{
    /// <summary>Start over with an empty model. Zip method 98 value 0; the default.</summary>
    Restart = 0,

    /// <summary>Cut the model down to the more useful contexts and continue. Zip method 98 value 1; slower.</summary>
    CutOff = 1,
}

/// <summary>
/// The PPMd variant I revision 1 model (zip method 98) together with its carry-less range coder.
/// This is the raw tier: <see cref="PpmdIEncoder"/> and <see cref="PpmdIDecoder"/> drive it over spans.
/// </summary>
/// <remarks>The freeze restore method of the original is not supported. The instance is not thread-safe.</remarks>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed partial class PpmdIModel : IPpmdModel
{
    /// <summary>The smallest model order. The format allows 2 to 16.</summary>
    public const int MinOrder = 2;

    /// <summary>The largest model order.</summary>
    public const int MaxOrder = 16;

    /// <summary>
    /// The smallest model memory size in bytes (64 KiB). The format itself has no minimum, but the cut-off restore method loops forever
    /// when the root context alone (about 1.5 KiB) takes more than three quarters of the memory, so tiny models are refused.
    /// </summary>
    public const int MinMemorySize = 1 << 16;

    /// <summary>The largest model memory size this implementation allocates (2 GiB minus 256 bytes).</summary>
    public const int MaxMemorySize = 0x7FFFFF00;

    private const int MaxFreq = PpmdTables.MaxFreq;
    private const int UnitSize = PpmdTables.UnitSize;
    private const int NumIndexes = PpmdTables.NumIndexes;
    private const int StateSize = 6;
    private const uint EmptyNode = 0xFFFFFFFF;
    private const int FlagRescaled = 1 << 2;
    private const int FlagPrevHigh = 1 << 4;

    private static readonly byte[] s_ns2BsIndx = BuildNs2BsIndx();
    private static readonly byte[] s_ns2Indx = BuildNs2Indx();

    private readonly byte[] _m;
    private readonly int _size;
    private readonly int _alignOffset;
    private readonly int _restoreMethod;
    private readonly int[] _freeList = new int[NumIndexes];
    private readonly int[] _stamps = new int[NumIndexes];
    private readonly int[] _ps = new int[MaxOrder + 1];
    private readonly int[] _expandCount = new int[NumIndexes];
    private readonly byte[] _mask = new byte[256];
    private readonly ushort[] _seeSumm = new ushort[(24 * 32) + 1];
    private readonly byte[] _seeShift = new byte[(24 * 32) + 1];
    private readonly byte[] _seeCount = new byte[(24 * 32) + 1];
    private readonly ushort[] _binSumm = new ushort[25 * 64];

    private int _minContext;
    private int _maxContext;
    private int _foundState;
    private int _orderFall;
    private int _initEsc;
    private int _prevSuccess;
    private int _runLength;
    private int _initRl;
    private int _glueCount;
    private int _loUnit;
    private int _hiUnit;
    private int _text;
    private int _unitsStart;

    /// <summary>Creates a model.</summary>
    /// <param name="order">The model order, 2 to 16.</param>
    /// <param name="memorySize">The model memory in bytes, <see cref="MinMemorySize"/> to <see cref="MaxMemorySize"/>.</param>
    /// <param name="restoreMethod">What to do when memory runs out.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its range.</exception>
    public PpmdIModel(int order, int memorySize, PpmdRestoreMethod restoreMethod = PpmdRestoreMethod.Restart)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(order, MinOrder);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(order, MaxOrder);
        ArgumentOutOfRangeException.ThrowIfLessThan(memorySize, MinMemorySize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(memorySize, MaxMemorySize);
        if (restoreMethod is not (PpmdRestoreMethod.Restart or PpmdRestoreMethod.CutOff))
        {
            throw new ArgumentOutOfRangeException(nameof(restoreMethod), restoreMethod, "Only the restart and cut-off restore methods are supported.");
        }

        Order = order;
        MemorySize = memorySize;
        RestoreMethod = restoreMethod;
        _restoreMethod = (int)restoreMethod;
        _size = memorySize;
        _alignOffset = (4 - memorySize) & 3;
        _m = new byte[_alignOffset + memorySize];
        RestartModel();
    }

    /// <summary>Gets the model order.</summary>
    public int Order { get; }

    /// <summary>Gets the model memory size in bytes.</summary>
    public int MemorySize { get; }

    /// <summary>Gets the restore method.</summary>
    public PpmdRestoreMethod RestoreMethod { get; }

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
        var t = new byte[260];
        int i;
        for (i = 0; i < 5; i++)
        {
            t[i] = (byte)i;
        }

        int m = i;
        int k = 1;
        for (; i < 260; i++)
        {
            t[i] = (byte)m;
            if (--k == 0)
            {
                k = ++m - 4;
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
    // A free block is a node: Stamp u32 @0 (0xFFFFFFFF = free), Next u32 @4, NU u32 @8.

    private void InsertNode(int node, int indx)
    {
        Span<byte> mem = _m;
        PpmdRaw.SetU32(mem, node, -1);
        PpmdRaw.SetU32(mem, node + 4, _freeList[indx]);
        PpmdRaw.SetU32(mem, node + 8, I2U(indx));
        _freeList[indx] = node;
        _stamps[indx]++;
    }

    private int RemoveNode(int indx)
    {
        Span<byte> mem = _m;
        int node = _freeList[indx];
        _freeList[indx] = PpmdRaw.U32(mem, node + 4);
        _stamps[indx]--;
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

    private bool IsEmptyNode(int node) => PpmdRaw.U32(_m, node) == -1;

    private void GlueFreeBlocks()
    {
        Span<byte> mem = _m;
        _glueCount = 1 << 13;
        Array.Clear(_stamps);

        // A guard (stamp 0) at LoUnit stops a block from gluing into the gap between LoUnit and HiUnit.
        if (_loUnit != _hiUnit)
        {
            PpmdRaw.SetU32(mem, _loUnit, 0);
        }

        int n = 0;
        int prev = -1; // -1: n itself; otherwise the address of a node's Next field
        for (int i = 0; i < NumIndexes; i++)
        {
            int next = _freeList[i];
            _freeList[i] = 0;
            while (next != 0)
            {
                int node = next;
                int nu = PpmdRaw.U32(mem, node + 8);
                if (prev < 0)
                {
                    n = next;
                }
                else
                {
                    PpmdRaw.SetU32(mem, prev, next);
                }

                next = PpmdRaw.U32(mem, node + 4);
                if (nu != 0)
                {
                    prev = node + 4;
                    int node2;
                    while (IsEmptyNode(node2 = node + (nu * UnitSize)))
                    {
                        nu += PpmdRaw.U32(mem, node2 + 8);
                        PpmdRaw.SetU32(mem, node2 + 8, 0);
                        PpmdRaw.SetU32(mem, node + 8, nu);
                    }
                }
            }
        }

        if (prev < 0)
        {
            n = 0;
        }
        else
        {
            PpmdRaw.SetU32(mem, prev, 0);
        }

        while (n != 0)
        {
            int node = n;
            int nu = PpmdRaw.U32(mem, node + 8);
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

    private int ShrinkUnits(int oldPtr, int oldNU, int newNU)
    {
        int i0 = U2I(oldNU);
        int i1 = U2I(newNU);
        if (i0 == i1)
        {
            return oldPtr;
        }

        if (_freeList[i1] != 0)
        {
            int ptr = RemoveNode(i1);
            Mem12Copy(ptr, oldPtr, newNU);
            InsertNode(oldPtr, i0);
            return ptr;
        }

        SplitBlock(oldPtr, i0, i1);
        return oldPtr;
    }

    private void FreeUnits(int ptr, int nu) => InsertNode(ptr, U2I(nu));

    private void SpecialFreeUnit(int ptr)
    {
        if (ptr != _unitsStart)
        {
            InsertNode(ptr, 0);
        }
        else
        {
            _unitsStart += UnitSize;
        }
    }

    private void ExpandTextArea()
    {
        Span<byte> mem = _m;
        int[] count = _expandCount;
        Array.Clear(count);
        if (_loUnit != _hiUnit)
        {
            PpmdRaw.SetU32(mem, _loUnit, 0);
        }

        int node = _unitsStart;
        while (IsEmptyNode(node))
        {
            int nu = PpmdRaw.U32(mem, node + 8);
            PpmdRaw.SetU32(mem, node, 0);
            count[U2I(nu)]++;
            node += nu * UnitSize;
        }

        _unitsStart = node;

        for (int i = 0; i < NumIndexes; i++)
        {
            int cnt = count[i];
            if (cnt == 0)
            {
                continue;
            }

            int prev = -1; // -1: _freeList[i]; otherwise the address of a node's Next field
            int n = _freeList[i];
            _stamps[i] -= cnt;
            for (;;)
            {
                int nd = n;
                n = PpmdRaw.U32(mem, nd + 4);
                if (PpmdRaw.U32(mem, nd) != 0)
                {
                    prev = nd + 4;
                    continue;
                }

                if (prev < 0)
                {
                    _freeList[i] = n;
                }
                else
                {
                    PpmdRaw.SetU32(mem, prev, n);
                }

                if (--cnt == 0)
                {
                    break;
                }
            }
        }
    }

    // ---- model ----

    private static int HiBitsPrepare(int sym) => sym + 0xC0;

    private static int HiBitsConvert3(int flags) => (flags >> (8 - 3)) & (1 << 3);

    private static int HiBitsConvert4(int flags) => (flags >> (8 - 4)) & (1 << 4);

    private static int HiBitsFlag3(int sym) => HiBitsConvert3(HiBitsPrepare(sym));

    private static int HiBitsFlag4(int sym) => HiBitsConvert4(HiBitsPrepare(sym));

    private void RestartModel()
    {
        Span<byte> mem = _m;
        Array.Clear(_freeList);
        Array.Clear(_stamps);
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
        PpmdRaw.At(mem, mc + 1) = 0;
        PpmdRaw.At(mem, mc) = 255;
        PpmdRaw.SetU16(mem, mc + 2, 256 + 1);
        PpmdRaw.SetU32(mem, mc + 4, s);
        PpmdRaw.SetU32(mem, mc + 8, 0);

        for (int i = 0; i < 256; i++, s += StateSize)
        {
            PpmdRaw.At(mem, s) = (byte)i;
            PpmdRaw.At(mem, s + 1) = 1;
            PpmdRaw.SetU32(mem, s + 2, 0);
        }

        int ix = 0;
        for (int m = 0; m < 25; m++)
        {
            while (s_ns2Indx[ix] == m)
            {
                ix++;
            }

            for (int k = 0; k < 8; k++)
            {
                ushort val = (ushort)(PpmdTables.BinScale - (PpmdTables.InitBinEsc[k] / (ix + 1)));
                for (int r = 0; r < 64; r += 8)
                {
                    _binSumm[(m * 64) + k + r] = val;
                }
            }
        }

        ix = 0;
        for (int m = 0; m < 24; m++)
        {
            while (s_ns2Indx[ix + 3] == m + 3)
            {
                ix++;
            }

            int summ = ((2 * ix) + 5) << (PpmdTables.PeriodBits - 4);
            for (int k = 0; k < 32; k++)
            {
                int idx = (m * 32) + k;
                _seeSumm[idx] = (ushort)summ;
                _seeShift[idx] = PpmdTables.PeriodBits - 4;
                _seeCount[idx] = 7;
            }
        }

        int dummy = 24 * 32;
        _seeSumm[dummy] = 0;
        _seeShift[dummy] = PpmdTables.PeriodBits;
        _seeCount[dummy] = 64;
    }

    // Re-scales a context's frequencies by 2^scale after symbols were removed, and recomputes its flags.
    private void Refresh(int ctx, int oldNU, int scale)
    {
        Span<byte> mem = _m;
        int i = PpmdRaw.At(mem, ctx);
        int s = ShrinkUnits(PpmdRaw.U32(mem, ctx + 4), oldNU, (i + 2) >> 1);
        PpmdRaw.SetU32(mem, ctx + 4, s);

        scale |= PpmdRaw.U16(mem, ctx + 2) >= (1 << 15) ? 1 : 0;

        int flags = HiBitsPrepare(PpmdRaw.At(mem, s));
        uint freq = PpmdRaw.At(mem, s + 1);
        uint escFreq = (uint)PpmdRaw.U16(mem, ctx + 2) - freq;
        freq = (freq + (uint)scale) >> scale;
        uint sumFreq = freq;
        PpmdRaw.At(mem, s + 1) = (byte)freq;

        do
        {
            s += StateSize;
            freq = PpmdRaw.At(mem, s + 1);
            escFreq -= freq;
            freq = (freq + (uint)scale) >> scale;
            sumFreq += freq;
            PpmdRaw.At(mem, s + 1) = (byte)freq;
            flags |= HiBitsPrepare(PpmdRaw.At(mem, s));
        }
        while (--i != 0);

        PpmdRaw.SetU16(mem, ctx + 2, (int)((sumFreq + ((escFreq + (uint)scale) >> scale)) & 0xFFFF));
        PpmdRaw.At(mem, ctx + 1) = (byte)((PpmdRaw.At(mem, ctx + 1) & (FlagPrevHigh + (FlagRescaled * scale))) + HiBitsConvert3(flags));
    }

    // Cuts the model down: turns successors at the maximum order into null links, drops raw and null successors
    // that are not at order 0, and frees contexts that are left with no successors.
    private int CutOff(int ctx, int order)
    {
        Span<byte> mem = _m;
        int ns = PpmdRaw.At(mem, ctx);
        if (ns == 0)
        {
            int s1 = ctx + 2;
            int successor = PpmdRaw.U32(mem, s1 + 2);
            if (successor >= _unitsStart)
            {
                successor = order < Order ? CutOff(successor, order + 1) : 0;
                PpmdRaw.SetU32(mem, s1 + 2, successor);
                if (successor != 0 || order <= 9)
                {
                    return ctx;
                }
            }

            SpecialFreeUnit(ctx);
            return 0;
        }

        int nu = (ns + 2) >> 1;
        int stats = PpmdRaw.U32(mem, ctx + 4);
        {
            int indx = U2I(nu);
            if ((uint)(stats - _unitsStart) <= (1 << 14) && (uint)stats <= (uint)_freeList[indx])
            {
                int ptr = RemoveNode(indx);
                PpmdRaw.SetU32(mem, ctx + 4, ptr);
                Mem12Copy(ptr, stats, nu);
                if (stats != _unitsStart)
                {
                    InsertNode(stats, indx);
                }
                else
                {
                    _unitsStart += U2B(I2U(indx));
                }

                stats = ptr;
            }
        }

        {
            int s = stats + (ns * StateSize);
            do
            {
                int successor = PpmdRaw.U32(mem, s + 2);
                if (successor < _unitsStart)
                {
                    int s2 = stats + (ns-- * StateSize);
                    if (order != 0)
                    {
                        if (s != s2)
                        {
                            Copy6(mem, s, s2);
                        }
                    }
                    else
                    {
                        Swap6(mem, s, s2);
                        PpmdRaw.SetU32(mem, s2 + 2, 0);
                    }
                }
                else
                {
                    PpmdRaw.SetU32(mem, s + 2, order < Order ? CutOff(successor, order + 1) : 0);
                }
            }
            while ((s -= StateSize) >= stats);
        }

        if (ns != PpmdRaw.At(mem, ctx) && order != 0)
        {
            if (ns < 0)
            {
                FreeUnits(stats, nu);
                SpecialFreeUnit(ctx);
                return 0;
            }

            PpmdRaw.At(mem, ctx) = (byte)ns;
            if (ns == 0)
            {
                byte sym = PpmdRaw.At(mem, stats);
                PpmdRaw.At(mem, ctx + 1) = (byte)((PpmdRaw.At(mem, ctx + 1) & FlagPrevHigh) + HiBitsFlag3(sym));
                PpmdRaw.At(mem, ctx + 2) = sym;
                PpmdRaw.At(mem, ctx + 3) = (byte)((PpmdRaw.At(mem, stats + 1) + 11) >> 3);
                PpmdRaw.SetU32(mem, ctx + 4, PpmdRaw.U32(mem, stats + 2));
                FreeUnits(stats, nu);
            }
            else
            {
                Refresh(ctx, nu, PpmdRaw.U16(mem, ctx + 2) > 16 * ns ? 1 : 0);
            }
        }

        return ctx;
    }

    private uint GetUsedMemory()
    {
        uint v = 0;
        for (int i = 0; i < NumIndexes; i++)
        {
            v += (uint)_stamps[i] * (uint)I2U(i);
        }

        return (uint)_size - (uint)(_hiUnit - _loUnit) - (uint)(_unitsStart - _text) - (v * UnitSize);
    }

    // Undoes the partial update after an allocation failure, then either restarts the model or cuts it down.
    private void RestoreModel(int ctxError)
    {
        Span<byte> mem = _m;
        _text = _alignOffset;

        int c;
        for (c = _maxContext; c != ctxError; c = PpmdRaw.U32(mem, c + 8))
        {
            if (--PpmdRaw.At(mem, c) == 0)
            {
                int s = PpmdRaw.U32(mem, c + 4);
                PpmdRaw.At(mem, c + 1) = (byte)((PpmdRaw.At(mem, c + 1) & FlagPrevHigh) + HiBitsFlag3(PpmdRaw.At(mem, s)));
                PpmdRaw.At(mem, c + 2) = PpmdRaw.At(mem, s);
                PpmdRaw.At(mem, c + 3) = (byte)((PpmdRaw.At(mem, s + 1) + 11) >> 3);
                PpmdRaw.SetU32(mem, c + 4, PpmdRaw.U32(mem, s + 2));
                SpecialFreeUnit(s);
            }
            else
            {
                Refresh(c, (PpmdRaw.At(mem, c) + 3) >> 1, 0);
            }
        }

        for (; c != _minContext; c = PpmdRaw.U32(mem, c + 8))
        {
            if (PpmdRaw.At(mem, c) == 0)
            {
                PpmdRaw.At(mem, c + 3) = (byte)((PpmdRaw.At(mem, c + 3) + 1) >> 1);
            }
            else
            {
                int summ = (PpmdRaw.U16(mem, c + 2) + 4) & 0xFFFF;
                PpmdRaw.SetU16(mem, c + 2, summ);
                if (summ > 128 + (4 * PpmdRaw.At(mem, c)))
                {
                    Refresh(c, (PpmdRaw.At(mem, c) + 2) >> 1, 1);
                }
            }
        }

        if (_restoreMethod == (int)PpmdRestoreMethod.Restart || GetUsedMemory() < (uint)(_size >> 1))
        {
            RestartModel();
        }
        else
        {
            while (PpmdRaw.U32(mem, _maxContext + 8) != 0)
            {
                _maxContext = PpmdRaw.U32(mem, _maxContext + 8);
            }

            do
            {
                CutOff(_maxContext, 0);
                ExpandTextArea();
            }
            while (GetUsedMemory() > 3 * (uint)(_size >> 2));

            _glueCount = 0;
            _orderFall = Order;
        }

        _minContext = _maxContext;
    }

    private int CreateSuccessors(bool skip, int s1, int c)
    {
        Span<byte> mem = _m;
        int upBranch = PpmdRaw.U32(mem, _foundState + 2);
        int numPs = 0;

        if (!skip)
        {
            _ps[numPs++] = _foundState;
        }

        while (PpmdRaw.U32(mem, c + 8) != 0)
        {
            c = PpmdRaw.U32(mem, c + 8);
            int s;
            if (s1 != 0)
            {
                s = s1;
                s1 = 0;
            }
            else if (PpmdRaw.At(mem, c) != 0)
            {
                byte sym = PpmdRaw.At(mem, _foundState);
                s = PpmdRaw.U32(mem, c + 4);
                while (PpmdRaw.At(mem, s) != sym)
                {
                    s += StateSize;
                }

                if (PpmdRaw.At(mem, s + 1) < MaxFreq - 9)
                {
                    PpmdRaw.At(mem, s + 1)++;
                    PpmdRaw.SetU16(mem, c + 2, PpmdRaw.U16(mem, c + 2) + 1);
                }
            }
            else
            {
                uint temp = PpmdRaw.At(mem, PpmdRaw.U32(mem, c + 8)) == 0 ? 1u : 0u;
                s = c + 2;
                PpmdRaw.At(mem, s + 1) = (byte)(PpmdRaw.At(mem, s + 1) + (temp & (PpmdRaw.At(mem, s + 1) < 24 ? 1u : 0u)));
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
        byte flags = (byte)(HiBitsFlag4(PpmdRaw.At(mem, _foundState)) + HiBitsFlag3(newSym));

        byte newFreq;
        if (PpmdRaw.At(mem, c) == 0)
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
            uint s0 = (uint)PpmdRaw.U16(mem, c + 2) - PpmdRaw.At(mem, c) - cf;
            newFreq = (byte)(1 + ((2 * cf <= s0) ? (5 * cf > s0 ? 1u : 0u) : (cf + (2 * s0) - 3) / s0));
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

            PpmdRaw.At(mem, c1 + 1) = flags;
            PpmdRaw.At(mem, c1) = 0;
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

    private int ReduceOrder(int s1, int c)
    {
        Span<byte> mem = _m;
        int s = 0;
        int c1 = c;
        int upBranch = _text;

        PpmdRaw.SetU32(mem, _foundState + 2, upBranch);
        _orderFall++;

        for (;;)
        {
            if (s1 != 0)
            {
                c = PpmdRaw.U32(mem, c + 8);
                s = s1;
                s1 = 0;
            }
            else
            {
                if (PpmdRaw.U32(mem, c + 8) == 0)
                {
                    return c;
                }

                c = PpmdRaw.U32(mem, c + 8);
                if (PpmdRaw.At(mem, c) != 0)
                {
                    s = PpmdRaw.U32(mem, c + 4);
                    if (PpmdRaw.At(mem, s) != PpmdRaw.At(mem, _foundState))
                    {
                        do
                        {
                            s += StateSize;
                        }
                        while (PpmdRaw.At(mem, s) != PpmdRaw.At(mem, _foundState));
                    }

                    if (PpmdRaw.At(mem, s + 1) < MaxFreq - 9)
                    {
                        PpmdRaw.At(mem, s + 1) += 2;
                        PpmdRaw.SetU16(mem, c + 2, PpmdRaw.U16(mem, c + 2) + 2);
                    }
                }
                else
                {
                    s = c + 2;
                    PpmdRaw.At(mem, s + 1) = (byte)(PpmdRaw.At(mem, s + 1) + (PpmdRaw.At(mem, s + 1) < 32 ? 1 : 0));
                }
            }

            if (PpmdRaw.U32(mem, s + 2) != 0)
            {
                break;
            }

            PpmdRaw.SetU32(mem, s + 2, upBranch);
            _orderFall++;
        }

        if (PpmdRaw.U32(mem, s + 2) <= upBranch)
        {
            int s2 = _foundState;
            _foundState = s;
            int successor = CreateSuccessors(false, 0, c);
            PpmdRaw.SetU32(mem, s + 2, successor);
            _foundState = s2;
        }

        {
            int successor = PpmdRaw.U32(mem, s + 2);
            if (_orderFall == 1 && c1 == _maxContext)
            {
                PpmdRaw.SetU32(mem, _foundState + 2, successor);
                _text--;
            }

            return successor;
        }
    }

    private void UpdateModel()
    {
        Span<byte> mem = _m;
        int minSuccessor = PpmdRaw.U32(mem, _foundState + 2);
        int fFreq = PpmdRaw.At(mem, _foundState + 1);
        byte fSymbol = PpmdRaw.At(mem, _foundState);
        int maxSuccessor;
        int c;
        int s = 0;

        if (fFreq < MaxFreq / 4 && PpmdRaw.U32(mem, _minContext + 8) != 0)
        {
            c = PpmdRaw.U32(mem, _minContext + 8);
            if (PpmdRaw.At(mem, c) == 0)
            {
                s = c + 2;
                if (PpmdRaw.At(mem, s + 1) < 32)
                {
                    PpmdRaw.At(mem, s + 1)++;
                }
            }
            else
            {
                s = PpmdRaw.U32(mem, c + 4);
                if (PpmdRaw.At(mem, s) != fSymbol)
                {
                    do
                    {
                        s += StateSize;
                    }
                    while (PpmdRaw.At(mem, s) != fSymbol);

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

        c = _maxContext;
        if (_orderFall == 0 && minSuccessor != 0)
        {
            int cs = CreateSuccessors(true, s, _minContext);
            if (cs == 0)
            {
                PpmdRaw.SetU32(mem, _foundState + 2, 0);
                RestoreModel(c);
                return;
            }

            PpmdRaw.SetU32(mem, _foundState + 2, cs);
            _minContext = _maxContext = cs;
            return;
        }

        {
            int text = _text;
            PpmdRaw.At(mem, text++) = PpmdRaw.At(mem, _foundState);
            _text = text;
            if (text >= _unitsStart)
            {
                RestoreModel(c);
                return;
            }

            maxSuccessor = text;
        }

        if (minSuccessor == 0)
        {
            int cs = ReduceOrder(s, _minContext);
            if (cs == 0)
            {
                RestoreModel(c);
                return;
            }

            minSuccessor = cs;
        }
        else if (minSuccessor < _unitsStart)
        {
            int cs = CreateSuccessors(false, s, _minContext);
            if (cs == 0)
            {
                RestoreModel(c);
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

        int flag = HiBitsFlag3(fSymbol);
        int ns = PpmdRaw.At(mem, _minContext);
        uint s0 = (uint)PpmdRaw.U16(mem, _minContext + 2) - (uint)ns - (uint)fFreq;

        for (; c != _minContext; c = PpmdRaw.U32(mem, c + 8))
        {
            int ns1 = PpmdRaw.At(mem, c);
            uint sum;

            if (ns1 != 0)
            {
                if ((ns1 & 1) != 0)
                {
                    int oldNU = (ns1 + 1) >> 1;
                    int i = U2I(oldNU);
                    if (i != U2I(oldNU + 1))
                    {
                        int ptr = AllocUnits(i + 1);
                        if (ptr == 0)
                        {
                            RestoreModel(c);
                            return;
                        }

                        int oldPtr = PpmdRaw.U32(mem, c + 4);
                        Mem12Copy(ptr, oldPtr, oldNU);
                        InsertNode(oldPtr, i);
                        PpmdRaw.SetU32(mem, c + 4, ptr);
                    }
                }

                sum = (uint)PpmdRaw.U16(mem, c + 2);
                sum += (3 * ns1) + 1 < ns ? 1u : 0u;
            }
            else
            {
                int ns2 = AllocUnits(0);
                if (ns2 == 0)
                {
                    RestoreModel(c);
                    return;
                }

                uint freq = PpmdRaw.At(mem, c + 3);
                PpmdRaw.At(mem, ns2) = PpmdRaw.At(mem, c + 2);
                PpmdRaw.SetU32(mem, ns2 + 2, PpmdRaw.U32(mem, c + 4));
                PpmdRaw.SetU32(mem, c + 4, ns2);
                if (freq < (MaxFreq / 4) - 1)
                {
                    freq <<= 1;
                }
                else
                {
                    freq = MaxFreq - 4;
                }

                PpmdRaw.At(mem, ns2 + 1) = (byte)freq;
                sum = freq + (uint)_initEsc + (ns > 2 ? 1u : 0u);
            }

            {
                int st = PpmdRaw.U32(mem, c + 4) + ((ns1 + 1) * StateSize);
                uint cf = 2 * (sum + 6) * (uint)fFreq;
                uint sf = s0 + sum;
                PpmdRaw.At(mem, st) = fSymbol;
                PpmdRaw.At(mem, c) = (byte)(ns1 + 1);
                PpmdRaw.SetU32(mem, st + 2, maxSuccessor);
                PpmdRaw.At(mem, c + 1) |= (byte)flag;
                if (cf < 6 * sf)
                {
                    cf = 1u + (cf > sf ? 1u : 0u) + (cf >= 4 * sf ? 1u : 0u);
                    sum += 4;
                }
                else
                {
                    cf = 4u + (cf > 9 * sf ? 1u : 0u) + (cf > 12 * sf ? 1u : 0u) + (cf > 15 * sf ? 1u : 0u);
                    sum += cf;
                }

                PpmdRaw.SetU16(mem, c + 2, (int)(sum & 0xFFFF));
                PpmdRaw.At(mem, st + 1) = (byte)cf;
            }
        }

        _maxContext = _minContext = minSuccessor;
        PpmdRaw.PrefetchStats(mem, minSuccessor);
    }

    private void Rescale()
    {
        Span<byte> mem = _m;
        int mc = _minContext;
        int stats = PpmdRaw.U32(mem, mc + 4);
        int s = _foundState;

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
        uint adder = _orderFall != 0 ? 1u : 0u;

        sumFreq = (sumFreq + 4 + adder) >> 1;
        int i = PpmdRaw.At(mem, mc);
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
            i = 0;
            do
            {
                i++;
                s -= StateSize;
            }
            while (PpmdRaw.At(mem, s + 1) == 0);

            escFreq += (uint)i;
            int numStats = PpmdRaw.At(mem, mc);
            int numStatsNew = numStats - i;
            PpmdRaw.At(mem, mc) = (byte)numStatsNew;
            int n0 = (numStats + 2) >> 1;

            if (numStatsNew == 0)
            {
                uint freq = ((2 * (uint)PpmdRaw.At(mem, stats + 1)) + escFreq - 1) / escFreq;
                if (freq > MaxFreq / 3)
                {
                    freq = MaxFreq / 3;
                }

                PpmdRaw.At(mem, mc + 1) = (byte)((PpmdRaw.At(mem, mc + 1) & FlagPrevHigh) + HiBitsFlag3(PpmdRaw.At(mem, stats)));
                s = mc + 2;
                Copy6(mem, s, stats);
                PpmdRaw.At(mem, s + 1) = (byte)freq;
                _foundState = s;
                InsertNode(stats, U2I(n0));
                return;
            }

            int n1 = (numStatsNew + 2) >> 1;
            if (n0 != n1)
            {
                PpmdRaw.SetU32(mem, mc + 4, ShrinkUnits(stats, n0, n1));
            }
        }

        PpmdRaw.SetU16(mem, mc + 2, (int)((sumFreq + escFreq - (escFreq >> 1)) & 0xFFFF));
        PpmdRaw.At(mem, mc + 1) |= FlagRescaled;
        _foundState = PpmdRaw.U32(mem, mc + 4);
    }

    private int MakeEscFreq(int numMasked, out uint escFreq)
    {
        Span<byte> mem = _m;
        int mc = _minContext;
        uint numStats = PpmdRaw.At(mem, mc);
        if (numStats != 0xFF)
        {
            int see = ((s_ns2Indx[numStats + 2] - 3) * 32)
                + (PpmdRaw.U16(mem, mc + 2) > 11 * (numStats + 1) ? 1 : 0)
                + (2 * (2 * numStats < PpmdRaw.At(mem, PpmdRaw.U32(mem, mc + 8)) + (uint)numMasked ? 1 : 0))
                + PpmdRaw.At(mem, mc + 1);
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
        return 24 * 32;
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
        if (_orderFall == 0 && c >= _unitsStart)
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
        _prevSuccess = 2 * freq >= summFreq ? 1 : 0;
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

    private int BinSummIndex()
    {
        Span<byte> mem = _m;
        int mc = _minContext;
        int one = mc + 2;
        return (s_ns2Indx[PpmdRaw.At(mem, one + 1) - 1] * 64)
            + _prevSuccess + ((_runLength >> 26) & 0x20)
            + s_ns2BsIndx[PpmdRaw.At(mem, PpmdRaw.U32(mem, mc + 8))]
            + PpmdRaw.At(mem, mc + 1);
    }
}
