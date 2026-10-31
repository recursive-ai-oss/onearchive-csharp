# Zip recovery

`ZipReaderOptions.Recovery` (default false) lets `ZipReader` get what it can out of a damaged archive. With it off, nothing
here applies and every malformation is a typed error. Sources: PKWARE APPNOTE 6.3.10, 4.3.7 (local file header), 4.3.9 (data
descriptor), 4.3.12 (central directory), 4.3.16 (end record), 4.4 (field meanings), 4.5.1 and 4.5.3 (extra fields, zip64).

## Reading

- **Seekable stream.** The central directory is read as usual. Recovery also reads the 4-byte signature at each entry's local header
  offset; if the directory is missing or unusable (no end record, bad offsets or sizes, any `CompressionFormatException` while
  loading it) or any entry's offset does not hold `PK\3\4`, the reader records a `DirectoryUnusable` damage record and reads
  the local headers from the start of the archive, as a forward-only stream is read. `SupportsRandomAccess` is then false.
- **Forward.** A local header is read where one is expected: at the start, after an entry, after a data descriptor. If the bytes
  there are not `PK\3\4`, or the header does not parse (zero-length name, unusable zip64 field, a field that runs past the end),
  the reader scans forward for the next candidate and resumes there. Central directory, zip64 end and end record signatures at an
  expected position end the archive, as they do without recovery. A header at an expected position is taken as it is if it parses.
- **Candidates found by scanning** must look like headers (APPNOTE 4.4): version needed at most 99; flag bits 7-10, 14 and 15
  clear; a known method (0-6, 8, 9, 10, 12, 14, 18, 19, 20, 93-99); the DOS date zero or with month 1-12 and day 1-31; a stored,
  unencrypted entry without a descriptor has equal sizes; name length 1-4096 with no byte below 0x20 or 0x7F, valid UTF-8 when
  bit 11 is set; the 30 fixed bytes, name and extra field no more than 64 KiB together; the extra field is a well-formed chain of
  records; a size of 0xFFFFFFFF comes with a zip64 record of at least 16 bytes. A candidate that fails is passed over one byte.
- **Damaged entries.** Reading an entry whose data does not decode, whose size or CRC-32 is wrong, or whose data ends early
  still raises `CompressionFormatException` from the entry stream; the reader also records it (`DamagedEntry`, or `Truncated`
  when the data ended early) with the entry's header offset, the distance to where the problem was seen, and the name.
  `GetNextEntry` then continues: for an entry with a known compressed size the input moves to where the data should end; if no
  header is there and the stream can seek, the reader goes back to the start of that entry's data and scans, so that a wrong
  size does not hide the entries behind it. Streams that cannot seek continue from the position reached. Entries skipped without
  being read are not checked; their data is only skipped over.
- **Truncation.** If the input ends where a header was expected, a zero-length `Truncated` record marks the place (and the
  bytes left over, if fewer than a header). The reader then reports the end of the archive without an exception.

## Reporting

`ZipReader.Damage` lists `ZipDamage(Kind, Offset, Length, EntryName, Reason)` in discovery order; `IsRecovered` is true when it is
not empty. `SkippedBytes` records give the archive offset and length of bytes passed over and are reported once per scan.
When a scan started from the data of the entry before it (the rewind above) and found its header earlier than that entry
claimed to end, the range starts at that data. A clean archive leaves the list empty. Archive offsets count from where the
reader started. `Archive.Open` and `Archive.Extract` pass the option through `ArchiveOpenOptions.Zip`.

## Limits

- All `ArchiveLimits` apply as before. The number of damage records counts against `MaxEntryCount`.
- `ZipReaderOptions.MaxRecoveryBytes` (default 1 GiB) bounds the bytes recovery passes over, plus the bytes it scans a second
  time after going back, plus the name and extra field bytes it examines in candidates that pass the fixed-field checks;
  exceeding it raises `CompressionLimitException`. The scan is one pass over the input buffer, a candidate is looked at only
  where a signature occurs, and the fixed fields reject nearly all of them at once, so work is linear in the input for any
  content, including data made of nothing but signatures, and bounded by the limit for content made to pass the checks. Going back happens at most once per entry and moves only forward in entry offsets.
- No attempt is made inside a compressed stream: the entry is damaged and the scan looks for the next header.

## Not done

Repairing or rewriting archives, recovering encrypted entries without the password, using the central directory to repair local
headers, and spanned archives.
