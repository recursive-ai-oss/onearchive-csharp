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
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public LzmaHashChainMatchFinder(int hashBytes, int dictionarySize, int niceLength, int depth)
        : base(CheckHashBytes(hashBytes), dictionarySize, niceLength, depth)
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
        int cur = UpdateHashes(buffer, position, out int c2, out int c3);
        _chain[cp] = cur;
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
            if (delta > dict)
            {
                break;
            }

            if (buffer[cur + maxLen] == buffer[position + maxLen] && buffer[cur] == buffer[position])
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

            cur = _chain[slot];
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

        _chain[cp] = UpdateHashes(buffer, position, out _, out _);
    }

    private protected override void ResetLinks() => Array.Clear(_chain);

    private protected override void RebaseLinks(int delta) => Rebase(_chain, delta);
}
