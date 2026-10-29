using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>Options for compressing with PPMd variant H, the model of the 7z "PPMD" coder.</summary>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class PpmdHOptions
{
    /// <summary>The default for <see cref="Order"/>.</summary>
    public const int DefaultOrder = 6;

    /// <summary>The default for <see cref="MemorySize"/>: 16 MiB.</summary>
    public const int DefaultMemorySize = 16 << 20;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static PpmdHOptions Default { get; } = new();

    /// <summary>
    /// Gets the model order, 2 to 64: how many preceding bytes form the longest context. Higher orders compress text better and run slower.
    /// The default is 6, 7-Zip's setting at its normal level.
    /// </summary>
    public int Order { get; init; } = DefaultOrder;

    /// <summary>
    /// Gets the model memory in bytes, <see cref="PpmdHModel.MinMemorySize"/> to <see cref="PpmdHModel.MaxMemorySize"/>. The decoder must be given the same
    /// value, and allocates it all, so it is also the decoder's memory cost. The default is 16 MiB.
    /// </summary>
    public int MemorySize { get; init; } = DefaultMemorySize;

    /// <summary>
    /// Gets a value indicating whether the end marker is written after the data. 7z streams do not use it (the container stores the size);
    /// a stream without a stored size needs it. The default is false.
    /// </summary>
    public bool WriteEndMarker { get; init; }

    /// <summary>Returns the five property bytes the 7z format stores for the PPMD coder: the order, then the memory size as a little-endian 32-bit number.</summary>
    /// <returns>The five bytes.</returns>
    public byte[] ToProperties()
    {
        var props = new byte[5];
        props[0] = (byte)Order;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(1), (uint)MemorySize);
        return props;
    }
}

/// <summary>Options for decompressing PPMd variant H data. The model parameters are not in the stream: they must match the encoder's.</summary>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class PpmdHDecompressionOptions
{
    /// <summary>The default for <see cref="MaxMemorySize"/>: 256 MiB.</summary>
    public const int DefaultMaxMemorySize = 256 << 20;

    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>Gets the model order the encoder used, 2 to 64.</summary>
    public int Order { get; init; } = PpmdHOptions.DefaultOrder;

    /// <summary>Gets the model memory the encoder used, in bytes.</summary>
    public int MemorySize { get; init; } = PpmdHOptions.DefaultMemorySize;

    /// <summary>
    /// Gets the number of bytes to decode, or -1 to decode until the end marker. 7z stores the size, so archive readers set it.
    /// The default is -1.
    /// </summary>
    public long OutputSize { get; init; } = -1;

    /// <summary>
    /// Gets the largest model memory the decoder will allocate. A <see cref="MemorySize"/> above it raises <see cref="CompressionLimitException"/>
    /// before anything is allocated, so a corrupt header cannot ask for gigabytes. The default is 256 MiB, which covers 7-Zip's highest level.
    /// </summary>
    public int MaxMemorySize { get; init; } = DefaultMaxMemorySize;

    /// <summary>Gets the largest output in bytes; decoding past it raises <see cref="CompressionLimitException"/>. The default is 1 GiB.</summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>Creates options from the five property bytes of a 7z PPMD coder.</summary>
    /// <param name="properties">The order byte and the little-endian 32-bit memory size.</param>
    /// <param name="outputSize">The decoded size, or -1 for "until the end marker".</param>
    /// <param name="maxMemorySize">The memory limit; 0 for <see cref="DefaultMaxMemorySize"/>.</param>
    /// <returns>Options with <see cref="Order"/> and <see cref="MemorySize"/> set.</returns>
    /// <exception cref="CompressionFormatException">The properties are not five bytes, the order is outside 2 to 64, or the memory size is below 2 KiB.</exception>
    /// <exception cref="CompressionLimitException">The memory size is above <paramref name="maxMemorySize"/> or above what this implementation can allocate.</exception>
    public static PpmdHDecompressionOptions FromProperties(ReadOnlySpan<byte> properties, long outputSize = -1, int maxMemorySize = 0)
    {
        if (properties.Length != 5)
        {
            throw new CompressionFormatException("ppmd7", 0, "five property bytes (order and memory size)");
        }

        int order = properties[0];
        uint mem = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(properties[1..]);
        if (order is < PpmdHModel.MinOrder or > PpmdHModel.MaxOrder)
        {
            throw new CompressionFormatException("ppmd7", 0, $"a model order from {PpmdHModel.MinOrder} to {PpmdHModel.MaxOrder}, not {order}");
        }

        if (mem < PpmdHModel.MinMemorySize)
        {
            throw new CompressionFormatException("ppmd7", 1, $"a memory size of at least {PpmdHModel.MinMemorySize} bytes, not {mem}");
        }

        int limit = maxMemorySize == 0 ? DefaultMaxMemorySize : maxMemorySize;
        if (mem > (uint)limit || mem > PpmdHModel.MaxMemorySize)
        {
            throw new CompressionLimitException("ppmd7", nameof(MaxMemorySize), limit, mem);
        }

        return new PpmdHDecompressionOptions { Order = order, MemorySize = (int)mem, OutputSize = outputSize, MaxMemorySize = limit };
    }
}

/// <summary>Options for compressing with PPMd variant I revision 1, the model of zip method 98.</summary>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class PpmdIOptions
{
    /// <summary>The default for <see cref="Order"/>.</summary>
    public const int DefaultOrder = 6;

    /// <summary>The default for <see cref="MemorySize"/>: 16 MiB.</summary>
    public const int DefaultMemorySize = 16 << 20;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static PpmdIOptions Default { get; } = new();

    /// <summary>Gets the model order, 2 to 16. The default is 6.</summary>
    public int Order { get; init; } = DefaultOrder;

    /// <summary>
    /// Gets the model memory in bytes. The zip header stores whole mebibytes from 1 to 256 (<see cref="ToHeader"/> requires that); the raw coder
    /// accepts any size from <see cref="PpmdIModel.MinMemorySize"/>. The default is 16 MiB.
    /// </summary>
    public int MemorySize { get; init; } = DefaultMemorySize;

    /// <summary>Gets what the model does when its memory is full. The default is <see cref="PpmdRestoreMethod.Restart"/>.</summary>
    public PpmdRestoreMethod RestoreMethod { get; init; } = PpmdRestoreMethod.Restart;

    /// <summary>
    /// Gets a value indicating whether the end marker is written after the data. The default is true: 7-Zip writes it in zip method 98 entries,
    /// and a reader that does not know the entry's size needs it to find the end.
    /// </summary>
    public bool WriteEndMarker { get; init; } = true;

    /// <summary>Returns the two-byte value that precedes the data of a zip method 98 entry (APPNOTE 5.10.4): order − 1, then size in MiB − 1 shifted by 4, then the restore method shifted by 12.</summary>
    /// <returns>The 16-bit value; it is stored little-endian.</returns>
    /// <exception cref="InvalidOperationException"><see cref="MemorySize"/> is not a whole number of MiB from 1 to 256.</exception>
    public ushort ToHeader()
    {
        if ((MemorySize & ((1 << 20) - 1)) != 0 || MemorySize < (1 << 20) || MemorySize > (256 << 20))
        {
            throw new InvalidOperationException("The zip PPMd header stores the memory size in whole MiB from 1 to 256.");
        }

        return (ushort)((Order - 1) + (((MemorySize >> 20) - 1) << 4) + ((int)RestoreMethod << 12));
    }
}

/// <summary>Options for decompressing PPMd variant I data. The model parameters are not in the stream: they must match the encoder's.</summary>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class PpmdIDecompressionOptions
{
    /// <summary>The default for <see cref="MaxMemorySize"/>: 256 MiB, the most a zip header can ask for.</summary>
    public const int DefaultMaxMemorySize = 256 << 20;

    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>Gets the model order the encoder used, 2 to 16.</summary>
    public int Order { get; init; } = PpmdIOptions.DefaultOrder;

    /// <summary>Gets the model memory the encoder used, in bytes.</summary>
    public int MemorySize { get; init; } = PpmdIOptions.DefaultMemorySize;

    /// <summary>Gets the restore method the encoder used.</summary>
    public PpmdRestoreMethod RestoreMethod { get; init; } = PpmdRestoreMethod.Restart;

    /// <summary>Gets the number of bytes to decode, or -1 to decode until the end marker. Zip stores the size, so archive readers set it. The default is -1.</summary>
    public long OutputSize { get; init; } = -1;

    /// <summary>Gets the largest model memory the decoder will allocate; a larger <see cref="MemorySize"/> raises <see cref="CompressionLimitException"/> before allocating. The default is 256 MiB.</summary>
    public int MaxMemorySize { get; init; } = DefaultMaxMemorySize;

    /// <summary>Gets the largest output in bytes; decoding past it raises <see cref="CompressionLimitException"/>. The default is 1 GiB.</summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>Creates options from the two-byte value in front of a zip method 98 entry's data.</summary>
    /// <param name="header">The 16-bit little-endian value (APPNOTE 5.10.4).</param>
    /// <param name="outputSize">The decoded size, or -1 for "until the end marker".</param>
    /// <returns>Options with the order, memory size and restore method set.</returns>
    /// <exception cref="CompressionFormatException">The order is 1 (below 2) or the restore method is not 0 or 1.</exception>
    public static PpmdIDecompressionOptions FromHeader(ushort header, long outputSize = -1)
    {
        int order = (header & 0xF) + 1;
        int mem = (((header >> 4) & 0xFF) + 1) << 20;
        int restore = header >> 12;
        if (order < PpmdIModel.MinOrder)
        {
            throw new CompressionFormatException("ppmd8", 0, "a model order from 2 to 16, not 1");
        }

        if (restore > (int)PpmdRestoreMethod.CutOff)
        {
            throw new CompressionFormatException("ppmd8", 1, $"restore method 0 or 1, not {restore}");
        }

        return new PpmdIDecompressionOptions { Order = order, MemorySize = mem, RestoreMethod = (PpmdRestoreMethod)restore, OutputSize = outputSize };
    }
}
