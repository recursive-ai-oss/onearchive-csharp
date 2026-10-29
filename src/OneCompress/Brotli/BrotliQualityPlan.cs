namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// What each quality level does. The levels trade time for ratio in four ways: how hard the match finder looks (hash bits, chain depth),
/// how the parse chooses (greedy, lazy, or cost-based shortest path), how much the entropy coder adapts (block splitting, literal contexts,
/// distance parameters) and whether the static dictionary is searched. The numbers are this library's own; see docs/brotli-encoder.md.
/// </summary>
internal sealed class BrotliQualityPlan
{
    /// <summary>The quality, 0 to 11.</summary>
    internal int Quality;

    /// <summary>Input bytes per meta-block.</summary>
    internal int BlockSize;

    /// <summary>log2 of the hash table size.</summary>
    internal int HashBits;

    /// <summary>How many bytes of a position the bucketed table hashes: 4, or 5 to 8.</summary>
    internal int HashLength = 4;

    /// <summary>log2 of the bucket count of a second table that hashes twelve bytes to find long matches, or 0 for none.</summary>
    internal int LongHashBits;

    /// <summary>log2 of the slots per bucket of the long table.</summary>
    internal int LongBucketBits;

    /// <summary>The most entries of a long bucket that are tried, newest first.</summary>
    internal int LongDepth;

    /// <summary>log2 of the slots per hash bucket in the bucketed table of qualities 2 to 9.</summary>
    internal int BucketBits;

    /// <summary>The most hash chain links followed per position; 1 means the table alone is used.</summary>
    internal int ChainDepth;

    /// <summary>True to find matches with a binary tree per hash bucket instead of a hash chain.</summary>
    internal bool BinaryTree;

    /// <summary>A match this long ends the search for a better one.</summary>
    internal int NiceLength = 64;

    /// <summary>How many positions ahead a match may be postponed for a better one: 0, 1 or 2.</summary>
    internal int Lazy;

    /// <summary>True to search the static dictionary.</summary>
    internal bool Dictionary;

    /// <summary>True to try every dictionary transform, not only the identity.</summary>
    internal bool DictionaryTransforms;

    /// <summary>True to split literals, commands and distances into block types.</summary>
    internal bool SplitBlocks;

    /// <summary>The shortest segment, in literals, that block splitting compares.</summary>
    internal int LiteralSegmentMin = 256;

    /// <summary>The most segments of a block that splitting compares for literals.</summary>
    internal int LiteralSegmentMax = 2048;

    /// <summary>The most segments that are merged pair by pair when block types are clustered; more are clustered sequentially.</summary>
    internal int SplitPairwiseLimit = BrotliClusterer.PairwiseLimit;

    /// <summary>The most segments of a block that splitting compares for commands and for distances; 0 keeps one block type for both.</summary>
    internal int CommandSegmentMax = 128;

    /// <summary>The most segments of commands or distances that are merged pair by pair when block types are clustered.</summary>
    internal int CommandPairwiseLimit = BrotliClusterer.PairwiseLimit;

    /// <summary>True to model literals with their two preceding bytes.</summary>
    internal bool LiteralContexts;

    /// <summary>True to search the context mode of every literal block type; false uses UTF-8.</summary>
    internal bool SearchContextMode;

    /// <summary>True to try several NPOSTFIX and NDIRECT combinations.</summary>
    internal bool SearchDistanceParameters;

    /// <summary>True to parse by shortest path over a cost model.</summary>
    internal bool Optimal;

    /// <summary>For the shortest-path parse, how many times the cost model is refined from the previous parse.</summary>
    internal int OptimalPasses;

    /// <summary>The longest candidate list per position the shortest-path parse keeps.</summary>
    internal int MaxSegments = 128;

    /// <summary>
    /// After a position with no match, the parse advances by <c>1 + (bytes since the last copy &gt;&gt; SkipShift)</c>, so stretches that do not
    /// compress are crossed faster the longer they last. 31 turns it off.
    /// </summary>
    internal int SkipShift = 31;

    /// <summary>
    /// 0 to enter every position in the bucketed table. Otherwise the positions stepped over after a miss are not entered, and neither are
    /// those inside a copy of at least this many bytes except its last two.
    /// </summary>
    internal int SparseInsert;

    /// <summary>For the one-slot parse: how many bytes of a position its hash reads (4 to 8), and so the shortest copy found through the table.</summary>
    internal int FastHashLength = 4;


    /// <summary>True for the qualities whose parse hands the writer a <see cref="BrotliFastBlock"/>.</summary>
    internal bool UsesFastBlock => Quality <= 2;

    internal static BrotliQualityPlan For(int quality)
    {
        var p = new BrotliQualityPlan { Quality = quality, BlockSize = 1 << 22 };
        switch (quality)
        {
            case 0:
                p.BlockSize = 1 << 17;
                p.HashBits = 15;
                p.ChainDepth = 1;
                p.SkipShift = 6;
                p.FastHashLength = 5;
                break;
            case 1:
                p.BlockSize = 1 << 17;
                p.HashBits = 17;
                p.ChainDepth = 1;
                p.SkipShift = 6;
                p.FastHashLength = 6;
                break;
            case 2:
                p.BlockSize = 1 << 17;
                p.HashBits = 16;
                p.BucketBits = 2;
                p.ChainDepth = 2;
                p.SkipShift = 6;
                p.SparseInsert = 32;
                p.FastHashLength = 5;
                break;
            case 3:
                p.BlockSize = 1 << 18;
                p.HashBits = 15;
                p.ChainDepth = 4;
                p.BucketBits = 3;
                p.HashLength = 6;
                p.SkipShift = 5;
                p.Lazy = 1;
                p.LiteralContexts = true;
                p.SparseInsert = 32;
                break;
            case 4:
                p.BlockSize = 1 << 20;
                p.HashBits = 15;
                p.ChainDepth = 8;
                p.BucketBits = 3;
                p.HashLength = 6;
                p.SkipShift = 5;
                p.Lazy = 1;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.LiteralSegmentMin = 1024;
                p.LiteralSegmentMax = 128;
                p.SplitPairwiseLimit = 64;
                p.CommandSegmentMax = 32;
                p.CommandPairwiseLimit = 16;
                break;
            case 5:
                p.HashBits = 16;
                p.ChainDepth = 8;
                p.BucketBits = 4;
                p.HashLength = 6;
                p.SkipShift = 5;
                p.Lazy = 1;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.LiteralSegmentMin = 1024;
                p.LiteralSegmentMax = 128;
                p.SplitPairwiseLimit = 64;
                break;
            case 6:
                p.HashBits = 18;
                p.ChainDepth = 16;
                p.BucketBits = 4;
                p.HashLength = 6;
                p.LongHashBits = 16;
                p.LongBucketBits = 3;
                p.LongDepth = 8;
                p.Lazy = 1;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                p.LiteralSegmentMin = 1024;
                p.LiteralSegmentMax = 128;
                p.SplitPairwiseLimit = 64;
                break;
            case 7:
                p.HashBits = 18;
                p.ChainDepth = 24;
                p.BucketBits = 5;
                p.HashLength = 6;
                p.LongHashBits = 16;
                p.LongBucketBits = 4;
                p.LongDepth = 16;
                p.Lazy = 2;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                p.LiteralSegmentMin = 512;
                p.LiteralSegmentMax = 256;
                p.SplitPairwiseLimit = 96;
                break;
            case 8:
                p.HashBits = 18;
                p.ChainDepth = 32;
                p.BucketBits = 5;
                p.HashLength = 6;
                p.LongHashBits = 17;
                p.LongBucketBits = 4;
                p.LongDepth = 16;
                p.Lazy = 2;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                p.LiteralSegmentMin = 512;
                p.LiteralSegmentMax = 256;
                p.SplitPairwiseLimit = 96;
                break;
            case 9:
                p.HashBits = 17;
                p.ChainDepth = 48;
                p.BucketBits = 6;
                p.HashLength = 6;
                p.LongHashBits = 17;
                p.LongBucketBits = 5;
                p.LongDepth = 32;
                p.Lazy = 2;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                p.LiteralSegmentMin = 512;
                p.LiteralSegmentMax = 256;
                p.SplitPairwiseLimit = 96;
                break;
            default:
                // 10 and 11: the shortest path over a cost model, refined from the previous pass.
                p.BlockSize = 1 << 20;
                p.HashBits = 20;
                p.ChainDepth = quality == 10 ? 64 : 128;
                p.NiceLength = 128;
                p.Optimal = true;
                p.OptimalPasses = quality == 10 ? 2 : 3;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                p.DictionaryTransforms = true;
                break;
        }

        p.BinaryTree = quality >= 10;
        return p;
    }
}
