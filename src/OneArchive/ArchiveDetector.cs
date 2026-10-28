using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneArchive.Tar;

namespace RecursiveAI.OneArchive;

/// <summary>
/// Recognises archive formats and compression layers from the first bytes of the data, and from file names. Magic bytes
/// win over the name: a file called <c>a.tar.gz</c> that holds zstd data is read as zstd.
/// </summary>
/// <remarks>
/// Detecting a container inside a compressed file needs the decompressed bytes, so <see cref="Archive.Open(Stream, ArchiveOpenOptions?)"/>
/// decompresses the first block itself; this class only looks at the bytes it is given.
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class ArchiveDetector
{
    /// <summary>The number of bytes needed to recognise every container (a tar header is one 512-byte block).</summary>
    public const int PrefixLength = 512;

    /// <summary>Recognises a compression layer from its magic bytes.</summary>
    /// <param name="prefix">The first bytes of the data.</param>
    /// <returns>The compression layer, or <see cref="ArchiveCompression.None"/> if no magic matches.</returns>
    public static ArchiveCompression DetectCompression(ReadOnlySpan<byte> prefix)
    {
        if (prefix.StartsWith(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }))
        {
            return ArchiveCompression.Zstd;
        }

        if (prefix.StartsWith(new byte[] { 0x04, 0x22, 0x4D, 0x18 }))
        {
            return ArchiveCompression.Lz4;
        }

        if (prefix.StartsWith(new byte[] { 0x1F, 0x8B }))
        {
            return ArchiveCompression.Gzip;
        }

        if (prefix.StartsWith("BZh"u8))
        {
            return ArchiveCompression.Bzip2;
        }

        if (prefix.StartsWith(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }))
        {
            return ArchiveCompression.Xz;
        }

        return ArchiveCompression.None;
    }

    /// <summary>Recognises a container format from the first bytes of the (decompressed) data.</summary>
    /// <param name="prefix">The first bytes; up to <see cref="PrefixLength"/> are used.</param>
    /// <returns>The format, or <see cref="ArchiveFormat.Unknown"/>.</returns>
    public static ArchiveFormat DetectFormat(ReadOnlySpan<byte> prefix)
    {
        // PKWARE APPNOTE 4.3.7 (local header), 4.3.16 (an empty archive is just an end record), 8.5.3 (span marker).
        if (prefix.StartsWith("PK\x03\x04"u8) || prefix.StartsWith("PK\x05\x06"u8) || prefix.StartsWith("PK\x07\x08"u8) || prefix.StartsWith("PK00"u8))
        {
            return ArchiveFormat.Zip;
        }

        // 7zFormat.txt "SignatureHeader": '7', 'z', BC AF 27 1C.
        if (prefix.StartsWith(new byte[] { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C }))
        {
            return ArchiveFormat.SevenZip;
        }

        if (prefix.Length >= TarHeader.BlockSize && LooksLikeTar(prefix[..TarHeader.BlockSize]))
        {
            return ArchiveFormat.Tar;
        }

        return ArchiveFormat.Unknown;
    }

    private static bool LooksLikeTar(ReadOnlySpan<byte> block)
    {
        // POSIX and GNU headers carry "ustar" at offset 257; that alone is enough, and a bad checksum is then reported
        // precisely by the reader.
        if (block.Slice(TarHeader.MagicAt, 5).SequenceEqual("ustar"u8))
        {
            return true;
        }

        // Version 7 headers have no magic: accept a valid checksum over a non-empty name and numeric fields.
        if (block[0] == 0 || TarHeader.IsZero(block))
        {
            return false;
        }

        try
        {
            long stored = TarHeader.ParseNumber(block.Slice(TarHeader.ChecksumAt, TarHeader.ChecksumLen), 0, "checksum");
            if (stored != TarHeader.ComputeChecksum(block, false) && stored != (int)TarHeader.ComputeChecksum(block, true))
            {
                return false;
            }

            TarHeader.ParseNumber(block.Slice(TarHeader.ModeAt, TarHeader.ModeLen), 0, "mode");
            TarHeader.ParseNumber(block.Slice(TarHeader.SizeAt, TarHeader.SizeLen), 0, "size");
            return true;
        }
        catch (RecursiveAI.OneCompress.CompressionFormatException)
        {
            return false;
        }
    }

    /// <summary>Guesses the container and compression from a file name's extension.</summary>
    /// <param name="fileName">A file name or path.</param>
    /// <returns>
    /// The container (<see cref="ArchiveFormat.Unknown"/> when the extension names only a compression layer such as
    /// <c>.zst</c>, or nothing) and the compression layer.
    /// </returns>
    public static (ArchiveFormat Format, ArchiveCompression Compression) FromFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        string name = Path.GetFileName(fileName).ToLowerInvariant();
        if (name.EndsWith(".7z", StringComparison.Ordinal))
        {
            return (ArchiveFormat.SevenZip, ArchiveCompression.None);
        }

        if (name.EndsWith(".zip", StringComparison.Ordinal))
        {
            return (ArchiveFormat.Zip, ArchiveCompression.None);
        }

        foreach ((string suffix, ArchiveCompression c) in s_tarSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (ArchiveFormat.Tar, c);
            }
        }

        foreach ((string suffix, ArchiveCompression c) in s_compressionSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (ArchiveFormat.Unknown, c);
            }
        }

        return (ArchiveFormat.Unknown, ArchiveCompression.None);
    }

    private static readonly (string, ArchiveCompression)[] s_tarSuffixes =
    [
        (".tar", ArchiveCompression.None),
        (".tar.zst", ArchiveCompression.Zstd), (".tar.zstd", ArchiveCompression.Zstd), (".tzst", ArchiveCompression.Zstd),
        (".tar.lz4", ArchiveCompression.Lz4), (".tlz4", ArchiveCompression.Lz4),
        (".tar.gz", ArchiveCompression.Gzip), (".tgz", ArchiveCompression.Gzip),
        (".tar.bz2", ArchiveCompression.Bzip2), (".tbz2", ArchiveCompression.Bzip2), (".tbz", ArchiveCompression.Bzip2),
        (".tar.xz", ArchiveCompression.Xz), (".txz", ArchiveCompression.Xz),
    ];

    private static readonly (string, ArchiveCompression)[] s_compressionSuffixes =
    [
        (".zst", ArchiveCompression.Zstd), (".lz4", ArchiveCompression.Lz4), (".gz", ArchiveCompression.Gzip),
        (".bz2", ArchiveCompression.Bzip2), (".xz", ArchiveCompression.Xz),
    ];
}
