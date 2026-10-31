# RecursiveAI.OneArchive

Archives and containers for .NET in managed code, with safe extraction by default and one `Archive` API over all of them. It builds on [`RecursiveAI.OneCompress`](https://www.nuget.org/packages/RecursiveAI.OneCompress) and has no other dependency than the .NET base class library.

**Preview.** Public types carry `[Experimental]` (diagnostic `ONEARCHIVE001`, see the [README](https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs)).

- **Formats:** tar (ustar, pax, GNU, sparse files), zip (zip64, data descriptors, WinZip AES, ZipCrypto for reading, recovery of damaged files), 7z (solid blocks, encoded headers, AES-256), cpio, ar, ISO 9660 with Joliet and Rock Ridge, cab (stored, MSZIP, LZX), the RAR 5 container (listing and stored entries), Microsoft compound files, and single compressed files and `.tar.*` combinations (gz, zst, lz4, xz, lzip, bz2, br).
- **Happy path:** `Archive.Extract("data.tar.zst", "out")`, `Archive.Open(path)` (entries, random access when the source can seek), `Archive.Create("backup.tar.zst").Add("assets/").Save()`. The format comes from the magic bytes, then the extension.
- **Per-format readers and writers** (`ZipReader`, `TarWriter`, `SevenZipReader`, ...) expose every header field.
- **Safe by default:** no write outside the target directory (absolute paths, `..`, drive letters, backslashes, symbolic and hard link tricks, devices), and limits on total size, expansion ratio, entry count, path length and nesting. A limit hit is a typed exception, never a truncated result.
- **Not supported:** compressed RAR entries, Quantum folders in cab, BCJ2 in 7z, spanned zip; AES in browser WebAssembly.

```csharp
Archive.Extract("data.tar.zst", "out");                       // nothing is written outside "out"
Archive.Create("backup.tar.zst").Add("assets/").Save();        // format and compression from the extension
using var archive = Archive.Open("data.tar");                 // entries; random access when the source can seek
```

See the [README](https://github.com/recursive-ai-oss/onearchive-csharp#readme) and the [docs](https://github.com/recursive-ai-oss/onearchive-csharp/blob/main/docs/README.md). Trimmed apps register only the codecs they need through `ArchiveCodecs` ([docs](https://github.com/recursive-ai-oss/onearchive-csharp/blob/main/docs/archive-codecs.md)).

Licensed under MIT OR Apache-2.0. Third-party material is listed in `THIRD-PARTY-NOTICES.md`, included in the package.
