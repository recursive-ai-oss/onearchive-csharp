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

    private int[] _head = [];
    private int[] _bucketPositions = [];
    private int[] _bucketCounts = [];
    private int[] _longPositions = [];
    private int[] _longCounts = [];
    private int[] _chain = [];
    private int _hashShift;
    private int _hashedUpTo;
    private int _blockEnd;

    private void Parse(int start, int end)
    {
        _blockEnd = end;
        if (_plan.Optimal)
        {
            ParseOptimal(start, end);
        }
        else if (_plan.Ways == 2)
        {
            ParseTwoWay(start, end);
        }
        else if (_plan.Ways == 4)
        {
            ParseFourWay(start, end);
        }
        else if (_plan.ChainDepth <= 1)
        {
            ParseFast(start, end);
        }
        else
        {
            ParseLazy(start, end);
        }
    }

    private void InitMatchFinder()
    {
        _hashShift = 32 - _plan.HashBits;
        if (_plan.UsesFastBlock)
        {
            // Position and the four bytes there, per slot, one slot per bucket (qualities 0 and 1) or two (quality 2).
            _head = new int[(2 * Math.Max(1, _plan.Ways)) << _plan.HashBits];
            Array.Fill(_head, -1);
        }
        else if (_plan.BinaryTree)
        {
            _head = new int[1 << _plan.HashBits];
            Array.Fill(_head, -1);
        }
        else if (_plan.ChainDepth > 1)
        {
            _bucketPositions = new int[1 << (_plan.HashBits + _plan.BucketBits)];
            _bucketCounts = new int[1 << _plan.HashBits];
            Array.Fill(_bucketPositions, -1);
            if (_plan.LongHashBits > 0)
            {
                _longPositions = new int[1 << (_plan.LongHashBits + _plan.LongBucketBits)];
                _longCounts = new int[1 << _plan.LongHashBits];
                Array.Fill(_longPositions, -1);
            }
        }

        _chain = [];
        _hashedUpTo = 0;
    }

    private void ResetMatchFinder()
    {
        Array.Fill(_head, -1);
        if (_bucketPositions.Length > 0)
        {
            Array.Fill(_bucketPositions, -1);
            Array.Clear(_bucketCounts);
        }

        if (_longPositions.Length > 0)
        {
            Array.Fill(_longPositions, -1);
            Array.Clear(_longCounts);
        }

        _hashedUpTo = 0;
    }

    private void ResizeChain(int size)
    {
        if (_plan.BinaryTree)
        {
            Array.Resize(ref _chain, 2 * size);
        }
    }

    // Data moved down by shift bytes: positions move with it and those that fell off the front are forgotten.
    private void Rebase(int shift)
    {
        // The table of qualities 0 and 1 holds a position and four bytes per slot; only the positions move.
        int step = _plan.UsesFastBlock ? 2 : 1;
        foreach (int[] table in new[] { _head, _bucketPositions, _longPositions })
        {
            for (int i = 0; i < table.Length; i += step)
            {
                int p = table[i];
                table[i] = p >= shift ? p - shift : -1;
            }
        }

        if (_chain.Length > 0)
        {
            int keep = Math.Max(0, _length);
            for (int i = 0; i < keep * 2; i++)
            {
                int p = _chain[i + (shift * 2)];
                _chain[i] = p >= shift ? p - shift : -1;
            }
        }

        _hashedUpTo = Math.Max(0, _hashedUpTo - shift);
    }

    private int Hash(int position) => (int)((BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(position)) * 0x1E35A7BDu) >> _hashShift);

    private void Insert(int position)
    {
        var unused = new Match();
        TreeInsert(position, _plan.ChainDepth, false, true, ref unused);
    }

    // Enters every position below limit that has not been entered yet and has four bytes inside the block.
    private void HashUpTo(int limit)
    {
        int p = _hashedUpTo;
        int need = _plan.NiceLength;
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
                if (_collecting)
                {
                    AddCandidate(len, distance, len);
                }
                else
                {
                    // The comparison stops at the nice length; a longer agreement is measured only for a match that is going to be considered.
                    int full = len >= _plan.NiceLength ? CommonLength(cur, pos, _blockEnd - pos) : len;
                    Consider(ref best, full, full, distance, false, DistanceCost + (16 * DistanceBits(distance)));
                }
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

}
