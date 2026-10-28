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

    public static LevelParameters For(int level) => level switch
    {
        0 => new() { Mode = ParseMode.Stored },
        1 => new() { Mode = ParseMode.Greedy, HashBits = 17, HashBytes = 4, Chain = 1, Nice = 32, MaxLazy = 32, Tail = 4 },
        2 => new() { Mode = ParseMode.Greedy, HashBits = 15, HashBytes = 3, Chain = 6, Nice = 40, MaxLazy = 32, Tail = 4 },
        3 => new() { Mode = ParseMode.Greedy, HashBits = 15, HashBytes = 3, Chain = 12, Nice = 48, MaxLazy = 48, Tail = 4 },
        4 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 8, Nice = 32, MaxLazy = 16, Good = 8 },
        5 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 12, Nice = 48, MaxLazy = 24, Good = 12 },
        6 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 20, Nice = 64, MaxLazy = 64, Good = 16 },
        7 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 48, Nice = 100, MaxLazy = 100, Good = 16 },
        8 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 128, Nice = 160, MaxLazy = 200, Good = 16 },
        9 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 512, Nice = 258, MaxLazy = 258, Good = 16 },
        10 => new() { Mode = ParseMode.Optimal, Chain = 24, Nice = 64, Passes = 1 },
        11 => new() { Mode = ParseMode.Optimal, Chain = 64, Nice = 128, Passes = 2 },
        _ => new() { Mode = ParseMode.Optimal, Chain = 256, Nice = 258, Passes = 3 },
    };
}

public sealed partial class DeflateEncoder
{
    // A 3-byte match this far back costs more bits than three literals would, so it is not taken.
    private const int TooFar = 4096;

    // The parsers below keep the entry count and the position in locals and store them back before they call anything that
    // reads the encoder's state (CheckBlock, which may close the block). An entry is written at words[count] only while
    // count < checkAt <= capacity, because a check follows every entry.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PutLiteral(uint[] words, uint[] literalCounts, ref int count, byte value)
    {
        DeflateEncRaw.Set(words, count++, (uint)value << 9);
        DeflateEncRaw.Increment(literalCounts, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PutMatch(uint[] words, uint[] literalCounts, uint[] distanceCounts, ref int count, int length, int distance)
    {
        DeflateEncRaw.Set(words, count++, (uint)length | ((uint)(distance - 1) << 9));
        DeflateEncRaw.Increment(literalCounts, 257 + DeflateEncRaw.Get(DeflateSymbols.LengthSlot, length));
        DeflateEncRaw.Increment(distanceCounts, DeflateSymbols.DistanceSlot(distance));
    }

    private void ParseLiterals(int limit)
    {
        byte[] window = _window;
        uint[] words = _sequences.Words;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        int count = _sequences.CountValue;
        int checkAt = _checkAt;
        int position = _position;
        while (position < limit)
        {
            PutLiteral(words, literalCounts, ref count, window[position++]);
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
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        uint[] distanceCounts = _sequences.DistanceCounts;
        int count = _sequences.CountValue;
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
                PutMatch(words, literalCounts, distanceCounts, ref count, run, 1);
                position += run;
            }
            else
            {
                PutLiteral(words, literalCounts, ref count, window[position++]);
            }

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

    private void ParseGreedy(int limit)
    {
        DeflateHashChainMatchFinder finder = _chain!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        uint[] words = _sequences.Words;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        uint[] distanceCounts = _sequences.DistanceCounts;
        int count = _sequences.CountValue;
        int checkAt = _checkAt;
        int position = _position;
        int fill = _fill;
        int insertEnd = fill - DeflateHashChainMatchFinder.RequiredLookahead + 1;
        int oldest = _oldest;
        finder.Bind(window, fill, oldest, p.Nice);
        while (position < limit)
        {
            int available = fill - position;
            int length = 0;
            int distance = 0;
            if (available >= DeflateHashChainMatchFinder.RequiredLookahead)
            {
                length = finder.Find(position, 2, p.Chain, out distance);
            }

            if (length >= DeflateTables.MinMatchLength && (length > 3 || distance <= TooFar))
            {
                PutMatch(words, literalCounts, distanceCounts, ref count, length, distance);
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
            }
            else
            {
                PutLiteral(words, literalCounts, ref count, window[position++]);
            }

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

    // Level 1: one probe per position, no chains. Interior positions of short matches go into the table so that text keeps finding
    // its repeats.
    private void ParseFastest(int limit)
    {
        DeflateHashChainMatchFinder finder = _chain!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        uint[] words = _sequences.Words;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        uint[] distanceCounts = _sequences.DistanceCounts;
        int count = _sequences.CountValue;
        int checkAt = _checkAt;
        int position = _position;
        int fill = _fill;
        int insertEnd = fill - DeflateHashChainMatchFinder.RequiredLookahead + 1;
        int oldest = _oldest;
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
                PutMatch(words, literalCounts, distanceCounts, ref count, length, distance);
                int end = position + length;
                {
                    int last = Math.Min(end, insertEnd);
                    int first = position + 1;
                    if (length > p.MaxLazy)
                    {
                        first = Math.Max(first, end - p.Tail);
                    }

                    for (int q = first; q < last; q++)
                    {
                        finder.InsertFirstUnchecked(window, q);
                    }
                }

                position = end;
            }
            else
            {
                PutLiteral(words, literalCounts, ref count, window[position++]);
            }

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

    // Lazy matching (RFC 1951 leaves the parse to the encoder; this is the usual refinement): before taking a match, look one
    // byte ahead, and if that offers a longer match, emit a literal and carry the longer match forward.
    private void ParseLazy(int limit)
    {
        DeflateHashChainMatchFinder finder = _chain!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        uint[] words = _sequences.Words;
        uint[] literalCounts = _sequences.LiteralLengthCounts;
        uint[] distanceCounts = _sequences.DistanceCounts;
        int count = _sequences.CountValue;
        int checkAt = _checkAt;
        int position = _position;
        int fill = _fill;
        int insertEnd = fill - DeflateHashChainMatchFinder.RequiredLookahead + 1;
        int oldest = _oldest;
        finder.Bind(window, fill, oldest, p.Nice);
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
                PutLiteral(words, literalCounts, ref count, window[position++]);
                if (count >= checkAt)
                {
                    _sequences.CountValue = count;
                    _position = position;
                    CheckBlock();
                    count = _sequences.CountValue;
                    checkAt = _checkAt;
                }

                continue;
            }

            // position is hashed. Try one byte ahead while the match is not long enough to settle for.
            int hashedTo = position + 1;
            while (length < p.MaxLazy && position + 1 < insertEnd)
            {
                int chain = length >= p.Good ? p.Chain >> 2 : p.Chain;
                int next = position + 1;
                int length2 = finder.Find(next, length, chain, out int distance2);
                hashedTo = next + 1;
                if (length2 > length && !(length2 == DeflateTables.MinMatchLength && distance2 > TooFar))
                {
                    PutLiteral(words, literalCounts, ref count, window[position]);
                    position = next;
                    length = length2;
                    distance = distance2;
                    if (count >= checkAt)
                    {
                        _sequences.CountValue = count;
                        _position = position;
                        CheckBlock();
                        count = _sequences.CountValue;
                        checkAt = _checkAt;
                    }

                    continue;
                }

                break;
            }

            // Take the match at position; hashedTo is the first position not yet in the chains.
            PutMatch(words, literalCounts, distanceCounts, ref count, length, distance);
            int matchEnd = position + length;
            int insertLast = Math.Min(matchEnd, insertEnd);
            finder.InsertRange(Math.Max(hashedTo, position + 1), insertLast);

            position = matchEnd;
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
}
