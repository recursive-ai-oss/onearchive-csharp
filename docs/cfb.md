# Compound file reader

Normative notes for `CompoundFileReader` in `RecursiveAI.OneArchive.Cfb`: read only. Source: [MS-CFB] Compound File Binary File Format,
revision 13.0, sections 2.1 to 2.9 (cited as "MS-CFB 2.x"). The format is the container of legacy Office documents (.doc, .xls, .ppt),
Outlook messages (.msg) and installer packages (.msi). Choices the specification leaves open are marked **[choice]**.

## Structures read

All integers are little-endian.

- **Header** (MS-CFB 2.2, 512 bytes at offset 0; in a version 4 file the rest of the first 4096-byte sector is skipped): signature
  `D0 CF 11 E0 A1 B1 1A E1`, major version 3 or 4, byte order mark `0xFFFE`, sector shift 9 (version 3) or 12 (version 4), mini sector shift 6,
  mini stream cutoff 4096, and the counts and first sectors of the directory, FAT, mini FAT and DIFAT. The header CLSID, minor version,
  reserved fields, directory sector count and transaction signature are exposed (`CompoundFileHeader`) and not checked.
- **Sectors** (MS-CFB 2.1): sector `n` starts at byte `(n + 1) * sector size`. A sector must lie wholly inside the file; the file's
  sector count is `floor(length / sector size) - 1`. Numbers above `0xFFFFFFFA` are never sectors.
- **DIFAT** (2.5): the first 109 FAT sector numbers are in the header, the rest in a chain of DIFAT sectors (each holds `sector size / 4 - 1`
  numbers and a link to the next). The chain has exactly the declared number of sectors and its last link is `ENDOFCHAIN`.
- **FAT** (2.3): the `FAT sector count` sectors named by the DIFAT, concatenated. A chain follows `FAT[s]` until `ENDOFCHAIN`.
- **Mini FAT and mini stream** (2.4): the mini FAT is a chain of exactly the declared number of sectors; the mini stream is the root
  entry's stream. A stream smaller than 4096 bytes lives in the mini stream in 64-byte mini sectors (a size of exactly 4096 is a normal stream).
- **Directory** (2.6): a chain of 128-byte entries. Entry 0 is the root storage and must have object type 5; no other entry may.
  Fields read: name (UTF-16, at most 31 characters and a terminating null), name length, object type (0 unallocated, 1 storage, 2 stream,
  5 root), color flag, left and right sibling IDs, child ID, CLSID, state bits, creation and modified FILETIME, starting sector and
  size. In version 3 files only the low 32 bits of the size count (2.6.1: older writers left the high bits uninitialized); in version 4 the
  size is 64 bits and must be below 2^63. Times that are zero, or beyond the range of `DateTimeOffset`, give no `ModifiedTime`/`CreationTime`;
  the raw FILETIME values are always exposed.

## Validation

The whole structure is checked the first time the reader is used, before any stream is read. Every failure is a
`CompressionFormatException` (format `cfb`) giving the byte offset of the offending field and what was expected, or
`CompressionLimitException` for a limit.

- Header fields as listed above; a cutoff other than 4096, a sector shift that does not match the version, a mini shift other than 6.
- The FAT sector count and DIFAT sector count may not exceed the number of sectors in the file; every sector number must be inside the file.
- **Each sector belongs to at most one chain** (MS-CFB 2.1): DIFAT sectors, FAT sectors, the directory, the mini FAT, the mini stream and every
  stream's chain claim their sectors in a bitmap, and claiming one twice is an error. This also catches loops in any chain. Mini sectors are
  claimed the same way, so streams cannot share mini sectors either. The total size declared by streams therefore never exceeds the file.
- A chain may not enter a free sector (`0xFFFFFFFF`), a FAT or DIFAT sector marker, or a sector outside the FAT's range.
- A stream's chain must be at least `ceil(size / sector size)` sectors long (mini: 64-byte units, and each mini sector must lie inside the
  mini stream). A longer chain is accepted **[choice]**; the extra sectors are claimed and not read. Zero-size streams need no chain.
- Directory: a type other than 0, 1, 2, 5; a color other than 0 or 1; an odd name length, one below 2 or above 64; a name whose terminating
  null is missing or that contains a null before it; an empty name on a non-root entry; a size of 2^63 or more.
- Tree: the tree under each storage is walked with explicit stacks (no recursion follows the input). Every sibling or child ID must be below the
  directory length or `NOSTREAM`, must name a storage or stream, and may be reached once (a loop or a shared subtree is an error). A stream's
  child ID must be `NOSTREAM`. Each storage's children, taken in tree order, must be strictly increasing under the comparison of MS-CFB 2.6.4:
  a shorter name is less; names of equal length are compared one UTF-16 code unit at a time after `char.ToUpperInvariant` (surrogates are
  not changed). Equal names are "unique name" errors, other disorder "order" errors. Colors are exposed, not checked **[choice]**
  (the red-black balance does not matter to a reader).
- Entries that are allocated but not reachable from the root are ignored.

## Limits

`CompoundFileReaderOptions.Limits` (`ArchiveLimits`) and `MaxDirectoryEntries`:

- `MaxDirectoryEntries` (default 262,144, **[choice]**): directory entries of all kinds, checked against the directory chain's length before
  anything is read.
- `MaxEntryCount` and `MaxPathDepth` are checked during the tree walk, so a hostile file fails before names are built; `MaxPathLength` and
  `MaxTotalSize` are checked per entry as it is returned. Depth counts storages from the root (a stream directly under the root has depth 1).
- There is no expansion ratio: nothing is decompressed, and the exclusive-sector rule bounds the output by the file's size.
- Memory is proportional to the file: the FAT and mini FAT are held as arrays (4 bytes per sector), the directory as a struct array.

## Listing and names

- `GetNextEntry` returns storages as `ArchiveEntryKind.Directory` and streams as `ArchiveEntryKind.File`; the root storage is not listed (it is
  `Root`). Order: depth first, each storage before its children, siblings in the tree's name order **[choice]**.
- `ArchiveEntry.Name` is the path from the root, components joined with `/`, each passed through `SanitizeName` **[choice]**. `RawName` is the
  entry's own stored name, unchanged.
- **Name mapping.** CFB names may hold any UTF-16 code unit, including control characters (`\u0005SummaryInformation` is common) and the
  separators `/` and `\`. A name component is rewritten one UTF-16 code unit at a time: each of U+0000-U+001F, U+007F-U+009F, `/ \ : * ? " < > | %`
  and each unpaired surrogate becomes `%` followed by its code unit in four uppercase hex digits (`%0005`, `%002F`). A component that is exactly
  `.` or `..` has its first character escaped, and one that ends in `.` or a space has its last character escaped (so Windows keeps the name).
  Valid surrogate pairs and all other characters are kept. Because `%` is always escaped, the mapping is one-to-one: different stored names never
  give the same component. (Names are unique only under the case-insensitive comparison, so on a case-insensitive file system two entries
  that differ only in case would still collide; extraction then follows the usual `ExistingFilePolicy`.)
- Lookups (`Find`, `OpenStream`) take the stored names separated by `/` and compare them as MS-CFB 2.6.4 does, by binary search over the
  sorted children.

## Access

- The source is a seekable `Stream` or `ReadOnlyMemory<byte>`. `Archive` copies a forward-only source into memory first, subject to `MaxTotalSize`.
- `OpenEntryStream(entry)` gives a seekable read-only stream for any entry in any order. Runs of consecutive sectors are read with one call.
  A storage opens as an empty stream.
- A reader is not thread-safe.

## Not done

Writing; the range lock sector (never read or written); transactions; the old beta-2 signature (`0E 11 FC 0D D0 CF 11 E0`), which is
rejected as a bad signature; reading the contents of streams (property sets, Word and Excel records, MAPI properties).

## Tests and tools

`tools/cfb/gen_cfb.py` writes the vectors in `testdata/cfb/` (version 3 and 4, DIFAT beyond 109 entries, the 4096-byte boundary, empty and
deeply nested storages, unusual names, a message-like tree, and about fifty damaged files that each break one rule) and
`manifest.json`; `olefile_check.py` confirms with the olefile package that the good ones read as described. `make-office-vectors.sh` makes
.doc/.xls/.ppt files with LibreOffice and records olefile's view of them in `testdata/cfb/office/`. The `cfb-read` fuzz target and its
seeds are in `fuzz/`.
