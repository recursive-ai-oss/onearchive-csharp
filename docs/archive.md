# Archives: the shared layer, tar, zip and single compressed files

How OneArchive models an archive, what extraction refuses by default, and what the tar reader and writer do. This
is normative: a port must reproduce the writer's bytes and the defaults below from this text.

## The model

A container is read through an `ArchiveReader` and written through an `ArchiveWriter`. Both are small abstract classes;
each format supplies one pair (`TarReader`/`TarWriter`), and everything above them (`Archive`, extraction, limits,
the builder) is written once against the base classes. A new container (zip, 7z, cpio, ar, ISO, cab, RAR5) adds its
pair and one case in `Archive`'s format switch; it does not change these interfaces.

Why this shape:

- **One entry type for every format.** `ArchiveEntry` carries what the formats have in common: name, kind, size,
  times, mode, owner ids and names, link target, device numbers. Format-specific data lives in a derived class
  (`TarEntry` has every header field, the pax records and the sparse map) and in `Extras`, a string map for attributes
  with no property (tar pax records, later zip extra fields). Extraction and the builder only know `ArchiveEntry`.
- **Sequential first.** `GetNextEntry()` plus `OpenEntryStream()` works on any stream, including a pipe. Random access
  (`SupportsRandomAccess`, `OpenEntryStream(entry)`) is optional and only offered when the source can seek and the format
  can address entries; zip and 7z will offer it from their central directories, tar from offsets it recorded while
  scanning.
- **Limits live in the base reader.** Entry count, name length, name depth and total declared size are checked in
  `ArchiveReader` as each entry is admitted, so no container has to remember them, and they apply before any content is
  read. Format-specific limits (tar's pax header size, sparse region count) live in the format's own options.
- **Sync and async share one body.** Every operation that does I/O is written once as an `async` method with a flag;
  with the flag off every `await` is on an already-completed operation, so the synchronous methods do not block a
  thread pool thread and do not duplicate code.
- **Safety is in the extractor, not in the readers.** A reader reports the archive as stored (a name `../x` is returned
  as `../x`); the extractor (`ArchiveExtractor`, shared by all formats) decides what may be written.
- **Errors are one hierarchy.** Everything derives from `CompressionException`: `CompressionFormatException` (format
  name, byte offset, what was expected, `IsTruncated`), `CompressionLimitException` (format, option name, limit, requested),
  and for archives `ArchiveSafetyException` (format, `Rule`, entry name) and `ArchiveUnsupportedException` (format, feature).

## Happy path

```csharp
using var archive = Archive.Open("data.tar.zst");           // path or Stream; format found from the bytes
foreach (var e in archive.Entries) { ... }                    // seekable sources only
using var s = archive.OpenEntry(archive.Find("a/b.txt")!);    // random access (re-decompresses when compressed)
using var reader = archive.OpenReader();                      // sequential, works on any source, once for non-seekable

Archive.Extract("data.tar.zst", "out");                       // safe defaults
Archive.Create("backup.tar.zst").Add("assets/").Add("notes.txt", "docs/notes.txt").Save();
```

`Archive.Create(path)` chooses the format and compression from the extension (`.tar`, `.tar.zst`/`.tzst`, `.tar.lz4`/`.tlz4`,
`.zip`, and `.zst`/`.lz4` for one compressed file) unless `ArchiveCreateOptions.Format`/`Compression` say otherwise. An extension
that names no writable format and no `Format` option is an `ArgumentException`. `Archive.Create(Stream)` defaults to
uncompressed tar. `.tar.gz` and `.gz` can be read; writing them waits for the Deflate encoder.

### Detection

1. Compression from the first bytes: Zstandard `28 B5 2F FD`, LZ4 frame `04 22 4D 18`, gzip `1F 8B`, bzip2 `BZh`,
   xz `FD 37 7A 58 5A 00`. Magic bytes win over the file name.
2. If compressed, the first 512 bytes of the decompressed stream are examined; otherwise the first 512 bytes.
3. Container from those bytes: `PK\3\4` (also `PK\5\6`, the whole of an empty zip, and the span markers `PK\7\8` and `PK00`) is
   zip; `ustar` at offset 257 is tar; a block with no magic but a valid header checksum, numeric mode and size fields and a
   non-empty name is a version 7 tar; 512 zero bytes inside a compressed stream are an empty tar, unless a name is known and
   does not say tar (a disk image starts with zeros too).
4. If nothing matched, the file name's extension decides (this is how an empty uncompressed `.tar` is recognised).
5. A compressed stream that is still unrecognised is a single compressed file (below).
6. Otherwise `CompressionFormatException` (`archive`, offset 0).

gzip is read (RFC 1952, all members in a row). bzip2 and xz are recognised but their codecs are not wired in yet: opening raises
`ArchiveUnsupportedException`. Creating gzip, bzip2 and xz is unavailable the same way. The place to add them is
`Internal/CompressionFilters.cs` (`OpenDecompress`, `OpenCompress`, `CanCompress`); nothing else changes.

## Entry model

`ArchiveEntry`: `Name` (`/`-separated, no trailing slash), `Kind` (File, Directory, Symlink, HardLink, CharacterDevice,
BlockDevice, Fifo), `Size`, `ModifiedTime`/`AccessTime`/`ChangeTime` (`DateTimeOffset?`, 100 ns), `Mode` (`UnixFileMode?`, 12 bits),
`Uid`, `Gid`, `UserName`, `GroupName`, `LinkTarget`, `DeviceMajor`, `DeviceMinor`, `Extras`.

## Safety defaults

Everything below is on by default; each loosening is an option on `ArchiveExtractOptions`.

| Rule | Default | Loosened by |
|---|---|---|
| Name leaves the target (`..`, including after `\` is read as `/`) | `ArchiveSafetyException`, rule `path-traversal` | `UnsafePaths = Sanitize` removes `.`/`..` components |
| Absolute name: leading `/` or `\` (UNC included) or a drive letter | rule `absolute-path` | `UnsafePaths = Sanitize` drops the root |
| NUL in a name | rule `invalid-name` (always) | |
| Symbolic link whose target is absolute, or has a `..` after a name, or climbs above the target directory | rule `link-escape` | `Symlinks = Unsafe` (create anyway) or `Skip` |
| Entry written under a path component that is a symbolic link (from the archive or already on disk) | rule `link-write-through` | `AllowWriteThroughSymlinks = true` |
| Hard link whose target is not a safe relative name of a file extracted earlier from this archive | rule `hardlink-target` | `HardLinks = Skip` |
| Device, FIFO | rule `special-file` | `SpecialFiles = Skip` (they are never created) |
| Destination exists (including a second entry with the same name) | rule `existing-file` | `Existing = Overwrite` or `Skip` |
| setuid, setgid, sticky bits | dropped | `KeepSpecialModeBits` |

Other behaviour, fixed:

- `\` and `/` both separate components on every platform; `.` and empty components are dropped; an entry named `.` or
  `./` that is a directory is the target itself, a file with that name is `invalid-name`. A first component of the form
  `X:` (a letter and a colon) is a drive.
- Symbolic-link targets are checked lexically (relative, no `..` after a name, leading `..` count at most the depth of the
  link's own directory). They are created with `File.CreateSymbolicLink`.
- A link replaced under `Existing = Overwrite` is deleted, never written through; files are created with
  `CreateNew`, which does not follow a link that appeared after the check.
- Hard links are extracted as **copies** of the file already written (managed code cannot create hard links). The
  target must be a regular file this run extracted.
- Parent directories that the archive does not list are created. Directory modes and times are applied after all
  entries (so a read-only directory does not block its contents).
- Ownership is not restored (no `chown` from managed code). Permissions are applied where the platform has them
  (`RestorePermissions`, default true); modification times for files and directories (`RestoreTimes`, default true).
- A file that fails midway (limit hit, truncation, cancellation) is deleted; earlier entries stay.
- Races with other processes changing the target while it is extracted are not defended against.

### Limits (`ArchiveLimits`)

A limit hit raises `CompressionLimitException` whose `LimitName` is the property name; never a truncated result.

| Property | Default | Checked |
|---|---|---|
| `MaxTotalSize` | 1 GiB | sum of declared entry sizes (sparse files at full size), as each header is read, before data |
| `MaxEntryCount` | 100,000 | per entry |
| `MaxPathLength` | 4096 chars | entry name and link target |
| `MaxPathDepth` | 64 components | entry name |
| `MaxExpansionRatio` | 1000 | bytes produced (tar stream bytes plus sparse holes) per byte read from the file, only once more than 32 MiB were produced; `Archive` only; 0 disables |
| `MaxWindowSize` | 8 MiB | Zstandard window a compressed archive may declare |

Tar adds (`TarReaderOptions`): `MaxExtendedHeaderSize` 1 MiB (one pax or long-name record block, checked before it is
buffered) and `MaxSparseRegions` 100,000.

For a compressed archive the whole decompressed stream is metered, and after the last entry the rest of the stream is read
so that the codec's own checks (Zstandard content checksum, truncation) run.

## Tar

### Reading

Dialects: V7, ustar, pax and GNU; `TarEntry.Format` says which a header was. Offsets in errors and in `TarEntry` count
bytes of the tar stream from where the reader started.

- **Header**: 512 bytes; the checksum (octal, unsigned or signed byte sum with the field read as spaces) must match, else
  `tar: invalid data at byte offset N: expected a valid header checksum`. Numeric fields are octal with space/NUL padding or
  GNU base-256 (bit 7 of the first byte; `0xFF` is negative). Names are NUL-terminated, decoded as UTF-8 when valid and
  as Latin-1 otherwise. The ustar `prefix` joins as `prefix/name` for ustar headers only.
- **Types**: `0`/NUL file, `1` hard link, `2` symlink, `3`/`4` devices, `5` directory, `6` FIFO, `7` file, `D` directory
  with a listing (skipped), `S` old GNU sparse, `x`/`X` pax extended header, `g` global pax header, `L`/`K` GNU long name and
  link name, `V` volume label (skipped), `M` multi-volume (`ArchiveUnsupportedException`). Any other type is a regular file
  (POSIX). A regular file whose name ends in `/` is a directory. Types 1-6 have no content whatever their size field says.
- **pax**: records `length key=value\n` (the length counts itself). Global records apply to all later entries until
  replaced; an empty value removes a key. Used: `path`, `linkpath`, `size`, `uid`, `gid`, `uname`, `gname`, `mtime`,
  `atime`, `ctime` (decimal seconds with optional fraction, digits beyond 100 ns dropped). Other keys become `Extras`.
- **GNU**: `L`/`K` records, atime/ctime in the old header, and sparse files in every form: old GNU (`S`, map in the header
  and extension blocks), pax 0.0 (repeated `GNU.sparse.offset`/`numbytes`), 0.1 (`GNU.sparse.map`) and 1.0 (map in the
  first blocks of the data, `GNU.sparse.major=1`, `minor=0`). A sparse entry's `Size` is its real size,
  `StoredSize` the bytes in the archive; its stream reads zeros for holes and, over a seekable source, seeks. Regions must be
  in order, inside the real size, and add up to the stored size. Extraction leaves holes as holes.
- **End**: a zero block ends the archive (one is enough; the rest is ignored). End of input exactly between entries with
  no zero block is `IsTruncated` unless `RequireEndOfArchive = false`; `IgnoreZeroBlocks` reads concatenated archives.
- Every length is validated before it is used: sizes against the stream length (seekable) or while skipping,
  pax and long-name sizes against `MaxExtendedHeaderSize`, region counts against `MaxSparseRegions`, times against years 1-9999,
  all arithmetic overflow-checked.

### Writing (byte-exact)

`TarWriter` writes `Pax` (default) or `Ustar`. For every entry, in this order: an optional pax extended header, the header, the
content padded with zeros to a multiple of 512. At the end: two zero blocks, then zeros to a multiple of `RecordSize`
(default 10240; 512 for none).

Header fields, all fixed width:

| Field | Written |
|---|---|
| name | UTF-8; directories get a trailing `/`. At most 100 bytes of ASCII; otherwise see below |
| mode | 7 octal digits + NUL of `Mode & 0xFFF`; default 0644 (directory 0755, symlink 0777) |
| uid, gid | 7 octal digits + NUL; above 2,097,151: field 0 and a pax record (ustar: error) |
| size | 11 octal digits + NUL, files only (0 for everything else); above 8,589,934,591: field 0 and pax `size` |
| mtime | 11 octal digits + NUL of whole seconds (floor); missing: 0. A fraction, a negative time or one beyond the field adds pax `mtime` (ustar: fraction dropped, out of range is an error) |
| chksum | 6 octal digits, NUL, space, computed with the field as 8 spaces |
| typeflag | `0` `5` `2` `1` `3` `4` `6` |
| linkname | link target, at most 100 ASCII bytes, else pax `linkpath` (ustar: error) |
| magic, version | `ustar\0`, `00` |
| uname, gname | at most 31 ASCII bytes, else pax `uname`/`gname` (ustar: error); empty when unset |
| devmajor, devminor | 7 octal digits + NUL; 0 for non-devices |
| prefix | ustar only: a name over 100 bytes is split at the leftmost `/` that leaves at most 100 bytes in `name`; the prefix must be at most 155 bytes, else error |

With `Pax`, a name over 100 bytes or with non-ASCII characters goes to pax `path` and the name field holds its first
100 bytes (cut at a character boundary). `atime`/`ctime` are written as pax records when the entry has them.
`Extras` are written as pax records (keys that the writer models itself, `charset` and `hdrcharset` are skipped; empty values are skipped).
Records are sorted by key in ordinal order and each is `length key=value\n`. The extended header is an entry named
`././@PaxHeader`, type `x`, mode 0000644, uid, gid and mtime 0, empty owner names, magic `ustar\0` `00`, zero device numbers.

Nothing host-dependent is written: no process id, clock, user database or locale. Output depends only on the entries and
the options; the hash matrix entries `tar-write/*`, `tar-zst/*`, `tar-lz4/*` and `tar-read/*` check it across platforms.

### The builder

`Archive.Create(...).Add(path)` records entries in call order; a directory is its own entry followed by its children
sorted by name (ordinal, UTF-16 code units), depth first. Times come from the file system (100 ns), modes from the file
(`0644`/`0755` where the platform has none), uid and gid are 0 with no names (not readable from managed code), symbolic links are
stored as links (`FollowSymlinks` stores their content), special files are not detected. `Normalize = true` sorts all entries by
name (stable, ordinal), sets the time to `NormalizedTime` (default the epoch), owners to 0 with no names, drops access and
change times, and sets modes to 0755 (directories, and files with any execute bit) or 0644 (symbolic links 0777), so equal
trees give equal bytes. Compression: Zstandard level 3 with a content checksum, LZ4 level 1; `CompressionLevel` overrides.
Single-threaded, so the bytes do not depend on the machine.

## Single compressed files

A `.gz`, `.zst` or `.lz4` file (any compressed stream the detector finds no container in) is an archive of one regular file,
`ArchiveFormat.SingleStream`, with `Archive.Compression` saying which codec. The entry's name is the gzip header's FNAME when it
has one (ISO 8859-1, as RFC 1952 2.3.1 says), otherwise the archive's file name without its `.gz`/`.zst`/`.zstd`/`.lz4`
extension, otherwise `data`. The gzip MTIME, when not zero, is the entry's modification time. `Size` is 0: the content's length
is not known before it has been read (it is limited and metered like every compressed archive). Writing takes exactly one file
entry (`ArchiveUnsupportedException` for a second entry or for anything but a regular file); the name is not stored, except in
a future gzip header. `Archive.Create("a.txt.zst")` and `ArchiveCreateOptions { Format = SingleStream, Compression = ... }`
produce them. `.tar.gz`/`.tgz` is a tar inside gzip, like `.tar.zst`.

## Zip

`ZipReader`/`ZipWriter` (namespace `RecursiveAI.OneArchive.Zip`), with `ZipEntry` carrying every header field. Section numbers
are those of PKWARE's APPNOTE 6.3.10; its text is not reproduced here.

### Reading

- **Two modes.** A stream that can seek is read through the central directory (4.3.12), which is authoritative; any other stream
  (or `ZipReaderOptions.ForceStreaming`) is read forward through the local headers (4.3.7), and the central directory is never
  read. Entries read forward have no comment, no Unix mode and no link kind (those live in the central directory), so a symbolic
  link arrives as a regular file holding its target and permissions are the extractor's defaults. `ArchiveComment` is only
  known in directory mode.
- **Finding the directory.** The end record (4.3.16) is searched in the last 22 + 65535 bytes, from the end; a candidate whose
  comment length ends exactly at the end of the data wins, else the nearest candidate whose comment fits. The zip64 locator
  (4.3.15) right before it and the zip64 end record (4.3.14) it points to (or the one just before the locator) replace the
  end record's values; a `0xFFFF`/`0xFFFFFFFF` marker without them is an error. Disk numbers other than 0 are
  `ArchiveUnsupportedException` (spanned archives). Data before the archive (a self-extractor stub) is allowed: when the
  directory is not at the stored offset but is at the offset shifted by the difference to the end record, every offset is shifted.
- **Checks before any entry is handed out** (all `CompressionFormatException` unless noted): the entry count is at most
  `ArchiveLimits.MaxEntryCount` (`CompressionLimitException`) and at most directory size / 46; the directory is at most
  `ZipReaderOptions.MaxCentralDirectorySize` (default 128 MiB, `CompressionLimitException`); every entry's local header lies
  before the directory; no two entries share a local header offset; the entry's compressed size fits between its local header
  and the next entry (or the directory). Together these make overlapping entries - one stored blob claimed by many names,
  data running into the next entry - impossible, so the total compressed bytes read never exceed the file's size.
- **Opening an entry** reads its local header and requires it to agree with the directory: same method, same name bytes, same
  encryption flag, and (when bit 3 is clear) the same sizes and CRC-32 where the local header states them. The data must end
  before the entry's boundary. A stored entry's compressed size must equal its size.
- **Content** is delivered through a stream that decodes on the fly and checks, at the last byte: the decoder consumed exactly the
  compressed size (deflate: the stream ends there; zstd: one or more frames fill it), the output is exactly the declared size
  (more output is detected one byte past it, never buffered), and the CRC-32 matches (`ZipReaderOptions.VerifyChecksum`, default on).
  Every byte is metered against `MaxExpansionRatio` (compressed bytes read versus bytes produced, whole archive, checked after
  32 MiB) and `MaxTotalSize`. Entry streams are forward-only.
- **Forward-only and data descriptors.** With bit 3 set, deflate and zstd entries are self-terminating, so the decoder finds the
  end and the descriptor (4.3.9) follows; the reader tries the four layouts (with or without the signature, 4- or 8-byte sizes, the
  one implied by a zip64 field in the local header first) and accepts the one whose CRC-32 and sizes equal what it decoded.
  `ZipEntry.IsSizeKnown` is false for such entries and `Size` is 0; the declared total limit cannot see them, so produced bytes are
  counted against `MaxTotalSize` as they are read. A stored entry with bit 3 is read only if its local header carries the size
  (our writer does this); otherwise it is `ArchiveUnsupportedException` (open a seekable stream instead). An entry the reader
  cannot decode but whose compressed size is known is skipped.
- **Names.** Bit 11 set: UTF-8. Otherwise the Info-ZIP Unicode Path field (0x7075, version 1) when its CRC-32 equals the CRC-32 of the
  stored name, otherwise IBM code page 437 (4.4.17), or `ZipReaderOptions.LegacyNameEncoding`. Names are returned as stored apart
  from a trailing `/` or `\`, which makes the entry a directory and is removed; `\` separates components for the extractor's safety
  rules (zip written on Windows). The archive comment and entry comments follow the same rules (the archive comment takes UTF-8 when valid).
- **Kind and mode.** Host system 3 (Unix) or 19: the high 16 bits of the external attributes are `st_mode`; the type bits give symlink,
  directory, device or FIFO (a socket is reported as FIFO), the low 12 bits the mode. Other hosts: DOS directory bit 0x10, no mode.
  A symbolic link's target is its data (UTF-8 when valid, else Latin-1), read when the directory is loaded, at most `4 * MaxPathLength` bytes.
- **Times.** NTFS field 0x000a (100 ns) wins over the extended timestamp 0x5455 (Unix seconds; modification, access, creation), which wins
  over the DOS date and time, read as UTC (the fields carry no zone); an impossible DOS value is no time.
- **Methods.** 0 store, 8 deflate, 93 zstd (4.4.5). Anything else, and encrypted entries (bit 0, bit 6 or method 99), are listed and raise
  `ArchiveUnsupportedException` naming the method when opened. Zip64 extra fields (0x0001) hold exactly the values whose header field is
  saturated, in the order size, compressed size, offset, disk; a short field is an error.

### Writing (byte-exact)

Everything not listed is as APPNOTE says. The same entries and options give the same bytes on every platform; the clock is never read.

- **Layout**: for each entry a local header, the data, and for descriptor entries the descriptor; then the central directory, the zip64
  end record and locator if needed, and the end record. "Version made by" is `0x033F` (Unix, 6.3); "version needed" is 10 (store), 20
  (deflate, directories and descriptor entries), 63 (zstd), at least 45 with zip64. Compression method per entry: `ZipWriterOptions.Method`
  (default deflate); directories, symbolic links and empty files are stored. Hard links, devices and FIFOs are
  `ArchiveUnsupportedException`. Deflate entries need the Deflate encoder (`ArchiveUnsupportedException` until it is wired in at
  `ZipWriter.OpenDeflate`); zstd entries are one frame at `Level` (default 3) with the content size and no frame checksum (the zip CRC-32
  covers the data).
- **Names**: UTF-8, bit 11 set when any byte is not ASCII (name or comment); directories get a trailing `/`. An empty name or NUL is an
  `ArgumentException`; names and comments are at most 65535 bytes.
- **Attributes**: external attributes = `(file type | mode) << 16 | (0x10 for directories)`; type is `0x8000` file, `0x4000` directory,
  `0xA000` symbolic link (whose data is its target, UTF-8); mode is the entry's, else 0644 (0755 directories, 0777 links).
- **Times**: DOS date and time are the entry's UTC time, `1980-01-01 00:00` when there is none or it is earlier, clamped to 2107-12-31 23:59:58.
  The extended timestamp field (0x5455) is written when the modification time fits 32 bits: flags then modification, access, change times
  (those that exist and fit) in the local header, modification time alone in the central directory. The NTFS field (0x000a, tag 1: modified,
  accessed, changed, missing ones taking the modification time) is written in both places when any time has a fractional second or does not fit 32 bits.
- **Extra field order**: zip64 (local header only when needed, see below), 0x5455, 0x000a, then the `ZipEntry.ExtraFields` the caller passed
  (except 0x0001, 0x5455, 0x000a and 0x7075, which the writer owns). The central directory repeats them, with its own zip64 field first.
- **Local headers and descriptors**: `DataDescriptors.Auto` patches the local header with CRC-32 and sizes when the output can seek and uses
  descriptors (bit 3, signature, 4-byte sizes, or 8-byte with a zip64 field) when it cannot; `Always` and `Never` force it. Entries whose data is
  known up front (directories, links, empty files) never have descriptors. A stored entry with a descriptor still states its sizes in the local
  header so that forward-only readers can find its end; other methods write zero there. The output of `Always` is the same on every stream.
- **Zip64**: an entry with a declared size of 3.75 GiB (`0xF0000000`) or more, or any entry with `Zip64 = Always`, gets a 16-byte zip64 field in
  its local header (`0xFFFFFFFF` in the 32-bit size fields; the values in the field). The central directory gets a zip64 field only for the values that
  reach `0xFFFFFFFF` (size, compressed size, offset, in that order). The zip64 end record and locator are written when the count reaches 65535, the
  directory's size or offset reaches `0xFFFFFFFF`, or `Always`; the end record then holds `0xFFFF`/`0xFFFFFFFF` for values that do not fit and the real
  values otherwise. `Never` turns each of these into an `ArgumentException`. An entry that compresses to 4 GiB or more without a zip64 local field fails the same way.
- **Checks**: content must be exactly `Size` bytes (`ArgumentException`); a failed write marks the writer failed and `Dispose` does not finish it.

## Platforms

Everything except the file-system parts (`Archive.Extract`, `Archive.Create(path)`, `Add(path)`, opening a path) works with streams only,
including under browser-WASM. Symbolic links and permissions need a platform that has them; on others the extractor raises
`ArchiveUnsupportedException` for links and skips permissions.
