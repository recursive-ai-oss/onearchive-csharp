using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Deflate64;

public sealed partial class Deflate64Encoder
{
    // What one level asks of the parser (docs/deflate64-encoder.md has the table).
    private readonly record struct LevelParameters(bool Lazy, int Chain, int Nice, int MaxLazy, int Good)
    {
        public static LevelParameters For(int level) => level switch
        {
            0 => default,
            1 => new(false, 2, 16, 0, 0),
            2 => new(false, 4, 32, 0, 0),
            3 => new(false, 16, 64, 0, 0),
            4 => new(true, 8, 32, 16, 8),
            5 => new(true, 16, 64, 32, 16),
            6 => new(true, 32, 128, 64, 32),
            7 => new(true, 128, 258, 128, 64),
            8 => new(true, 512, 258, 258, 128),
            _ => new(true, 2048, 1024, 1024, 256),
        };
    }

    // A match of three bytes farther back than this costs more bits than three literals, so it is not taken.
    private const int TooFar = 4096;

    // A match longer than this enters only its last this-many positions into the finder: inside a long repeat every position
    // would find the same earlier copy anyway.
    private const int MaxInsert = 512;

    private int[] _head = [];
    private ushort[] _previous = [];
    private int[] _tokenLength = [];
    private int[] _tokenValue = [];
    private int _tokenCount;
    private int _blockStart;
    private int _blockBytes;

    private void ResetFinder()
    {
        _head.AsSpan().Fill(Null);
        _previous.AsSpan().Clear();
        _tokenCount = 0;
        _blockBytes = 0;
        _blockStart = 0;
    }

    // After the caller moved the window's contents down by delta: positions are window indices.
    private void SlideFinder(int delta)
    {
        int[] head = _head;
        for (int i = 0; i < head.Length; i++)
        {
            int v = head[i] - delta;
            head[i] = v < 0 ? Null : v;
        }

        _blockStart -= delta;
    }

    private int Hash(int position)
    {
        byte[] w = _window;
        uint v = (uint)(w[position] | (w[position + 1] << 8) | (w[position + 2] << 16));
        return (int)((v * 2654435761u) >> (32 - HashBits));
    }

    // Enters position into the finder and returns the nearest earlier position with the same hash (or Null).
    private int Insert(int position)
    {
        int hash = Hash(position);
        int candidate = _head[hash];
        _head[hash] = position;
        int delta = position - candidate;
        _previous[position & (HistorySize - 1)] = candidate >= 0 && delta <= MaxDistance ? (ushort)delta : (ushort)0;
        return candidate;
    }

    // The longest match at position that is longer than `best`, searching the chain from `candidate` (already looked up by Insert).
    private int Search(int position, int candidate, int best, int maxLength, int chain, out int distance)
    {
        distance = 0;
        byte[] w = _window;
        int limit = Math.Max(0, position - MaxDistance);
        int nice = Math.Min(_parameters.Nice, maxLength);
        while (candidate >= limit && candidate >= 0)
        {
            if (best < maxLength && w[candidate + best] == w[position + best] && w[candidate] == w[position])
            {
                int length = DeflateEncRaw.MatchLength(w, candidate, position, maxLength);
                if (length > best)
                {
                    best = length;
                    distance = position - candidate;
                    if (length >= nice)
                    {
                        break;
                    }
                }
            }

            if (--chain <= 0)
            {
                break;
            }

            int step = _previous[candidate & (HistorySize - 1)];
            if (step == 0)
            {
                break;
            }

            candidate -= step;
        }

        return distance == 0 ? 0 : best;
    }

    private void PutLiteral(byte value)
    {
        _tokenLength[_tokenCount] = 0;
        _tokenValue[_tokenCount++] = value;
        _blockBytes++;
    }

    private void PutMatch(int length, int distance)
    {
        _tokenLength[_tokenCount] = length;
        _tokenValue[_tokenCount++] = distance;
        _blockBytes += length;
    }

    private void CheckBlock()
    {
        if (_tokenCount >= TokenCapacity)
        {
            CloseBlock(final: false);
        }
    }

    // Enters positions first..last-1 (those with three bytes behind them) into the finder.
    private void InsertRange(int first, int last, int hashEnd)
    {
        last = Math.Min(last, hashEnd);
        first = Math.Max(first, last - MaxInsert);
        for (int p = first; p < last; p++)
        {
            Insert(p);
        }
    }

    // Parses window[_position..limit) into tokens, closing a block whenever the token arrays fill.
    private void Parse(int limit)
    {
        int position = _position;
        int hashEnd = limit - 2; // positions below this have three bytes to hash
        LevelParameters p = _parameters;
        while (position < limit)
        {
            int length = 0;
            int distance = 0;
            int hashedTo = position;
            if (position < hashEnd)
            {
                int maxLength = Math.Min(Deflate64Tables.MaxMatchLength, limit - position);
                int candidate = Insert(position);
                hashedTo = position + 1;
                length = Search(position, candidate, 2, maxLength, p.Chain, out distance);
                if (length == 3 && distance > TooFar)
                {
                    length = 0;
                }
            }

            if (length < Deflate64Tables.MinMatchLength)
            {
                PutLiteral(_window[position++]);
                CheckBlock();
                continue;
            }

            if (p.Lazy)
            {
                // Try one byte further while this match is short: a longer one there wins, at the price of one literal.
                while (length < p.MaxLazy && position + 1 < hashEnd)
                {
                    int next = position + 1;
                    int maxLength = Math.Min(Deflate64Tables.MaxMatchLength, limit - next);
                    int candidate = Insert(next);
                    hashedTo = next + 1;
                    int chain = length >= p.Good ? p.Chain >> 2 : p.Chain;
                    int length2 = Search(next, candidate, length, maxLength, chain, out int distance2);
                    if (length2 > length && !(length2 == 3 && distance2 > TooFar))
                    {
                        PutLiteral(_window[position]);
                        CheckBlock();
                        position = next;
                        length = length2;
                        distance = distance2;
                        continue;
                    }

                    break;
                }
            }

            PutMatch(length, distance);
            int end = position + length;
            InsertRange(Math.Max(hashedTo, position + 1), end, hashEnd);
            position = end;
            CheckBlock();
        }

        _position = position;
    }
}
