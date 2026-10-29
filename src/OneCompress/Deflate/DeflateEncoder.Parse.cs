using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Deflate;

internal enum ParseMode
{
    Stored,
    Greedy,
    Lazy,
    Optimal,
}

/// <summary>What one compression level asks of the parser. See docs/deflate-encoder.md for the table.</summary>
internal readonly struct LevelParameters
{
    public ParseMode Mode { get; init; }

    public int HashBits { get; init; }

    public int HashBytes { get; init; }

    /// <summary>The most chain candidates compared per position.</summary>
    public int Chain { get; init; }

    /// <summary>A match this long ends the search.</summary>
    public int Nice { get; init; }

    /// <summary>Greedy: a match longer than this enters only its last <see cref="Tail"/> positions into the finder. Lazy: a match this long is taken without looking one byte further.</summary>
    public int MaxLazy { get; init; }

    /// <summary>Optimal parsing: how many times a run is parsed, re-pricing from the previous pass.</summary>
    public int Passes { get; init; }

    /// <summary>A previous match at least this long cuts the next search's chain to a quarter.</summary>
    public int Good { get; init; }

    /// <summary>Greedy: inside a match longer than <see cref="MaxLazy"/>, only the last this many positions are entered into the finder.</summary>
    public int Tail { get; init; }

    /// <summary>Lazy: when positive, also look two bytes ahead, and take that match instead if it scores better than the current one by more than this (see <c>Gain</c>).</summary>
    public int Lazy2 { get; init; }

    /// <summary>
    /// Greedy and lazy: after this many literals in a row with no match (0 = never), the parser stops searching at every position:
    /// it emits (misses - SkipAfter) / 8 further bytes as literals without looking for matches, so data that does not compress costs
    /// little time. A match resets the count.
    /// </summary>
    public int SkipAfter { get; init; }

    public static LevelParameters For(int level) => level switch
    {
        0 => new() { Mode = ParseMode.Stored },
        1 => new() { Mode = ParseMode.Greedy, HashBits = 17, HashBytes = 4, Chain = 1, Nice = 32, MaxLazy = 32, Tail = 4, SkipAfter = 16 },
        2 => new() { Mode = ParseMode.Greedy, HashBits = 15, HashBytes = 4, Chain = 12, Nice = 40, MaxLazy = 32, Tail = 4, SkipAfter = 16 },
        3 => new() { Mode = ParseMode.Greedy, HashBits = 15, HashBytes = 4, Chain = 24, Nice = 48, MaxLazy = 48, Tail = 4, SkipAfter = 16 },
        4 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 4, Chain = 8, Nice = 32, MaxLazy = 16, Good = 8, SkipAfter = 16 },
        5 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 12, Nice = 48, MaxLazy = 24, Good = 12, SkipAfter = 16 },
        6 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 20, Nice = 64, MaxLazy = 64, Good = 16, SkipAfter = 16 },
        7 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 48, Nice = 100, MaxLazy = 100, Good = 16, SkipAfter = 16 },
        8 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 128, Nice = 160, MaxLazy = 200, Good = 16, Lazy2 = 4 },
        9 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 512, Nice = 258, MaxLazy = 258, Good = 16, Lazy2 = 4 },
        10 => new() { Mode = ParseMode.Optimal, Chain = 24, Nice = 64, Passes = 1 },
        11 => new() { Mode = ParseMode.Optimal, Chain = 64, Nice = 128, Passes = 2 },
        _ => new() { Mode = ParseMode.Optimal, Chain = 256, Nice = 258, Passes = 3 },
    };
}

public sealed partial class DeflateEncoder
{
    // A 3-byte match this far back costs more bits than three literals would, so it is not taken.
    private const int TooFar = 8192;

    // The greedy levels keep the older, stricter limit: their searches find many more 3-byte matches that would not pay.
    private const int GreedyTooFar = 4096;

    // Lazy matching compares candidate matches by a score that stands in for their cost: 4 units per byte covered, minus one per
    // bit of distance (a far match needs more extra bits and its distance code is rarer, so it is dearer). A match one byte later
    // replaces the current one when its score is higher by more than LazyMargin, so a same-length match that is much nearer wins.
    private const int LazyMargin = 1;

    private const int SkipShift = 3;

    private static int Gain(int length, int distance) => length * 4 - (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)distance));

    // The parsers below keep the entry count and the position in locals and store them back before they call anything that
    // reads the encoder's state (CheckBlock, which may close the block). A match is written at words[matches] only while
    // matches <= count < checkAt <= capacity, because a check follows every entry.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PutLiteral(uint[] literalCounts, ref int count, byte value)
    {
        // A literal is only counted: the bytes of a run of literals are read back from the window when the block is written
        // (see DeflateSequenceBuffer), so nothing is stored per literal.
        count++;
        DeflateEncRaw.Increment(literalCounts, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PutMatch(uint[] words, uint[] starts, uint[] literalCounts, uint[] distanceCounts, ref int count, ref int matches, int position, int length, int distance)
    {
        // Match number m is words[m] with the window position it starts at in starts[m]; count also counts the literals.
        count++;
        DeflateEncRaw.Set(words, matches, (uint)length | ((uint)(distance - 1) << 9));
        DeflateEncRaw.Set(starts, matches++, (uint)position);
        DeflateEncRaw.Increment(literalCounts, 257 + DeflateEncRaw.Get(DeflateSymbols.LengthSlot, length));
        DeflateEncRaw.Increment(distanceCounts, DeflateSymbols.DistanceSlot(distance));
    }

    private void ParseLiterals(int limit)
    {
        byte[] window = _window;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        int count = _sequences.CountValue;
        int checkAt = _checkAt;
        int position = _position;
        while (position < limit)
        {
            PutLiteral(literalCounts, ref count, window[position++]);
            if (count >= checkAt)
            {
                _sequences.CountValue = count;
                _position = position;
                CheckBlock();
                count = _sequences.CountValue;
                checkAt = _checkAt;
            }
        }

        _sequences.CountValue = count;
        _position = position;
    }

    // Matches only at distance 1: a run of the byte just before.
    private void ParseRuns(int limit)
    {
        byte[] window = _window;
        uint[] words = _sequences.Words;
        uint[] starts = _sequences.Starts;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        uint[] distanceCounts = _sequences.DistanceCounts;
        int count = _sequences.CountValue;
        int matches = _sequences.MatchCountValue;
        int checkAt = _checkAt;
        int position = _position;
        while (position < limit)
        {
            int available = _fill - position;
            int run = 0;
            if (position > _oldest && available >= DeflateTables.MinMatchLength)
            {
                run = DeflateEncRaw.MatchLength(window, position - 1, position, Math.Min(DeflateTables.MaxMatchLength, available));
            }

            if (run >= DeflateTables.MinMatchLength)
            {
                PutMatch(words, starts, literalCounts, distanceCounts, ref count, ref matches, position, run, 1);
                position += run;
            }
            else
            {
                PutLiteral(literalCounts, ref count, window[position++]);
            }

            if (count >= checkAt)
            {
                _sequences.CountValue = count;
                _sequences.MatchCountValue = matches;
                _position = position;
                CheckBlock();
                count = _sequences.CountValue;
                matches = _sequences.MatchCountValue;
                checkAt = _checkAt;
            }
        }

        _sequences.CountValue = count;
        _sequences.MatchCountValue = matches;
        _position = position;
    }

    private void ParseGreedy(int limit)
    {
        DeflateHashChainMatchFinder finder = _chain!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        uint[] words = _sequences.Words;
        uint[] starts = _sequences.Starts;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        uint[] distanceCounts = _sequences.DistanceCounts;
        int count = _sequences.CountValue;
        int matches = _sequences.MatchCountValue;
        int checkAt = _checkAt;
        int position = _position;
        int fill = _fill;
        int insertEnd = fill - DeflateHashChainMatchFinder.RequiredLookahead + 1;
        int oldest = _oldest;
        finder.Bind(window, fill, oldest, p.Nice);
        int misses = 0;
        int skipT = p.SkipAfter == 0 ? int.MaxValue : p.SkipAfter;
        while (position < limit)
        {
            int available = fill - position;
            int length = 0;
            int distance = 0;
            if (available >= DeflateHashChainMatchFinder.RequiredLookahead)
            {
                length = finder.Find(position, 2, p.Chain, out distance);
            }

            if (length >= DeflateTables.MinMatchLength && (length > 3 || distance <= GreedyTooFar))
            {
                PutMatch(words, starts, literalCounts, distanceCounts, ref count, ref matches, position, length, distance);
                int end = position + length;
                {
                    int last = Math.Min(end, insertEnd);
                    int first = position + 1;
                    if (length > p.MaxLazy)
                    {
                        first = Math.Max(first, end - p.Tail);
                    }

                    finder.InsertRange(first, last);
                }

                position = end;
                misses = 0;
            }
            else
            {
                PutLiteral(literalCounts, ref count, window[position++]);
                misses++;
            }

            if (count >= checkAt)
            {
                _sequences.CountValue = count;
                _sequences.MatchCountValue = matches;
                _position = position;
                CheckBlock();
                count = _sequences.CountValue;
                matches = _sequences.MatchCountValue;
                checkAt = _checkAt;
            }

            if (misses > skipT)
            {
                int extra = Math.Min(Math.Min((misses - skipT) >> SkipShift, limit - position), checkAt - count - 1);
                for (int i = 0; i < extra; i++)
                {
                    PutLiteral(literalCounts, ref count, window[position++]);
                }
            }
        }

        _sequences.CountValue = count;
        _sequences.MatchCountValue = matches;
        _position = position;
    }

    // Level 1: one probe per position, no chains. Interior positions of short matches go into the table so that text keeps finding
    // its repeats.

    private void ParseFastest(int limit)
    {
        DeflateHashChainMatchFinder finder = _chain!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        uint[] words = _sequences.Words;
        uint[] starts = _sequences.Starts;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        uint[] distanceCounts = _sequences.DistanceCounts;
        int count = _sequences.CountValue;
        int matches = _sequences.MatchCountValue;
        int checkAt = _checkAt;
        int position = _position;
        int fill = _fill;
        int insertEnd = fill - DeflateHashChainMatchFinder.RequiredLookahead + 1;
        int oldest = _oldest;
        int misses = 0;
        int skipT = p.SkipAfter == 0 ? int.MaxValue : p.SkipAfter;
        while (position < limit)
        {
            int length = 0;
            int distance = 0;
            int available = fill - position;
            if (available >= DeflateHashChainMatchFinder.RequiredLookahead)
            {
                length = finder.FindFirstUnchecked(window, position, Math.Min(DeflateTables.MaxMatchLength, available), oldest, out distance);
            }

            if (length != 0)
            {
                PutMatch(words, starts, literalCounts, distanceCounts, ref count, ref matches, position, length, distance);
                int end = position + length;
                {
                    int last = Math.Min(end, insertEnd);
                    int first = position + 1;
                    if (length > p.MaxLazy)
                    {
                        first = Math.Max(first, end - p.Tail);
                    }

                    finder.InsertFirstRange(window, first, last);
                }

                position = end;
                misses = 0;
            }
            else
            {
                PutLiteral(literalCounts, ref count, window[position++]);
                misses++;
            }

            if (count >= checkAt)
            {
                _sequences.CountValue = count;
                _sequences.MatchCountValue = matches;
                _position = position;
                CheckBlock();
                count = _sequences.CountValue;
                matches = _sequences.MatchCountValue;
                checkAt = _checkAt;
            }

            if (misses > skipT)
            {
                int extra = Math.Min(Math.Min((misses - skipT) >> SkipShift, limit - position), checkAt - count - 1);
                for (int i = 0; i < extra; i++)
                {
                    PutLiteral(literalCounts, ref count, window[position++]);
                }
            }
        }

        _sequences.CountValue = count;
        _sequences.MatchCountValue = matches;
        _position = position;
    }

    // Lazy matching (RFC 1951 leaves the parse to the encoder; this is the usual refinement): before taking a match, look one
    // byte ahead, and if that offers a longer match, emit a literal and carry the longer match forward.
    private void ParseLazy(int limit)
    {
        DeflateHashChainMatchFinder finder = _chain!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        uint[] words = _sequences.Words;
        uint[] starts = _sequences.Starts;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        uint[] distanceCounts = _sequences.DistanceCounts;
        int count = _sequences.CountValue;
        int matches = _sequences.MatchCountValue;
        int checkAt = _checkAt;
        int position = _position;
        int fill = _fill;
        int insertEnd = fill - DeflateHashChainMatchFinder.RequiredLookahead + 1;
        int oldest = _oldest;
        finder.Bind(window, fill, oldest, p.Nice);
        int misses = 0;
        int skipT = p.SkipAfter == 0 ? int.MaxValue : p.SkipAfter;
        while (position < limit)
        {
            int available = fill - position;
            int length = 0;
            int distance = 0;
            if (available >= DeflateHashChainMatchFinder.RequiredLookahead)
            {
                length = finder.Find(position, 2, p.Chain, out distance);
                if (length == DeflateTables.MinMatchLength && distance > TooFar)
                {
                    length = 0;
                }
            }

            if (length < DeflateTables.MinMatchLength)
            {
                PutLiteral(literalCounts, ref count, window[position++]);
                misses++;
                if (count >= checkAt)
                {
                    _sequences.CountValue = count;
                    _sequences.MatchCountValue = matches;
                    _position = position;
                    CheckBlock();
                    count = _sequences.CountValue;
                    matches = _sequences.MatchCountValue;
                    checkAt = _checkAt;
                }

                if (misses > skipT)
                {
                    int extra = Math.Min(Math.Min((misses - skipT) >> SkipShift, limit - position), checkAt - count - 1);
                    for (int i = 0; i < extra; i++)
                    {
                        PutLiteral(literalCounts, ref count, window[position++]);
                    }
                }

                continue;
            }

            // position is hashed. Try one byte ahead while the match is not long enough to settle for.
            int hashedTo = position + 1;
            while (length < p.MaxLazy && position + 1 < insertEnd)
            {
                int chain = length >= p.Good ? p.Chain >> 2 : p.Chain;
                int next = position + 1;
                // Find reports only matches longer than its second argument, so ask for one as long as the current match.
                int length2 = finder.Find(next, length - 1, chain, out int distance2);
                hashedTo = next + 1;
                if (distance2 > 0 && Gain(length2, distance2) > Gain(length, distance) + LazyMargin
                    && !(length2 == DeflateTables.MinMatchLength && distance2 > TooFar))
                {
                    PutLiteral(literalCounts, ref count, window[position]);
                    position = next;
                    length = length2;
                    distance = distance2;
                    if (count >= checkAt)
                    {
                        _sequences.CountValue = count;
                        _sequences.MatchCountValue = matches;
                        _position = position;
                        CheckBlock();
                        count = _sequences.CountValue;
                        matches = _sequences.MatchCountValue;
                        checkAt = _checkAt;
                    }

                    continue;
                }

                if (p.Lazy2 > 0 && position + 2 < insertEnd)
                {
                    int next2 = position + 2;
                    int length3 = finder.Find(next2, length, chain, out int distance3);
                    hashedTo = next2 + 1;
                    if (distance3 > 0 && Gain(length3, distance3) > Gain(length, distance) + p.Lazy2
                        && !(length3 == DeflateTables.MinMatchLength && distance3 > TooFar))
                    {
                        PutLiteral(literalCounts, ref count, window[position]);
                        if (count >= checkAt)
                        {
                            _sequences.CountValue = count;
                            _sequences.MatchCountValue = matches;
                            _position = position + 1;
                            CheckBlock();
                            count = _sequences.CountValue;
                            matches = _sequences.MatchCountValue;
                            checkAt = _checkAt;
                        }

                        PutLiteral(literalCounts, ref count, window[position + 1]);
                        position = next2;
                        length = length3;
                        distance = distance3;
                        if (count >= checkAt)
                        {
                            _sequences.CountValue = count;
                            _sequences.MatchCountValue = matches;
                            _position = position;
                            CheckBlock();
                            count = _sequences.CountValue;
                            matches = _sequences.MatchCountValue;
                            checkAt = _checkAt;
                        }

                        continue;
                    }
                }

                break;
            }

            // Take the match at position; hashedTo is the first position not yet in the chains.
            misses = 0;
            PutMatch(words, starts, literalCounts, distanceCounts, ref count, ref matches, position, length, distance);
            int matchEnd = position + length;
            int insertLast = Math.Min(matchEnd, insertEnd);
            finder.InsertRange(Math.Max(hashedTo, position + 1), insertLast);

            position = matchEnd;
            if (count >= checkAt)
            {
                _sequences.CountValue = count;
                _sequences.MatchCountValue = matches;
                _position = position;
                CheckBlock();
                count = _sequences.CountValue;
                matches = _sequences.MatchCountValue;
                checkAt = _checkAt;
            }
        }

        _sequences.CountValue = count;
        _sequences.MatchCountValue = matches;
        _position = position;
    }
}
