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
public static class ArchiveDetector
{
    /// <summary>The number of bytes needed to recognise every container (a tar header is one 512-byte block).</summary>
    public const int PrefixLength = 512;

    /// <summary>
    /// The number of bytes needed to recognise an ISO 9660 image as well: its first volume descriptor starts at byte 32768 (ECMA-119 clause 6.2.1).
    /// <see cref="DetectFormat"/> recognises an image only when it is given this many bytes.
    /// </summary>
    public const int ExtendedPrefixLength = 32768 + 6;

    /// <summary>Recognises a compression layer from its magic bytes.</summary>
    /// <param name="prefix">The first bytes of the data.</param>
    /// <returns>
    /// The compression layer, or <see cref="ArchiveCompression.None"/> if no magic matches. Brotli is never returned: RFC 7932 section
    /// 9.1 gives its streams no magic, so only <see cref="FromFileName"/> can select it.
    /// </returns>
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

        // lzip manual "File format": the member header starts with "LZIP".
        if (prefix.StartsWith("LZIP"u8))
        {
            return ArchiveCompression.Lzip;
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

        // [MS-CAB] 2.1: the cabinet signature 'MSCF'.
        if (prefix.StartsWith("MSCF"u8))
        {
            return ArchiveFormat.Cab;
        }

        // [MS-CFB] 2.2: the header signature D0 CF 11 E0 A1 B1 1A E1.
        if (prefix.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            return ArchiveFormat.CompoundFile;
        }

        // RAR 5.0 technote, "RAR 5.0 signature": 52 61 72 21 1A 07 01 00. The RAR 4 signature (52 61 72 21 1A 07 00) is recognised too,
        // so that the reader can say RAR 4 is not supported yet instead of failing to recognise the file.
        if (prefix.StartsWith(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }) || prefix.StartsWith(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }))
        {
            return ArchiveFormat.Rar;
        }

        // ar(5): "!<arch>\n"; a thin archive ("!<thin>\n") is recognised so that the reader can say it is unsupported.
        if (prefix.StartsWith("!<arch>\n"u8) || prefix.StartsWith("!<thin>\n"u8))
        {
            return ArchiveFormat.Ar;
        }

        // GNU cpio manual and POSIX cpio: the six-digit ASCII magic of the newc, crc and odc headers.
        if (prefix.StartsWith("070701"u8) || prefix.StartsWith("070702"u8) || prefix.StartsWith("070707"u8))
        {
            return ArchiveFormat.Cpio;
        }

        if (prefix.Length >= TarHeader.BlockSize && LooksLikeTar(prefix[..TarHeader.BlockSize]))
        {
            return ArchiveFormat.Tar;
        }

        // ECMA-119 clause 8.4: the first volume descriptor is at sector 16 and carries the standard identifier "CD001" in bytes 1 to 5.
        if (prefix.Length >= ExtendedPrefixLength && prefix.Slice(32769, 5).SequenceEqual("CD001"u8))
        {
            return ArchiveFormat.Iso;
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

        if (name.EndsWith(".cab", StringComparison.Ordinal))
        {
            return (ArchiveFormat.Cab, ArchiveCompression.None);
        }

        // Compound files carry many application extensions; the signature decides when there is one, so these only matter for a damaged start.
        if (name.EndsWith(".doc", StringComparison.Ordinal) || name.EndsWith(".xls", StringComparison.Ordinal) || name.EndsWith(".ppt", StringComparison.Ordinal)
            || name.EndsWith(".msg", StringComparison.Ordinal) || name.EndsWith(".msi", StringComparison.Ordinal) || name.EndsWith(".cfb", StringComparison.Ordinal))
        {
            return (ArchiveFormat.CompoundFile, ArchiveCompression.None);
        }

        if (name.EndsWith(".rar", StringComparison.Ordinal) || IsRarVolumeName(name))
        {
            return (ArchiveFormat.Rar, ArchiveCompression.None);
        }

        if (name.EndsWith(".iso", StringComparison.Ordinal))
        {
            return (ArchiveFormat.Iso, ArchiveCompression.None);
        }

        if (name.EndsWith(".zip", StringComparison.Ordinal))
        {
            return (ArchiveFormat.Zip, ArchiveCompression.None);
        }

        if (name.EndsWith(".a", StringComparison.Ordinal) || name.EndsWith(".ar", StringComparison.Ordinal) || name.EndsWith(".deb", StringComparison.Ordinal) || name.EndsWith(".udeb", StringComparison.Ordinal))
        {
            return (ArchiveFormat.Ar, ArchiveCompression.None);
        }

        foreach ((string suffix, ArchiveCompression c) in s_cpioSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (ArchiveFormat.Cpio, c);
            }
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

    // Old-style volume names: name.r00, name.r01, ... (name.s00 after r99).
    private static bool IsRarVolumeName(string name) =>
        name.Length > 4 && name[^4] == '.' && (name[^3] == 'r' || name[^3] == 's') && char.IsAsciiDigit(name[^2]) && char.IsAsciiDigit(name[^1]);

    private static readonly (string, ArchiveCompression)[] s_cpioSuffixes =
    [
        (".cpio", ArchiveCompression.None),
        (".cpio.zst", ArchiveCompression.Zstd), (".cpio.zstd", ArchiveCompression.Zstd),
        (".cpio.lz4", ArchiveCompression.Lz4),
        (".cpio.gz", ArchiveCompression.Gzip),
        (".cpio.bz2", ArchiveCompression.Bzip2),
        (".cpio.xz", ArchiveCompression.Xz),
        (".cpio.lz", ArchiveCompression.Lzip),
        (".cpio.br", ArchiveCompression.Brotli),
    ];

    private static readonly (string, ArchiveCompression)[] s_tarSuffixes =
    [
        (".tar", ArchiveCompression.None),
        (".tar.zst", ArchiveCompression.Zstd), (".tar.zstd", ArchiveCompression.Zstd), (".tzst", ArchiveCompression.Zstd),
        (".tar.lz4", ArchiveCompression.Lz4), (".tlz4", ArchiveCompression.Lz4),
        (".tar.gz", ArchiveCompression.Gzip), (".tgz", ArchiveCompression.Gzip),
        (".tar.bz2", ArchiveCompression.Bzip2), (".tbz2", ArchiveCompression.Bzip2), (".tbz", ArchiveCompression.Bzip2),
        (".tar.xz", ArchiveCompression.Xz), (".txz", ArchiveCompression.Xz),
        (".tar.lz", ArchiveCompression.Lzip), (".tlz", ArchiveCompression.Lzip),
        (".tar.br", ArchiveCompression.Brotli), (".tbr", ArchiveCompression.Brotli),
    ];

    private static readonly (string, ArchiveCompression)[] s_compressionSuffixes =
    [
        (".zst", ArchiveCompression.Zstd), (".lz4", ArchiveCompression.Lz4), (".gz", ArchiveCompression.Gzip),
        (".bz2", ArchiveCompression.Bzip2), (".xz", ArchiveCompression.Xz),
        (".lz", ArchiveCompression.Lzip), (".br", ArchiveCompression.Brotli),
    ];
}
