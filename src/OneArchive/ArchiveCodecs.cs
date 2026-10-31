using RecursiveAI.OneArchive.Internal;

namespace RecursiveAI.OneArchive;

/// <summary>
/// Chooses which codecs <see cref="Archive"/> and the per-format readers and writers can use.
/// </summary>
/// <remarks>
/// <para>By default nothing needs to be done: the first time an archive needs a codec, every codec is registered, so every
/// format and method works with no configuration. That default keeps every codec in the app.</para>
/// <para>An app that wants a small NativeAOT or trimmed binary turns the default off and registers only the codecs it uses.
/// In the project file:</para>
/// <code>
/// &lt;ItemGroup&gt;
///   &lt;RuntimeHostConfigurationOption Include="RecursiveAI.OneArchive.AllCodecs" Value="false" Trim="true" /&gt;
/// &lt;/ItemGroup&gt;
/// </code>
/// <para>and at startup calls, for example, <c>ArchiveCodecs.AddZstd()</c>. With the switch off, an archive that needs a codec
/// the app did not register fails with an <see cref="ArchiveUnsupportedException"/> that names the format, the method and the
/// <c>Add</c> method to call. Without the trimmer the switch can also be set at run time with
/// <c>AppContext.SetSwitch("RecursiveAI.OneArchive.AllCodecs", false)</c> before the first archive is touched.</para>
/// <para>The methods are idempotent and thread-safe. Registering a codec registers all it needs, but not the codecs a format
/// reaches beside it: a <c>.tar.zst</c> needs <see cref="AddZstd"/> only; zip entries need <see cref="AddDeflate"/> for the
/// default method; a 7z archive with a compressed header needs <see cref="AddLzma"/>; cabinets need <see cref="AddDeflate"/>
/// (MSZIP) or <see cref="AddLzx"/>.</para>
/// </remarks>
public static class ArchiveCodecs
{
    /// <summary>Registers Zstandard: <c>.zst</c> and <c>.tar.zst</c>, zip method 93, 7z Zstandard.</summary>
    public static void AddZstd() => CodecRegistry.Register(CodecId.Zstd, new ZstdArchiveCodec());

    /// <summary>Registers LZ4 frames: <c>.lz4</c> and <c>.tar.lz4</c>.</summary>
    public static void AddLz4() => CodecRegistry.Register(CodecId.Lz4, new Lz4ArchiveCodec());

    /// <summary>Registers deflate: gzip (<c>.gz</c>, <c>.tar.gz</c>), zip method 8, 7z Deflate and cabinet MSZIP.</summary>
    public static void AddDeflate() => CodecRegistry.Register(CodecId.Deflate, new DeflateArchiveCodec());

    /// <summary>Registers Deflate64: zip method 9 and 7z Deflate64.</summary>
    public static void AddDeflate64() => CodecRegistry.Register(CodecId.Deflate64, new Deflate64ArchiveCodec());

    /// <summary>Registers bzip2: <c>.bz2</c>, <c>.tar.bz2</c>, zip method 12 and 7z BZip2.</summary>
    public static void AddBZip2() => CodecRegistry.Register(CodecId.BZip2, new BZip2ArchiveCodec());

    /// <summary>Registers LZMA and LZMA2: zip method 14, 7z LZMA and LZMA2, and 7z header compression.</summary>
    public static void AddLzma() => CodecRegistry.Register(CodecId.Lzma, new LzmaArchiveCodec());

    /// <summary>Registers xz: <c>.xz</c>, <c>.tar.xz</c> and zip method 95.</summary>
    public static void AddXz() => CodecRegistry.Register(CodecId.Xz, new XzArchiveCodec());

    /// <summary>Registers lzip: <c>.lz</c> and <c>.tar.lz</c>.</summary>
    public static void AddLzip() => CodecRegistry.Register(CodecId.Lzip, new LzipArchiveCodec());

    /// <summary>Registers brotli, including its built-in dictionary: <c>.br</c> and <c>.tar.br</c>.</summary>
    public static void AddBrotli() => CodecRegistry.Register(CodecId.Brotli, new BrotliArchiveCodec());

    /// <summary>Registers LZX for cabinets.</summary>
    public static void AddLzx() => CodecRegistry.Register(CodecId.Lzx, new LzxArchiveCodec());

    /// <summary>Registers the 7z branch converters (x86, ARM, ...) and the delta filter.</summary>
    public static void AddFilters() => CodecRegistry.Register(CodecId.Filters, new FiltersArchiveCodec());

    /// <summary>Registers PPMd: zip method 98 (variant I) and the 7z PPMD coder (variant H).</summary>
    public static void AddPpmd() => CodecRegistry.Register(CodecId.Ppmd, new PpmdArchiveCodec());

    /// <summary>Registers every codec. This is what the default does on first use.</summary>
    public static void AddAll()
    {
        AddZstd();
        AddLz4();
        AddDeflate();
        AddDeflate64();
        AddBZip2();
        AddLzma();
        AddXz();
        AddLzip();
        AddBrotli();
        AddLzx();
        AddFilters();
        AddPpmd();
    }
}
