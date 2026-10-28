using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

public sealed partial class LzmaEncoderCore
{
    // The fast parser: greedy with one step of lazy matching. It takes the longest match unless a repeated distance is nearly as long
    // (a repeated distance is much cheaper to code), and gives up a match for a literal when the next position starts a clearly better one.
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

        int nice = Math.Min(_settings.NiceLength, maxLen);
        int mainLen = 0;
        uint mainDist = 0;
        if (n > 0)
        {
            mainLen = Math.Min(_matches[n - 1].Length, maxLen);
            mainDist = (uint)_matches[n - 1].Distance - 1;
        }

        int repLen = 0;
        int repIndex = 0;
        for (int i = 0; i < 4; i++)
        {
            int l = RepLength(p, _reps[i], maxLen);
            if (l > repLen)
            {
                repLen = l;
                repIndex = i;
            }
        }

        if (repLen >= nice)
        {
            EmitRep(repIndex, repLen);
            Finish();
            return;
        }

        if (mainLen >= nice)
        {
            EmitMatch(mainLen, mainDist);
            Finish();
            return;
        }

        bool useRep = repLen >= 2 && (repLen + 1 >= mainLen || (repLen + 2 >= mainLen && mainDist >= (1u << 9)) || (repLen + 3 >= mainLen && mainDist >= (1u << 15)));
        int length;
        if (useRep)
        {
            length = repLen;
        }
        else if (mainLen >= 3 || (mainLen == 2 && mainDist < 0x80))
        {
            length = mainLen;
        }
        else
        {
            EmitLiteralOrShortRep(p);
            AdvanceFinder(_pos);
            return;
        }

        // Lazy step: does the next position start a clearly better match?
        if (maxLen > 2 && horizon > 2)
        {
            int n2 = PeekNext(p);
            if (n2 > 0)
            {
                int next = Math.Min(_matchesNext[n2 - 1].Length, Math.Min(maxLen - 1, LzmaModel.MaxMatchLength));
                uint nextDist = (uint)_matchesNext[n2 - 1].Distance - 1;
                bool defer = useRep
                    ? next >= length + 3
                    : (next >= length + 1 && (nextDist >> 7) < mainDist) || next >= length + 2;
                if (defer)
                {
                    EmitLiteralOrShortRep(p);
                    return;
                }
            }
        }

        if (useRep)
        {
            EmitRep(repIndex, length);
        }
        else
        {
            EmitMatch(length, mainDist);
        }

        Finish();
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
