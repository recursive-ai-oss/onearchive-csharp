using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>A match a <see cref="LzmaMatchFinder"/> reports: the bytes at the current position repeat the bytes <see cref="Distance"/> positions earlier for <see cref="Length"/> bytes.</summary>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public struct LzmaMatch
{
    /// <summary>The number of matching bytes, 2 to 273.</summary>
    public int Length { get; set; }

    /// <summary>The distance back to the earlier occurrence, 1 or more (not the minus-one form the format stores).</summary>
    public int Distance { get; set; }
}

/// <summary>
/// Finds earlier occurrences of the bytes at the current position inside a sliding window. A finder is told about every position in
/// order, either with <see cref="FindMatches"/> (which also reports matches) or with <see cref="Skip"/> (which only records the position).
/// </summary>
/// <remarks>
/// <para>Positions are indexes into the caller's buffer. Index 0 is never a match source: the finder uses 0 for "empty", so a caller starts its
/// data at index 1. When the caller moves the data down in its buffer by <c>delta</c> it calls <see cref="Slide"/> so the stored positions
/// follow. The buffer must have at least 16 spare bytes after the last valid byte (the finders read ahead by whole words but never use
/// bytes past <c>available</c>).</para>
/// <para>The matches returned have strictly increasing lengths, and each is the closest distance found for that length. Searching stops
/// at the nice length or after <c>depth</c> candidates. The results depend only on the bytes, the positions' order and the constructor's
/// parameters: never on where the caller's buffer slides.</para>
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public abstract class LzmaMatchFinder
{
    /// <summary>The number of spare bytes the caller's buffer must keep after its last valid byte.</summary>
    public const int BufferPadding = 16;

    private protected const int Hash3Bits = 18;

    /// <summary>The largest number of matches one call reports.</summary>
    public const int MaxMatches = LzmaModel.MaxMatchLength + 1;

    private protected readonly int _dictionarySize;
    private protected readonly int _niceLength;
    private protected readonly int _depth;
    private protected readonly int _cyclicSize;
    private protected readonly int _hashBytes;
    private protected readonly int _mainBits;
    private protected readonly int[] _hash2;
    private protected readonly int[]? _hash3;
    private protected readonly int[] _main;
    private protected int _cyclicPos;

    private protected LzmaMatchFinder(int hashBytes, int dictionarySize, int niceLength, int depth, int hashDictionarySize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dictionarySize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(niceLength, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        _hashBytes = hashBytes;
        _dictionarySize = dictionarySize;
        _niceLength = Math.Min(niceLength, LzmaModel.MaxMatchLength);
        _depth = depth;
        _cyclicSize = dictionarySize + 1;
        _hash2 = new int[1 << 16];
        if (hashBytes == 4)
        {
            _hash3 = new int[1 << Hash3Bits];
        }

        if (hashBytes == 2)
        {
            _mainBits = 16;
            _main = _hash2;
        }
        else
        {
            // The hash size follows the dictionary size the options ask for, not the window actually used, so the candidates (and
            // therefore the output) never depend on how much input the caller announced.
            int bits = 32 - BitOperations.LeadingZeroCount((uint)Math.Max((hashDictionarySize > 0 ? hashDictionarySize : dictionarySize) - 1, 1));
            _mainBits = Math.Clamp(bits, 16, 24);
            _main = new int[1 << _mainBits];
        }
    }

    /// <summary>Gets the dictionary size: the largest distance a match may have.</summary>
    public int DictionarySize => _dictionarySize;

    /// <summary>Gets the length at which the search stops.</summary>
    public int NiceLength => _niceLength;

    /// <summary>Forgets every position; the next call is for the first position of a fresh buffer.</summary>
    public void Reset()
    {
        Array.Clear(_hash2);
        if (_hash3 is not null)
        {
            Array.Clear(_hash3);
        }

        if (!ReferenceEquals(_main, _hash2))
        {
            Array.Clear(_main);
        }

        _cyclicPos = 0;
        ResetLinks();
    }

    /// <summary>Records <paramref name="position"/> and reports the matches that start there.</summary>
    /// <param name="buffer">The data window.</param>
    /// <param name="position">The index of the byte to match, at least 1. Each call is for the position after the previous call's.</param>
    /// <param name="available">The number of valid bytes from <paramref name="position"/> on.</param>
    /// <param name="matches">Receives the matches, at least <see cref="MaxMatches"/> entries.</param>
    /// <returns>The number of matches written.</returns>
    public abstract int FindMatches(byte[] buffer, int position, int available, Span<LzmaMatch> matches);

    /// <summary>Records <paramref name="position"/> without looking for matches.</summary>
    /// <param name="buffer">The data window.</param>
    /// <param name="position">The index of the byte; each call is for the position after the previous call's.</param>
    /// <param name="available">The number of valid bytes from <paramref name="position"/> on.</param>
    public abstract void Skip(byte[] buffer, int position, int available);

    /// <summary>Tells the finder the caller moved its data down by <paramref name="delta"/> bytes, dropping the first <paramref name="delta"/>.</summary>
    /// <param name="delta">The number of bytes dropped.</param>
    public void Slide(int delta)
    {
        Rebase(_hash2, delta);
        if (_hash3 is not null)
        {
            Rebase(_hash3, delta);
        }

        if (!ReferenceEquals(_main, _hash2))
        {
            Rebase(_main, delta);
        }

        RebaseLinks(delta);
    }

    private protected abstract void ResetLinks();

    private protected abstract void RebaseLinks(int delta);

    private protected static void Rebase(int[] table, int delta)
    {
        for (int i = 0; i < table.Length; i++)
        {
            int v = table[i];
            table[i] = v > delta ? v - delta : 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static uint HashOf(uint value, int bits) => (value * 2654435761u) >> (32 - bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static uint Read32(byte[] buffer, int index) => LzmaMatchRaw.Read32(buffer, index);

    // The number of equal bytes at a and b, at most limit, given that the first `start` bytes are known to be equal.
    private protected static int CommonLength(byte[] buffer, int a, int b, int start, int limit)
    {
        int len = start;
        while (len + 8 <= limit)
        {
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(a + len)) ^ BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(b + len));
            if (x != 0)
            {
                return len + (BitOperations.TrailingZeroCount(x) >> 3);
            }

            len += 8;
        }

        while (len < limit && buffer[a + len] == buffer[b + len])
        {
            len++;
        }

        return len;
    }

    // Updates the 2-, 3- and main-hash tables for `position`, and reports the earlier positions they held. Returns the main-hash
    // candidate; `d2` and `d3` are the earlier positions of the short hashes (0 for none).
    private protected int UpdateHashes(byte[] buffer, int position, out int c2, out int c3)
    {
        // The callers have run CheckWindow, so buffer[position .. position + 3] exist whenever the hash covers that many bytes;
        // each table index is masked or shifted down to its table's size.
        c3 = 0;
        if (_hashBytes == 2)
        {
            uint v2 = (uint)(LzmaMatchRaw.B(buffer, position) | (LzmaMatchRaw.B(buffer, position + 1) << 8));
            ref int slot2 = ref LzmaMatchRaw.L(_hash2, (int)v2);
            int old = slot2;
            slot2 = position;
            c2 = 0;
            return old;
        }

        uint v = Read32(buffer, position);
        ref int slotH2 = ref LzmaMatchRaw.L(_hash2, (int)(v & 0xFFFF));
        c2 = slotH2;
        slotH2 = position;
        uint hm;
        if (_hashBytes == 4)
        {
            ref int slotH3 = ref LzmaMatchRaw.L(_hash3!, (int)HashOf(v & 0xFFFFFF, Hash3Bits));
            c3 = slotH3;
            slotH3 = position;
            hm = HashOf(v, _mainBits);
        }
        else
        {
            hm = HashOf(v & 0xFFFFFF, _mainBits);
        }

        ref int slotM = ref LzmaMatchRaw.L(_main, (int)hm);
        int cur = slotM;
        slotM = position;
        return cur;
    }

    // The searches read buffer[position .. position + lenLimit] and the hash functions four bytes at position, without bounds
    // checks; this is the one check that makes that safe.
    private protected static void CheckWindow(byte[] buffer, int position, int lenLimit)
    {
        if (position < 0 || lenLimit < 0 || (long)position + lenLimit >= buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "The position and the available length must lie inside the buffer.");
        }
    }

    // Memory hints for positions the finder will reach soon: the hash buckets of a position far ahead, and, for a position a few
    // steps ahead (whose buckets were fetched earlier), the node its bucket holds, so that the first step of its tree walk finds
    // that node's links and data in cache. The buckets can change before the position is reached; a stale hint only wastes a fetch.
    // Nothing here changes any table or result.
    private protected void PrefetchAhead(byte[] buffer, int position, int available, int cp, int[] links, int stride)
    {
        const int Far = 16;
        const int Near = 6;
        if (_hashBytes < 3 || available < Far + 4)
        {
            return;
        }

        uint far = Read32(buffer, position + Far);
        uint near = Read32(buffer, position + Near);
        uint hf;
        uint hn;
        if (_hashBytes == 4)
        {
            hf = HashOf(far, _mainBits);
            hn = HashOf(near, _mainBits);
        }
        else
        {
            hf = HashOf(far & 0xFFFFFF, _mainBits);
            hn = HashOf(near & 0xFFFFFF, _mainBits);
        }

        LzmaMatchRaw.PrefetchLink(_main, (int)hf);
        int node = _main[hn];
        if (node > 0)
        {
            int slot = cp + Near - (position + Near - node);
            if (slot < 0)
            {
                slot += _cyclicSize;
            }
            else if (slot >= _cyclicSize)
            {
                slot -= _cyclicSize;
            }

            LzmaMatchRaw.PrefetchByte(buffer, node);
            LzmaMatchRaw.PrefetchLink(links, stride * slot);
        }
    }

    // Adds the 2- and 3-byte candidates of the short hashes and returns the longest length found so far (1 if none).
    private protected int AddShortMatches(byte[] buffer, int position, int lenLimit, int c2, int c3, Span<LzmaMatch> matches, ref int count)
    {
        int maxLen = 1;
        if (c2 > 0)
        {
            int delta = position - c2;
            if (delta <= _dictionarySize && buffer[c2] == buffer[position] && buffer[c2 + 1] == buffer[position + 1])
            {
                maxLen = 2;
                matches[count++] = new LzmaMatch { Length = 2, Distance = delta };
            }
        }

        if (c3 > 0)
        {
            int delta = position - c3;
            if (delta <= _dictionarySize)
            {
                int len = CommonLength(buffer, c3, position, 0, lenLimit);
                if (len > maxLen)
                {
                    maxLen = len;
                    matches[count++] = new LzmaMatch { Length = len, Distance = delta };
                }
            }
        }

        return maxLen;
    }
}
