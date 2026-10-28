# cpio and ar

Normative notes for the `Cpio` and `Ar` containers in `RecursiveAI.OneArchive`: what is read, what is written, and every
choice the formats leave open. A port should reproduce the same bytes from this text.

Sources: the GNU cpio manual ("new ASCII" and "crc" formats), POSIX.1 pax's cpio interchange format ("odc"), the `ar(5)` and
`deb(5)` man pages, and public descriptions of the GNU and BSD long-name conventions. Gaps were closed by running GNU cpio 2.15,
GNU ar (binutils 2.46) and bsdtar 3.8 as black boxes on small trees; the findings are marked **[tool]**. The vectors in
`testdata/cpio/` and `testdata/ar/` come from `python3 testdata/cpio/generate.py`.

## cpio

### Header layouts

| Variant | Magic | Header | Fields | Padding |
|---|---|---|---|---|
| newc | `070701` | 110 bytes | 13 fields of 8 upper-case hex digits after the magic: inode, mode, uid, gid, nlink, mtime, filesize, dev major, dev minor, rdev major, rdev minor, namesize, check | header + name padded to 4; data padded to 4 |
| crc | `070702` | as newc | as newc; `check` is the 32-bit sum of the data bytes (regular files only) | as newc |
| odc | `070707` | 76 bytes | octal: dev (6), inode (6), mode (6), uid (6), gid (6), nlink (6), rdev (6), mtime (11), namesize (6), filesize (11) | none |

- `namesize` counts the NUL that ends the name. The name is UTF-8.
- The archive ends with an entry named `TRAILER!!!` (nlink 1, everything else zero), then zero bytes up to the block size
  (default 512, as GNU cpio writes). Readers ignore what follows the trailer.
- The mode is the full `st_mode`: type in `0170000` (`0100000` file, `040000` directory, `0120000` symlink, `020000` character
  device, `060000` block device, `010000` FIFO, `0140000` socket), permissions in the low 12 bits.
- A symbolic link's data is its target; `filesize` is the target's length.
- Device numbers: newc and crc store rdev major and minor separately. **[tool]** odc stores one 18-bit number, `major * 256 + minor`
  (GNU cpio and bsdtar agree: major 259, minor 3 is `0o201403`). The writer refuses an odc major above 1023 or minor above 255.
- The old binary format (`0xC771`) is recognised and raises `ArchiveUnsupportedException`.

### Hard links

newc and crc store a file with a link count above one once: the headers of all names but the last have `filesize` 0, the last one
carries the data. **[tool]** GNU cpio -o does this for the names it meets in the input; odc stores the data under every name.

Reader: a newc or crc regular file with `nlink > 1` and `filesize 0` is a *stub* (`IsLinkStub`). On a seekable stream the reader
makes one pass over the headers (at most `MaxEntryCount` of them; a header that fails to parse ends the pass, and the main read
reports the same error in order), finds the member with the same (dev major, dev minor, inode) that holds data, and gives the stub
that content (`ContentFromLaterLink`, `Size` is the data size, `StoredSize` stays 0). Every stub counts toward
`MaxTotalSize`. On a forward-only stream, or with `ResolveLinkStubs = false`, a stub is empty.

Writer: a generic `ArchiveEntryKind.HardLink` raises `ArchiveUnsupportedException`, because the data belongs to the last name
and a streaming writer cannot know which that is. A caller who wants links writes `CpioEntry` values with the same `Inode` and
`NLink`, empty content for all names but the last, and the data with the last. **[tool]** GNU cpio -i and bsdtar both restore those
as one file.

### Writer defaults

- Inode: `CpioEntry.Inode` if above zero, else a counter that starts at 1 and counts every entry written. Device fields are zero.
- Link count: `CpioEntry.NLink` if above zero, else 2 for a directory and 1 otherwise.
- Mode: the entry's permission bits, or `0644` (files), `0755` (directories), `0777` (symlinks). Uid and gid: the entry's, else 0.
- Time: the entry's modification time in seconds (before 1970 is refused), else 0. Nothing reads the clock.
- A directory's trailing `/` is dropped. Sockets are not written; a FIFO is.
- crc: **[tool]** GNU cpio sums regular files only; a symlink's `check` is 0, and so is ours. The sum comes before the data, so
  seekable content is read twice; content that cannot seek is buffered up to `MaxBufferedSize` (64 MiB), else
  `ArchiveUnsupportedException`.
- Field overflow (odc: 18 bits for inode, mode, uid, gid, nlink and dev, 33 bits for time and size; newc: 32 bits) raises
  `ArchiveUnsupportedException` naming the field and entry.

### Reader rules and limits

- All entries must carry the magic of the first.
- Every number is validated as digits of the right base. `namesize` must be at least 1, the name must end in NUL and hold no other,
  and the name length is checked against `MaxPathLength` *before* the buffer is allocated. A symlink's data is checked against
  `MaxPathLength` before it is read.
- A stored size is checked against `MaxTotalSize` through the shared entry admission, before any content is read.
- The crc sum is checked when an entry's data is read from its first to its last byte in order (`VerifyChecksum`, default on).
  Skipped entries and entries read by seeking are not checked. **[tool]** A symlink's check is not verified (GNU writes 0).
- `RequireTrailer` (default on): an archive that ends at an entry boundary without `TRAILER!!!` is a truncated-archive error.
- A socket reads as `ArchiveEntryKind.Fifo`; `RawMode` tells them apart. A mode with no file type is an error.
- Device and FIFO entries are listed with their numbers; safe extraction never creates them (`SpecialFiles` policy).

## ar

### Layout

`!<arch>\n`, then members. A member is a 60-byte header, its data, and one `\n` if the data length is odd:

| Bytes | Field | Encoding |
|---|---|---|
| 0–15 | name | ASCII/UTF-8 padded with spaces |
| 16–27 | mtime | decimal, space padded (blank reads as 0) |
| 28–33 | uid | decimal |
| 34–39 | gid | decimal |
| 40–47 | mode | octal (blank reads as 0) |
| 48–57 | size | decimal, required |
| 58–59 | end marker | `` `\n `` |

The archive has no trailer. A final odd-sized member whose pad byte is missing is accepted.

### Names

- **Inline.** The name followed by `/` (System V and GNU), or the bare name (BSD). The reader removes trailing spaces, then one
  trailing `/`.
- **GNU long names.** A member named `//` holds the long names, each ending in `/\n`; a member whose name field is `/` followed by
  digits takes the name at that offset in the table, up to the newline, minus one trailing `/`. The table comes before the
  members that use it. The reader reads it into memory (at most `MaxNameTableSize`, default 8 MiB).
- **BSD long names.** The field is `#1/` and the name's byte length; the name is the first bytes of the data (trailing NULs removed)
  and the member's size counts it. **[tool]** bsdtar writes the name unpadded and uses `#1/` for names longer than 16 bytes or with a
  space.
- **Symbol tables** are `/` and `/SYM64/` (GNU) and `__.SYMDEF`, `__.SYMDEF SORTED`, `__.SYMDEF_64`, `__.SYMDEF_64 SORTED` (BSD).
  They are not interpreted. By default they and the `//` table are left out of the listing; `IncludeSpecialMembers` returns them
  as entries with `MemberKind` set and their data at `DataOffset`. They are not safe extraction names, which is why they are off by
  default. A `.deb` package is an ar archive of `debian-binary`, `control.tar.*` and `data.tar.*`, and reads like any other.
- **[tool]** GNU ar writes mtime 0, uid 0, gid 0 and mode `644` in its default deterministic mode, and leaves the time, uid, gid and
  mode fields of `/` and `//` blank. bsdtar writes real times and the mode with type bits (`100664`). The reader takes all of these.
- A thin archive (`!<thin>\n`) raises `ArchiveUnsupportedException`.

### Writer

- Only regular files can be stored; other kinds raise `ArchiveUnsupportedException`. `ArchiveBuilder` leaves directories out of an
  ar and keeps the files' paths in their names.
- Header values: mtime 0 unless the entry has one; uid and gid from the entry (0 default, at most 999999); mode `0100000 | permission
  bits` (default `0644`) in octal; size at most 9,999,999,999.
- GNU variant (default): a name of up to 15 bytes that does not start with `/` is written as `name/`; others go to the `//` table
  as `name/\n` and the field is `/offset`. The table (and an optional caller-built `/` symbol table, `SetSymbolTable`) is written
  before the members, so the members are held until `Finish`: in memory up to `MaxMemoryBuffer` (16 MiB), then in a temporary
  file. Where temporary files are not available, a larger archive raises `ArchiveUnsupportedException`.
- BSD variant: names of up to 16 bytes with no space, not ending in `/` and not starting `#1/` are written bare; all others as
  `#1/length` with the name unpadded at the start of the data. It streams.
- Common variant: `name/` only, names above 15 bytes are refused. It streams.
- A name may not be empty or contain NUL (and for ar a line feed).

### Reader limits

Header fields are validated as digits; the end marker must be present. A BSD name length is checked against `MaxPathLength` and
against the member's size before the name is read; the `//` table size against `MaxNameTableSize` before it is read; a `/N` reference
against the table's length. Left-out special members count toward `MaxEntryCount`.

## Archive happy path

- Detection: the magic (`!<arch>\n`, `!<thin>\n`, `070701`, `070702`, `070707`), then the extension: `.cpio` and `.cpio.gz|zst|zstd|lz4|bz2|xz`;
  `.a`, `.ar`, `.deb`, `.udeb`.
- `Archive.Create` picks the format from the extension, or `ArchiveCreateOptions.Format`; `.cpio.gz` and `.cpio.zst` compress the whole
  archive, as for tar. cpio reads and writes `ArchiveCreateOptions.Cpio` / `ArchiveOpenOptions.Cpio`, ar the same with `Ar`.
- Extraction applies the shared path rules to every name; a stored `../x`, an absolute path or a symlink escape is refused as for tar.
