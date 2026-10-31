# tar

Source: POSIX ustar and pax (IEEE Std 1003.1) and the GNU tar manual's format chapter. The normative reader and writer rules are in
`archive.md` ("Tar").

| | |
|---|---|
| Types | `TarReader`, `TarWriter`, `TarEntry` (every header field, the pax records and the sparse map), `TarReaderOptions`, `TarWriterOptions` |
| Reads | V7, ustar, pax (global and per-entry records), GNU long names and links, and sparse files in the old GNU, pax 0.0, 0.1 and 1.0 forms |
| Writes | pax (default) or ustar, byte-exact: records sorted by key, no host-dependent fields, a fixed record size (default 10240) |
| Limits | `MaxExtendedHeaderSize` 1 MiB, `MaxSparseRegions` 100,000, plus the shared `ArchiveLimits` |
| Compressed forms | `.tar.gz`, `.tar.zst`, `.tar.lz4`, `.tar.xz`, `.tar.lz`, `.tar.bz2` (read only) are one archive to `Archive.Open` |
| Not supported | multi-volume archives (typed unsupported error) |
