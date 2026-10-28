using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// The block randomisation that very old bzip2 versions (before 0.9.5) could apply to blocks that sorted slowly, and that every
/// decoder must still undo. After the inverse Burrows-Wheeler transform, the bytes at a fixed schedule of positions have their
/// lowest bit flipped. The schedule is cyclic: 512 gaps between flips, the first flip at the first gap minus 2. The gap table is
/// not published anywhere; it was measured from the behaviour of the bzip2 tool (see <c>tools/bzip2</c>).
/// </summary>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static partial class BZip2Randomization
{
    /// <summary>The number of gaps in the cycle.</summary>
    public const int Period = 512;

    /// <summary>Gets the position, counted from the start of the block after the inverse transform, of the first flipped byte.</summary>
    public static int FirstFlip => GapData[0] - 2;

    /// <summary>Gets the distance from flip number <paramref name="index"/> (0 is the first) to the next flip.</summary>
    /// <param name="index">The flip's number, counted from the start of the block.</param>
    /// <returns>The gap in bytes, 50 to 999.</returns>
    public static int GapAfter(int index) => GapData[(index + 1) & (Period - 1)];

    /// <summary>Flips the bytes of a whole block that the schedule selects. The operation is its own inverse.</summary>
    /// <param name="block">The block, from its first byte after the inverse transform.</param>
    public static void Apply(Span<byte> block)
    {
        int at = FirstFlip;
        int index = 0;
        while (at < block.Length)
        {
            block[at] ^= 1;
            at += GapAfter(index++);
        }
    }
}
