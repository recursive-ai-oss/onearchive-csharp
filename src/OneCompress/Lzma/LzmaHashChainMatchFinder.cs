using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// A hash-chain match finder: each position is linked to the previous position with the same hash, and a search follows the links
/// for at most <c>depth</c> steps. Fast and small; it finds the nearest candidates first, so deep searches pay off less than with
/// <see cref="LzmaBinaryTreeMatchFinder"/>.
/// </summary>
/// <remarks>With a main hash of 3 bytes (<see cref="LzmaMatchFinderKind.HashChain3"/>) or 4 bytes (<see cref="LzmaMatchFinderKind.HashChain4"/>) plus a direct 2-byte table (and for 4 a 3-byte table) for short matches. The links live in a cyclic array of dictionary size + 1 entries.</remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzmaHashChainMatchFinder : LzmaMatchFinder
{
    private readonly int[] _chain;

    /// <summary>Creates a finder.</summary>
    /// <param name="hashBytes">The bytes the main hash covers: 3 or 4.</param>
    /// <param name="dictionarySize">The largest match distance.</param>
    /// <param name="niceLength">The length at which a search stops, 2 to 273.</param>
    /// <param name="depth">The most chain steps per position, at least 1.</param>
    /// <param name="hashDictionarySize">The dictionary size that sizes the hash tables when it differs from <paramref name="dictionarySize"/> (a window cut down for a short input keeps the tables of the full dictionary, so the matches found do not depend on the input length); 0 for <paramref name="dictionarySize"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public LzmaHashChainMatchFinder(int hashBytes, int dictionarySize, int niceLength, int depth, int hashDictionarySize = 0)
        : base(CheckHashBytes(hashBytes), dictionarySize, niceLength, depth, hashDictionarySize)
    {
        _chain = new int[_cyclicSize];
    }

    private static int CheckHashBytes(int hashBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hashBytes, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashBytes, 4);
        return hashBytes;
    }

    /// <inheritdoc />
    public override int FindMatches(byte[] buffer, int position, int available, Span<LzmaMatch> matches)
    {
        int cp = _cyclicPos;
        _cyclicPos = cp + 1 == _cyclicSize ? 0 : cp + 1;
        if (available < _hashBytes)
        {
            _chain[cp] = 0;
            return 0;
        }

        int lenLimit = Math.Min(available, LzmaModel.MaxMatchLength);
        CheckWindow(buffer, position, lenLimit);
        PrefetchBuckets(buffer, position, available);
        int cur = UpdateHashes(buffer, position, out int c2, out int c3);
        LzmaMatchRaw.L(_chain, cp) = cur;
        int count = 0;
        int maxLen = AddShortMatches(buffer, position, lenLimit, c2, c3, matches, ref count);
        int nice = Math.Min(_niceLength, lenLimit);
        if (maxLen >= nice)
        {
            return count;
        }

        int dict = _dictionarySize;
        for (int steps = _depth; cur > 0 && steps > 0; steps--)
        {
            int delta = position - cur;
            // cur is a stored position: behind this one and within the dictionary.
            if ((uint)(delta - 1) >= (uint)dict)
            {
                break;
            }

            if (LzmaMatchRaw.B(buffer, cur + maxLen) == LzmaMatchRaw.B(buffer, position + maxLen) && LzmaMatchRaw.B(buffer, cur) == LzmaMatchRaw.B(buffer, position))
            {
                int len = CommonLength(buffer, cur, position, 1, lenLimit);
                if (len > maxLen)
                {
                    maxLen = len;
                    matches[count++] = new LzmaMatch { Length = len, Distance = delta };
                    if (len >= nice)
                    {
                        break;
                    }
                }
            }

            int slot = cp - delta;
            if (slot < 0)
            {
                slot += _cyclicSize;
            }

            cur = LzmaMatchRaw.L(_chain, slot);
        }

        return count;
    }

    /// <inheritdoc />
    public override void Skip(byte[] buffer, int position, int available)
    {
        int cp = _cyclicPos;
        _cyclicPos = cp + 1 == _cyclicSize ? 0 : cp + 1;
        if (available < _hashBytes)
        {
            _chain[cp] = 0;
            return;
        }

        CheckWindow(buffer, position, Math.Min(available, LzmaModel.MaxMatchLength));
        PrefetchBuckets(buffer, position, available);
        LzmaMatchRaw.L(_chain, cp) = UpdateHashes(buffer, position, out _, out _);
    }

    /// <inheritdoc />
    internal override void SkipRange(byte[] buffer, int position, int count, int bufEnd)
    {
        // Positions with at least `Margin` valid bytes behind them take the loop below; the last few go through Skip.
        const int Margin = 16;
        int end = position + count;
        int fast = Math.Min(end, bufEnd - Margin + 1);
        if (position < 0 || bufEnd < 0 || bufEnd + 4 > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "The position and the available length must lie inside the buffer.");
        }

        int p = position;
        if (p < fast)
        {
            int[] chain = _chain;
            int cyclic = _cyclicSize;
            int cp = _cyclicPos;
            if (_hashBytes == 4)
            {
                for (; p < fast; p++)
                {
                    // Prefetch the buckets of a position a few steps ahead, then update the three tables for this one; each table
                    // index is masked or shifted down to its table's size, and p + 16 < bufEnd + 4 <= buffer.Length.
                    uint ahead = Read32(buffer, p + 12);
                    LzmaMatchRaw.PrefetchLink(_hash2, (int)(ahead & 0xFFFF));
                    LzmaMatchRaw.PrefetchLink(_hash3!, (int)HashOf(ahead & 0xFFFFFF, Hash3Bits));
                    LzmaMatchRaw.PrefetchLink(_main, (int)HashOf(ahead, _mainBits));
                    uint v = Read32(buffer, p);
                    LzmaMatchRaw.L(_hash2, (int)(v & 0xFFFF)) = p;
                    LzmaMatchRaw.L(_hash3!, (int)HashOf(v & 0xFFFFFF, Hash3Bits)) = p;
                    ref int slotM = ref LzmaMatchRaw.L(_main, (int)HashOf(v, _mainBits));
                    LzmaMatchRaw.L(chain, cp) = slotM;
                    slotM = p;
                    cp = cp + 1 == cyclic ? 0 : cp + 1;
                }
            }
            else
            {
                for (; p < fast; p++)
                {
                    uint ahead = Read32(buffer, p + 12);
                    LzmaMatchRaw.PrefetchLink(_hash2, (int)(ahead & 0xFFFF));
                    LzmaMatchRaw.PrefetchLink(_main, (int)HashOf(ahead & 0xFFFFFF, _mainBits));
                    uint v = Read32(buffer, p);
                    LzmaMatchRaw.L(_hash2, (int)(v & 0xFFFF)) = p;
                    ref int slotM = ref LzmaMatchRaw.L(_main, (int)HashOf(v & 0xFFFFFF, _mainBits));
                    LzmaMatchRaw.L(chain, cp) = slotM;
                    slotM = p;
                    cp = cp + 1 == cyclic ? 0 : cp + 1;
                }
            }

            _cyclicPos = cp;
        }

        for (; p < end; p++)
        {
            Skip(buffer, p, bufEnd - p);
        }
    }

    private protected override void ResetLinks() => Array.Clear(_chain);

    private protected override void RebaseLinks(int delta) => Rebase(_chain, delta);
}
