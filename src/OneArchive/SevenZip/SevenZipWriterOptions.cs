using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>The compression method a <see cref="SevenZipWriter"/> puts at the end of each folder's coder chain.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum SevenZipMethod
{
    /// <summary>LZMA2 (method ID 21), the default.</summary>
    Lzma2 = 0,

    /// <summary>LZMA (method ID 03 01 01).</summary>
    Lzma = 1,

    /// <summary>No compression (method ID 00).</summary>
    Copy = 2,

    /// <summary>Raw Deflate (method ID 04 01 08).</summary>
    Deflate = 3,

    /// <summary>Zstandard frames under the external-codec ID F7 11 01 listed in <c>Methods.txt</c>. Standard 7-Zip builds cannot read it.</summary>
    Zstd = 4,

    /// <summary>Raw Deflate64 (method ID 04 01 09), at <see cref="SevenZipWriterOptions.Level"/> as given (0-9).</summary>
    Deflate64 = 5,

    /// <summary>PPMd variant H (method ID 03 04 01) with the order and model memory of 7-Zip's level, or <see cref="SevenZipWriterOptions.Ppmd"/>.</summary>
    Ppmd = 6,
}

/// <summary>The filter placed in front of the compression method.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum SevenZipFilter
{
    /// <summary>Choose per folder from the first bytes of its first file: executables get the branch filter of their instruction set, everything else none. A change of choice starts a new folder.</summary>
    Auto = 0,

    /// <summary>No filter.</summary>
    None = 1,

    /// <summary>Delta with <see cref="SevenZipWriterOptions.DeltaDistance"/>.</summary>
    Delta = 2,

    /// <summary>x86 branch converter (BCJ).</summary>
    X86 = 3,

    /// <summary>PowerPC (big-endian) branch converter.</summary>
    PowerPc = 4,

    /// <summary>IA-64 branch converter.</summary>
    Ia64 = 5,

    /// <summary>ARM (little-endian) branch converter.</summary>
    Arm = 6,

    /// <summary>ARM Thumb branch converter.</summary>
    ArmThumb = 7,

    /// <summary>SPARC branch converter.</summary>
    Sparc = 8,

    /// <summary>ARM64 branch converter.</summary>
    Arm64 = 9,

    /// <summary>RISC-V branch converter.</summary>
    RiscV = 10,
}

/// <summary>
/// Options for <see cref="SevenZipWriter"/>. The bytes written depend only on these options and the entries.
/// Documented in <c>docs/7z-writer.md</c>.
/// </summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class SevenZipWriterOptions
{
    /// <summary>The default solid block size: 2 GiB of file content per folder.</summary>
    public const long DefaultSolidBlockSize = 2L << 30;

    /// <summary>Gets the method. Default <see cref="SevenZipMethod.Lzma2"/>. Level 0 always means <see cref="SevenZipMethod.Copy"/>.</summary>
    public SevenZipMethod Method { get; init; } = SevenZipMethod.Lzma2;

    /// <summary>
    /// Gets the PPMd parameters for <see cref="SevenZipMethod.Ppmd"/>. Default <see langword="null"/>: the order and memory of 7-Zip's level. As 7-Zip does, a folder of known size gets
    /// a smaller model than asked for (the smallest power of two that is at least 16 times its size, from 64 KiB). Documented in <c>docs/ppmd.md</c>.
    /// </summary>
    public RecursiveAI.OneCompress.Ppmd.PpmdHOptions? Ppmd { get; init; }

    /// <summary>Gets the compression level, 0 to 9, with the meaning of 7-Zip's <c>-mx</c>. Default 5.</summary>
    public int Level { get; init; } = 5;

    /// <summary>Gets the LZMA dictionary size in bytes; 0 takes the level's. Ignored by other methods and when <see cref="Lzma"/> is set. Default 0.</summary>
    public uint DictionarySize { get; init; }

    /// <summary>Gets full LZMA options that replace the ones derived from <see cref="Level"/> and <see cref="DictionarySize"/>. Default <see langword="null"/>.</summary>
    public LzmaOptions? Lzma { get; init; }

    /// <summary>Gets whether several files share a folder (one compression stream). Default <see langword="true"/>.</summary>
    public bool Solid { get; init; } = true;

    /// <summary>Gets the most file content in bytes a solid folder takes before the next file starts a new one. Default <see cref="DefaultSolidBlockSize"/>.</summary>
    public long SolidBlockSize { get; init; } = DefaultSolidBlockSize;

    /// <summary>Gets the most files a solid folder holds; 0 for no limit. Default 0.</summary>
    public int SolidFileCount { get; init; }

    /// <summary>Gets the filter. Default <see cref="SevenZipFilter.Auto"/>.</summary>
    public SevenZipFilter Filter { get; init; } = SevenZipFilter.Auto;

    /// <summary>Gets the distance in bytes, 1 to 256, of <see cref="SevenZipFilter.Delta"/>. Default 1.</summary>
    public int DeltaDistance { get; init; } = 1;

    /// <summary>Gets whether the header is compressed with LZMA. Default <see langword="true"/>.</summary>
    public bool EncodeHeader { get; init; } = true;

    /// <summary>
    /// Gets the password. When it is not empty, every folder is encrypted with AES-256 (7z method 06 F1 07 01); the key is
    /// derived from the password as UTF-16 with <see cref="KeyDerivationPower"/>. Default empty: no encryption.
    /// </summary>
    public ArchivePassword Password { get; init; }

    /// <summary>
    /// Gets whether the header (file names, sizes, times) is encrypted too when a <see cref="Password"/> is set. This forces
    /// the header to be stored encoded, whatever <see cref="EncodeHeader"/> says. Default <see langword="true"/>.
    /// </summary>
    public bool EncryptHeader { get; init; } = true;

    /// <summary>Gets the key-derivation cost as a power of two of SHA-256 rounds, 0 to 24. Default 19, what 7-Zip writes.</summary>
    public int KeyDerivationPower { get; init; } = 19;

    /// <summary>
    /// Gets a function that fills the span it is given with the 16-byte initialisation vector of one encrypted folder (or the
    /// header). Default <see langword="null"/>: cryptographically random bytes. This exists so tests and cross-platform
    /// comparisons can make encrypted output reproducible; a repeated IV weakens the encryption and must not be used for real archives.
    /// </summary>
    public Action<Span<byte>>? RandomBytes { get; init; }
}
