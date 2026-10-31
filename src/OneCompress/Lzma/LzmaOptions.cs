using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>How the encoder turns the matches a match finder reports into symbols.</summary>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum LzmaMode
{
    /// <summary>Greedy parsing with one step of lazy matching: fast, a few percent larger output.</summary>
    Fast = 1,

    /// <summary>Price-based optimal parsing over a window of up to 4096 bytes, using the adaptive probabilities as costs.</summary>
    Normal = 2,
}

/// <summary>The match finders. The number is how many bytes the main hash covers.</summary>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum LzmaMatchFinderKind
{
    /// <summary>Hash chains, with a 2-byte table for short matches and a main hash of 3 bytes. Smallest memory, fastest.</summary>
    HashChain3 = 1,

    /// <summary>Hash chains with 2-, 3- and 4-byte hashes.</summary>
    HashChain4 = 2,

    /// <summary>A binary tree per 2-byte value: a tree search only for matches of at least 2 bytes. Slow on repetitive data.</summary>
    BinaryTree2 = 3,

    /// <summary>Binary trees with 2- and 3-byte hashes.</summary>
    BinaryTree3 = 4,

    /// <summary>Binary trees with 2-, 3- and 4-byte hashes. The best ratio; used by the higher presets.</summary>
    BinaryTree4 = 5,
}

/// <summary>
/// Options of the LZMA family encoders (<see cref="LzmaEncoder"/>, <see cref="Lzma2Encoder"/>, and the xz and lzip encoders that
/// contain them). Every property that is zero or null takes the value of the <see cref="Preset"/>.
/// </summary>
/// <remarks>
/// <para>The presets 0 to 9 and <see cref="Extreme"/> follow the reference <c>xz</c> tool's: dictionary sizes 256 KiB, 1, 2, 4, 4, 8, 8,
/// 16, 32 and 64 MiB; hash chains and <see cref="LzmaMode.Fast"/> for presets 0 to 3, binary trees and <see cref="LzmaMode.Normal"/>
/// for 4 to 9. The nice lengths and depths are in <c>docs/lzma-encoder.md</c>.</para>
/// <para>The compressed bytes depend only on the input and these options.</para>
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzmaOptions
{
    /// <summary>The preset used when none is set: 6, like the reference tool.</summary>
    public const int DefaultPreset = 6;

    /// <summary>The largest dictionary size an encoder accepts: 768 MiB. A binary-tree match finder keeps two 32-bit links per byte of dictionary, which must fit in one .NET array.</summary>
    public const uint MaxDictionarySize = 3u << 28;

    /// <summary>The largest nice length.</summary>
    public const int MaxNiceLength = LzmaModel.MaxMatchLength;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static LzmaOptions Default { get; } = new();

    /// <summary>Gets the preset, 0 to 9. Default <see cref="DefaultPreset"/>.</summary>
    public int Preset { get; init; } = DefaultPreset;

    /// <summary>Gets a value indicating whether the slower variant of the preset is used: a longer nice length and deeper search (and for presets 0 to 3 the binary-tree parser). Default false.</summary>
    public bool Extreme { get; init; }

    /// <summary>Gets the number of high bits of the previous byte used as literal context, 0 to 8 (0 to 4 inside LZMA2). Default 3.</summary>
    public int Lc { get; init; } = 3;

    /// <summary>Gets the number of low position bits used as literal context, 0 to 4 (<see cref="Lc"/> + <see cref="Lp"/> at most 4 inside LZMA2). Default 0.</summary>
    public int Lp { get; init; }

    /// <summary>Gets the number of low position bits used as symbol context, 0 to 4. Default 2.</summary>
    public int Pb { get; init; } = 2;

    /// <summary>Gets the dictionary size in bytes, 4 KiB to <see cref="MaxDictionarySize"/>; 0 takes the preset's. Default 0.</summary>
    public uint DictionarySize { get; init; }

    /// <summary>Gets the match length at which the search stops, 2 to 273 (at least the match finder's minimum); 0 takes the preset's. Default 0.</summary>
    public int NiceLength { get; init; }

    /// <summary>Gets the most candidates the match finder examines per position; 0 chooses from the match finder and nice length. Default 0.</summary>
    public int Depth { get; init; }

    /// <summary>Gets the parser; <see langword="null"/> takes the preset's.</summary>
    public LzmaMode? Mode { get; init; }

    /// <summary>Gets the match finder; <see langword="null"/> takes the preset's.</summary>
    public LzmaMatchFinderKind? MatchFinder { get; init; }

    /// <summary>
    /// Gets a value indicating whether an end-of-stream marker is written to a <c>.lzma</c> stream whose length is known. A stream of unknown
    /// length always gets one. Ignored by LZMA2, xz and lzip (lzip always writes it). Default false.
    /// </summary>
    public bool WriteEndMarker { get; init; }

    /// <summary>Resolves the preset and the overrides into the values an encoder runs with.</summary>
    /// <returns>The effective settings.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of its range.</exception>
    public LzmaEncoderSettings Resolve()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Preset, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Preset, 9);
        ArgumentOutOfRangeException.ThrowIfLessThan(Lc, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Lc, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(Lp, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Lp, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(Pb, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Pb, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DictionarySize, MaxDictionarySize);
        ArgumentOutOfRangeException.ThrowIfLessThan(NiceLength, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(NiceLength, MaxNiceLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(Depth, 0);
        if (NiceLength == 1)
        {
            throw new ArgumentOutOfRangeException(nameof(NiceLength), "The nice length must be 0 (the preset's) or at least 2.");
        }

        if (DictionarySize != 0 && DictionarySize < LzmaProperties.MinimumDictionarySize)
        {
            throw new ArgumentOutOfRangeException(nameof(DictionarySize), "The dictionary size must be 0 (the preset's) or at least 4096.");
        }

        return LzmaEncoderSettings.Create(this);
    }
}

/// <summary>The values an LZMA encoder runs with after <see cref="LzmaOptions.Resolve"/>.</summary>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly struct LzmaEncoderSettings
{
    // Dictionary sizes of presets 0 to 9 (the xz preset table).
    private static ReadOnlySpan<uint> PresetDictionary => [1u << 18, 1u << 20, 1u << 21, 1u << 22, 1u << 22, 1u << 23, 1u << 23, 1u << 24, 1u << 25, 1u << 26];

    // Nice lengths of presets 0 to 9 and of their extreme variants; see docs/lzma-encoder.md for how they were chosen.
    private static ReadOnlySpan<byte> PresetNice => [6, 8, 24, 48, 16, 32, 64, 64, 64, 64];

    // Search depths of the hash-chain presets 0 to 3; the binary-tree presets derive theirs from the nice length.
    private static ReadOnlySpan<byte> PresetChainDepth => [6, 8, 12, 24];

    private LzmaEncoderSettings(int lc, int lp, int pb, uint dictionarySize, int niceLength, int depth, int lazyLength, LzmaMode mode, LzmaMatchFinderKind matchFinder, bool endMarker)
    {
        Lc = lc;
        Lp = lp;
        Pb = pb;
        DictionarySize = dictionarySize;
        NiceLength = niceLength;
        Depth = depth;
        LazyLength = lazyLength;
        Mode = mode;
        MatchFinder = matchFinder;
        WriteEndMarker = endMarker;
    }

    /// <summary>Gets the literal context bits.</summary>
    public int Lc { get; }

    /// <summary>Gets the literal position bits.</summary>
    public int Lp { get; }

    /// <summary>Gets the position bits.</summary>
    public int Pb { get; }

    /// <summary>Gets the dictionary size in bytes.</summary>
    public uint DictionarySize { get; }

    /// <summary>Gets the nice length.</summary>
    public int NiceLength { get; }

    /// <summary>Gets the search depth (never 0).</summary>
    public int Depth { get; }

    // The fast parser looks one position ahead only when the match it found is shorter than this; int.MaxValue leaves it to the nice length.
    internal int LazyLength { get; }

    /// <summary>Gets the parser.</summary>
    public LzmaMode Mode { get; }

    /// <summary>Gets the match finder.</summary>
    public LzmaMatchFinderKind MatchFinder { get; }

    /// <summary>Gets whether a known-length <c>.lzma</c> stream gets an end marker.</summary>
    public bool WriteEndMarker { get; }

    /// <summary>Gets the minimum nice length of a match finder (the shortest match it can report is 2, 3 or 4 bytes).</summary>
    /// <param name="kind">The match finder.</param>
    /// <returns>2, 3 or 4.</returns>
    public static int GetMinimumNiceLength(LzmaMatchFinderKind kind) => kind switch
    {
        LzmaMatchFinderKind.BinaryTree2 => 2,
        LzmaMatchFinderKind.HashChain3 or LzmaMatchFinderKind.BinaryTree3 => 3,
        _ => 4,
    };

    internal static LzmaEncoderSettings Create(LzmaOptions o)
    {
        int p = o.Preset;
        bool ext = o.Extreme;
        LzmaMode mode = o.Mode ?? ((p <= 3 && !ext) ? LzmaMode.Fast : LzmaMode.Normal);
        LzmaMatchFinderKind mf = o.MatchFinder ?? (p <= 3 ? (ext ? LzmaMatchFinderKind.BinaryTree4 : LzmaMatchFinderKind.HashChain4) : LzmaMatchFinderKind.BinaryTree4);
        uint dict = o.DictionarySize != 0 ? o.DictionarySize : PresetDictionary[p];
        int nice = o.NiceLength;
        if (nice == 0)
        {
            nice = PresetNice[p];
            if (ext)
            {
                nice = p <= 3 ? 64 : 273;
            }
        }

        nice = Math.Max(nice, GetMinimumNiceLength(mf));
        nice = Math.Min(nice, LzmaModel.MaxMatchLength);
        int depth = o.Depth;
        if (depth == 0)
        {
            bool tree = mf >= LzmaMatchFinderKind.BinaryTree2;
            depth = tree ? 16 + (nice / 2) : p <= 3 && !ext ? PresetChainDepth[p] : 4 + (nice / 4);
            if (ext)
            {
                depth = Math.Max(depth, tree ? 512 : 100);
            }
        }

        // Presets 0 and 1 are the speed presets: they skip the look-ahead for any match of 3 (preset 0) or 4 (preset 1) bytes or more.
        int lazy = ext ? int.MaxValue : p switch { 0 => 3, 1 => 4, _ => int.MaxValue };
        return new LzmaEncoderSettings(o.Lc, o.Lp, o.Pb, dict, nice, depth, lazy, mode, mf, o.WriteEndMarker);
    }
}
