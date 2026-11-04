namespace RecursiveAI.OneArchive.Zip;

/// <summary>
/// The header of an entry whose stored bytes are copied into an archive as they are (<see cref="ZipWriter.WriteRawEntry(ZipRawEntry, Stream)"/>):
/// every field the local header and the central directory record carry, set by the caller or taken from a
/// <see cref="ZipEntry"/> with <see cref="FromEntry(ZipEntry)"/>. The writer checks the fields it needs to lay the entry
/// out; it does not look inside the data, so the method, flags, CRC-32 and sizes are the caller's word for what the bytes are.
/// </summary>
/// <remarks>
/// Documented in <c>docs/zip.md</c> ("Raw entry copy").
/// </remarks>
public sealed class ZipRawEntry
{
    private static readonly IReadOnlyList<ZipExtraField> s_noFields = [];

    /// <summary>Gets the name bytes exactly as they are to be written (APPNOTE 4.4.17). Required: 1 to 65535 bytes with no NUL. Whether they are UTF-8 is stated by bit 11 of <see cref="Flags"/>.</summary>
    public ReadOnlyMemory<byte> RawName { get; init; }

    /// <summary>Gets the method field of the headers (APPNOTE 4.4.5): for a WinZip AES entry <see cref="ZipMethod.Aes"/> (99), with the real method in the AES extra field. Default <see cref="ZipMethod.Store"/>.</summary>
    public ZipMethod Method { get; init; }

    /// <summary>
    /// Gets the general purpose bit flag (APPNOTE 4.4.4), written as given. Bit 3 (data descriptor) makes the writer leave the CRC-32 and sizes out of the
    /// local header and write a data descriptor after the data; the central directory has the real values either way. Keep bit 3 for traditional-encryption
    /// entries that have it: their check byte depends on it. Default 0.
    /// </summary>
    public ushort Flags { get; init; }

    /// <summary>Gets the CRC-32 of the uncompressed data (0 for WinZip AE-2 entries, which carry none). Default 0.</summary>
    public uint Crc32 { get; init; }

    /// <summary>Gets the number of bytes that follow the header: the compressed data plus any encryption header and authentication code. The stream passed to the writer must hold exactly this many.</summary>
    public long CompressedSize { get; init; }

    /// <summary>Gets the uncompressed size.</summary>
    public long UncompressedSize { get; init; }

    /// <summary>Gets the DOS time (APPNOTE 4.4.6). Default 0.</summary>
    public ushort DosTime { get; init; }

    /// <summary>Gets the DOS date (APPNOTE 4.4.6). Default 0x0021, 1980-01-01.</summary>
    public ushort DosDate { get; init; } = 0x0021;

    /// <summary>
    /// Gets the extra field records written to the local header and to the central directory record, in this order. A zip64 field (id 1) is not written as given: the
    /// writer makes its own where the values need one. The Android alignment padding (id 0xD935) is dropped, because offsets change when an entry is copied. Everything else is kept, including the
    /// AES field of an encrypted entry. Default none.
    /// </summary>
    public IReadOnlyList<ZipExtraField> ExtraFields { get; init; } = s_noFields;

    /// <summary>Gets the entry comment bytes for the central directory (at most 65535). Default none.</summary>
    public ReadOnlyMemory<byte> RawComment { get; init; }

    /// <summary>Gets the "version made by" field. Default 0: the writer uses its own (Unix host, specification 6.3).</summary>
    public ushort VersionMadeBy { get; init; }

    /// <summary>Gets the "version needed to extract" field. The writer raises it to 20 for a data descriptor and to 45 for zip64. Default 0.</summary>
    public ushort VersionNeeded { get; init; }

    /// <summary>Gets the internal file attributes. Default 0.</summary>
    public ushort InternalAttributes { get; init; }

    /// <summary>Gets the external file attributes (the Unix mode is in the high 16 bits for a Unix host). Default 0.</summary>
    public uint ExternalAttributes { get; init; }

    /// <summary>Makes the header that copies <paramref name="entry"/> unchanged: its name, comment and extra fields as stored, method, flags, CRC-32, sizes, DOS time and attributes.</summary>
    /// <param name="entry">An entry from a <see cref="ZipReader"/>.</param>
    /// <returns>A header for <see cref="ZipWriter.WriteRawEntry(ZipRawEntry, Stream)"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The entry's sizes and CRC-32 are not known (<see cref="ZipEntry.IsSizeKnown"/> is false: a forward-only read of an entry with a data descriptor, which <see cref="ZipReader.OpenRawEntryStream(ZipEntry)"/> refuses too).</exception>
    /// <remarks>
    /// The extra fields are the ones the reader saw: the central directory's when it read the directory, the local header's when it read forward-only. A comment is only
    /// known from the central directory.
    /// </remarks>
    public static ZipRawEntry FromEntry(ZipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsSizeKnown)
        {
            throw new ArgumentException("The entry's sizes and CRC-32 follow its data in a data descriptor and are not known; read the archive from a stream that can seek.", nameof(entry));
        }

        return new ZipRawEntry
        {
            RawName = entry.RawName,
            Method = entry.Method,
            Flags = entry.Flags,
            Crc32 = entry.Crc32,
            CompressedSize = entry.CompressedSize,
            UncompressedSize = entry.UncompressedSize,
            DosTime = entry.DosTime,
            DosDate = entry.DosDate,
            ExtraFields = entry.ExtraFields,
            RawComment = entry.RawComment,
            VersionMadeBy = entry.VersionMadeBy,
            VersionNeeded = entry.VersionNeeded,
            InternalAttributes = entry.InternalAttributes,
            ExternalAttributes = entry.ExternalAttributes,
        };
    }
}
