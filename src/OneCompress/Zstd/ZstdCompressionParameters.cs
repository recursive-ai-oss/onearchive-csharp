using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>The match-finding strategies of Zstandard, from fastest to strongest; the numbers follow the level tables.</summary>
public enum ZstdStrategy
{
    /// <summary>One hash table, one candidate per position.</summary>
    Fast = 1,

    /// <summary>Two hash tables: one of short hashes and one of long hashes.</summary>
    DoubleFast = 2,

    /// <summary>Hash chains, taking the first usable match.</summary>
    Greedy = 3,

    /// <summary>Hash chains with one step of lazy matching.</summary>
    Lazy = 4,

    /// <summary>Hash chains with two steps of lazy matching.</summary>
    Lazy2 = 5,

    /// <summary>Binary-tree matches with lazy matching.</summary>
    BinaryTreeLazy2 = 6,

    /// <summary>Optimal parsing over binary-tree matches.</summary>
    BinaryTreeOptimal = 7,

    /// <summary>Optimal parsing with deeper search.</summary>
    BinaryTreeUltra = 8,

    /// <summary>Optimal parsing with a first pass for statistics.</summary>
    BinaryTreeUltra2 = 9,
}

/// <summary>
/// The knobs a match finder runs with. <see cref="Resolve"/> derives them from a level the way the level tables define them;
/// callers of the raw layer can also set every field themselves (a default value has all fields zero, which is not usable).
/// </summary>
public readonly struct ZstdCompressionParameters
{
    /// <summary>The smallest window log the format can describe.</summary>
    public const int MinWindowLog = 10;

    /// <summary>Gets the log of the window size: how far back a match may reach.</summary>
    public int WindowLog { get; init; }

    /// <summary>Gets the log of the size of the secondary table (the short-hash table for <see cref="ZstdStrategy.DoubleFast"/>, the chain table for chain strategies).</summary>
    public int ChainLog { get; init; }

    /// <summary>Gets the log of the size of the main hash table.</summary>
    public int HashLog { get; init; }

    /// <summary>Gets the number of search attempts for the chain and tree strategies.</summary>
    public int SearchLog { get; init; }

    /// <summary>Gets the length of the hashed prefix of a position, which is also the shortest match the fast strategies look for.</summary>
    public int MinMatch { get; init; }

    /// <summary>Gets the skip acceleration of the fast strategy (negative levels), or the target match length of the strong strategies.</summary>
    public int TargetLength { get; init; }

    /// <summary>Gets the match-finding strategy.</summary>
    public ZstdStrategy Strategy { get; init; }

    /// <summary>
    /// Gets a value indicating whether literals are entropy coded. The negative levels leave them raw: at those speeds the
    /// Huffman pass would cost more than the size it saves is worth.
    /// </summary>
    public bool CompressLiterals { get; init; }

    /// <summary>Gets the window size in bytes.</summary>
    public long WindowSize => 1L << WindowLog;

    /// <summary>The lowest level the options accept: <c>-131072</c>, an acceleration of 131072.</summary>
    public const int MinLevel = -131072;

    /// <summary>The highest level this version implements.</summary>
    public const int MaxLevel = 4;

    // Level tables, one row per level, for source sizes above 256 KiB, up to 256 KiB, up to 128 KiB and up to 16 KiB.
    // Columns: window log, chain log, hash log, search log, minimum match, strategy. Row 0 is the base of the negative levels.
    // The values were read off the reference tool's reported parameters for each size class (docs/zstd-encoder.md).
    private static readonly short[][] s_table =
    [
        [19, 12, 13, 1, 6, 1, /**/ 19, 13, 14, 1, 7, 1, /**/ 20, 15, 16, 1, 6, 1, /**/ 21, 16, 17, 1, 5, 2, /**/ 21, 18, 18, 1, 5, 2],
        [18, 12, 13, 1, 5, 1, /**/ 18, 13, 14, 1, 6, 1, /**/ 18, 14, 14, 1, 5, 2, /**/ 18, 16, 16, 1, 4, 2, /**/ 18, 16, 17, 3, 5, 2],
        [17, 12, 12, 1, 5, 1, /**/ 17, 12, 13, 1, 6, 1, /**/ 17, 13, 15, 1, 5, 1, /**/ 17, 15, 16, 2, 5, 2, /**/ 17, 17, 17, 2, 4, 2],
        [14, 12, 13, 1, 5, 1, /**/ 14, 14, 15, 1, 5, 1, /**/ 14, 14, 15, 1, 4, 1, /**/ 14, 14, 15, 2, 4, 2, /**/ 14, 14, 14, 4, 4, 2],
    ];

    /// <summary>
    /// Derives the parameters of a level for a source of a known (or unknown) size and an optional dictionary: the size class
    /// picks the table, and the window and tables shrink to what a source of that size can use.
    /// </summary>
    /// <param name="level">The level: 1 to 4, 0 for the default (3), or negative for the faster-than-1 levels; the level is the acceleration.</param>
    /// <param name="contentSize">The number of bytes to compress, or -1 when unknown.</param>
    /// <param name="dictionarySize">The size of the dictionary content, 0 for none.</param>
    /// <returns>The parameters.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside <see cref="MinLevel"/> to <see cref="MaxLevel"/>.</exception>
    public static ZstdCompressionParameters Resolve(int level, long contentSize, int dictionarySize)
    {
        if (level == 0)
        {
            level = 3;
        }

        if (level < MinLevel || level > MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"Levels from {MinLevel} to {MaxLevel} are supported.");
        }

        int tableId = 0;
        long rSize = -1;
        if (contentSize >= 0)
        {
            rSize = contentSize + dictionarySize;
            tableId = (rSize <= 256 * 1024 ? 1 : 0) + (rSize <= 128 * 1024 ? 1 : 0) + (rSize <= 16 * 1024 ? 1 : 0);
        }

        short[] row = s_table[tableId];
        int column = level < 0 ? 0 : level * 6;
        int windowLog = row[column];
        int chainLog = row[column + 1];
        int hashLog = row[column + 2];
        int searchLog = row[column + 3];
        int minMatch = row[column + 4];
        var strategy = (ZstdStrategy)row[column + 5];
        if (rSize >= 0)
        {
            int sourceLog = rSize <= 1 ? 0 : BitOperations.Log2((ulong)(rSize - 1)) + 1;
            windowLog = Math.Min(windowLog, Math.Max(sourceLog, MinWindowLog));
        }

        hashLog = Math.Min(hashLog, windowLog + 1);
        chainLog = Math.Min(chainLog, windowLog);
        return new ZstdCompressionParameters
        {
            WindowLog = windowLog,
            ChainLog = chainLog,
            HashLog = hashLog,
            SearchLog = searchLog,
            MinMatch = minMatch,
            TargetLength = level < 0 ? -level : 0,
            Strategy = strategy,
            CompressLiterals = level >= 0,
        };
    }
}
