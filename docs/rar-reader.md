# RAR 5 reader

Normative notes for `RarReader` in `RecursiveAI.OneArchive.Rar`: read only. Source: RARLAB's "RAR 5.0 archive format" technote (section names are
cited in the code). The technote sends readers to the vendor's source for algorithms; that is never followed. Gaps were closed by running
`rar` 7.23 as a black box (`tools/rar-testdata/generate.sh` makes the committed archives); findings are marked **[tool]**.

## What is read

Signature (`52 61 72 21 1A 07 01 00`, searched for within `MaxSfxSize`, default 1 MiB, to skip a self-extracting module), then headers in file
order: optional archive encryption header, main archive header, an optional `CMT` comment service header, then file headers, their service headers,
and an end-of-archive header. The reader never uses the locator, the quick-open data or the recovery record: it walks the headers in order, so the
names listed are the names extracted (the technote's security note). The source need not seek.

- **Header block**: CRC-32 (u32), header size (vint, at most 3 bytes, so at most 2 MiB; at least 2), then that many bytes: type, flags, optional
  extra-area size, optional data size, type fields, extra area. The CRC covers the size field and the body and is always checked. An extra area
  larger than the header, a record that overruns the area, a record field that overruns its record, a vint over 64 bits or over 10 bytes, a data size
  of 2^63 or more: `CompressionFormatException` naming the offset (counted from the first byte of the file, SFX module included).
  Unknown header types, and service headers other than the first `CMT`, are skipped with their data.
- **vint**: seven bits per byte, low group first. Padding bytes of `0x80` are accepted. **[tool]** `rar` pre-allocates several fields and pads them
  (a compression-information field of `80 00`, a quick-open offset of several bytes).
- **Main header**: archive flags (`0x1` volume, `0x2` volume number present, `0x4` solid, `0x8` recovery record, `0x10` locked), volume number, and the
  locator (quick-open and recovery offsets, 0 meaning "not filled in", read as absent) and metadata (original name cut at the first zero byte; a zero first
  byte means no name; creation time in FILETIME, Unix seconds or Unix nanoseconds) records. **[tool]** `-m0 -s` does not set the solid flag: `rar` ignores
  `-s` for stored data. With compression, solid archives list entries in a different order than given, and may swap which of two hard-linked names holds the
  data.
- **File header**: flags (`0x1` directory, `0x2` Unix mtime, `0x4` CRC-32, `0x8` unpacked size unknown), unpacked size, attributes, optional mtime (u32), optional
  CRC-32, compression information, host OS, name (UTF-8, no zero). Compression information: bits 0-5 algorithm version, bit 6 solid, bits 7-9 method (0 stored,
  1-5), bits 10-14 `N`, bits 15-19 and bit 20 only for version 1. Dictionary size is `128 KiB << N`; for version 1 plus `(that / 32) * bits 15-19`.
- **Extra records** (file and service headers): encryption (presence only), hash (type 0 = BLAKE2sp, 32 bytes), file time, file version, redirection, Unix owner,
  service data (ignored). **[tool]** The file time record holds a u32 for each time present when the Unix flag is set (not u64), then one u32 of nanoseconds per time
  when the nanosecond flag is also set; without the Unix flag each time is a u64 FILETIME. Times are kept to 100 ns; a value outside the representable range reads as absent.
- **End header**: flag `0x1` means the set continues in another volume.

## Entry model

| RAR | `RarEntry` |
|---|---|
| directory flag | `Kind = Directory`, size 0 |
| redirection 1 (Unix symlink), 2 (Windows symlink), 3 (junction) | `Symlink`, `LinkTarget`; for 2 and 3 `\` in the target becomes `/` |
| redirection 4 (hard link), 5 (file copy) | `HardLink`, `LinkTarget`. A file copy is stored without data and names the file it copies; extracting it as a hard link gives the same content. Size is 0 for every link: **[tool]** the header carries the target's size, which is not content of this entry. |
| host OS 1 (Unix) | `Mode` = low 12 bits of the attribute word (`st_mode`), `Attributes` = the whole word |
| host OS 0 (Windows) | `Mode` null, `Attributes` = DOS bits |
| header mtime, then the file time record | `ModifiedTime` (the record wins); atime in `AccessTime`; creation time in `CreationTime`; `ChangeTime` is not set |
| Unix owner record | `UserName`, `GroupName`, `Uid`, `Gid` |
| unknown-size flag | `SizeIsUnknown`; `Size` is the first part's data size |

Names are exactly as stored: `\` is data in a Unix-host name and invalid in a Windows-host name, but the extraction rules treat both as separators, so neither can
escape. Names with bytes that are not valid UTF-8 read with replacement characters.

## Stored entries and checks

Method 0 data is the data area. A stored entry that does not continue must have a data area equal to its unpacked size (unless the size is flagged unknown or the data
is encrypted, which pads to 16 bytes). CRC-32 and BLAKE2sp are checked when the stream is read to its end (`VerifyChecksum`, default true); a mismatch is
`CompressionFormatException` naming the stored and computed values. BLAKE2sp is in `Blake2sp.cs`: BLAKE2s from RFC 7693 and the eight-leaf tree of the BLAKE2 paper (fanout 8,
depth 2, inner length 32; leaf `i` hashes the 64-byte blocks `i`, `i + 8`, ...; the last leaf and the root carry the last-node flag). It is internal because RAR is its only
user; it is tested against RFC 7693's BLAKE2s vectors, digests computed with Python's `hashlib` in tree mode, and the hashes `rar` writes.

Compressed entries (methods 1-5) are listed in full and raise `RarUnsupportedException` (a `ArchiveUnsupportedException`) from `OpenEntryStream`:
"RAR compressed entries are not supported yet", naming the entry, method and dictionary size. Nothing is written for them. Encrypted data raises the same type with kind `EncryptedEntry`.
A RAR 4 signature (`52 61 72 21 1A 07 00`) is detected as RAR and raises kind `Rar4Archive` from `Archive.Open` and the reader. An archive encryption header (`rar -hp`) raises kind
`EncryptedHeaders`; reading it would need AES-256 with PBKDF2 and a password check the technote leaves to the vendor's source, so it is not implemented.

## Volumes

The reader is given the first volume and `RarReaderOptions.Volumes`, a function from volume number to a stream (or null). At an end-of-archive header with flag `0x1`, or when a file's data
continues (header flag `0x10`), it opens the next volume, checks its signature and main header (volume flag set, volume number equal to the expected one) and takes over the continuation file
header (flag `0x8`, same name). A file split over volumes reads as one stream. **[tool]** `rar -v40k` names volumes `name.part1.rar`, `name.part2.rar`, ...; `-vn` did not change this.

The CRC-32 or BLAKE2sp in a file header covers that volume's data for every part but the last, and the whole file in the last part's header; the reader checks each part and the whole.
A missing volume is `CompressionFormatException` with `IsTruncated`, raised where the volume is needed (when its data is read or skipped, or when listing reaches it).
`RarVolumes.ForFile` finds volumes on disk: `name.partN.rar` (digit width kept) or `name.rar`, `name.r00` ... `name.r99`, `name.s00` ...; `Archive.Open(path)` uses it. Opening a
volume other than the first is `ArchiveUnsupportedException`.

## Limits

Shared `ArchiveLimits`: entry count, name and link-target length, path depth, total declared size (checked from the header, before data is read; an unpacked size of 2^62 on a
compressed entry is a limit error, 2^63 or more a format error). Header size is bounded by the format (2 MiB); no allocation depends on a data size.
