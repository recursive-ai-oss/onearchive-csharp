# Compound file reader and writer

Normative notes for `CompoundFileReader` and `CompoundFileWriter` in `RecursiveAI.OneArchive.Cfb`. Source: [MS-CFB] Compound File Binary File Format,
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

Transacted or in-place editing; property-set encoding; the range lock sector (never read or written); transactions; the old beta-2 signature (`0E 11 FC 0D D0 CF 11 E0`), which is
rejected as a bad signature; reading the contents of streams (property sets, Word and Excel records, MAPI properties).

## Tests and tools

`tools/cfb/gen_cfb.py` writes the vectors in `testdata/cfb/` (version 3 and 4, DIFAT beyond 109 entries, the 4096-byte boundary, empty and
deeply nested storages, unusual names, a message-like tree, and about fifty damaged files that each break one rule) and
`manifest.json`; `olefile_check.py` confirms with the olefile package that the good ones read as described. `make-office-vectors.sh` makes
.doc/.xls/.ppt files with LibreOffice and records olefile's view of them in `testdata/cfb/office/`. The `cfb-read`, `cfb-rewrite` and `cfb-write` fuzz targets and their
seeds are in `fuzz/`. `check-written.sh` rewrites every good vector and the Office files in both versions and has olefile list the result.

# Writer

`CompoundFileWriter` (an `ArchiveWriter`; `Archive.Create("x.cfb")`) writes version 3 (512-byte sectors, the default) or version 4
(4096-byte sectors) from storages and streams. Options (`CompoundFileWriterOptions`): `MajorVersion` (3 or 4), `StrictNames` (default true) and
`MaxBufferedBytes` (default 1 GiB). The output depends only on the entries and the options: the clock is never read, and the bytes are the same on
every platform.

## Entries

- A directory entry is a storage, a file entry a stream; other kinds raise `ArchiveUnsupportedException`. The entry name is a path of
  components separated by `/` (empty components are skipped; `.` and `..` components are rejected). Missing parent storages are created with
  zero CLSID, state bits and times. An explicit directory entry for a storage that was only implied sets its properties; a second explicit
  entry, or a stream and a storage with the same name, is a duplicate.
- An entry with an empty name and kind directory describes the root storage: its CLSID, state bits and times. The root's stored name is
  `Root Entry`.
- Properties: a `CfbEntry` supplies `ClassId`, `StateBits` and the raw FILETIMEs (`CreationFileTime`, `ModifiedFileTime`; a zero raw value
  falls back to the time below). Any entry supplies `ModifiedTime` (modified) and `ChangeTime` (created) as FILETIMEs, exact to 100 ns; no
  time gives 0, "not recorded". A time before 1601-01-01 is an `ArgumentException`.
- **Names [choice].** Each path component is the reader's `SanitizeName` form of the stored name, and the writer inverts it
  (`UnsanitizeName`): `%` followed by exactly four uppercase hex digits is that UTF-16 code unit; any other `%` is itself. So the entry
  names the reader returns can be fed straight back to the writer, and a rewrite keeps every stored name. A literal `%` followed by four
  uppercase hex digits therefore has to be written as `%0025` plus the digits. The stored name must have 1 to 31 UTF-16 code units and no
  NUL (MS-CFB 2.6.1), and be unique within its storage under the 2.6.4 comparison (case-insensitive), else `ArgumentException`. With
  `StrictNames` (default) a name containing any of `/ \ : !` is also rejected, as MS-CFB 2.6.1 forbids them; turn it off to store such names (the reader of this
  library accepts them, which is how every vector is rewritten in the tests). A rejected entry changes nothing: the writer stays usable.
- Version 3 streams larger than 2^31 - 1 bytes raise `ArchiveUnsupportedException`. The directory is limited to 262,144 entries, so every
  output reads with the default `MaxDirectoryEntries`; more raises `CompressionLimitException`, as does exceeding `MaxBufferedBytes`.

## Layout [choice]

The layout needs every stream's size, so content is buffered until `Finish` and the file is written in one forward pass, sectors in this order:
header (padded to a whole sector in version 4), directory, mini FAT, mini stream, the data of the streams of 4096 bytes or more in directory
order, FAT, DIFAT. Each sector chain is contiguous.

- **Directory** (2.6): entry 0 is the root; the others follow in preorder, the children of each storage sorted by the 2.6.4 comparison
  (shorter name first, then `char.ToUpperInvariant` per UTF-16 code unit, as the reader does). The unused tail of the last directory sector
  holds free entries (type 0, all-zero except left, right and child = `NOSTREAM`). Version 4 files store the directory sector count in the
  header; version 3 files store 0. Storages have start sector 0 and size 0; empty streams have start sector `ENDOFCHAIN` and size 0. Times, CLSID
  and state bits are written as given. The root's start sector and size describe the mini stream (`ENDOFCHAIN` and 0 when there is none).
- **Siblings (2.6.4).** The children of a storage are linked as a binary search tree by repeatedly taking the middle of the sorted run (the
  upper middle for an even count) as the node; `Child` of the storage is the top node. Colors keep the tree a valid red-black tree: all nodes
  are black when the count is 2^k - 1, otherwise the nodes on the deepest level, which is not full, are red. Every path then has the same number of
  black nodes and no red node has a red child.
- **Mini stream (2.4, 2.5).** A stream of 1 to 4095 bytes is stored in 64-byte mini sectors, each stream's chain contiguous, in directory
  order, and padded with zeros to a mini sector; the mini stream is padded with zeros to a whole sector. The mini FAT has one entry per
  mini sector, padded with `FREESECT`. A stream of exactly 4096 bytes or more is a normal stream.
- **FAT and DIFAT (2.3, 2.5).** The FAT holds one chain per structure, `FATSECT` for each FAT sector and `DIFSECT` for each DIFAT sector;
  unused entries are `FREESECT`. The FAT sector count is the smallest `n` such that all sectors, FAT and DIFAT included, fit in `n` sectors of
  entries. The first 109 FAT sector numbers go in the header (the rest `FREESECT`); the others go in DIFAT sectors of `sector size / 4 - 1`
  numbers each, unused ones `FREESECT`, with the last entry linking to the next DIFAT sector or `ENDOFCHAIN`. The header holds the first
  DIFAT sector (`ENDOFCHAIN` when none) and the count.
- **Header (2.2).** Signature, zero CLSID, minor version `0x3E`, major version 3 or 4, byte order `0xFFFE`, sector shift 9 or 12, mini shift 6,
  zero reserved bytes, mini cutoff 4096, transaction signature 0, first mini FAT sector (`ENDOFCHAIN` when none) and count.
- The last sector is padded with zeros. Sector numbers stay below `0xFFFFFFFA`, else `CompressionLimitException`.
