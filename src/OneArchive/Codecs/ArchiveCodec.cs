using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneArchive.SevenZip;

namespace RecursiveAI.OneArchive.Internal;

/// <summary>The codecs the archive formats can use. Each is one registry slot.</summary>
internal enum CodecId
{
    Zstd,
    Lz4,
    Deflate,
    Deflate64,
    BZip2,
    Lzma,
    Xz,
    Lzip,
    Brotli,
    Lzx,
    Filters,
}

/// <summary>A zip entry's decoder: the step function, and whether more frames may follow the first one's end.</summary>
internal sealed class ZipBlockDecoder(StepDecode decode, bool multiFrame = false)
{
    public StepDecode Decode => decode;

    /// <summary>True when the method allows several concatenated frames in one entry (zstd).</summary>
    public bool MultiFrame => multiFrame;
}

/// <summary>Decodes one cabinet data block ([MS-CAB] 2.4); history carries from block to block.</summary>
internal abstract class CabBlockDecoder
{
    /// <summary>Decodes <paramref name="data"/> into <paramref name="output"/>; <paramref name="dataAt"/> is the data's offset in the cabinet.</summary>
    public abstract void Decode(ReadOnlySpan<byte> data, Span<byte> output, long dataAt);
}

/// <summary>
/// What one codec offers the archive formats. A codec overrides only the roles its formats use; the others throw. Archive code
/// reaches codecs only through <see cref="CodecRegistry"/>, so a codec nobody registers is never referenced and trims away.
/// </summary>
internal abstract class ArchiveCodec
{
    /// <summary>Single-stream layer (.zst, .gz, tar.*): decompress.</summary>
    public virtual Stream OpenDecompress(Stream source, ArchiveLimits limits) => throw NoRole("decompressing a stream");

    /// <summary>Single-stream layer: compress.</summary>
    public virtual Stream OpenCompress(Stream sink, int? level, bool leaveOpen) => throw NoRole("compressing a stream");

    /// <summary>A gzip layer's stored name and modification time, once the decompressor has read the header.</summary>
    public virtual bool TryGetGZipHeader(Stream decompressor, out string? name, out uint modificationTime)
    {
        name = null;
        modificationTime = 0;
        return false;
    }

    /// <summary>A gzip writer whose header carries the entry's name and time (RFC 1952 2.3).</summary>
    public virtual Stream OpenGZipCompress(Stream sink, int? level, string? name, uint modificationTime, bool leaveOpen) => throw NoRole("writing gzip");

    /// <summary>A zip entry decoder. <paramref name="size"/> is the declared size (-1 if unknown).</summary>
    public virtual ZipBlockDecoder CreateZipDecoder(long size, long dataOffset, ArchiveLimits limits) => throw NoRole("reading zip entries");

    /// <summary>A zip entry encoder over a sink it leaves open.</summary>
    public virtual Stream OpenZipCompress(Stream sink, int? level, long size) => throw NoRole("writing zip entries");

    /// <summary>A 7z coder's decoder (<paramref name="coder"/> names the method and holds its properties).</summary>
    public virtual Stream OpenSevenZipDecoder(SevenZipCoder coder, Stream input, ArchiveLimits limits, SevenZipReaderOptions options, long unpackSize, long errorOffset) =>
        throw NoRole("reading 7z coders");

    /// <summary>A 7z compression coder over a sink it leaves open. <paramref name="setProperties"/> receives the coder's property bytes once known.</summary>
    public virtual Stream OpenSevenZipCompress(SevenZipMethod method, Stream sink, SevenZipWriterOptions options, long knownLength, Action<byte[]> setProperties, int deferThreshold) =>
        throw NoRole("writing 7z coders");

    /// <summary>Compresses a 7z header with LZMA; returns the data and the coder properties.</summary>
    public virtual byte[] SevenZipCompressHeader(byte[] data, out byte[] properties) => throw NoRole("compressing a 7z header");

    /// <summary>A 7z filter (branch converter or delta) in front of the compressor.</summary>
    public virtual Stream OpenSevenZipFilter(Stream next, SevenZipFilter filter, int deltaDistance) => throw NoRole("writing 7z filters");

    /// <summary>Cabinet decoder for MSZIP or LZX folders.</summary>
    public virtual CabBlockDecoder CreateCabDecoder(int windowBits) => throw NoRole("reading cabinets");

    private InvalidOperationException NoRole(string role) => new($"{GetType().Name} does not support {role}.");
}

/// <summary>
/// Holds the codecs an app has registered. With the all-codecs switch on (the default), any lookup that finds an empty slot
/// registers every codec first; with it off (a trimmed app), only what the app registered exists, and a missing codec is a
/// typed <see cref="ArchiveUnsupportedException"/>.
/// </summary>
internal static class CodecRegistry
{
    private static readonly ArchiveCodec?[] Slots = new ArchiveCodec?[Enum.GetValues<CodecId>().Length];

    /// <summary>The feature switch the trimmer folds to a constant when an app sets it to false in its project file.</summary>
    [FeatureSwitchDefinition("RecursiveAI.OneArchive.AllCodecs")]
    private static bool AllCodecsEnabled => !AppContext.TryGetSwitch("RecursiveAI.OneArchive.AllCodecs", out bool enabled) || enabled;

    public static void Register(CodecId id, ArchiveCodec codec) => Volatile.Write(ref Slots[(int)id], codec);

    /// <summary>Returns the codec, or throws the typed error naming <paramref name="format"/> and <paramref name="what"/>.</summary>
    public static ArchiveCodec Require(CodecId id, string format, string what)
    {
        ArchiveCodec? codec = Volatile.Read(ref Slots[(int)id]);
        if (codec is null && AllCodecsEnabled)
        {
            ArchiveCodecs.AddAll();
            codec = Volatile.Read(ref Slots[(int)id]);
        }

        return codec ?? throw new ArchiveUnsupportedException(format, $"{what} (this app did not include the {id} codec; call ArchiveCodecs.{AddMethod(id)}() at startup)");
    }

    private static string AddMethod(CodecId id) => id switch
    {
        CodecId.Zstd => nameof(ArchiveCodecs.AddZstd),
        CodecId.Lz4 => nameof(ArchiveCodecs.AddLz4),
        CodecId.Deflate => nameof(ArchiveCodecs.AddDeflate),
        CodecId.Deflate64 => nameof(ArchiveCodecs.AddDeflate64),
        CodecId.BZip2 => nameof(ArchiveCodecs.AddBZip2),
        CodecId.Lzma => nameof(ArchiveCodecs.AddLzma),
        CodecId.Xz => nameof(ArchiveCodecs.AddXz),
        CodecId.Lzip => nameof(ArchiveCodecs.AddLzip),
        CodecId.Brotli => nameof(ArchiveCodecs.AddBrotli),
        CodecId.Lzx => nameof(ArchiveCodecs.AddLzx),
        _ => nameof(ArchiveCodecs.AddFilters),
    };
}
