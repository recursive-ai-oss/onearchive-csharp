using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>Constants of the LZ4 block and frame formats, and the error helpers every LZ4 type shares.</summary>
internal static class Lz4Format
{
    public const string Name = "lz4";

    // Frame format, "General Structure of LZ4 Frame format" and "Skippable Frames".
    public const uint FrameMagic = 0x184D2204;
    public const uint LegacyMagic = 0x184C2102;
    public const uint SkippableMagicBase = 0x184D2A50;
    public const uint SkippableMagicMask = 0xFFFFFFF0;

    /// <summary>The longest frame header: magic (4), FLG, BD, content size (8), dictionary ID (4), header checksum.</summary>
    public const int FrameHeaderMax = 4 + 2 + 8 + 4 + 1;

    /// <summary>The largest offset a sequence can carry ("Compressed block format").</summary>
    public const int MaxOffset = 65535;

    /// <summary>The shortest match, "minmatch".</summary>
    public const int MinMatch = 4;

    /// <summary>Block format, "End of block conditions": the last sequence holds at least this many literals.</summary>
    public const int LastLiterals = 5;

    /// <summary>Block format, "End of block conditions": the last match starts at least this far before the end.</summary>
    public const int MatchFindLimit = 12;

    /// <summary>The legacy frame's fixed block size (8 MiB).</summary>
    public const int LegacyBlockSize = 8 << 20;

    /// <summary>The largest compressed legacy block: the worst-case expansion of a full block (size + size/255 + 16).</summary>
    public const int LegacyCompressedMax = LegacyBlockSize + (LegacyBlockSize / 255) + 16;

    public static bool IsSkippable(uint magic) => (magic & SkippableMagicMask) == SkippableMagicBase;

    public static bool IsFrameStart(uint magic) => magic == FrameMagic || magic == LegacyMagic || IsSkippable(magic);

    [DoesNotReturn]
    public static void ThrowTruncated(long offset, string expected) =>
        throw new CompressionFormatException(Name, offset, expected, isTruncated: true);

    [DoesNotReturn]
    public static void ThrowInvalid(long offset, string expected) =>
        throw new CompressionFormatException(Name, offset, expected);

    [DoesNotReturn]
    public static void ThrowLimit(string limitName, long limit, long requested) =>
        throw new CompressionLimitException(Name, limitName, limit, requested);
}
