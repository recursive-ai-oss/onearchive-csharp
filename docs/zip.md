# zip

Source: PKWARE's APPNOTE 6.3.10 (cited by section) and WinZip's AES specification. The normative reader and writer rules are in
`archive.md` ("Zip"); encryption is in `zip-encryption.md` and damaged archives in `zip-recovery.md`.

| | |
|---|---|
| Types | `ZipReader`, `ZipWriter`, `ZipEntry` (every directory field), `ZipReaderOptions`, `ZipWriterOptions`, `ZipMethod`, `ZipDamage` |
| Methods | store, deflate, deflate64, bzip2, zstd and PPMd (variant I) read and written; LZMA and xz read |
| Encryption | WinZip AES-128/192/256 (AE-1 and AE-2) read and written; traditional (ZipCrypto) read only |
| Layout | zip64 on write when needed or on request, data descriptors on non-seekable output, UTF-8 names, extended timestamps |
| Reading | seekable sources use the central directory; other streams are read forward through local headers; overlapping entries, duplicate offsets and oversized directories are rejected before any entry is handed out |
| Lazy directory | `ZipReaderOptions.LazyDirectory` (off by default) reads the central directory record by record instead of holding it; memory does not depend on the entry count |
| Recovery | `ZipReaderOptions.Recovery` (off by default) resynchronises after damage and lists what it skipped |
| Limits | `MaxCentralDirectorySize` 128 MiB, plus the shared `ArchiveLimits` including the expansion-ratio limit |
| Not supported | spanned archives and PKWARE strong encryption (typed unsupported errors) |

## Lazy central directory

A mail export or a backup can hold a million entries. The default reader turns every central directory record (APPNOTE 4.3.12)
into a `ZipEntry` at the first `GetNextEntry`, about 650 bytes held per entry for short names (nearer 1 KB with long ones). With `ZipReaderOptions.LazyDirectory = true`
the reader holds a 64 KiB buffer and the record in hand, whatever the count, and any entry it returned can be opened
later with `OpenEntryStream(entry)` by seeking to its local header. The caller holds what it chooses to keep.

The mode applies to a seekable stream; a stream that cannot seek, or `ForceStreaming`, is read forward as before.

**What is the same.** The entries (every field), their order, the content, the typed errors, and the point at which a malformed
directory is refused: before the first entry is returned. To do that the reader makes a validation pass over the whole
directory without keeping it, and then a second pass that produces the entries. The end record, the zip64 end record and
locator (4.3.14 to 4.3.16, with a stub in front of the archive), `ArchiveLimits.MaxEntryCount` (checked against the declared
count before any record is read), `MaxTotalSize` (summed as entries are handed out, before their content is read), the
expansion limits (at read time) and the path limits work as in the default mode.

**Overlap and shared headers.** The default reader sorts all entries by local header offset to give each entry the offset
where its data must end, and to refuse two entries that share a header. The lazy reader gets the same answer without the sorted
list:

- If the directory lists entries in increasing offset order (what every common writer produces), an entry's neighbour is the next
  record, and the validation pass checks each entry against it with one record of memory. The last entry ends at the directory.
- If the offsets are not in increasing order, or two are equal, the validation pass keeps the offsets only (8 bytes per entry),
  sorts them, refuses a repeated offset, and checks every entry against its successor in offset order. The same sorted array
  finds an entry's end when it is handed out (binary search).

So overlapping entries, one stored blob claimed by many names, and data running into the next entry are refused exactly as in
the default mode, and the total compressed bytes read still cannot exceed the file's size.

**What differs.**

- `MaxCentralDirectorySize` does not apply: it bounds what the reader holds, and the lazy reader holds none of it. The directory
  length is still bounded by the archive's length and the entry count by `MaxEntryCount`.
- A symbolic link's target is the entry's data. The default reader reads every link's data while loading the directory; the lazy
  reader reads it when it hands the link out, so a link whose data cannot be read fails at that entry, not at open. An encrypted
  or unsupported link is listed with an empty target in both modes.
- With `Recovery`, the check that every local header is where the directory says (a 4-byte read per entry) is made in the
  validation pass; a directory that fails any check falls back to reading the local headers, as in the default mode, before any
  entry is returned.
- The reader is forward-only over the directory: it cannot be rewound. Open a new reader to enumerate again.
- `OpenEntryStream(entry)` accepts only entries this reader returned. The reader keeps no table of them; each entry carries the
  reader's private token, and the entry's own fields (offsets, method, flags, names, sizes) are all the open needs. Encrypted
  entries open with the reader's password as in the default mode.

**Cost.** Enumerating a million tiny entries reads the directory twice, 0.3 s in total, against 1.5 s for the default mode (which
spends its time in the garbage collector, holding 630 MiB). Extraction of a large entry is the same speed in both modes.
