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

using System.Buffers.Binary;
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

    private int U16(int a) => BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(_m, a, 2));

    private void SetU16(int a, int v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(_m, a, 2), (ushort)v);
    }

    private int U32(int a) => BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(_m, a, 4));

    private void SetU32(int a, int v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(_m, a, 4), v);
    }

    // A state is 6 bytes; both parts are read before either is written, so overlapping ranges copy correctly.
    private void Copy6(int dst, int src)
    {
        int lo = U32(src);
        int hi = U16(src + 4);
        SetU32(dst, lo);
        SetU16(dst + 4, hi);
    }

    private void Swap6(int a, int b)
    {
        for (int i = 0; i < StateSize; i++)
        {
            (_m[a + i], _m[b + i]) = (_m[b + i], _m[a + i]);
        }
    }

    private static int U2B(int nu) => nu * UnitSize;

    private static int U2I(int nu) => PpmdTables.Units2Indx[nu - 1];

    private static int I2U(int indx) => PpmdTables.Indx2Units[indx];

    // ---- sub-allocator ----
    // A free block is a node: Stamp u32 @0 (0xFFFFFFFF = free), Next u32 @4, NU u32 @8.

    private void InsertNode(int node, int indx)
    {
        SetU32(node, -1);
        SetU32(node + 4, _freeList[indx]);
        SetU32(node + 8, I2U(indx));
        _freeList[indx] = node;
        _stamps[indx]++;
    }

    private int RemoveNode(int indx)
    {
        int node = _freeList[indx];
        _freeList[indx] = U32(node + 4);
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

    private bool IsEmptyNode(int node) => U32(node) == -1;

    private void GlueFreeBlocks()
    {
        _glueCount = 1 << 13;
        Array.Clear(_stamps);

        // A guard (stamp 0) at LoUnit stops a block from gluing into the gap between LoUnit and HiUnit.
        if (_loUnit != _hiUnit)
        {
            SetU32(_loUnit, 0);
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
                int nu = U32(node + 8);
                if (prev < 0)
                {
                    n = next;
                }
                else
                {
                    SetU32(prev, next);
                }

                next = U32(node + 4);
                if (nu != 0)
                {
                    prev = node + 4;
                    int node2;
                    while (IsEmptyNode(node2 = node + (nu * UnitSize)))
                    {
                        nu += U32(node2 + 8);
                        SetU32(node2 + 8, 0);
                        SetU32(node + 8, nu);
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
            SetU32(prev, 0);
        }

        while (n != 0)
        {
            int node = n;
            int nu = U32(node + 8);
            n = U32(node + 4);
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
        int[] count = _expandCount;
        Array.Clear(count);
        if (_loUnit != _hiUnit)
        {
            SetU32(_loUnit, 0);
        }

        int node = _unitsStart;
        while (IsEmptyNode(node))
        {
            int nu = U32(node + 8);
            SetU32(node, 0);
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
                n = U32(nd + 4);
                if (U32(nd) != 0)
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
                    SetU32(prev, n);
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
        _m[mc + 1] = 0;
        _m[mc] = 255;
        SetU16(mc + 2, 256 + 1);
        SetU32(mc + 4, s);
        SetU32(mc + 8, 0);

        for (int i = 0; i < 256; i++, s += StateSize)
        {
            _m[s] = (byte)i;
            _m[s + 1] = 1;
            SetU32(s + 2, 0);
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
        int i = _m[ctx];
        int s = ShrinkUnits(U32(ctx + 4), oldNU, (i + 2) >> 1);
        SetU32(ctx + 4, s);

        scale |= U16(ctx + 2) >= (1 << 15) ? 1 : 0;

        int flags = HiBitsPrepare(_m[s]);
        uint freq = _m[s + 1];
        uint escFreq = (uint)U16(ctx + 2) - freq;
        freq = (freq + (uint)scale) >> scale;
        uint sumFreq = freq;
        _m[s + 1] = (byte)freq;

        do
        {
            s += StateSize;
            freq = _m[s + 1];
            escFreq -= freq;
            freq = (freq + (uint)scale) >> scale;
            sumFreq += freq;
            _m[s + 1] = (byte)freq;
            flags |= HiBitsPrepare(_m[s]);
        }
        while (--i != 0);

        SetU16(ctx + 2, (int)((sumFreq + ((escFreq + (uint)scale) >> scale)) & 0xFFFF));
        _m[ctx + 1] = (byte)((_m[ctx + 1] & (FlagPrevHigh + (FlagRescaled * scale))) + HiBitsConvert3(flags));
    }

    // Cuts the model down: turns successors at the maximum order into null links, drops raw and null successors
    // that are not at order 0, and frees contexts that are left with no successors.
    private int CutOff(int ctx, int order)
    {
        int ns = _m[ctx];
        if (ns == 0)
        {
            int s1 = ctx + 2;
            int successor = U32(s1 + 2);
            if (successor >= _unitsStart)
            {
                successor = order < Order ? CutOff(successor, order + 1) : 0;
                SetU32(s1 + 2, successor);
                if (successor != 0 || order <= 9)
                {
                    return ctx;
                }
            }

            SpecialFreeUnit(ctx);
            return 0;
        }

        int nu = (ns + 2) >> 1;
        int stats = U32(ctx + 4);
        {
            int indx = U2I(nu);
            if ((uint)(stats - _unitsStart) <= (1 << 14) && (uint)stats <= (uint)_freeList[indx])
            {
                int ptr = RemoveNode(indx);
                SetU32(ctx + 4, ptr);
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
                int successor = U32(s + 2);
                if (successor < _unitsStart)
                {
                    int s2 = stats + (ns-- * StateSize);
                    if (order != 0)
                    {
                        if (s != s2)
                        {
                            Copy6(s, s2);
                        }
                    }
                    else
                    {
                        Swap6(s, s2);
                        SetU32(s2 + 2, 0);
                    }
                }
                else
                {
                    SetU32(s + 2, order < Order ? CutOff(successor, order + 1) : 0);
                }
            }
            while ((s -= StateSize) >= stats);
        }

        if (ns != _m[ctx] && order != 0)
        {
            if (ns < 0)
            {
                FreeUnits(stats, nu);
                SpecialFreeUnit(ctx);
                return 0;
            }

            _m[ctx] = (byte)ns;
            if (ns == 0)
            {
                byte sym = _m[stats];
                _m[ctx + 1] = (byte)((_m[ctx + 1] & FlagPrevHigh) + HiBitsFlag3(sym));
                _m[ctx + 2] = sym;
                _m[ctx + 3] = (byte)((_m[stats + 1] + 11) >> 3);
                SetU32(ctx + 4, U32(stats + 2));
                FreeUnits(stats, nu);
            }
            else
            {
                Refresh(ctx, nu, U16(ctx + 2) > 16 * ns ? 1 : 0);
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
        _text = _alignOffset;

        int c;
        for (c = _maxContext; c != ctxError; c = U32(c + 8))
        {
            if (--_m[c] == 0)
            {
                int s = U32(c + 4);
                _m[c + 1] = (byte)((_m[c + 1] & FlagPrevHigh) + HiBitsFlag3(_m[s]));
                _m[c + 2] = _m[s];
                _m[c + 3] = (byte)((_m[s + 1] + 11) >> 3);
                SetU32(c + 4, U32(s + 2));
                SpecialFreeUnit(s);
            }
            else
            {
                Refresh(c, (_m[c] + 3) >> 1, 0);
            }
        }

        for (; c != _minContext; c = U32(c + 8))
        {
            if (_m[c] == 0)
            {
                _m[c + 3] = (byte)((_m[c + 3] + 1) >> 1);
            }
            else
            {
                int summ = (U16(c + 2) + 4) & 0xFFFF;
                SetU16(c + 2, summ);
                if (summ > 128 + (4 * _m[c]))
                {
                    Refresh(c, (_m[c] + 2) >> 1, 1);
                }
            }
        }

        if (_restoreMethod == (int)PpmdRestoreMethod.Restart || GetUsedMemory() < (uint)(_size >> 1))
        {
            RestartModel();
        }
        else
        {
            while (U32(_maxContext + 8) != 0)
            {
                _maxContext = U32(_maxContext + 8);
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
        int upBranch = U32(_foundState + 2);
        int numPs = 0;

        if (!skip)
        {
            _ps[numPs++] = _foundState;
        }

        while (U32(c + 8) != 0)
        {
            c = U32(c + 8);
            int s;
            if (s1 != 0)
            {
                s = s1;
                s1 = 0;
            }
            else if (_m[c] != 0)
            {
                byte sym = _m[_foundState];
                s = U32(c + 4);
                while (_m[s] != sym)
                {
                    s += StateSize;
                }

                if (_m[s + 1] < MaxFreq - 9)
                {
                    _m[s + 1]++;
                    SetU16(c + 2, U16(c + 2) + 1);
                }
            }
            else
            {
                uint temp = _m[U32(c + 8)] == 0 ? 1u : 0u;
                s = c + 2;
                _m[s + 1] = (byte)(_m[s + 1] + (temp & (_m[s + 1] < 24 ? 1u : 0u)));
            }

            int successor = U32(s + 2);
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

        byte newSym = _m[upBranch];
        upBranch++;
        byte flags = (byte)(HiBitsFlag4(_m[_foundState]) + HiBitsFlag3(newSym));

        byte newFreq;
        if (_m[c] == 0)
        {
            newFreq = _m[c + 3];
        }
        else
        {
            int s = U32(c + 4);
            while (_m[s] != newSym)
            {
                s += StateSize;
            }

            uint cf = (uint)_m[s + 1] - 1;
            uint s0 = (uint)U16(c + 2) - _m[c] - cf;
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

            _m[c1 + 1] = flags;
            _m[c1] = 0;
            _m[c1 + 2] = newSym;
            _m[c1 + 3] = newFreq;
            SetU32(c1 + 4, upBranch);
            SetU32(c1 + 8, c);
            SetU32(_ps[--numPs] + 2, c1);
            c = c1;
        }
        while (numPs != 0);

        return c;
    }

    private int ReduceOrder(int s1, int c)
    {
        int s = 0;
        int c1 = c;
        int upBranch = _text;

        SetU32(_foundState + 2, upBranch);
        _orderFall++;

        for (;;)
        {
            if (s1 != 0)
            {
                c = U32(c + 8);
                s = s1;
                s1 = 0;
            }
            else
            {
                if (U32(c + 8) == 0)
                {
                    return c;
                }

                c = U32(c + 8);
                if (_m[c] != 0)
                {
                    s = U32(c + 4);
                    if (_m[s] != _m[_foundState])
                    {
                        do
                        {
                            s += StateSize;
                        }
                        while (_m[s] != _m[_foundState]);
                    }

                    if (_m[s + 1] < MaxFreq - 9)
                    {
                        _m[s + 1] += 2;
                        SetU16(c + 2, U16(c + 2) + 2);
                    }
                }
                else
                {
                    s = c + 2;
                    _m[s + 1] = (byte)(_m[s + 1] + (_m[s + 1] < 32 ? 1 : 0));
                }
            }

            if (U32(s + 2) != 0)
            {
                break;
            }

            SetU32(s + 2, upBranch);
            _orderFall++;
        }

        if (U32(s + 2) <= upBranch)
        {
            int s2 = _foundState;
            _foundState = s;
            int successor = CreateSuccessors(false, 0, c);
            SetU32(s + 2, successor);
            _foundState = s2;
        }

        {
            int successor = U32(s + 2);
            if (_orderFall == 1 && c1 == _maxContext)
            {
                SetU32(_foundState + 2, successor);
                _text--;
            }

            return successor;
        }
    }

    private void UpdateModel()
    {
        int minSuccessor = U32(_foundState + 2);
        int fFreq = _m[_foundState + 1];
        byte fSymbol = _m[_foundState];
        int maxSuccessor;
        int c;
        int s = 0;

        if (fFreq < MaxFreq / 4 && U32(_minContext + 8) != 0)
        {
            c = U32(_minContext + 8);
            if (_m[c] == 0)
            {
                s = c + 2;
                if (_m[s + 1] < 32)
                {
                    _m[s + 1]++;
                }
            }
            else
            {
                s = U32(c + 4);
                if (_m[s] != fSymbol)
                {
                    do
                    {
                        s += StateSize;
                    }
                    while (_m[s] != fSymbol);

                    if (_m[s + 1] >= _m[s - StateSize + 1])
                    {
                        Swap6(s, s - StateSize);
                        s -= StateSize;
                    }
                }

                if (_m[s + 1] < MaxFreq - 9)
                {
                    _m[s + 1] += 2;
                    SetU16(c + 2, U16(c + 2) + 2);
                }
            }
        }

        c = _maxContext;
        if (_orderFall == 0 && minSuccessor != 0)
        {
            int cs = CreateSuccessors(true, s, _minContext);
            if (cs == 0)
            {
                SetU32(_foundState + 2, 0);
                RestoreModel(c);
                return;
            }

            SetU32(_foundState + 2, cs);
            _minContext = _maxContext = cs;
            return;
        }

        {
            int text = _text;
            _m[text++] = _m[_foundState];
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
        int ns = _m[_minContext];
        uint s0 = (uint)U16(_minContext + 2) - (uint)ns - (uint)fFreq;

        for (; c != _minContext; c = U32(c + 8))
        {
            int ns1 = _m[c];
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

                        int oldPtr = U32(c + 4);
                        Mem12Copy(ptr, oldPtr, oldNU);
                        InsertNode(oldPtr, i);
                        SetU32(c + 4, ptr);
                    }
                }

                sum = (uint)U16(c + 2);
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

                uint freq = _m[c + 3];
                _m[ns2] = _m[c + 2];
                SetU32(ns2 + 2, U32(c + 4));
                SetU32(c + 4, ns2);
                if (freq < (MaxFreq / 4) - 1)
                {
                    freq <<= 1;
                }
                else
                {
                    freq = MaxFreq - 4;
                }

                _m[ns2 + 1] = (byte)freq;
                sum = freq + (uint)_initEsc + (ns > 2 ? 1u : 0u);
            }

            {
                int st = U32(c + 4) + ((ns1 + 1) * StateSize);
                uint cf = 2 * (sum + 6) * (uint)fFreq;
                uint sf = s0 + sum;
                _m[st] = fSymbol;
                _m[c] = (byte)(ns1 + 1);
                SetU32(st + 2, maxSuccessor);
                _m[c + 1] |= (byte)flag;
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

                SetU16(c + 2, (int)(sum & 0xFFFF));
                _m[st + 1] = (byte)cf;
            }
        }

        _maxContext = _minContext = minSuccessor;
    }

    private void Rescale()
    {
        int mc = _minContext;
        int stats = U32(mc + 4);
        int s = _foundState;

        if (s != stats)
        {
            byte tSym = _m[s];
            byte tFreq = _m[s + 1];
            int tSucc = U32(s + 2);
            do
            {
                Copy6(s, s - StateSize);
                s -= StateSize;
            }
            while (s != stats);
            _m[s] = tSym;
            _m[s + 1] = tFreq;
            SetU32(s + 2, tSucc);
        }

        uint sumFreq = _m[s + 1];
        uint escFreq = (uint)U16(mc + 2) - sumFreq;
        uint adder = _orderFall != 0 ? 1u : 0u;

        sumFreq = (sumFreq + 4 + adder) >> 1;
        int i = _m[mc];
        _m[s + 1] = (byte)sumFreq;

        do
        {
            s += StateSize;
            uint freq = _m[s + 1];
            escFreq -= freq;
            freq = (freq + adder) >> 1;
            sumFreq += freq;
            _m[s + 1] = (byte)freq;
            if (freq > _m[s - StateSize + 1])
            {
                byte tSym = _m[s];
                int tSucc = U32(s + 2);
                int s1 = s;
                do
                {
                    Copy6(s1, s1 - StateSize);
                    s1 -= StateSize;
                }
                while (s1 != stats && freq > _m[s1 - StateSize + 1]);

                _m[s1] = tSym;
                _m[s1 + 1] = (byte)freq;
                SetU32(s1 + 2, tSucc);
            }
        }
        while (--i != 0);

        if (_m[s + 1] == 0)
        {
            i = 0;
            do
            {
                i++;
                s -= StateSize;
            }
            while (_m[s + 1] == 0);

            escFreq += (uint)i;
            int numStats = _m[mc];
            int numStatsNew = numStats - i;
            _m[mc] = (byte)numStatsNew;
            int n0 = (numStats + 2) >> 1;

            if (numStatsNew == 0)
            {
                uint freq = ((2 * (uint)_m[stats + 1]) + escFreq - 1) / escFreq;
                if (freq > MaxFreq / 3)
                {
                    freq = MaxFreq / 3;
                }

                _m[mc + 1] = (byte)((_m[mc + 1] & FlagPrevHigh) + HiBitsFlag3(_m[stats]));
                s = mc + 2;
                Copy6(s, stats);
                _m[s + 1] = (byte)freq;
                _foundState = s;
                InsertNode(stats, U2I(n0));
                return;
            }

            int n1 = (numStatsNew + 2) >> 1;
            if (n0 != n1)
            {
                SetU32(mc + 4, ShrinkUnits(stats, n0, n1));
            }
        }

        SetU16(mc + 2, (int)((sumFreq + escFreq - (escFreq >> 1)) & 0xFFFF));
        _m[mc + 1] |= FlagRescaled;
        _foundState = U32(mc + 4);
    }

    private int MakeEscFreq(int numMasked, out uint escFreq)
    {
        int mc = _minContext;
        uint numStats = _m[mc];
        if (numStats != 0xFF)
        {
            int see = ((s_ns2Indx[numStats + 2] - 3) * 32)
                + (U16(mc + 2) > 11 * (numStats + 1) ? 1 : 0)
                + (2 * (2 * numStats < _m[U32(mc + 8)] + (uint)numMasked ? 1 : 0))
                + _m[mc + 1];
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
        int c = U32(_foundState + 2);
        if (_orderFall == 0 && c >= _unitsStart)
        {
            _maxContext = _minContext = c;
        }
        else
        {
            UpdateModel();
        }
    }

    private void Update1()
    {
        int s = _foundState;
        int mc = _minContext;
        uint freq = (uint)_m[s + 1] + 4;
        SetU16(mc + 2, U16(mc + 2) + 4);
        _m[s + 1] = (byte)freq;
        if (freq > _m[s - StateSize + 1])
        {
            Swap6(s, s - StateSize);
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
        int s = _foundState;
        int mc = _minContext;
        uint freq = _m[s + 1];
        uint summFreq = (uint)U16(mc + 2);
        _prevSuccess = 2 * freq >= summFreq ? 1 : 0;
        _runLength += _prevSuccess;
        SetU16(mc + 2, (int)((summFreq + 4) & 0xFFFF));
        freq += 4;
        _m[s + 1] = (byte)freq;
        if (freq > MaxFreq)
        {
            Rescale();
        }

        NextContext();
    }

    private void Update2()
    {
        int s = _foundState;
        int mc = _minContext;
        uint freq = (uint)_m[s + 1] + 4;
        _runLength = _initRl;
        SetU16(mc + 2, U16(mc + 2) + 4);
        _m[s + 1] = (byte)freq;
        if (freq > MaxFreq)
        {
            Rescale();
        }

        UpdateModel();
    }

    private int BinSummIndex()
    {
        int mc = _minContext;
        int one = mc + 2;
        return (s_ns2Indx[_m[one + 1] - 1] * 64)
            + _prevSuccess + ((_runLength >> 26) & 0x20)
            + s_ns2BsIndx[_m[U32(mc + 8)]]
            + _m[mc + 1];
    }
}
