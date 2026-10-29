// Ported from C/Ppmd.h, C/Ppmd7.c and C/Ppmd8.c of the LZMA SDK / 7-Zip 26.05 (Igor Pavlov).
//
// Original notices, quoted exactly:
//   Ppmd.h:   "Ppmd.h -- PPMD codec common code
//              2023-03-05 : Igor Pavlov : Public domain
//              This code is based on PPMd var.H (2001): Dmitry Shkarin : Public domain"
//   Ppmd7.c:  "Ppmd7.c -- PPMdH codec
//              2023-09-07 : Igor Pavlov : Public domain
//              This code is based on PPMd var.H (2001): Dmitry Shkarin : Public domain"
//   Ppmd8.c:  "Ppmd8.c -- PPMdI codec
//              : Igor Pavlov : Public domain
//              This code is based on PPMd var.I (2002): Dmitry Shkarin : Public domain"

namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>Constants and lookup tables that both PPMd variants share.</summary>
internal static class PpmdTables
{
    /// <summary>Bytes in one allocation unit; contexts take one, symbol lists take several.</summary>
    internal const int UnitSize = 12;

    /// <summary>The highest symbol frequency a context keeps before it rescales.</summary>
    internal const int MaxFreq = 124;

    internal const int IntBits = 7;
    internal const int PeriodBits = 7;
    internal const int BinScale = 1 << (IntBits + PeriodBits);

    // 4 + 4 + 4 + (128 + 3 - 4 - 8 - 12) / 4 = 38 free-list sizes.
    internal const int NumIndexes = 4 + 4 + 4 + ((128 + 3 - 1 * 4 - 2 * 4 - 3 * 4) / 4);

    /// <summary>Allocation unit counts of the free-list classes (1, 2, 3, 4, 6, 8, ... 128).</summary>
    internal static readonly byte[] Indx2Units = new byte[NumIndexes + 2];

    /// <summary>The smallest class that holds a given number of units, indexed by <c>units - 1</c>.</summary>
    internal static readonly byte[] Units2Indx = new byte[128];

    internal static readonly byte[] ExpEscape = [25, 14, 9, 7, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2];

    internal static readonly ushort[] InitBinEsc = [0x3CDD, 0x1F3F, 0x59BF, 0x48F3, 0x64A1, 0x5ABC, 0x6632, 0x6051];

    static PpmdTables()
    {
        int k = 0;
        for (int i = 0; i < NumIndexes; i++)
        {
            int step = i >= 12 ? 4 : (i >> 2) + 1;
            do
            {
                Units2Indx[k++] = (byte)i;
            }
            while (--step != 0);
            Indx2Units[i] = (byte)k;
        }
    }
}
