using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneCompress.Brotli;

internal sealed partial class BrotliEncoderCore
{
    // Cost model of the greedy and lazy parses, in 1/16 bit: what a literal costs on average, and what a command and its distance cost.
    private const int LiteralCost = 90;
    private const int CommandCost = 144;      // a copy at the last distance: command symbol and extra bits
    private const int RingDistanceCost = 200; // a copy at one of the other recent distances
    private const int DistanceCost = 224;     // a copy at a new distance, before its extra bits (16 per bit)

    private readonly BrotliDictionaryIndex.Hit[] _hits = new BrotliDictionaryIndex.Hit[16];
    private readonly byte[] _transformed = new byte[BrotliTransforms.MaxTransformedLength];
    private int[] _head = [];
    private int[] _chain = [];
    private int _hashShift;
    private int _hashedUpTo;
    private int _blockEnd;

    private struct Match
    {
        internal int Length;   // the copy length symbol value
        internal int Covered;  // bytes of input the match accounts for
        internal int Distance; // the distance, or the encoded dictionary distance
        internal bool Dictionary;
        internal int Score;    // estimated savings in 1/16 bit; a match is only taken if positive
    }

    private void InitMatchFinder()
    {
        _hashShift = 32 - _plan.HashBits;
        _head = new int[1 << _plan.HashBits];
        Array.Fill(_head, -1);
        _chain = [];
        _hashedUpTo = 0;
    }

    private void ResetMatchFinder()
    {
        Array.Fill(_head, -1);
        _hashedUpTo = 0;
    }

    private void ResizeChain(int size)
    {
        if (_plan.ChainDepth > 1)
        {
            Array.Resize(ref _chain, _plan.BinaryTree ? 2 * size : size);
        }
    }

    // Data moved down by shift bytes: positions move with it and those that fell off the front are forgotten.
    private void Rebase(int shift)
    {
        for (int i = 0; i < _head.Length; i++)
        {
            int p = _head[i];
            _head[i] = p >= shift ? p - shift : -1;
        }

        if (_chain.Length > 0)
        {
            int keep = Math.Max(0, _length);
            int unit = _plan.BinaryTree ? 2 : 1;
            for (int i = 0; i < keep * unit; i++)
            {
                int p = _chain[i + (shift * unit)];
                _chain[i] = p >= shift ? p - shift : -1;
            }
        }

        _hashedUpTo = Math.Max(0, _hashedUpTo - shift);
    }

    private int Hash(int position) => (int)((BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(position)) * 0x1E35A7BDu) >> _hashShift);

    private void Insert(int position)
    {
        if (_plan.BinaryTree)
        {
            var unused = new Match();
            TreeInsert(position, _plan.ChainDepth, false, true, ref unused);
            return;
        }

        int h = Hash(position);
        if (_chain.Length > 0)
        {
            _chain[position] = _head[h];
        }

        _head[h] = position;
    }

    // Enters every position below limit that has not been entered yet and has four bytes inside the block.
    private void HashUpTo(int limit)
    {
        int p = _hashedUpTo;
        int need = _plan.BinaryTree ? _plan.NiceLength : 4;
        while (p < limit && p + need <= _blockEnd)
        {
            Insert(p);
            p++;
        }

        _hashedUpTo = p;
    }

    // Binary tree match finder. Every entered position is a node of a tree keyed by the string that starts there; the tree of a hash bucket
    // is re-rooted at each new position, so the nodes met on the way down are the candidates, ordered from recent to old only roughly but
    // with ever longer common prefixes. _chain holds two links per position: smaller (2p) and larger (2p + 1). Comparisons stop at the
    // nice length, so a node whose string agrees that far is replaced by the new position, which inherits its subtrees.
    private void TreeInsert(int pos, int depth, bool record, bool enter, ref Match best)
    {
        ReadOnlySpan<byte> buffer = _buffer;
        int maxLength = Math.Min(_plan.NiceLength, _blockEnd - pos);
        int maxDistance = (int)Math.Min(_window, _streamBase + pos);
        // The caller enters only positions whose comparisons stay inside the data known now: the order of a string against the others
        // would otherwise be decided by bytes that are not there yet. The others are only searched, and entered with the next block.
        int h = Hash(pos);
        int cur = _head[h];
        if (enter)
        {
            _head[h] = pos;
        }

        int[] son = _chain;
        int lessSlot = 2 * pos;
        int greaterSlot = (2 * pos) + 1;
        int lessLength = 0;
        int greaterLength = 0;
        int longest = 3;
        while (true)
        {
            int distance = pos - cur;
            if (cur < 0 || distance > maxDistance || depth-- <= 0)
            {
                if (enter)
                {
                    son[lessSlot] = -1;
                    son[greaterSlot] = -1;
                }

                return;
            }

            int len = CommonLengthFrom(cur, pos, Math.Min(lessLength, greaterLength), maxLength);
            // The length rests on the prefix the tree order implies; a match is only taken once its bytes have been compared.
            if (record && len > longest && CommonLength(cur, pos, len) == len)
            {
                longest = len;
                Consider(ref best, len, len, distance, false, DistanceCost + (16 * DistanceBits(distance)));
            }

            if (len >= maxLength)
            {
                if (enter)
                {
                    son[lessSlot] = son[2 * cur];
                    son[greaterSlot] = son[(2 * cur) + 1];
                }

                return;
            }

            if (buffer[cur + len] < buffer[pos + len])
            {
                if (enter)
                {
                    son[lessSlot] = cur;
                }

                lessSlot = (2 * cur) + 1;
                lessLength = len;
                cur = son[lessSlot];
            }
            else
            {
                if (enter)
                {
                    son[greaterSlot] = cur;
                }

                greaterSlot = 2 * cur;
                greaterLength = len;
                cur = son[greaterSlot];
            }
        }
    }

    private int CommonLengthFrom(int a, int b, int start, int max)
    {
        ReadOnlySpan<byte> s = _buffer;
        int len = start;
        while (len + 8 <= max)
        {
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(a + len)) ^ BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(b + len));
            if (x != 0)
            {
                return len + (BitOperations.TrailingZeroCount(x) >> 3);
            }

            len += 8;
        }

        while (len < max && s[a + len] == s[b + len])
        {
            len++;
        }

        return len;
    }

    private int CommonLength(int a, int b, int max)
    {
        ReadOnlySpan<byte> s = _buffer;
        int len = 0;
        while (len + 8 <= max)
        {
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(a + len)) ^ BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(b + len));
            if (x != 0)
            {
                return len + (BitOperations.TrailingZeroCount(x) >> 3);
            }

            len += 8;
        }

        while (len < max && s[a + len] == s[b + len])
        {
            len++;
        }

        return len;
    }

    private static int DistanceBits(int distance) => Math.Max(0, (32 - BitOperations.LeadingZeroCount((uint)distance)) - 2);

    private static void Consider(ref Match best, int length, int covered, int distance, bool dictionary, int cost)
    {
        int score = (covered * LiteralCost) - cost;
        if (score > best.Score || (score == best.Score && covered > best.Covered))
        {
            best = new Match { Length = length, Covered = covered, Distance = distance, Dictionary = dictionary, Score = score };
        }
    }

    // The best match at a position, given the four last distances. Also enters the position in the tables.
    private Match FindMatch(int pos, int end, ReadOnlySpan<int> ring, bool lazyProbe = false)
    {
        var best = new Match { Score = 0 };
        int maxLength = end - pos;
        long streamPosition = _streamBase + pos;
        int maxDistance = (int)Math.Min(_window, streamPosition);

        // The recent distances cost the least to name, so even three bytes are worth a copy.
        for (int r = 0; r < 4; r++)
        {
            int d = ring[r];
            if (d > maxDistance || maxLength < 3)
            {
                continue;
            }

            int len = CommonLength(pos - d, pos, maxLength);
            if (len >= 3)
            {
                Consider(ref best, len, len, d, false, r == 0 ? CommandCost : RingDistanceCost);
            }
        }

        if (pos + 4 <= end)
        {
            HashUpTo(pos);
            if (_plan.BinaryTree)
            {
                bool enter = _hashedUpTo == pos && pos + _plan.NiceLength <= _blockEnd;
                TreeInsert(pos, lazyProbe ? Math.Max(1, _plan.ChainDepth >> 1) : _plan.ChainDepth, true, enter, ref best);
                if (enter)
                {
                    _hashedUpTo = pos + 1;
                }
            }
            else
            {
                ChainSearch(pos, end, maxLength, maxDistance, lazyProbe, ref best);
                if (_hashedUpTo == pos)
                {
                    Insert(pos);
                    _hashedUpTo = pos + 1;
                }
            }

            if (_plan.Dictionary && best.Length < 12)
            {
                Span<BrotliDictionaryIndex.Hit> hits = _hits;
                int n = BrotliDictionaryIndex.Instance.Search(_buffer.AsSpan(0, end), pos, maxLength, _plan.DictionaryTransforms, hits, _transformed);
                for (int i = 0; i < n; i++)
                {
                    // RFC 7932 section 8: the distance is beyond everything a copy can reach, by the word's id.
                    long distance = maxDistance + 1L + hits[i].WordId;
                    Consider(ref best, hits[i].WordLength, hits[i].Covered, (int)distance, true, DistanceCost + (16 * DistanceBits((int)distance)));
                }
            }
        }

        return best;
    }

    private void ChainSearch(int pos, int end, int maxLength, int maxDistance, bool lazyProbe, ref Match best)
    {
        ReadOnlySpan<byte> buffer = _buffer;
        int h = Hash(pos);
        int candidate = _head[h];
        int depth = lazyProbe ? Math.Max(1, _plan.ChainDepth >> 2) : _plan.ChainDepth;
        uint prefix = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(pos));
        int bestLen = best.Dictionary ? 0 : best.Length;
        while (candidate >= 0 && depth-- > 0)
        {
            int d = pos - candidate;
            if (d > maxDistance)
            {
                break;
            }

            // A longer match than the best one must agree at the byte just past it, and every match starts with the same four bytes.
            if ((bestLen == 0 || (bestLen < maxLength && buffer[candidate + bestLen] == buffer[pos + bestLen]))
                && BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(candidate)) == prefix)
            {
                int len = CommonLength(candidate, pos, maxLength);
                Consider(ref best, len, len, d, false, DistanceCost + (16 * DistanceBits(d)));
                if (!best.Dictionary)
                {
                    bestLen = best.Length;
                }

                if (len >= _plan.NiceLength)
                {
                    break;
                }
            }

            if (_chain.Length == 0)
            {
                break;
            }

            candidate = _chain[candidate];
        }
    }

    private void Parse(int start, int end)
    {
        _blockEnd = end;
        Span<int> ring = stackalloc int[4];
        _ring.AsSpan(0, 4).CopyTo(ring);
        int pos = start;
        int insertStart = start;
        bool skipping = _plan.ChainDepth <= 1;
        while (pos < end)
        {
            Match m = FindMatch(pos, end, ring);
            if (m.Score <= 0)
            {
                pos += skipping ? 1 + ((pos - insertStart) >> 6) : 1;
                continue;
            }

            // Lazy matching: a match one or two bytes further on may be worth more than this one by more than the literals it leaves behind.
            while (_plan.Lazy > 0 && m.Covered < 64 && pos + 1 < end)
            {
                Match next = FindMatch(pos + 1, end, ring, true);
                if (next.Score > m.Score + LiteralCost)
                {
                    pos++;
                    m = next;
                    continue;
                }

                if (_plan.Lazy > 1 && pos + 2 < end)
                {
                    Match after = FindMatch(pos + 2, end, ring, true);
                    if (after.Score > m.Score + (2 * LiteralCost))
                    {
                        pos += 2;
                        m = after;
                        continue;
                    }
                }

                break;
            }

            _commands.Add(pos - insertStart, m.Length, m.Distance, m.Covered, m.Dictionary);
            if (!m.Dictionary && m.Distance != ring[0])
            {
                ring[3] = ring[2];
                ring[2] = ring[1];
                ring[1] = ring[0];
                ring[0] = m.Distance;
            }

            pos += m.Covered;
            insertStart = pos;
            if (_plan.ChainDepth > 1 || m.Covered < 32)
            {
                HashUpTo(pos);
            }
            else
            {
                _hashedUpTo = Math.Max(_hashedUpTo, pos);
            }
        }

        if (insertStart < end)
        {
            _commands.Add(end - insertStart, 0, 0, 0, false);
        }
    }
}
