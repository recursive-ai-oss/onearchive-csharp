using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneArchive.Zip;

// Raw entry copy: the local header and data of an entry whose stored bytes are taken as they are (APPNOTE 4.3.7, 4.3.9),
// recorded in the central directory like any other entry (AddDirectoryRecord).
public sealed partial class ZipWriter
{
    // Android's zipalign pads local headers with this field to align the data; copying moves the data, so the padding is stale.
    private const ushort ExtraAlignment = 0xD935;

    /// <summary>
    /// Writes an entry whose data is already compressed (and possibly encrypted): the local header from <paramref name="header"/>, then
    /// <paramref name="rawData"/> unchanged, and records the entry for the central directory. Nothing is compressed, encrypted or checked
    /// against the CRC-32; <see cref="ZipWriterOptions.Method"/>, <see cref="ZipWriterOptions.Password"/> and <see cref="ZipWriterOptions.Level"/> do not apply.
    /// </summary>
    /// <param name="header">The entry's header fields. <see cref="ZipRawEntry.FromEntry(ZipEntry)"/> makes one from an entry that was read.</param>
    /// <param name="rawData">A stream holding exactly <see cref="ZipRawEntry.CompressedSize"/> bytes, for example from <see cref="ZipReader.OpenRawEntryStream(ZipEntry)"/>. It is read to its end, and one byte past the declared size is looked for.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The header is unusable (empty or too long name, NUL in the name, negative size, extra fields or comment too long), the entry needs zip64 and <see cref="ZipWriterOptions.Zip64"/> is <see cref="ZipZip64Mode.Never"/>, or <paramref name="rawData"/> does not hold exactly the declared number of bytes. After the last case the archive cannot be continued.</exception>
    /// <exception cref="InvalidOperationException">The archive was finished, or an earlier write failed.</exception>
    /// <remarks>
    /// Layout and rules are in <c>docs/zip.md</c> ("Raw entry copy"): zip64 fields are rewritten from the sizes and offset, bit 3 of the flags selects a
    /// data descriptor, and the header's other extra fields are kept.
    /// </remarks>
    public void WriteRawEntry(ZipRawEntry header, Stream rawData) => Internal.Sync.Result(WriteRawEntryAsync(header, rawData, false, default));

    /// <summary>Writes an entry whose data is already compressed (and possibly encrypted). See <see cref="WriteRawEntry(ZipRawEntry, Stream)"/>.</summary>
    /// <param name="header">The entry's header fields.</param>
    /// <param name="rawData">A stream holding exactly <see cref="ZipRawEntry.CompressedSize"/> bytes.</param>
    /// <param name="cancellationToken">A token that cancels the write; the archive cannot be continued after that.</param>
    /// <returns>A task that completes when the entry is written.</returns>
    public ValueTask WriteRawEntryAsync(ZipRawEntry header, Stream rawData, CancellationToken cancellationToken = default) =>
        WriteRawEntryAsync(header, rawData, true, cancellationToken);

    private async ValueTask WriteRawEntryAsync(ZipRawEntry header, Stream rawData, bool async, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rawData);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The archive was already finished.");
        }

        if (_failed)
        {
            throw new InvalidOperationException("An earlier write failed; the archive cannot be continued.");
        }

        ct.ThrowIfCancellationRequested();
        byte[] name = header.RawName.ToArray();
        if (name.Length == 0 || name.Length > 65535 || Array.IndexOf(name, (byte)0) >= 0)
        {
            throw new ArgumentException("An entry name must be 1 to 65535 bytes and contain no NUL.", nameof(header));
        }

        if (header.RawComment.Length > 65535)
        {
            throw new ArgumentException("An entry comment is longer than 65535 bytes.", nameof(header));
        }

        if (header.CompressedSize < 0 || header.UncompressedSize < 0)
        {
            throw new ArgumentException("The entry's sizes must not be negative.", nameof(header));
        }

        // The extra fields: the caller's, minus the zip64 field (rewritten below) and stale alignment padding.
        var extras = new MemoryStream();
        foreach (ZipExtraField f in header.ExtraFields)
        {
            if (f.Id is ZipFormat.ExtraZip64 or ExtraAlignment)
            {
                continue;
            }

            if (f.Data.Length > 65535 - 4)
            {
                throw new ArgumentException("An extra field is longer than 65531 bytes.", nameof(header));
            }

            extras.Write(Field(f.Id, f.Data.ToArray()));
        }

        byte[] user = extras.ToArray();
        long comp = header.CompressedSize, size = header.UncompressedSize;
        bool descriptor = (header.Flags & ZipFormat.FlagDescriptor) != 0;
        bool zip64 = _options.Zip64 == ZipZip64Mode.Always || comp >= ZipFormat.Max32 || size >= ZipFormat.Max32;
        if (zip64 && _options.Zip64 == ZipZip64Mode.Never)
        {
            throw new ArgumentException("The entry is too large for a zip without zip64 (ZipZip64Mode.Never).", nameof(header));
        }

        // As the ordinary writer does for a stored entry: a forward-only reader cannot find the end of stored data without the size.
        bool sizesInHeader = !descriptor || header.Method == ZipMethod.Store;
        int extraLength = (zip64 ? 20 : 0) + user.Length;
        if (extraLength > 65535)
        {
            throw new ArgumentException("The entry's extra fields are longer than 65535 bytes.", nameof(header));
        }

        ushort needed = header.VersionNeeded;
        if (descriptor)
        {
            needed = Math.Max(needed, (ushort)20);
        }

        if (zip64)
        {
            needed = Math.Max(needed, (ushort)45);
        }

        long headerOffset = _pos;
        byte[] local = new byte[ZipFormat.LocalHeaderSize + name.Length + extraLength];
        Span<byte> h = local;
        BinaryPrimitives.WriteUInt32LittleEndian(h, ZipFormat.LocalSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], needed);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], header.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], (ushort)header.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], header.DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], header.DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[14..], descriptor ? 0 : header.Crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(h[18..], zip64 ? ZipFormat.Max32 : (sizesInHeader ? (uint)comp : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(h[22..], zip64 ? ZipFormat.Max32 : (sizesInHeader ? (uint)size : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)extraLength);
        int at = ZipFormat.LocalHeaderSize;
        name.CopyTo(h[at..]);
        at += name.Length;
        if (zip64)
        {
            // APPNOTE 4.5.3: in a local header the field holds both sizes, uncompressed first.
            BinaryPrimitives.WriteUInt16LittleEndian(h[at..], ZipFormat.ExtraZip64);
            BinaryPrimitives.WriteUInt16LittleEndian(h[(at + 2)..], 16);
            if (sizesInHeader)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(h[(at + 4)..], (ulong)size);
                BinaryPrimitives.WriteUInt64LittleEndian(h[(at + 12)..], (ulong)comp);
            }

            at += 20;
        }

        user.CopyTo(h[at..]);

        var p = new Prepared
        {
            Name = name,
            Comment = header.RawComment.ToArray(),
            Flags = header.Flags,
            Size = size,
            ExternalAttributes = header.ExternalAttributes,
            InternalAttributes = header.InternalAttributes,
            VersionMadeBy = header.VersionMadeBy ?? VersionMadeBy,
            RawMethod = (ushort)header.Method,
            Zip64Local = zip64,
            Descriptor = descriptor,
        };

        try
        {
            await WriteBytesAsync(local, async, ct).ConfigureAwait(false);
            await CopyRawAsync(rawData, comp, async, ct).ConfigureAwait(false);
            if (descriptor)
            {
                // APPNOTE 4.3.9: signature, CRC-32, compressed size, size; 8-byte sizes with a zip64 field in the local header.
                byte[] d = new byte[zip64 ? 24 : 16];
                BinaryPrimitives.WriteUInt32LittleEndian(d, ZipFormat.DescriptorSignature);
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), header.Crc32);
                if (zip64)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(8), (ulong)comp);
                    BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(16), (ulong)size);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), (uint)comp);
                    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)size);
                }

                await WriteBytesAsync(d, async, ct).ConfigureAwait(false);
            }

            AddDirectoryRecord(nameof(header), p, header.Flags, needed, header.DosTime, header.DosDate, header.Crc32, comp, headerOffset, [], [], user);
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    // Copies exactly `count` bytes and checks that the stream has nothing more.
    private async ValueTask CopyRawAsync(Stream rawData, long count, bool async, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long left = count;
            while (left > 0)
            {
                int want = (int)Math.Min(left, buffer.Length);
                int n = async
                    ? await rawData.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false)
                    : rawData.Read(buffer, 0, want);
                if (n == 0)
                {
                    throw new ArgumentException($"The raw data ended after {count - left} bytes but the header declares a compressed size of {count}.", nameof(rawData));
                }

                await WriteBytesAsync(buffer.AsMemory(0, n), async, ct).ConfigureAwait(false);
                left -= n;
            }

            int extra = async
                ? await rawData.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false)
                : rawData.Read(buffer, 0, 1);
            if (extra != 0)
            {
                throw new ArgumentException($"The raw data is longer than the compressed size of {count} bytes the header declares.", nameof(rawData));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
