# ISO 9660 reader and writer

Normative notes for `RecursiveAI.OneArchive.Iso`. Clause numbers refer to ECMA-119; SUSP is IEEE P1281 and RRIP is IEEE P1282.

## Reading

- Descriptors are scanned from sector 16 (clause 8): primary (type 1), supplementary/Joliet (type 2, escape sequences `%/@`, `%/C`, `%/E`), boot record (listed only), terminator (type 255, version 1, identifier `CD001`). At most 256 sectors are examined; a missing terminator is a format error.
- The logical block size must be 2048; any other value raises `ArchiveUnsupportedException`. Both-endian fields whose halves disagree are a format error.
- Rock Ridge is active when the root directory's "." record carries `SP` and either a `PX` entry or an `ER` entry naming RRIP/IEEE. It applies to the primary hierarchy only. Entries read: `PX` (36- and 44-byte forms), `PN`, `SL`, `NM`, `TF` (short and long time forms), `CL`/`PL`/`RE` (relocated directories; the `RR_MOVED` placeholder at depth 1 is hidden), `CE`, `ZF` (listed; extracting raises `ArchiveUnsupportedException`), `SP`, `ER`, `ST`.
- `Tree = Auto` lists the primary hierarchy when Rock Ridge is active, else Joliet when present, else the primary hierarchy. `IsoEntry.Name` is the name from the chosen source; `IsoEntry.IsoPath` is always the identifier path of the hierarchy read.
- Multi-extent files (several directory records with the multi-extent flag, clause 9.1.6) are one entry whose `Extents` list the pieces.
- Safety: every extent must lie inside the image; a directory seen twice (loop or duplicate) is a format error; continuation areas (`CE`) must lie inside one sector, inside the image, must not overlap one another or the area being read, and are limited by `MaxSystemUseSize` per record and `MaxContinuationBytes` per image (default 64 KiB and 128 MiB); directories are limited by `MaxDirectorySize` (16 MiB); the shared `ArchiveLimits` bound entry count, path length and depth. Traversal uses an explicit stack, not recursion.

## Writing

- Defaults: interchange level 3, Joliet on, Rock Ridge on. Level 3 is the default because real names travel in Joliet and Rock Ridge, so the level only decides how much the plain names are cut, and it is the only level that allows files of 4 GiB or more (written as several extents of at most 4 GiB - 2048 bytes).
- Layout: 16 zero sectors, the primary descriptor, the Joliet descriptor, the terminator, the path tables (little-endian then big-endian, primary then Joliet), the primary directories, the Rock Ridge continuation areas, the Joliet directories, then file data in the order the entries were written, each file starting on a sector. The continuation areas follow the primary directories directly because some readers (bsdtar 3.8) only find an `NM` or `SL` continuation that lies next to the directory being parsed.
- Directory records are sorted as clause 9.3 asks, path tables as clause 6.9.1 asks. Names cut to the level that collide get a three-digit counter in the order the entries were written. Joliet names are cut to 64 UTF-16 units keeping a short extension, forbidden characters become `_`.
- Rock Ridge per record: `PX` (44 bytes), `TF` (modify, plus access and attributes when the entry has them), `NM`, `SL`, `PN` for devices. The root "." record carries `SP` and `ER` (`RRIP_1991A`). A name or link target that does not fit the record is split: the first part fills the record, the rest goes into a continuation area.
- Determinism: no clock is read. Volume dates are "not specified" unless `VolumeTime` is set; entries and implied directories without a time use `DefaultTime` (1970-01-01). Hard links share one extent. Zero-length files get extent 0. Deep directories (more than eight levels) are written as they are, not relocated.
- Symbolic links, devices and FIFOs need Rock Ridge; without it the writer raises `ArchiveUnsupportedException`.
