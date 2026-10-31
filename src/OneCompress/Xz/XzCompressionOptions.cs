using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Xz;

/// <summary>A filter that runs before LZMA2 in an xz block: a branch/call/jump filter for executable code, or the delta filter.</summary>
/// <remarks>The xz file format specification, section 5.3: filter IDs and properties. Create instances with <see cref="Branch"/> and <see cref="Delta"/>.</remarks>
public sealed class XzFilter
{
    private readonly BranchArchitecture? _architecture;
    private readonly uint _startOffset;
    private readonly int _distance;

    private XzFilter(BranchArchitecture? architecture, uint startOffset, int distance)
    {
        _architecture = architecture;
        _startOffset = startOffset;
        _distance = distance;
    }

    /// <summary>Gets the xz filter ID.</summary>
    public int Id => _architecture is { } a ? (int)a : 0x03;

    /// <summary>Creates a branch/call/jump filter.</summary>
    /// <param name="architecture">The instruction set.</param>
    /// <param name="startOffset">The address of the block's first byte; a multiple of the architecture's alignment (see <see cref="BranchFilter.GetAlignment"/>). Written to the block header when not zero.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The architecture is unknown or the offset is not aligned.</exception>
    public static XzFilter Branch(BranchArchitecture architecture, uint startOffset = 0)
    {
        int alignment = BranchFilter.GetAlignment(architecture);
        if (startOffset % (uint)alignment != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset), $"The start offset must be a multiple of {alignment} for this architecture.");
        }

        return new XzFilter(architecture, startOffset, 0);
    }

    /// <summary>Creates a delta filter.</summary>
    /// <param name="distance">The stride in bytes, 1 to 256.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="distance"/> is outside 1 to 256.</exception>
    public static XzFilter Delta(int distance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, 256);
        return new XzFilter(null, 0, distance);
    }

    internal int WriteProperties(Span<byte> destination)
    {
        if (_architecture is null)
        {
            destination[0] = (byte)(_distance - 1);
            return 1;
        }

        if (_startOffset == 0)
        {
            return 0;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination, _startOffset);
        return 4;
    }

    internal IFilterStage CreateStage() => _architecture is { } a
        ? new BranchStage(new BranchFilter(a, _startOffset, encode: true))
        : new DeltaStage(new DeltaFilter(_distance, encode: true));

    internal interface IFilterStage
    {
        // Converts the leading bytes of data in place; returns how many are final.
        int Convert(Span<byte> data);

        void Reset();
    }

    private sealed class BranchStage(BranchFilter filter) : IFilterStage
    {
        public int Convert(Span<byte> data) => filter.Convert(data);

        public void Reset() => filter.Reset();
    }

    private sealed class DeltaStage(DeltaFilter filter) : IFilterStage
    {
        public int Convert(Span<byte> data)
        {
            filter.Convert(data);
            return data.Length;
        }

        public void Reset() => filter.Reset();
    }
}

/// <summary>Options for compressing to the xz format.</summary>
/// <remarks>The output depends only on the input and these options: <see cref="BlockSize"/> cuts the input into blocks whose boundaries are fixed by it, never by buffering or threads.</remarks>
public sealed class XzCompressionOptions
{
    /// <summary>The most filters a block may have, including LZMA2.</summary>
    public const int MaxFilters = 4;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static XzCompressionOptions Default { get; } = new();

    /// <summary>Gets the LZMA2 options: preset, dictionary size, lc, lp, pb, nice length and so on. Default <see cref="LzmaOptions.Default"/> (preset 6).</summary>
    public LzmaOptions Lzma { get; init; } = LzmaOptions.Default;

    /// <summary>Gets the integrity check of each block. Default <see cref="XzCheck.Crc64"/>, like the reference tool.</summary>
    public XzCheck Check { get; init; } = XzCheck.Crc64;

    /// <summary>
    /// Gets the uncompressed size of a block. 0 (the default) writes the whole input as one block. A smaller size writes independent blocks
    /// (each restarts the dictionary and the filters), which costs some ratio and lets blocks be compressed or decompressed in parallel.
    /// </summary>
    public long BlockSize { get; init; }

    /// <summary>Gets the filters that run before LZMA2, in the order they are applied when compressing; at most <see cref="MaxFilters"/> - 1. Default none.</summary>
    public IReadOnlyList<XzFilter>? Filters { get; init; }
}
