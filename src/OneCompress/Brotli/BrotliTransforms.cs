
namespace RecursiveAI.OneCompress.Brotli;

/// <summary>The elementary transform of a brotli word transformation (RFC 7932 section 8).</summary>
public enum BrotliTransformKind
{
    /// <summary>The word unchanged.</summary>
    Identity = 0,

    /// <summary>The first character is upper-cased in the RFC's sense.</summary>
    FermentFirst = 1,

    /// <summary>Every character is upper-cased in the RFC's sense.</summary>
    FermentAll = 2,

    /// <summary>The first <c>n</c> bytes of the word are dropped (1 to 9).</summary>
    OmitFirst = 3,

    /// <summary>The last <c>n</c> bytes of the word are dropped (1 to 9).</summary>
    OmitLast = 4,
}

/// <summary>
/// The 121 word transformations that apply to a static dictionary word (RFC 7932 section 8 and Appendix B): each is a prefix, an
/// elementary transform of the word and a suffix.
/// </summary>
public static class BrotliTransforms
{
    /// <summary>The number of transformations (121).</summary>
    public const int Count = 121;

    /// <summary>The most bytes a transformation adds to a word (13).</summary>
    public const int MaxAddedBytes = 13;

    /// <summary>The longest result of a transformation: a 24-byte word plus <see cref="MaxAddedBytes"/>.</summary>
    public const int MaxTransformedLength = BrotliWords.MaxWordLength + MaxAddedBytes;

    private readonly struct Entry
    {
        internal Entry(int prefixStart, int prefixLength, int id, int suffixStart, int suffixLength)
        {
            PrefixStart = prefixStart;
            PrefixLength = prefixLength;
            Id = id;
            SuffixStart = suffixStart;
            SuffixLength = suffixLength;
        }

        internal int PrefixStart { get; }

        internal int PrefixLength { get; }

        internal int Id { get; }

        internal int SuffixStart { get; }

        internal int SuffixLength { get; }
    }

    private static readonly Entry[] s_entries = Parse();

    // The data holds each transform as prefix, 0, elementary transform id, suffix, 0 (see the generated file).
    private static Entry[] Parse()
    {
        ReadOnlySpan<byte> blob = BrotliTransformData.Blob;
        var entries = new Entry[Count];
        int pos = 0;
        for (int i = 0; i < Count; i++)
        {
            int prefixStart = pos;
            int prefixLength = blob.Slice(pos).IndexOf((byte)0);
            pos += prefixLength + 1;
            int id = blob[pos++];
            int suffixStart = pos;
            int suffixLength = blob.Slice(pos).IndexOf((byte)0);
            pos += suffixLength + 1;
            entries[i] = new Entry(prefixStart, prefixLength, id, suffixStart, suffixLength);
        }

        return entries;
    }

    /// <summary>Returns the bytes that transformation <paramref name="transformId"/> puts before the word.</summary>
    /// <param name="transformId">The transformation, 0 to 120.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transformId"/> is outside 0 to 120.</exception>
    public static ReadOnlySpan<byte> GetPrefix(int transformId)
    {
        Entry e = s_entries[CheckId(transformId)];
        return BrotliTransformData.Blob.Slice(e.PrefixStart, e.PrefixLength);
    }

    /// <summary>Returns the bytes that transformation <paramref name="transformId"/> puts after the word.</summary>
    /// <param name="transformId">The transformation, 0 to 120.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transformId"/> is outside 0 to 120.</exception>
    public static ReadOnlySpan<byte> GetSuffix(int transformId)
    {
        Entry e = s_entries[CheckId(transformId)];
        return BrotliTransformData.Blob.Slice(e.SuffixStart, e.SuffixLength);
    }

    /// <summary>Returns the elementary transform that transformation <paramref name="transformId"/> applies to the word.</summary>
    /// <param name="transformId">The transformation, 0 to 120.</param>
    /// <param name="omitted">For <see cref="BrotliTransformKind.OmitFirst"/> and <see cref="BrotliTransformKind.OmitLast"/> the number of bytes dropped (1 to 9); otherwise 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transformId"/> is outside 0 to 120.</exception>
    public static BrotliTransformKind GetKind(int transformId, out int omitted)
    {
        int id = s_entries[CheckId(transformId)].Id;
        omitted = 0;
        if (id <= 2)
        {
            return (BrotliTransformKind)id;
        }

        if (id <= 11)
        {
            omitted = id - 2;
            return BrotliTransformKind.OmitFirst;
        }

        omitted = id - 11;
        return BrotliTransformKind.OmitLast;
    }

    /// <summary>Applies transformation <paramref name="transformId"/> to a dictionary word.</summary>
    /// <param name="transformId">The transformation, 0 to 120.</param>
    /// <param name="word">The base word, normally 4 to 24 bytes from <see cref="BrotliWords"/>; any length up to 24 works.</param>
    /// <param name="destination">Receives the result; <see cref="MaxTransformedLength"/> bytes always suffice.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="transformId"/> is outside 0 to 120, or <paramref name="word"/> is longer than 24 bytes.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short for the result.</exception>
    public static int Apply(int transformId, ReadOnlySpan<byte> word, Span<byte> destination)
    {
        Entry e = s_entries[CheckId(transformId)];
        if (word.Length > BrotliWords.MaxWordLength)
        {
            throw new ArgumentOutOfRangeException(nameof(word), "A dictionary word has at most 24 bytes.");
        }

        if (destination.Length < MaxTransformedLength && destination.Length < e.PrefixLength + word.Length + e.SuffixLength)
        {
            throw new ArgumentException("The destination is too short for the transformed word.", nameof(destination));
        }

        return ApplyUnchecked(e, word, destination);
    }

    // The decoder calls this with a word from the dictionary and a destination of MaxTransformedLength bytes.
    internal static int ApplyUnchecked(int transformId, ReadOnlySpan<byte> word, Span<byte> destination) =>
        ApplyUnchecked(s_entries[transformId], word, destination);

    private static int ApplyUnchecked(Entry e, ReadOnlySpan<byte> word, Span<byte> destination)
    {
        ReadOnlySpan<byte> blob = BrotliTransformData.Blob;
        blob.Slice(e.PrefixStart, e.PrefixLength).CopyTo(destination);
        int n = e.PrefixLength;
        int id = e.Id;
        if (id <= 2)
        {
            word.CopyTo(destination.Slice(n));
            Span<byte> copy = destination.Slice(n, word.Length);
            if (id == 1)
            {
                // RFC 7932 section 8, FermentFirst.
                if (copy.Length > 0)
                {
                    Ferment(copy, 0);
                }
            }
            else if (id == 2)
            {
                // FermentAll.
                int i = 0;
                while (i < copy.Length)
                {
                    i += Ferment(copy, i);
                }
            }

            n += word.Length;
        }
        else if (id <= 11)
        {
            // OmitFirst1..9: the last (length - k) bytes, or nothing if the word is no longer than k.
            int k = id - 2;
            if (word.Length > k)
            {
                word.Slice(k).CopyTo(destination.Slice(n));
                n += word.Length - k;
            }
        }
        else
        {
            // OmitLast1..9: the first (length - k) bytes.
            int k = id - 11;
            if (word.Length > k)
            {
                word.Slice(0, word.Length - k).CopyTo(destination.Slice(n));
                n += word.Length - k;
            }
        }

        blob.Slice(e.SuffixStart, e.SuffixLength).CopyTo(destination.Slice(n));
        return n + e.SuffixLength;
    }

    // RFC 7932 section 8: Ferment. Changes the case of a Latin letter, or flips a bit in the byte after a two-byte lead or the
    // second byte after a three-byte lead, and returns the number of bytes the character takes.
    private static int Ferment(Span<byte> word, int pos)
    {
        byte b = word[pos];
        if (b < 192)
        {
            if (b >= 97 && b <= 122)
            {
                word[pos] = (byte)(b ^ 32);
            }

            return 1;
        }

        if (b < 224)
        {
            if (pos + 1 < word.Length)
            {
                word[pos + 1] ^= 32;
            }

            return 2;
        }

        if (pos + 2 < word.Length)
        {
            word[pos + 2] ^= 5;
        }

        return 3;
    }

    private static int CheckId(int transformId)
    {
        if ((uint)transformId >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(transformId), transformId, "The transformation id must be 0 to 120.");
        }

        return transformId;
    }
}
