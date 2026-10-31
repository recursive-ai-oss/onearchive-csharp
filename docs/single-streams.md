# Single compressed files and .tar.* archives

A `.gz`, `.bz2`, `.zst`, `.lz4`, `.xz` or `.lz` file that holds no container is an archive of one file (`ArchiveFormat.SingleStream`).
Brotli (`.br`) has no magic number, so it is recognised by its file name only. A tar inside any of these is one archive. The
normative rules (detection order, the entry name, writing) are in `archive.md` ("Detection", "Single compressed files").

| | |
|---|---|
| Types | `SingleStreamReader`, `SingleStreamWriter`, `ArchiveCompression` |
| Detection | magic bytes first, then the first 512 bytes of the decompressed stream, then the file name |
| Entry name | the gzip header's file name, else the archive name without its compression extension, else `data` |
| Writing | exactly one regular file; levels through `ArchiveCreateOptions.CompressionLevel` |
| Limits | the whole decompressed stream is metered against `MaxTotalSize` and `MaxExpansionRatio`; the window (Zstandard) or dictionary (xz, lzip) a stream may declare is limited by `MaxWindowSize`, 8 MiB by default |
