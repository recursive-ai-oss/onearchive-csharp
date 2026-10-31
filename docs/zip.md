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
| Recovery | `ZipReaderOptions.Recovery` (off by default) resynchronises after damage and lists what it skipped |
| Limits | `MaxCentralDirectorySize` 128 MiB, plus the shared `ArchiveLimits` including the expansion-ratio limit |
| Not supported | spanned archives and PKWARE strong encryption (typed unsupported errors) |
