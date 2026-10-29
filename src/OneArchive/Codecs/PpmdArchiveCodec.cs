using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Ppmd;

namespace RecursiveAI.OneArchive.Internal;

/// <summary>PPMd for zip method 98 (variant I) and the 7z PPMD coder (variant H). Documented in <c>docs/ppmd.md</c>.</summary>
internal sealed class PpmdArchiveCodec : ArchiveCodec
{
    private const int DefaultLevel = 5;

    // The order and model memory for each level, the numbers 7-Zip's -mx levels use for a large input
    // (testdata/ppmd/levels/NOTES.md). Level 0 is "no compression" and never gets here.
    private static (int Order, int MemoryLog2) SevenZipLevel(int level) => Math.Clamp(level, 1, 9) switch
    {
        1 => (4, 20),
        2 => (4, 21),
        3 => (5, 22),
        4 => (5, 23),
        5 => (6, 24),
        6 => (8, 25),
        7 => (16, 26),
        8 => (24, 27),
        _ => (32, 28),
    };

    private static (int Order, int MemoryMiB, PpmdRestoreMethod Restore) ZipLevel(int level) => Math.Clamp(level, 1, 9) switch
    {
        1 => (4, 1, PpmdRestoreMethod.Restart),
        2 => (5, 2, PpmdRestoreMethod.Restart),
        3 => (6, 4, PpmdRestoreMethod.Restart),
        4 => (7, 8, PpmdRestoreMethod.Restart),
        5 => (8, 16, PpmdRestoreMethod.Restart),
        6 => (9, 32, PpmdRestoreMethod.Restart),
        7 => (10, 64, PpmdRestoreMethod.CutOff),
        8 => (11, 128, PpmdRestoreMethod.CutOff),
        _ => (12, 256, PpmdRestoreMethod.CutOff),
    };

    // A small input does not need a big model: the smallest power of two (from 64 KiB) that is at least 16 times the input.
    private static long Reduce(long memory, long size)
    {
        if (size < 0)
        {
            return memory;
        }

        for (int k = 16; k <= 31; k++)
        {
            if (size <= (1L << k) / 16)
            {
                return Math.Min(memory, 1L << k);
            }
        }

        return memory;
    }

    // ---- zip ----

    public override ZipBlockDecoder CreateZipDecoder(long size, long dataOffset, ArchiveLimits limits) =>
        CreateZipDecoder(size, -1, dataOffset, limits);

    public override ZipBlockDecoder CreateZipDecoder(long size, long compressedSize, long dataOffset, ArchiveLimits limits) =>
        new(new ZipPpmdDecoder(size, compressedSize, dataOffset, limits).Decompress);

    public override Stream OpenZipCompress(Stream sink, int? level, long size) => OpenZipCompress(sink, level, size, null);

    public override Stream OpenZipCompress(Stream sink, int? level, long size, object? methodOptions)
    {
        PpmdIOptions options;
        if (methodOptions is PpmdIOptions given)
        {
            options = given;
        }
        else
        {
            (int order, int mib, PpmdRestoreMethod restore) = ZipLevel(level ?? DefaultLevel);
            options = new PpmdIOptions { Order = order, MemorySize = mib << 20, RestoreMethod = restore };
        }

        // The header stores whole MiB, so a reduced model is rounded up to the next MiB.
        long memory = Reduce(options.MemorySize, size);
        memory = Math.Max(1, (memory + (1 << 20) - 1) >> 20) << 20;
        options = new PpmdIOptions { Order = options.Order, MemorySize = (int)memory, RestoreMethod = options.RestoreMethod, WriteEndMarker = options.WriteEndMarker };
        Span<byte> header = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(header, options.ToHeader());
        sink.Write(header);
        return new PpmdStream(sink, options, leaveOpen: true);
    }

    // ---- 7z ----

    public override Stream OpenSevenZipDecoder(SevenZipCoder coder, Stream input, ArchiveLimits limits, SevenZipReaderOptions options, long unpackSize, long errorOffset)
    {
        ReadOnlySpan<byte> props = coder.Properties.Span;
        if (props.Length != 5)
        {
            throw SevenZipFolderDecoder.BadProps(errorOffset, coder, "five bytes (the order and the model memory size)");
        }

        int order = props[0];
        if (order is < PpmdHModel.MinOrder or > PpmdHModel.MaxOrder)
        {
            throw SevenZipFolderDecoder.BadProps(errorOffset, coder, $"an order from {PpmdHModel.MinOrder} to {PpmdHModel.MaxOrder}, not {order},");
        }

        uint memory = BinaryPrimitives.ReadUInt32LittleEndian(props[1..]);
        if (memory < PpmdHModel.MinMemorySize)
        {
            throw SevenZipFolderDecoder.BadProps(errorOffset, coder, $"a model memory size of at least {PpmdHModel.MinMemorySize} bytes, not {memory},");
        }

        long limit = Math.Min(limits.MaxPpmdMemorySize, PpmdHModel.MaxMemorySize);
        if (memory > limit)
        {
            throw new CompressionLimitException(SevenZipFormat.Name, nameof(ArchiveLimits.MaxPpmdMemorySize), limits.MaxPpmdMemorySize, memory);
        }

        return new PpmdStream(input, new PpmdHDecompressionOptions
        {
            Order = order,
            MemorySize = (int)memory,
            OutputSize = unpackSize,
            MaxMemorySize = (int)limit,
            MaxOutputSize = long.MaxValue,
        });
    }

    public override Stream OpenSevenZipCompress(SevenZipMethod method, Stream sink, SevenZipWriterOptions options, long knownLength, Action<byte[]> setProperties, int deferThreshold)
    {
        PpmdHOptions chosen;
        if (options.Ppmd is { } given)
        {
            chosen = given;
        }
        else
        {
            (int order, int log2) = SevenZipLevel(options.Level);
            chosen = new PpmdHOptions { Order = order, MemorySize = 1 << log2 };
        }

        long memory = Math.Max(Reduce(chosen.MemorySize, knownLength), PpmdHModel.MinMemorySize);
        chosen = new PpmdHOptions { Order = chosen.Order, MemorySize = (int)Math.Min(memory, chosen.MemorySize), WriteEndMarker = false };
        setProperties(chosen.ToProperties());
        return new PpmdStream(sink, chosen, leaveOpen: true);
    }
}

/// <summary>
/// Decodes a zip method 98 entry: the two-byte parameter header, the range-coded data, and the end marker 7-Zip writes after it.
/// The PPMd decoder cannot stop in the middle of a symbol, so bytes of a symbol that is not complete yet are kept here until the rest arrives.
/// </summary>
internal sealed class ZipPpmdDecoder(long size, long compressedSize, long dataOffset, ArchiveLimits limits)
{
    private PpmdIDecoder? _decoder;
    private byte[] _carry = [];
    private int _carryLength;
    private long _used;
    private bool _markerDone;

    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        consumed = 0;
        written = 0;
        int pos = 0;
        if (_decoder is null)
        {
            int take = Math.Min(2 - _carryLength, source.Length);
            Append(source[..take]);
            pos = take;
            if (_carryLength < 2)
            {
                consumed = pos;
                return OperationStatus.NeedMoreData;
            }

            StartDecoder(BinaryPrimitives.ReadUInt16LittleEndian(_carry));
            _carryLength = 0;
            _used = 2;
        }

        OperationStatus status;
        int used;
        int w;
        if (_carryLength == 0)
        {
            status = Run(source[pos..], destination, out used, out w);
            written = w;
            if (status == OperationStatus.NeedMoreData)
            {
                // Keep the unfinished symbol: every byte offered is ours now.
                Append(source[(pos + used)..]);
                consumed = source.Length;
            }
            else
            {
                consumed = pos + used;
            }

            return status;
        }

        // Join the kept bytes with new ones, a few at a time, until the symbol they belong to is complete.
        int joinedFrom = pos;
        while (true)
        {
            int more = Math.Min(source.Length - pos, 512);
            Append(source.Slice(pos, more));
            pos += more;
            status = Run(_carry.AsSpan(0, _carryLength), destination[written..], out used, out w);
            written += w;
            if (status == OperationStatus.NeedMoreData && w == 0 && pos < source.Length)
            {
                continue;
            }

            break;
        }

        int left = _carryLength - used;
        if (status != OperationStatus.NeedMoreData)
        {
            // Hand back the part of the source that was not needed: it is the tail of what was appended.
            int giveBack = Math.Min(left, pos - joinedFrom);
            left -= giveBack;
            pos -= giveBack;
            _carryLength -= giveBack;
        }

        Buffer.BlockCopy(_carry, used, _carry, 0, left);
        _carryLength = left;
        consumed = pos;
        return status;
    }

    private void StartDecoder(ushort header)
    {
        PpmdIDecompressionOptions parsed;
        try
        {
            parsed = PpmdIDecompressionOptions.FromHeader(header, size);
        }
        catch (CompressionFormatException e)
        {
            throw ZipFormat.Bad(dataOffset, e.Expected + " in the PPMd parameters");
        }

        long max = Math.Min(limits.MaxPpmdMemorySize, PpmdIModel.MaxMemorySize);
        if (parsed.MemorySize > max)
        {
            throw new CompressionLimitException(ZipFormat.Name, nameof(ArchiveLimits.MaxPpmdMemorySize), limits.MaxPpmdMemorySize, parsed.MemorySize);
        }

        _decoder = new PpmdIDecoder(new PpmdIDecompressionOptions
        {
            Order = parsed.Order,
            MemorySize = parsed.MemorySize,
            RestoreMethod = parsed.RestoreMethod,
            OutputSize = size,
            MaxMemorySize = (int)max,
            MaxOutputSize = long.MaxValue,
        });
    }

    // Decodes, and after the last byte of output reads the end marker that 7-Zip writes (other writers leave it out: then the
    // entry's compressed size, or the bytes that follow, say so).
    private OperationStatus Run(ReadOnlySpan<byte> input, Span<byte> destination, out int used, out int written)
    {
        OperationStatus status = _decoder!.Decompress(input, destination, out used, out written, isFinalBlock: false);
        _used += used;
        if (status == OperationStatus.Done && !_markerDone)
        {
            if (compressedSize >= 0 && _used >= compressedSize)
            {
                _markerDone = true;
            }
            else
            {
                switch (_decoder.ReadEndMarker(input[used..], out int marker))
                {
                    case OperationStatus.Done:
                        used += marker;
                        _used += marker;
                        _markerDone = true;
                        break;
                    case OperationStatus.NeedMoreData:
                        status = OperationStatus.NeedMoreData;
                        break;
                    default:
                        _markerDone = true; // not a marker: the stream has none
                        break;
                }
            }
        }

        return status;
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (_carryLength + bytes.Length > _carry.Length)
        {
            Array.Resize(ref _carry, Math.Max(_carryLength + bytes.Length, 1024));
        }

        bytes.CopyTo(_carry.AsSpan(_carryLength));
        _carryLength += bytes.Length;
    }
}
