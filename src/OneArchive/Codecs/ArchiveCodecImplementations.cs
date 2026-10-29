using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Brotli;
using RecursiveAI.OneCompress.BZip2;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Deflate64;
using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Lzx;
using RecursiveAI.OneCompress.Xz;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneArchive.Internal;

// One class per codec. This file is the only place the archive code names a codec's types, so a codec that is never
// registered (ArchiveCodecs) is never reachable and the trimmer drops it.

internal sealed class ZstdArchiveCodec : ArchiveCodec
{
    // MaxOutputSize is unbounded in the decoders below: the archive reader bounds what it takes (total size, expansion ratio).
    public override Stream OpenDecompress(Stream source, ArchiveLimits limits) =>
        new ZstdStream(source, new ZstdDecompressionOptions { MaxWindowSize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });

    public override Stream OpenCompress(Stream sink, int? level, bool leaveOpen) =>
        new ZstdStream(sink, new ZstdCompressionOptions { Level = level ?? 3, Checksum = true }, leaveOpen);

    public override ZipBlockDecoder CreateZipDecoder(long size, long dataOffset, ArchiveLimits limits)
    {
        var d = new ZstdDecoder(new ZstdDecompressionOptions { MaxWindowSize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });
        return new ZipBlockDecoder(d.Decompress, multiFrame: true);
    }

    // The CRC-32 in the zip header covers the data, so a zstd frame carries no checksum of its own.
    public override Stream OpenZipCompress(Stream sink, int? level, long size) =>
        new ZstdStream(sink, new ZstdCompressionOptions { Level = level ?? 3, Checksum = false, ContentSize = size }, leaveOpen: true);

    public override Stream OpenSevenZipDecoder(SevenZipCoder coder, Stream input, ArchiveLimits limits, SevenZipReaderOptions options, long unpackSize, long errorOffset) =>
        new ZstdStream(input, new ZstdDecompressionOptions { MaxWindowSize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });

    private static ReadOnlySpan<int> SevenZipLevels => [0, 1, 2, 3, 5, 7, 9, 12, 16, 19];

    public override Stream OpenSevenZipCompress(SevenZipMethod method, Stream sink, SevenZipWriterOptions options, long knownLength, Action<byte[]> setProperties, int deferThreshold) =>
        new ZstdStream(sink, new ZstdCompressionOptions { Level = SevenZipLevels[options.Level] }, leaveOpen: true);
}

internal sealed class Lz4ArchiveCodec : ArchiveCodec
{
    public override Stream OpenDecompress(Stream source, ArchiveLimits limits) =>
        new Lz4Stream(source, new Lz4DecompressionOptions { MaxOutputSize = long.MaxValue });

    public override Stream OpenCompress(Stream sink, int? level, bool leaveOpen) =>
        new Lz4Stream(sink, new Lz4CompressionOptions { Level = level ?? Lz4CompressionOptions.DefaultLevel }, leaveOpen);
}

internal sealed class DeflateArchiveCodec : ArchiveCodec
{
    public override Stream OpenDecompress(Stream source, ArchiveLimits limits) =>
        new DeflateCodecStream(source, DeflateFormat.GZip, new DeflateDecompressionOptions { MaxOutputSize = long.MaxValue });

    public override Stream OpenCompress(Stream sink, int? level, bool leaveOpen) =>
        new DeflateEncoderStream(sink, DeflateFormat.GZip, new DeflateOptions { Level = level ?? DeflateOptions.DefaultLevel }, leaveOpen);

    public override bool TryGetGZipHeader(Stream decompressor, out string? name, out uint modificationTime)
    {
        name = null;
        modificationTime = 0;
        if (decompressor is not DeflateCodecStream gz)
        {
            return false;
        }

        GZipHeader header = gz.CurrentGZipHeader;
        name = header.GetName();
        modificationTime = (uint)header.ModificationTime;
        return true;
    }

    public override Stream OpenGZipCompress(Stream sink, int? level, string? name, uint modificationTime, bool leaveOpen) =>
        new DeflateEncoderStream(sink, DeflateFormat.GZip, new DeflateOptions
        {
            Level = level ?? DeflateOptions.DefaultLevel,
            GZipFileName = name,
            GZipModificationTime = modificationTime,
        }, leaveOpen);

    public override ZipBlockDecoder CreateZipDecoder(long size, long dataOffset, ArchiveLimits limits) =>
        new(new DeflateDecoder(DeflateFormat.Raw, new DeflateDecompressionOptions { MaxOutputSize = long.MaxValue }).Decompress);

    public override Stream OpenZipCompress(Stream sink, int? level, long size) =>
        new DeflateEncoderStream(sink, DeflateFormat.Raw, new DeflateOptions { Level = level ?? DeflateOptions.DefaultLevel }, leaveOpen: true);

    public override Stream OpenSevenZipDecoder(SevenZipCoder coder, Stream input, ArchiveLimits limits, SevenZipReaderOptions options, long unpackSize, long errorOffset) =>
        new DeflateCodecStream(input, DeflateFormat.Raw, new DeflateDecompressionOptions { MaxOutputSize = long.MaxValue });

    public override Stream OpenSevenZipCompress(SevenZipMethod method, Stream sink, SevenZipWriterOptions options, long knownLength, Action<byte[]> setProperties, int deferThreshold) =>
        new DeflateEncoderStream(sink, DeflateFormat.Raw, new DeflateOptions { Level = options.Level }, leaveOpen: true);

    public override CabBlockDecoder CreateCabDecoder(int windowBits) => new CabMsZipDecoder();
}

internal sealed class Deflate64ArchiveCodec : ArchiveCodec
{
    public override ZipBlockDecoder CreateZipDecoder(long size, long dataOffset, ArchiveLimits limits) =>
        new(new Deflate64Decoder(new Deflate64DecompressionOptions { MaxOutputSize = long.MaxValue }).Decompress);

    public override Stream OpenZipCompress(Stream sink, int? level, long size) =>
        new Deflate64Stream(sink, new Deflate64Options { Level = Math.Min(level ?? Deflate64Options.DefaultLevel, 9) }, leaveOpen: true);

    public override Stream OpenSevenZipDecoder(SevenZipCoder coder, Stream input, ArchiveLimits limits, SevenZipReaderOptions options, long unpackSize, long errorOffset) =>
        new Deflate64Stream(input, new Deflate64DecompressionOptions { MaxOutputSize = long.MaxValue });

    public override Stream OpenSevenZipCompress(SevenZipMethod method, Stream sink, SevenZipWriterOptions options, long knownLength, Action<byte[]> setProperties, int deferThreshold) =>
        new Deflate64Stream(sink, new Deflate64Options { Level = options.Level }, leaveOpen: true);
}

internal sealed class BZip2ArchiveCodec : ArchiveCodec
{
    public override Stream OpenDecompress(Stream source, ArchiveLimits limits) =>
        new BZip2Stream(source, new BZip2DecompressionOptions { MaxOutputSize = long.MaxValue });

    public override Stream OpenCompress(Stream sink, int? level, bool leaveOpen) =>
        new BZip2Stream(sink, new BZip2Options { Level = Math.Clamp(level ?? BZip2Options.DefaultLevel, 1, 9) }, leaveOpen);

    public override ZipBlockDecoder CreateZipDecoder(long size, long dataOffset, ArchiveLimits limits) =>
        new(new BZip2Decoder(new BZip2DecompressionOptions { MaxOutputSize = long.MaxValue }).Decompress);

    public override Stream OpenZipCompress(Stream sink, int? level, long size) =>
        new BZip2Stream(sink, new BZip2Options { Level = Math.Clamp(level ?? BZip2Options.DefaultLevel, 1, 9) }, leaveOpen: true);

    public override Stream OpenSevenZipDecoder(SevenZipCoder coder, Stream input, ArchiveLimits limits, SevenZipReaderOptions options, long unpackSize, long errorOffset) =>
        new BZip2Stream(input, new BZip2DecompressionOptions { MaxOutputSize = long.MaxValue });
}

internal sealed class XzArchiveCodec : ArchiveCodec
{
    public override Stream OpenDecompress(Stream source, ArchiveLimits limits) =>
        new XzStream(source, new XzDecompressionOptions { MaxDictionarySize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });

    public override Stream OpenCompress(Stream sink, int? level, bool leaveOpen) =>
        new XzEncoderStream(sink, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = level ?? LzmaOptions.DefaultPreset } }, leaveOpen);

    public override ZipBlockDecoder CreateZipDecoder(long size, long dataOffset, ArchiveLimits limits) =>
        new(new XzDecoder(new XzDecompressionOptions { MaxDictionarySize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue }).Decompress);
}

internal sealed class LzipArchiveCodec : ArchiveCodec
{
    public override Stream OpenDecompress(Stream source, ArchiveLimits limits) =>
        new LzipStream(source, new LzipDecompressionOptions { MaxDictionarySize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });

    public override Stream OpenCompress(Stream sink, int? level, bool leaveOpen) =>
        new LzipEncoderStream(sink, new LzipOptions { Level = level ?? LzipOptions.DefaultLevel }, leaveOpen);
}

internal sealed class BrotliArchiveCodec : ArchiveCodec
{
    // Quality 5 (the tool's own default is 11): the encoder here is far slower at 10 and 11, and 5 is the first quality with block
    // splitting and context modelling. Window 22 bits (4 MiB, the tool's default) stays inside the default ArchiveLimits.MaxWindowSize.
    private const int DefaultQuality = 5;

    // RFC 7932 section 9.1: a stream declares its own window; the limit keeps an untrusted one from sizing the ring buffer.
    public override Stream OpenDecompress(Stream source, ArchiveLimits limits) =>
        new BrotliStream(source, new BrotliDecompressionOptions { MaxWindowSize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });

    public override Stream OpenCompress(Stream sink, int? level, bool leaveOpen) =>
        new BrotliStream(sink, new BrotliOptions { Quality = level ?? DefaultQuality, WindowBits = BrotliOptions.DefaultWindowBits }, leaveOpen);
}

internal sealed class LzmaArchiveCodec : ArchiveCodec
{
    public override ZipBlockDecoder CreateZipDecoder(long size, long dataOffset, ArchiveLimits limits) =>
        new(new ZipLzmaDecoder(size, dataOffset, new LzmaDecompressionOptions { MaxDictionarySize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue }).Decompress);

    public override Stream OpenSevenZipDecoder(SevenZipCoder coder, Stream input, ArchiveLimits limits, SevenZipReaderOptions options, long unpackSize, long errorOffset)
    {
        ReadOnlySpan<byte> props = coder.Properties.Span;
        var lzmaOptions = new LzmaDecompressionOptions { MaxDictionarySize = options.MaxDictionarySize, MaxOutputSize = long.MaxValue };
        if (coder.MethodId == SevenZipFormat.MethodLzma)
        {
            if (props.Length != 5 || !LzmaProperties.TryParsePropertiesByte(props[0], out int lc, out int lp, out int pb))
            {
                throw SevenZipFolderDecoder.BadProps(errorOffset, coder, "five bytes (lc/lp/pb and the dictionary size)");
            }

            uint dict = BinaryPrimitives.ReadUInt32LittleEndian(props[1..]);
            LzmaDecoder d;
            try
            {
                d = LzmaDecoder.CreateRaw(new LzmaProperties(lc, lp, pb, Math.Max(dict, LzmaProperties.MinimumDictionarySize)), unpackSize, lzmaOptions);
            }
            catch (ArgumentException)
            {
                throw SevenZipFolderDecoder.BadProps(errorOffset, coder, "valid lc, lp, pb and dictionary size");
            }

            return new SevenZipDecoderStream(input, d.Decompress, d.ThrowIfIncomplete, errorOffset);
        }

        if (props.Length != 1 || props[0] > 40)
        {
            throw SevenZipFolderDecoder.BadProps(errorOffset, coder, "one byte of at most 40 (the dictionary size)");
        }

        var d2 = new Lzma2Decoder(props[0], lzmaOptions);
        return new SevenZipDecoderStream(input, d2.Decompress, d2.ThrowIfIncomplete, errorOffset);
    }

    public override Stream OpenSevenZipCompress(SevenZipMethod method, Stream sink, SevenZipWriterOptions options, long knownLength, Action<byte[]> setProperties, int deferThreshold)
    {
        if (method == SevenZipMethod.Lzma2)
        {
            return new DeferredCompressorStream(length =>
            {
                var enc = new Lzma2Encoder(OptionsFor(options), knownLength >= 0 ? knownLength : length);
                setProperties([enc.DictionaryProperty]);
                return new EncodeStepStream(sink, enc.Compress, 0);
            }, deferThreshold);
        }

        return new DeferredCompressorStream(length =>
        {
            var enc = new LzmaEncoder(OptionsFor(options), knownLength >= 0 ? knownLength : length);
            setProperties(PropertyBytes(enc.Properties));
            return new EncodeStepStream(sink, enc.Compress, 13);
        }, deferThreshold);
    }

    public override byte[] SevenZipCompressHeader(byte[] data, out byte[] properties)
    {
        var enc = new LzmaEncoder(new LzmaOptions { Preset = 5, DictionarySize = Math.Max(LzmaProperties.MinimumDictionarySize, (uint)Math.Min(data.Length, 1 << 24)) }, data.Length);
        properties = PropertyBytes(enc.Properties);
        using var ms = new MemoryStream();
        using (var s = new EncodeStepStream(ms, enc.Compress, 13))
        {
            s.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] PropertyBytes(LzmaProperties p)
    {
        byte[] props = new byte[5];
        props[0] = p.PropertiesByte;
        BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(1), p.DictionarySize);
        return props;
    }

    private static LzmaOptions OptionsFor(SevenZipWriterOptions options)
    {
        if (options.Lzma is { } o)
        {
            return o;
        }

        int level = options.Level;
        uint dict = options.DictionarySize != 0 ? options.DictionarySize : DictionaryForLevel(level);
        // Levels 1 and 2 use the hash-chain preset 3: measured on text, the lower presets lose 6-18% against 7-Zip at -mx=1.
        return new LzmaOptions { Preset = Math.Max(level, 3), DictionarySize = Math.Max(dict, LzmaProperties.MinimumDictionarySize) };
    }

    // Dictionary sizes by level: levels 1, 3 and 5 follow 7-Zip 26.00's LZMA2 choices, measured by listing archives it wrote;
    // the levels between interpolate, and the top levels stop at 64 MiB to bound memory.
    private static uint DictionaryForLevel(int level) => level switch
    {
        1 => 1u << 18,
        2 => 1u << 20,
        3 => 1u << 22,
        4 => 1u << 24,
        5 or 6 => 1u << 25,
        _ => 1u << 26,
    };
}

internal sealed class FiltersArchiveCodec : ArchiveCodec
{
    // Methods.txt: the branch filters have a short ID (04..0B) and, for the older ones, a long 03 03 xx xx ID.
    private static BranchArchitecture? BranchOf(ulong id) => id switch
    {
        0x04 or 0x03030103 => BranchArchitecture.X86,
        0x05 or 0x03030205 => BranchArchitecture.PowerPc,
        0x06 or 0x03030401 => BranchArchitecture.Ia64,
        0x07 or 0x03030501 => BranchArchitecture.Arm,
        0x08 or 0x03030701 => BranchArchitecture.ArmThumb,
        0x09 or 0x03030805 => BranchArchitecture.Sparc,
        0x0A => BranchArchitecture.Arm64,
        0x0B => BranchArchitecture.RiscV,
        _ => null,
    };

    public override Stream OpenSevenZipDecoder(SevenZipCoder coder, Stream input, ArchiveLimits limits, SevenZipReaderOptions options, long unpackSize, long errorOffset)
    {
        ReadOnlySpan<byte> props = coder.Properties.Span;
        if (BranchOf(coder.MethodId) is { } arch)
        {
            // An optional 4-byte start offset (the address the first byte is assumed to have).
            if (props.Length != 0 && props.Length != 4)
            {
                throw SevenZipFolderDecoder.BadProps(errorOffset, coder, "no property or a 4-byte start offset");
            }

            uint start = props.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(props) : 0;
            try
            {
                return new SevenZipBranchStream(input, new BranchFilter(arch, start));
            }
            catch (ArgumentException)
            {
                throw SevenZipFolderDecoder.BadProps(errorOffset, coder, "a start offset the filter accepts");
            }
        }

        if (props.Length != 1)
        {
            throw SevenZipFolderDecoder.BadProps(errorOffset, coder, "one byte (the distance minus one)");
        }

        return new SevenZipDeltaStream(input, new DeltaFilter(props[0] + 1));
    }

    public override Stream OpenSevenZipFilter(Stream next, SevenZipFilter filter, int deltaDistance) =>
        filter == SevenZipFilter.Delta
            ? new FilterEncodeStream(next, null, new DeltaFilter(deltaDistance, encode: true))
            : new FilterEncodeStream(next, new BranchFilter(Architecture(filter), 0, encode: true), null);

    private static BranchArchitecture Architecture(SevenZipFilter f) => f switch
    {
        SevenZipFilter.X86 => BranchArchitecture.X86,
        SevenZipFilter.PowerPc => BranchArchitecture.PowerPc,
        SevenZipFilter.Ia64 => BranchArchitecture.Ia64,
        SevenZipFilter.Arm => BranchArchitecture.Arm,
        SevenZipFilter.ArmThumb => BranchArchitecture.ArmThumb,
        SevenZipFilter.Sparc => BranchArchitecture.Sparc,
        SevenZipFilter.Arm64 => BranchArchitecture.Arm64,
        _ => BranchArchitecture.RiscV,
    };
}

internal sealed class LzxArchiveCodec : ArchiveCodec
{
    public override CabBlockDecoder CreateCabDecoder(int windowBits) => new CabLzxDecoder(windowBits);
}

/// <summary>
/// MSZIP ([MS-CAB] 2.4 and the MSZIP description): each block is "CK" and a deflate stream whose matches may reach into the
/// previous 32 KiB of output.
/// </summary>
internal sealed class CabMsZipDecoder : CabBlockDecoder
{
    private const int HistorySize = 32768;
    private byte[]? _history;
    private int _historyLength;
    private DeflateDecoder? _inflate;
    private int _inflateHistory = -1;

    private static CompressionFormatException Bad(long offset, string expected, bool truncated = false) => new(Cab.CabReader.Name, offset, expected, truncated);

    public override void Decode(ReadOnlySpan<byte> data, Span<byte> output, long dataAt)
    {
        if (data.Length < 2 || data[0] != (byte)'C' || data[1] != (byte)'K')
        {
            throw Bad(dataAt, "the MSZIP block signature 'CK'");
        }

        _history ??= new byte[HistorySize];
        if (_inflate is null || _inflateHistory != _historyLength)
        {
            _inflate = new DeflateDecoder(DeflateFormat.Raw, new DeflateDecompressionOptions
            {
                Dictionary = _history.AsMemory(0, _historyLength),
                MaxOutputSize = Cab.CabReader.MaxBlockUncompressed,
            });
            _inflateHistory = _historyLength;
        }
        else
        {
            _inflate.Reset();
        }

        try
        {
            OperationStatus status = _inflate.Decompress(data[2..], output, out int consumed, out int written);
            if (status != OperationStatus.Done || written != output.Length)
            {
                string what = status == OperationStatus.DestinationTooSmall
                    ? $"a deflate block that ends after {output.Length} bytes"
                    : $"{output.Length} bytes from the deflate block (found {written})";
                throw Bad(dataAt + 2 + consumed, what, status == OperationStatus.NeedMoreData);
            }
        }
        catch (CompressionFormatException ex) when (ex.Format != Cab.CabReader.Name)
        {
            _inflate = null;
            throw Bad(dataAt + 2 + ex.Offset, "valid MSZIP data: " + ex.Expected, ex.IsTruncated);
        }

        // The next block's history is the last 32 KiB of output so far.
        if (output.Length >= HistorySize)
        {
            output[^HistorySize..].CopyTo(_history);
            _historyLength = HistorySize;
        }
        else
        {
            int keep = Math.Min(_historyLength, HistorySize - output.Length);
            _history.AsSpan(_historyLength - keep, keep).CopyTo(_history);
            output.CopyTo(_history.AsSpan(keep));
            _historyLength = keep + output.Length;
        }
    }
}

/// <summary>[MS-PATCH] 2 as cabinets use it: one 32768-byte frame per data block.</summary>
internal sealed class CabLzxDecoder(int windowBits) : CabBlockDecoder
{
    private LzxDecoder? _lzx;

    public override void Decode(ReadOnlySpan<byte> data, Span<byte> output, long dataAt)
    {
        _lzx ??= new LzxDecoder(windowBits);
        long before = _lzx.TotalConsumed;
        try
        {
            _lzx.DecodeFrame(data, output, out _);
        }
        catch (CompressionFormatException ex) when (ex.Format != Cab.CabReader.Name)
        {
            throw new CompressionFormatException(Cab.CabReader.Name, dataAt + Math.Max(0, ex.Offset - before), "valid LZX data: " + ex.Expected, ex.IsTruncated);
        }
    }
}
