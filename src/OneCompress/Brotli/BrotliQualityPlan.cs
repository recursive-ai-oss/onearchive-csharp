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

    internal static BrotliQualityPlan For(int quality)
    {
        var p = new BrotliQualityPlan { Quality = quality, BlockSize = 1 << 22 };
        switch (quality)
        {
            case 0:
                p.BlockSize = 1 << 17;
                p.HashBits = 15;
                p.ChainDepth = 1;
                break;
            case 1:
                p.BlockSize = 1 << 17;
                p.HashBits = 16;
                p.ChainDepth = 1;
                break;
            case 2:
                p.BlockSize = 1 << 17;
                p.HashBits = 16;
                p.ChainDepth = 4;
                break;
            case 3:
                p.BlockSize = 1 << 18;
                p.HashBits = 17;
                p.ChainDepth = 8;
                p.Lazy = 1;
                p.LiteralContexts = true;
                break;
            case 4:
                p.BlockSize = 1 << 20;
                p.HashBits = 17;
                p.ChainDepth = 16;
                p.Lazy = 1;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                break;
            case 5:
                p.HashBits = 18;
                p.ChainDepth = 12;
                p.Lazy = 1;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.Dictionary = true;
                break;
            case 6:
                p.HashBits = 18;
                p.ChainDepth = 16;
                p.Lazy = 1;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                break;
            case 7:
                p.HashBits = 19;
                p.ChainDepth = 20;
                p.Lazy = 2;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                break;
            case 8:
                p.HashBits = 19;
                p.ChainDepth = 32;
                p.Lazy = 2;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                p.DictionaryTransforms = true;
                break;
            case 9:
                p.HashBits = 20;
                p.ChainDepth = 48;
                p.Lazy = 2;
                p.LiteralContexts = true;
                p.SplitBlocks = true;
                p.SearchContextMode = true;
                p.SearchDistanceParameters = true;
                p.Dictionary = true;
                p.DictionaryTransforms = true;
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

        p.BinaryTree = quality >= 5;
        return p;
    }
}
