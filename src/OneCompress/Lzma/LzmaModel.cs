using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The fixed structure of the LZMA model: the 12-state machine that remembers what the last symbols were, the layout of the
/// probability array, and the sizes of the length and distance codes. A decoder and an encoder that share this layout keep identical
/// probabilities, which is what makes the range decoder's decisions line up with the encoder's.
/// </summary>
/// <remarks>
/// LZMA specification, "The main loop of decoder", "The match length decoding" and "The match distance decoding". The probability
/// layout (the offsets below) is this library's own: all probabilities live in one <c>ushort[]</c> of
/// <see cref="ProbabilityCount"/> entries, the literal tables last.
/// </remarks>
public static class LzmaModel
{
    /// <summary>The number of states of the state machine.</summary>
    public const int StateCount = 12;

    /// <summary>States below this value follow a literal; the others follow a match.</summary>
    public const int LiteralStateLimit = 7;

    /// <summary>The shortest match length.</summary>
    public const int MinMatchLength = 2;

    /// <summary>The longest match length.</summary>
    public const int MaxMatchLength = 273;

    /// <summary>The number of length classes that select a position-slot tree.</summary>
    public const int LengthToPositionStates = 4;

    /// <summary>The first position slot whose distance has direct bits.</summary>
    public const int EndPositionModelIndex = 14;

    /// <summary>The number of low distance bits coded with the shared align tree.</summary>
    public const int AlignBits = 4;

    /// <summary>The number of probabilities of one literal coder (a 3-level tree of 8 bits plus two match-bit trees).</summary>
    public const int LiteralCoderSize = 0x300;

    /// <summary>The largest <c>1 &lt;&lt; pb</c>.</summary>
    public const int MaxPositionStates = 16;

    /// <summary>Offset of the "is match" probabilities, indexed by <c>(state &lt;&lt; 4) + posState</c>.</summary>
    public const int IsMatch = 0;

    /// <summary>Offset of the "is rep" probabilities, indexed by state.</summary>
    public const int IsRep = IsMatch + (StateCount << 4);

    /// <summary>Offset of the "rep group 0" probabilities, indexed by state.</summary>
    public const int IsRepG0 = IsRep + StateCount;

    /// <summary>Offset of the "rep group 1" probabilities, indexed by state.</summary>
    public const int IsRepG1 = IsRepG0 + StateCount;

    /// <summary>Offset of the "rep group 2" probabilities, indexed by state.</summary>
    public const int IsRepG2 = IsRepG1 + StateCount;

    /// <summary>Offset of the "rep 0 long" probabilities, indexed by <c>(state &lt;&lt; 4) + posState</c>.</summary>
    public const int IsRep0Long = IsRepG2 + StateCount;

    /// <summary>Offset of the position-slot trees: four 6-bit trees of 64 entries, by length class.</summary>
    public const int PositionSlot = IsRep0Long + (StateCount << 4);

    /// <summary>Offset of the reverse trees for the low bits of distance slots 4 to 13 (115 entries).</summary>
    public const int PositionSpecial = PositionSlot + (LengthToPositionStates << 6);

    /// <summary>Offset of the 4-bit align tree.</summary>
    public const int Align = PositionSpecial + 115;

    /// <summary>The number of probabilities of one length coder.</summary>
    public const int LengthCoderSize = 2 + (MaxPositionStates * 8 * 2) + 256;

    /// <summary>Offset of the length coder for matches: choice, choice2, low trees by posState, mid trees by posState, high tree.</summary>
    public const int LengthCoder = Align + 16;

    /// <summary>Offset of the length coder for repeated matches; same layout as <see cref="LengthCoder"/>.</summary>
    public const int RepLengthCoder = LengthCoder + LengthCoderSize;

    /// <summary>Offset of the literal coders, <see cref="LiteralCoderSize"/> entries each, selected by position and previous byte.</summary>
    public const int Literal = RepLengthCoder + LengthCoderSize;

    /// <summary>Within a length coder: the offset of the second choice bit.</summary>
    public const int LengthChoice2 = 1;

    /// <summary>Within a length coder: the offset of the low trees (8 entries per posState).</summary>
    public const int LengthLow = 2;

    /// <summary>Within a length coder: the offset of the mid trees (8 entries per posState).</summary>
    public const int LengthMid = LengthLow + (MaxPositionStates * 8);

    /// <summary>Within a length coder: the offset of the 8-bit high tree.</summary>
    public const int LengthHigh = LengthMid + (MaxPositionStates * 8);

    /// <summary>Gets the number of probabilities a model with the given literal bits needs.</summary>
    /// <param name="lc">Literal context bits, 0 to 8.</param>
    /// <param name="lp">Literal position bits, 0 to 4.</param>
    /// <returns>The length of the probability array.</returns>
    public static int ProbabilityCount(int lc, int lp) => Literal + (LiteralCoderSize << (lc + lp));

    /// <summary>Gets the state after a literal.</summary>
    /// <param name="state">The state before, 0 to 11.</param>
    /// <returns>The next state.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextStateAfterLiteral(int state) => state < 4 ? 0 : state < 10 ? state - 3 : state - 6;

    /// <summary>Gets the state after a match with a newly coded distance.</summary>
    /// <param name="state">The state before.</param>
    /// <returns>The next state.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextStateAfterMatch(int state) => state < LiteralStateLimit ? 7 : 10;

    /// <summary>Gets the state after a repeated match.</summary>
    /// <param name="state">The state before.</param>
    /// <returns>The next state.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextStateAfterRep(int state) => state < LiteralStateLimit ? 8 : 11;

    /// <summary>Gets the state after a short repeated match (one byte at the last distance).</summary>
    /// <param name="state">The state before.</param>
    /// <returns>The next state.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextStateAfterShortRep(int state) => state < LiteralStateLimit ? 9 : 11;

    /// <summary>Fills a probability array with the initial value.</summary>
    /// <param name="probabilities">The array to reset.</param>
    public static void ResetProbabilities(Span<ushort> probabilities) => probabilities.Fill(LzmaRangeDecoder.InitialProbability);
}
