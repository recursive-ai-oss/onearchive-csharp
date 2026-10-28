using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

public sealed partial class LzmaEncoderCore
{
    // The fast parser: greedy with one step of lazy matching, but choosing by price. Among the repeated distances and the finder's
    // matches it takes the one that saves the most bits against coding the same bytes as literals, and it codes a literal instead
    // when the match at the next position would make the two-position plan cheaper.
    private void ParseFast(int horizon)
    {
        int p = _pos;
        int maxLen = Math.Min(Math.Min(_bufEnd - p, LzmaModel.MaxMatchLength), horizon);
        int n = MatchesAt(p, _matches);
        if (maxLen < 2)
        {
            EmitLiteralOrShortRep(p);
            AdvanceFinder(_pos);
            return;
        }

        if (_priceAge >= _rebuildInterval)
        {
            UpdatePrices();
        }

        int repMax = 0;
        for (int i = 0; i < 4; i++)
        {
            int l = RepLength(p, _reps[i], maxLen);
            _repLens[i] = l;
            repMax = Math.Max(repMax, l);
        }

        if (n == 0 && repMax < 2)
        {
            EmitLiteralOrShortRep(p);
            _priceAge++;
            AdvanceFinder(_pos);
            return;
        }

        int nice = Math.Min(_settings.NiceLength, maxLen);
        int posState = (int)((_posBase + p) & _pbMask);
        int literal = LzmaPrices.Zero(_probs[LzmaModel.IsMatch + (_state << 4) + posState]) + LiteralPrice(p, _state, _reps[0]);
        Choice best = Choose(p, n, _matches, _repLens, maxLen, _state, posState, literal);
        if (best.Length < 2)
        {
            EmitLiteralOrShortRep(p);
            _priceAge++;
            AdvanceFinder(_pos);
            return;
        }

        if (best.Length < nice && maxLen > 2 && horizon > 2)
        {
            int n2 = PeekNext(p);
            int state2 = LzmaModel.NextStateAfterLiteral(_state);
            int posState2 = (int)((_posBase + p + 1) & _pbMask);
            int maxLen2 = Math.Min(maxLen - 1, LzmaModel.MaxMatchLength);
            for (int i = 0; i < 4; i++)
            {
                _repLensNext[i] = RepLength(p + 1, _reps[i], maxLen2);
            }

            Choice next = Choose(p + 1, n2, _matchesNext, _repLensNext, maxLen2, state2, posState2, literal);
            if (next.Length >= 2)
            {
                int end = Math.Max(p + best.Length, p + 1 + next.Length);
                long now = best.Price + ((long)(end - p - best.Length) * literal);
                long later = literal + next.Price + ((long)(end - p - 1 - next.Length) * literal);
                if (later < now)
                {
                    EmitLiteralOrShortRep(p);
                    _priceAge++;
                    return;
                }
            }
        }

        if (best.Rep >= 0)
        {
            EmitRep(best.Rep, best.Length);
        }
        else
        {
            EmitMatch(best.Length, best.Distance);
        }

        _priceAge++;
        Finish();
    }

    private readonly struct Choice(int length, uint distance, int rep, int price)
    {
        public int Length { get; } = length;

        public uint Distance { get; } = distance;

        public int Rep { get; } = rep;

        public int Price { get; } = price;
    }

    // The candidate at position p (a repeated distance or one of the finder's matches) that saves the most bits over `literal` per byte.
    private Choice Choose(int p, int count, LzmaMatch[] matches, int[] repLens, int maxLen, int state, int posState, int literal)
    {
        int bestLength = 0;
        uint bestDistance = 0;
        int bestRep = -1;
        int bestPrice = 0;
        long bestSaved = 0;
        for (int i = 0; i < 4; i++)
        {
            int l = repLens[i];
            if (l >= 2)
            {
                int price = RepPrice(i, l, state, posState);
                long saved = ((long)l * literal) - price;
                if (saved > bestSaved)
                {
                    bestSaved = saved;
                    bestLength = l;
                    bestRep = i;
                    bestPrice = price;
                }
            }
        }

        for (int k = 0; k < count; k++)
        {
            int l = Math.Min(matches[k].Length, maxLen);
            if (l < 2)
            {
                continue;
            }

            uint d0 = (uint)matches[k].Distance - 1;
            int price = MatchPrice(l, d0, state, posState);
            long saved = ((long)l * literal) - price;
            if (saved >= bestSaved && saved > 0)
            {
                bestSaved = saved;
                bestLength = l;
                bestDistance = d0;
                bestRep = -1;
                bestPrice = price;
            }
        }

        return new Choice(bestLength, bestDistance, bestRep, bestPrice);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Finish()
    {
        _cacheCount = -1;
        AdvanceFinder(_pos);
    }

    private void EmitLiteralOrShortRep(int p)
    {
        int src = p - (int)_reps[0] - 1;
        if (src >= 1 && _buf[src] == _buf[p])
        {
            EmitShortRep();
        }
        else
        {
            EmitLiteral();
        }
    }
}
