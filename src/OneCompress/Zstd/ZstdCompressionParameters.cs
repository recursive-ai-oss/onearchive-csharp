using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>The match-finding strategies of Zstandard, from fastest to strongest; the numbers follow the level tables.</summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
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
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
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

    /// <summary>
    /// Gets the log of the size of the main hash table of the dictionary's own search index, or 0 when it is the same as
    /// <see cref="HashLog"/>. A small source compressed with a large dictionary works on small tables of its own and looks the
    /// dictionary up in an index built once, whose size suits the dictionary.
    /// </summary>
    public int DictionaryHashLog { get; init; }

    /// <summary>Gets the log of the size of the dictionary index's secondary table, or 0 when it is the same as <see cref="ChainLog"/>.</summary>
    public int DictionaryChainLog { get; init; }

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
    public const int MaxLevel = 22;

    // Level tables, one row per level (row 0 is the base of the negative levels), for source sizes above 256 KiB, up to 256 KiB, up to
    // 128 KiB and up to 16 KiB. Columns: window log, chain log, hash log, search log, minimum match, target length, strategy.
    // The values were read off the reference tool's reported parameters for each size class (docs/zstd-encoder.md).
    private const int Columns = 7;

    private static readonly short[][] s_table =
    [
        [
            19, 12, 13, 1, 6, 0, 1,
            19, 13, 14, 1, 7, 0, 1,
            20, 15, 16, 1, 6, 0, 1,
            21, 16, 17, 1, 5, 0, 2,
            21, 18, 18, 1, 5, 0, 2,
            21, 18, 19, 3, 5, 2, 3,
            21, 18, 19, 3, 5, 4, 4,
            21, 19, 20, 4, 5, 8, 4,
            21, 19, 20, 4, 5, 16, 5,
            22, 20, 21, 4, 5, 16, 5,
            22, 21, 22, 5, 5, 16, 5,
            22, 21, 22, 6, 5, 16, 5,
            22, 22, 23, 6, 5, 32, 5,
            22, 22, 22, 4, 5, 32, 6,
            22, 22, 23, 5, 5, 32, 6,
            22, 23, 23, 6, 5, 32, 6,
            22, 22, 22, 5, 5, 48, 7,
            23, 23, 22, 5, 4, 64, 7,
            23, 23, 22, 6, 3, 64, 8,
            23, 24, 22, 7, 3, 256, 9,
            25, 25, 23, 7, 3, 256, 9,
            26, 26, 24, 7, 3, 512, 9,
            27, 27, 25, 9, 3, 999, 9,
        ],
        [
            18, 12, 13, 1, 5, 0, 1,
            18, 13, 14, 1, 6, 0, 1,
            18, 14, 14, 1, 5, 0, 2,
            18, 16, 16, 1, 4, 0, 2,
            18, 16, 17, 3, 5, 2, 3,
            18, 17, 18, 5, 5, 2, 3,
            18, 18, 19, 3, 5, 4, 4,
            18, 18, 19, 4, 4, 4, 4,
            18, 18, 19, 4, 4, 8, 5,
            18, 18, 19, 5, 4, 8, 5,
            18, 18, 19, 6, 4, 8, 5,
            18, 18, 19, 5, 4, 12, 6,
            18, 19, 19, 7, 4, 12, 6,
            18, 18, 19, 4, 4, 16, 7,
            18, 18, 19, 4, 3, 32, 7,
            18, 18, 19, 6, 3, 128, 7,
            18, 19, 19, 6, 3, 128, 8,
            18, 19, 19, 8, 3, 256, 8,
            18, 19, 19, 6, 3, 128, 9,
            18, 19, 19, 8, 3, 256, 9,
            18, 19, 19, 10, 3, 512, 9,
            18, 19, 19, 12, 3, 512, 9,
            18, 19, 19, 13, 3, 999, 9,
        ],
        [
            17, 12, 12, 1, 5, 0, 1,
            17, 12, 13, 1, 6, 0, 1,
            17, 13, 15, 1, 5, 0, 1,
            17, 15, 16, 2, 5, 0, 2,
            17, 17, 17, 2, 4, 0, 2,
            17, 16, 17, 3, 4, 2, 3,
            17, 16, 17, 3, 4, 4, 4,
            17, 16, 17, 3, 4, 8, 5,
            17, 16, 17, 4, 4, 8, 5,
            17, 16, 17, 5, 4, 8, 5,
            17, 16, 17, 6, 4, 8, 5,
            17, 17, 17, 5, 4, 8, 6,
            17, 18, 17, 7, 4, 12, 6,
            17, 18, 17, 3, 4, 12, 7,
            17, 18, 17, 4, 3, 32, 7,
            17, 18, 17, 6, 3, 256, 7,
            17, 18, 17, 6, 3, 128, 8,
            17, 18, 17, 8, 3, 256, 8,
            17, 18, 17, 10, 3, 512, 8,
            17, 18, 17, 5, 3, 256, 9,
            17, 18, 17, 7, 3, 512, 9,
            17, 18, 17, 9, 3, 512, 9,
            17, 18, 17, 11, 3, 999, 9,
        ],
        [
            14, 12, 13, 1, 5, 0, 1,
            14, 14, 15, 1, 5, 0, 1,
            14, 14, 15, 1, 4, 0, 1,
            14, 14, 15, 2, 4, 0, 2,
            14, 14, 14, 4, 4, 2, 3,
            14, 14, 14, 3, 4, 4, 4,
            14, 14, 14, 4, 4, 8, 5,
            14, 14, 14, 6, 4, 8, 5,
            14, 14, 14, 8, 4, 8, 5,
            14, 15, 14, 5, 4, 8, 6,
            14, 15, 14, 9, 4, 8, 6,
            14, 15, 14, 3, 4, 12, 7,
            14, 15, 14, 4, 3, 24, 7,
            14, 15, 14, 5, 3, 32, 8,
            14, 15, 15, 6, 3, 64, 8,
            14, 15, 15, 7, 3, 256, 8,
            14, 15, 15, 5, 3, 48, 9,
            14, 15, 15, 6, 3, 128, 9,
            14, 15, 15, 7, 3, 256, 9,
            14, 15, 15, 8, 3, 256, 9,
            14, 15, 15, 8, 3, 512, 9,
            14, 15, 15, 9, 3, 512, 9,
            14, 15, 15, 10, 3, 999, 9,
        ],
    ];

    // The binary-tree strategies keep two links per position, so their table covers half as many positions as it has slots.
    private static int MaxChainLog(ZstdStrategy strategy, int windowLog) => strategy >= ZstdStrategy.BinaryTreeLazy2 ? windowLog + 1 : windowLog;

    /// <summary>
    /// Derives the parameters of a level for a source of a known (or unknown) size and an optional dictionary: the size class
    /// picks the table, and the window and tables shrink to what a source of that size can use.
    /// </summary>
    /// <param name="level">The level: 1 to 22 (20 and above are the reference tool's ultra levels), 0 for the default (3), or negative for the faster-than-1 levels; the level is the acceleration.</param>
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
        int column = level < 0 ? 0 : level * Columns;
        int windowLog = row[column];
        int chainLog = row[column + 1];
        int hashLog = row[column + 2];
        int searchLog = row[column + 3];
        int minMatch = row[column + 4];
        int targetLength = level < 0 ? -level : row[column + 5];
        var strategy = (ZstdStrategy)row[column + 6];
        if (rSize >= 0)
        {
            int sourceLog = rSize <= 1 ? 0 : BitOperations.Log2((ulong)(rSize - 1)) + 1;
            windowLog = Math.Min(windowLog, Math.Max(sourceLog, MinWindowLog));
        }

        hashLog = Math.Min(hashLog, windowLog + 1);
        chainLog = Math.Min(chainLog, MaxChainLog(strategy, windowLog));
        int dictionaryHashLog = 0;
        int dictionaryChainLog = 0;
        if (dictionarySize > 0)
        {
            // The tables the source fills need only suit the source; the dictionary keeps the sizes its own length calls for.
            // Its index holds one entry per content position, so tables beyond the content's size only cost memory (a 4 KiB
            // dictionary at level 22 would otherwise ask for 256 MiB): the chain index is capped at the content length, which
            // loses no position, and the hash tables at 16 slots per position.
            int contentLog = Math.Max(BitOperations.Log2((uint)Math.Max(dictionarySize - 1, 1)) + 1, MinWindowLog);
            bool hashed = strategy <= ZstdStrategy.DoubleFast;
            dictionaryHashLog = Math.Min(hashLog, contentLog + 4);
            dictionaryChainLog = hashed ? Math.Min(chainLog, contentLog + 4) : Math.Min(chainLog, contentLog);
            if (contentSize >= 0)
            {
                int sourceWindowLog = Math.Min(windowLog, Math.Max(contentSize <= 1 ? 0 : BitOperations.Log2((ulong)(contentSize - 1)) + 1, MinWindowLog));
                hashLog = Math.Min(hashLog, sourceWindowLog + 1);
                chainLog = Math.Min(chainLog, MaxChainLog(strategy, sourceWindowLog));
            }
        }

        return new ZstdCompressionParameters
        {
            DictionaryHashLog = dictionaryHashLog,
            DictionaryChainLog = dictionaryChainLog,
            WindowLog = windowLog,
            ChainLog = chainLog,
            HashLog = hashLog,
            SearchLog = searchLog,
            MinMatch = minMatch,
            TargetLength = targetLength,
            Strategy = strategy,
            CompressLiterals = level >= 0,
        };
    }
}
