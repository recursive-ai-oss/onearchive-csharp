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

    /// <summary>Greedy: matches longer than this skip hashing their interior. Lazy: a match this long is taken without looking one byte further.</summary>
    public int MaxLazy { get; init; }

    /// <summary>A previous match at least this long cuts the next search's chain to a quarter.</summary>
    public int Good { get; init; }

    public static LevelParameters For(int level) => level switch
    {
        0 => new() { Mode = ParseMode.Stored },
        1 => new() { Mode = ParseMode.Greedy, HashBits = 15, HashBytes = 4, Chain = 1, Nice = 32, MaxLazy = 8 },
        2 => new() { Mode = ParseMode.Greedy, HashBits = 15, HashBytes = 4, Chain = 4, Nice = 32, MaxLazy = 16 },
        3 => new() { Mode = ParseMode.Greedy, HashBits = 15, HashBytes = 3, Chain = 8, Nice = 48, MaxLazy = 32 },
        4 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 16, Nice = 32, MaxLazy = 16, Good = 8 },
        5 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 32, Nice = 64, MaxLazy = 32, Good = 16 },
        6 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 128, Nice = 128, MaxLazy = 64, Good = 32 },
        7 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 256, Nice = 160, MaxLazy = 128, Good = 32 },
        8 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 1024, Nice = 258, MaxLazy = 258, Good = 32 },
        9 => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 4096, Nice = 258, MaxLazy = 258, Good = 32 },
        _ => new() { Mode = ParseMode.Lazy, HashBits = 15, HashBytes = 3, Chain = 4096, Nice = 258, MaxLazy = 258, Good = 32 },
    };
}

public sealed partial class DeflateEncoder
{
    // A 3-byte match this far back costs more bits than three literals would, so it is not taken.
    private const int TooFar = 4096;

    private void ParseLiterals(int limit)
    {
        byte[] window = _window;
        int position = _position;
        while (position < limit)
        {
            _sequences.AddLiteralUnchecked(window[position++]);
            if (_sequences.Count >= _checkAt)
            {
                _position = position;
                CheckBlock();
            }
        }

        _position = position;
    }

    // Matches only at distance 1: a run of the byte just before.
    private void ParseRuns(int limit)
    {
        byte[] window = _window;
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
                _sequences.AddMatchUnchecked(run, 1);
                position += run;
            }
            else
            {
                _sequences.AddLiteralUnchecked(window[position++]);
            }

            if (_sequences.Count >= _checkAt)
            {
                _position = position;
                CheckBlock();
            }
        }

        _position = position;
    }

    private void ParseGreedy(int limit)
    {
        DeflateHashChainMatchFinder finder = _chain!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        int position = _position;
        int fill = _fill;
        while (position < limit)
        {
            int available = fill - position;
            int length = 0;
            int distance = 0;
            if (available >= DeflateHashChainMatchFinder.RequiredLookahead)
            {
                length = finder.FindLongestUnchecked(window, position, Math.Min(DeflateTables.MaxMatchLength, available), 2, p.Chain, p.Nice, _oldest, out distance);
            }

            if (length >= DeflateTables.MinMatchLength && (length > 3 || distance <= TooFar))
            {
                _sequences.AddMatchUnchecked(length, distance);
                int end = position + length;
                if (length <= p.MaxLazy)
                {
                    int last = Math.Min(end, fill - DeflateHashChainMatchFinder.RequiredLookahead + 1);
                    for (int q = position + 1; q < last; q++)
                    {
                        finder.InsertUnchecked(window, q);
                    }
                }

                position = end;
            }
            else
            {
                _sequences.AddLiteralUnchecked(window[position++]);
            }

            if (_sequences.Count >= _checkAt)
            {
                _position = position;
                CheckBlock();
            }
        }

        _position = position;
    }

    // Lazy matching (RFC 1951 leaves the parse to the encoder; this is the usual refinement): before taking a match, look one
    // byte ahead, and if that offers a longer match, emit a literal and carry the longer match forward.
    private void ParseLazy(int limit)
    {
        DeflateHashChainMatchFinder finder = _chain!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        int position = _position;
        int fill = _fill;
        int insertEnd = fill - DeflateHashChainMatchFinder.RequiredLookahead + 1;
        while (position < limit)
        {
            int available = fill - position;
            int length = 0;
            int distance = 0;
            if (available >= DeflateHashChainMatchFinder.RequiredLookahead)
            {
                length = finder.FindLongestUnchecked(window, position, Math.Min(DeflateTables.MaxMatchLength, available), 2, p.Chain, p.Nice, _oldest, out distance);
                if (length == DeflateTables.MinMatchLength && distance > TooFar)
                {
                    length = 0;
                }
            }

            if (length < DeflateTables.MinMatchLength)
            {
                _sequences.AddLiteralUnchecked(window[position++]);
                if (_sequences.Count >= _checkAt)
                {
                    _position = position;
                    CheckBlock();
                }

                continue;
            }

            // position is hashed. Try one byte ahead while the match is not long enough to settle for.
            while (length < p.MaxLazy && position + 1 < fill - DeflateHashChainMatchFinder.RequiredLookahead + 1)
            {
                int chain = length >= p.Good ? p.Chain >> 2 : p.Chain;
                int next = position + 1;
                int available2 = fill - next;
                int length2 = finder.FindLongestUnchecked(window, next, Math.Min(DeflateTables.MaxMatchLength, available2), length, chain, p.Nice, _oldest, out int distance2);
                if (length2 > length && !(length2 == DeflateTables.MinMatchLength && distance2 > TooFar))
                {
                    _sequences.AddLiteralUnchecked(window[position]);
                    position = next;
                    length = length2;
                    distance = distance2;
                    if (_sequences.Count >= _checkAt)
                    {
                        _position = position;
                        CheckBlock();
                    }

                    continue;
                }

                // The look-ahead position is hashed too; remember that so it is not inserted twice.
                _sequences.AddMatchUnchecked(length, distance);
                int matchEnd = position + length;
                for (int q = next + 1; q < matchEnd && q < insertEnd; q++)
                {
                    finder.InsertUnchecked(window, q);
                }

                position = matchEnd;
                length = 0;
                break;
            }

            if (length != 0)
            {
                // Took the match without the look-ahead (long enough, or no room).
                _sequences.AddMatchUnchecked(length, distance);
                int matchEnd = position + length;
                for (int q = position + 1; q < matchEnd && q < insertEnd; q++)
                {
                    finder.InsertUnchecked(window, q);
                }

                position = matchEnd;
            }

            if (_sequences.Count >= _checkAt)
            {
                _position = position;
                CheckBlock();
            }
        }

        _position = position;
    }

    // Replaced by the optimal parser in the next step; until then the deepest lazy search stands in.
    private void ParseOptimal(int limit) => ParseLazy(limit);
}
