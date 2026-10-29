using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

internal sealed partial class BrotliEncoderCore
{
    private const int PrefetchDistance = 8;

    private readonly BrotliDictionaryIndex.Hit[] _hits = new BrotliDictionaryIndex.Hit[16];
    private readonly byte[] _transformed = new byte[BrotliTransforms.MaxTransformedLength];

    private struct Match
    {
        internal int Length;   // the copy length symbol value
        internal int Covered;  // bytes of input the match accounts for
        internal int Distance; // the distance, or the encoded dictionary distance
        internal bool Dictionary;
        internal int Score;    // estimated savings in 1/16 bit; a match is only taken if positive
    }

    // Bucketed hash table: every hash value owns 2^BucketBits slots that hold the most recent positions with that hash, oldest overwritten
    // first. The count of entries ever made in a bucket says where the next goes and where the newest is.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BucketInsert(int position)
    {
        int h = BucketHash(position);
        ref int count = ref BrotliEncRaw.Slot(_bucketCounts, h);
        BrotliEncRaw.Slot(_bucketPositions, (h << _plan.BucketBits) + (count & ((1 << _plan.BucketBits) - 1))) = position;
        count++;
        if (_plan.LongHashBits > 0)
        {
            int g = LongHash(position);
            ref int longCount = ref BrotliEncRaw.Slot(_longCounts, g);
            BrotliEncRaw.Slot(_longPositions, (g << _plan.LongBucketBits) + (longCount & ((1 << _plan.LongBucketBits) - 1))) = position;
            longCount++;
        }
    }

    // The bucket of a position in the long table: a hash of its first twelve bytes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LongHash(int position) =>
        (int)(((BrotliEncRaw.Load64(_buffer, position) * 0x9E3779B185EBCA87UL) ^ (BrotliEncRaw.Load32(_buffer, position + 8) * 0xC2B2AE3D27D4EB4FUL)) >> (64 - _plan.LongHashBits));

    // The bucket of a position: a multiplicative hash of its first HashLength bytes (4, or 5 to 8 read as one 64-bit word).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BucketHash(int position) => _plan.HashLength == 4
        ? (int)((BrotliEncRaw.Load32(_buffer, position) * 0x1E35A7BDu) >> _hashShift)
        : (int)(((BrotliEncRaw.Load64(_buffer, position) << (8 * (8 - _plan.HashLength))) * 0x9E3779B185EBCA87UL) >> (64 - _plan.HashBits));

    // Positions from here on do not have all the bytes their hash reads inside the block; hashing them would read bytes that are not
    // input yet, so they are entered with the next block instead.
    private int HashableEnd => _blockEnd - (_plan.LongHashBits > 0 ? 11 : _plan.HashLength == 4 ? 3 : 7);

    // Brings the table lines a later position will use into the cache while the current one is being searched: the tables are far larger
    // than the cache, and a search waits on its bucket otherwise. The position must have 12 bytes inside the block.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrefetchBuckets(int position)
    {
        int h = BucketHash(position);
        BrotliEncRaw.Prefetch(_bucketCounts, h);
        BrotliEncRaw.Prefetch(_bucketPositions, h << _plan.BucketBits);
        if (_plan.LongHashBits > 0)
        {
            int g = LongHash(position);
            BrotliEncRaw.Prefetch(_longCounts, g);
            BrotliEncRaw.Prefetch(_longPositions, g << _plan.LongBucketBits);
        }
    }

    // Enters every position below limit that has not been entered yet and has enough bytes inside the block to be hashed.
    private void BucketUpTo(int limit)
    {
        int p = _hashedUpTo;
        int stop = Math.Min(limit, HashableEnd);
        int ahead = _blockEnd - 12;
        while (p < stop)
        {
            if (p + PrefetchDistance <= ahead)
            {
                PrefetchBuckets(p + PrefetchDistance);
            }

            BucketInsert(p);
            p++;
        }

        _hashedUpTo = p;
    }

    // The parse of qualities 2 to 9: greedy or lazy over a bucketed hash table, scored by an estimate of the bits a match saves.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ParseLazy(int start, int end)
    {
        Span<int> ring = stackalloc int[4];
        _ring.AsSpan(0, 4).CopyTo(ring);
        int pos = start;
        int insertStart = start;
        while (pos < end)
        {
            Match m = FindBucketed(pos, end, ring, false);
            if (m.Score <= 0)
            {
                int step = 1 + ((pos - insertStart) >> _plan.SkipShift);
                pos += step;
                if (_plan.SparseInsert > 0 && step > 1)
                {
                    // Positions stepped over are not entered; the one before the next probe is.
                    _hashedUpTo = Math.Max(_hashedUpTo, pos - 1);
                }

                continue;
            }

            // Lazy matching: a match one or two bytes further on may be worth more than this one by more than the literals it leaves behind.
            while (_plan.Lazy > 0 && m.Covered < 64 && pos + 1 < end)
            {
                Match next = FindBucketed(pos + 1, end, ring, true);
                if (next.Score > m.Score + LiteralCost)
                {
                    pos++;
                    m = next;
                    continue;
                }

                if (_plan.Lazy > 1 && pos + 2 < end)
                {
                    Match after = FindBucketed(pos + 2, end, ring, true);
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
            if (_plan.SparseInsert > 0 && m.Covered >= _plan.SparseInsert)
            {
                // Inside a long copy only the last two positions are entered, so that the text after it can find the copy's tail.
                _hashedUpTo = Math.Max(_hashedUpTo, pos - 2);
            }

            BucketUpTo(pos);
        }

        if (insertStart < end)
        {
            _commands.Add(end - insertStart, 0, 0, 0, false);
        }
    }

    // The best match at a position, given the four last distances. Also enters the position in the table.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private Match FindBucketed(int pos, int end, ReadOnlySpan<int> ring, bool lazyProbe)
    {
        byte[] buffer = _buffer;
        var best = new Match { Score = 0 };
        int maxLength = end - pos;
        int maxDistance = (int)Math.Min(_window, _streamBase + pos);

        // The recent distances cost the least to name, so even three bytes are worth a copy.
        if (maxLength >= 4)
        {
            // Most positions have no copy at a recent distance; three bytes compared in one load rule that out without a length scan.
            uint first3 = BrotliEncRaw.Load32(buffer, pos) & 0xFFFFFF;
            for (int r = 0; r < 4; r++)
            {
                int d = ring[r];
                if (d > maxDistance || (BrotliEncRaw.Load32(buffer, pos - d) & 0xFFFFFF) != first3)
                {
                    continue;
                }

                int len = BrotliEncRaw.CommonLength(buffer, pos - d, pos, 3, maxLength);
                Consider(ref best, len, len, d, false, r == 0 ? CommandCost : RingDistanceCost);
            }
        }
        else if (maxLength >= 3)
        {
            for (int r = 0; r < 4; r++)
            {
                int d = ring[r];
                if (d > maxDistance)
                {
                    continue;
                }

                int len = BrotliEncRaw.CommonLength(buffer, pos - d, pos, 0, maxLength);
                if (len >= 3)
                {
                    Consider(ref best, len, len, d, false, r == 0 ? CommandCost : RingDistanceCost);
                }
            }
        }

        int hashLength = _plan.HashLength;
        if (pos + (hashLength == 4 ? 4 : 8) <= end)
        {
            BucketUpTo(pos);
            if (pos + PrefetchDistance + 12 <= end)
            {
                PrefetchBuckets(pos + PrefetchDistance);
            }

            ulong word = BrotliEncRaw.Load64Or32(buffer, pos, hashLength);
            int h = BucketHash(pos);
            int bucketBits = _plan.BucketBits;
            int mask = (1 << bucketBits) - 1;
            int[] positions = _bucketPositions;
            int count = BrotliEncRaw.Slot(_bucketCounts, h);
            int depth = lazyProbe ? Math.Max(1, _plan.ChainDepth >> 1) : _plan.ChainDepth;
            int bestLen = best.Dictionary ? 0 : best.Length;
            if (_plan.LongHashBits > 0 && pos + 12 <= end)
            {
                // Long matches first: the newest few positions that share twelve bytes' hash.
                int g = LongHash(pos);
                int longMask = (1 << _plan.LongBucketBits) - 1;
                int longCount = BrotliEncRaw.Slot(_longCounts, g);
                int longDepth = lazyProbe ? Math.Max(1, _plan.LongDepth >> 1) : _plan.LongDepth;
                ulong first = BrotliEncRaw.Load64(buffer, pos);
                for (int i = 1; i <= longDepth; i++)
                {
                    int candidate = BrotliEncRaw.Slot(_longPositions, (g << _plan.LongBucketBits) + ((longCount - i) & longMask));
                    if (candidate < 0 || pos - candidate > maxDistance)
                    {
                        break;
                    }

                    if ((bestLen == 0 || (bestLen < maxLength && BrotliEncRaw.At(buffer, candidate + bestLen) == BrotliEncRaw.At(buffer, pos + bestLen)))
                        && BrotliEncRaw.Load64(buffer, candidate) == first)
                    {
                        int len = BrotliEncRaw.CommonLength(buffer, candidate, pos, 8, maxLength);
                        Consider(ref best, len, len, pos - candidate, false, DistanceCost + (16 * DistanceBits(pos - candidate)));
                        if (!best.Dictionary)
                        {
                            bestLen = best.Length;
                        }

                        if (len >= _plan.NiceLength)
                        {
                            break;
                        }
                    }
                }
            }

            for (int i = 1; i <= depth; i++)
            {
                int candidate = BrotliEncRaw.Slot(positions, (h << bucketBits) + ((count - i) & mask));
                if (candidate < 0)
                {
                    break;
                }

                int d = pos - candidate;
                if (d > maxDistance)
                {
                    break;
                }

                // A longer match than the best one must agree at the byte just past it, and every match starts with the same four bytes.
                if ((bestLen == 0 || (bestLen < maxLength && BrotliEncRaw.At(buffer, candidate + bestLen) == BrotliEncRaw.At(buffer, pos + bestLen)))
                    && BrotliEncRaw.Load64Or32(buffer, candidate, hashLength) == word)
                {
                    int len = BrotliEncRaw.CommonLength(buffer, candidate, pos, hashLength, maxLength);
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
            }

            if (_hashedUpTo == pos && pos < HashableEnd)
            {
                BucketInsert(pos);
                _hashedUpTo = pos + 1;
            }

            if (_plan.Dictionary && best.Length < 12)
            {
                Span<BrotliDictionaryIndex.Hit> hits = _hits;
                // A dictionary copy costs at least DistanceCost plus the extra bits of the smallest distance it can have (RFC 7932 section 8),
                // so a word that covers fewer bytes than that cost pays for cannot beat the match found so far.
                int cheapest = DistanceCost + (16 * DistanceBits(maxDistance + 1));
                int minCovered = (best.Score + cheapest + LiteralCost - 1) / LiteralCost;
                BrotliDictionaryIndex index = BrotliDictionaryIndex.Instance;
                int n = _plan.DictionaryTransforms || minCovered < 7 || pos + 8 > end || index.MayHaveLongWord(buffer, pos)
                    ? index.Search(buffer.AsSpan(0, end), pos, maxLength, _plan.DictionaryTransforms, hits, _transformed, minCovered)
                    : 0;
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
}
