using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Deflate64;

public sealed partial class Deflate64Encoder
{
    // What one level asks of the parser (docs/deflate64-encoder.md has the table).
    private readonly record struct LevelParameters(bool Lazy, int Chain, int Nice, int MaxLazy, int Good, int Passes = 0)
    {
        public static LevelParameters For(int level) => level switch
        {
            0 => default,
            <= 4 => new(true, 16, 32, 16, 8),
            <= 6 => new(false, 16, 64, 0, 0, 1),
            <= 8 => new(false, 32, 128, 0, 0, 1),
            _ => new(false, 256, 257, 0, 0, 3),
        };
    }

    // A match longer than this enters only its last this-many positions into the finder: inside a long repeat every position
    // would find the same earlier copy anyway.
    private const int MaxInsert = 64;

    private int[] _head = [];
    private ushort[] _previous = [];
    private int[] _tokenLength = [];
    private int[] _tokenValue = [];
    private int _tokenCount;
    private int _blockStart;
    private int _blockBytes;
    private readonly DeflateBlockSplitter _splitter = new(2048, 300);
    private readonly uint[] _splitFrequencies = new uint[DeflateSequenceBuffer.LiteralLengthSymbolCount];
    private int _checkAt = CheckInterval;

    private void ResetFinder()
    {
        if (_optimal is not null)
        {
            _optimal.Reset();
        }

        _head.AsSpan().Fill(Null);
        _previous.AsSpan().Clear();
        _tokenCount = 0;
        _blockBytes = 0;
        _blockStart = 0;
        ResetSplit();
    }

    // After the caller moved the window's contents down by delta: positions are window indices.
    private void SlideFinder(int delta)
    {
        if (_optimal is not null)
        {
            _optimal.Finder.Slide(delta);
            _blockStart -= delta;
            return;
        }

        int[] head = _head;
        for (int i = 0; i < head.Length; i++)
        {
            int v = head[i] - delta;
            head[i] = v < 0 ? Null : v;
        }

        _blockStart -= delta;
    }

    // The caller guarantees position + 2 is inside the filled window (positions are hashed only below hashEnd).
    private int Hash(int position)
    {
        byte[] w = _window;
        uint v = (uint)(DeflateEncRaw.Byte(w, position) | (DeflateEncRaw.Byte(w, position + 1) << 8) | (DeflateEncRaw.Byte(w, position + 2) << 16));
        return (int)((v * 2654435761u) >> (32 - HashBits));
    }

    // Enters position into the finder and returns the nearest earlier position with the same hash (or Null).
    private int Insert(int position)
    {
        int hash = Hash(position);
        // hash is shifted down to HashBits, the length of _head; the link index is masked to the window, the length of _previous.
        int candidate = DeflateEncRaw.Get(_head, hash);
        DeflateEncRaw.Set(_head, hash, position);
        int delta = position - candidate;
        DeflateEncRaw.SetLink(_previous, position & (HistorySize - 1), candidate >= 0 && delta <= MaxDistance ? (ushort)delta : (ushort)0);
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
            // best < maxLength <= the bytes left after position, and candidate < position, so every read is inside the filled window.
            if (best < maxLength && DeflateEncRaw.Byte(w, candidate + best) == DeflateEncRaw.Byte(w, position + best) && DeflateEncRaw.Byte(w, candidate) == DeflateEncRaw.Byte(w, position))
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

            int step = DeflateEncRaw.GetLink(_previous, candidate & (HistorySize - 1));
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
        _splitFrequencies[value]++;
        _blockBytes++;
    }

    private void PutMatch(int length, int distance)
    {
        _tokenLength[_tokenCount] = length;
        _tokenValue[_tokenCount++] = distance;
        _splitFrequencies[257 + LengthSlot(length)]++;
        _blockBytes += length;
    }

    private void ResetSplit()
    {
        Array.Clear(_splitFrequencies);
        _splitter.Reset();
        _checkAt = CheckInterval;
    }

    // Ends the block when it is full or when the mix of symbols has drifted far enough that a new Huffman code pays for its header.
    private void CheckBlock()
    {
        if (_tokenCount >= TokenCapacity)
        {
            CloseBlock(final: false);
        }
        else if (_tokenCount >= _checkAt)
        {
            if (_splitter.Observe(_splitFrequencies))
            {
                CloseBlock(final: false);
            }
            else
            {
                _checkAt = _tokenCount + CheckInterval;
            }
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
        if (_optimal is not null)
        {
            ParseOptimal(limit);
            return;
        }

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
                    if (length2 > length)
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
