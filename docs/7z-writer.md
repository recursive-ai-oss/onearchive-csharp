# 7z writer

How `SevenZipWriter` (`src/OneArchive/SevenZip/`) writes 7z archives and the choices the format document leaves open.
Sources: `7zFormat.txt` (structures) and `Methods.txt` (method IDs); every point they do not settle was fixed by listing
archives written by 7-Zip 26.00 (see the reader notes in `7z-reader.md`). `Archive.Create("a.7z")` uses the writer;
`ArchiveCreateOptions.SevenZip` carries its options and `CompressionLevel` overrides `Level`.

## Determinism

The bytes depend only on the entries (in the order the caller gives them) and `SevenZipWriterOptions`. No clock is read, no
thread is used, and nothing depends on the platform, the CPU's vector support or how the content streams chunk their reads.

## Layout

32-byte start header (signature, version 0.4, CRC over bytes 12–31, next-header offset, size and CRC), the packed streams of
every folder in order, then the header. The start header is written as zeros first and patched last, so the output must seek.
**A non-seekable output is supported by building the whole archive in a `MemoryStream` and copying it out at `Finish`**
(limited to 2 GiB); a seekable output is written in place, starting at its current position.

With `EncodeHeader` (default) the header is compressed with LZMA (preset 5, dictionary up to 16 MiB but no larger than the
header, size known so no end marker) and stored after the packed streams; the next header is then an `EncodedHeader` (0x17)
record: one pack stream, one LZMA folder with its unpack size and CRC. Without it the plain `Header` (0x01) is the next header.
An archive with no entries is just the 32-byte start header with next-header size 0, as 7-Zip writes it.

## Header content

- `MainStreamsInfo` only when some file has content: pack sizes, folders, and for each folder its file count, the sizes of
  all but the last file and a CRC-32 for every file. Folders carry no folder CRC; the per-file CRCs are in the substreams.
- `FilesInfo` in caller order: `EmptyStream` (directories, empty files and nothing else), `EmptyFile` (the empty-stream
  entries that are files), names (UTF-16LE, NUL-terminated, a trailing `/` removed, other characters kept as given),
  creation, access and modification times (100 ns ticks since 1601-01-01, only the properties some entry has, an
  all-defined byte or a bit vector), and attributes. No padding (`kDummy`) entries are written.
- Attributes: `0x20` (archive) for files and symbolic links, `0x10` for directories; when the entry has a `Mode`, or is a
  symbolic link, `0x8000` is added and the high 16 bits hold `st_mode` (type `0x8000`, `0x4000` or `0xA000` plus the low 12
  permission bits). A symbolic link without a mode gets `0777`.
- Symbolic links are files whose content is the UTF-8 link target. Hard links, devices and FIFOs raise
  `ArchiveUnsupportedException`. Anti-items are not written. Times before 1601 raise `ArgumentException`.

## Folders and coders

A file with content goes into the current folder, or starts a new one. A folder is closed before a file when `Solid` is off,
the filter chosen for the file differs from the folder's, the folder already holds `SolidFileCount` files (when non-zero), or
adding the file would pass `SolidBlockSize` bytes of content (unless the folder is empty, so a huge file still gets a folder).
Empty files and directories never touch folders.

Coder order in the header follows 7-Zip: the compression method first, then the filter, with one bond pair (input 1 ← output
0); the folder's single packed stream is the method's input. Both coders list the folder's unpack size.

| Method | ID | Properties |
|---|---|---|
| Copy | `00` | none |
| LZMA2 | `21` | 1 byte, the dictionary size code |
| LZMA | `03 01 01` | 5 bytes: `(pb*5+lp)*9+lc`, dictionary size LE32 |
| Deflate | `04 01 08` | none; raw Deflate at the given level (0–9) |
| Deflate64 | `04 01 09` | none; raw Deflate64 at the given level (0–9) |
| Zstandard | `F7 11 01` (the external-codec ID in `Methods.txt`) | none; the frame is self-describing. Standard 7-Zip builds cannot read it. |

Filters: Delta `03` (property: distance − 1); x86 `03 03 01 03`, PowerPC `03 03 02 05`, IA-64 `03 03 04 01`, ARM `03 03 05 01`,
ARM Thumb `03 03 07 01`, SPARC `03 03 08 05` (the long IDs 7-Zip itself writes for these, checked); ARM64 `0A` and RISC-V `0B`
(short IDs). Start offset 0, no properties.

`Filter = Auto` (default) looks at the first up to 4096 bytes of the file that would open a folder: ELF (class any, machine
3/62 x86, 40 ARM, 183 ARM64, 243 RISC-V, 50 IA-64 little-endian; 20/21 PowerPC, 2/43 SPARC big-endian) and PE (`MZ`, the
`PE\0\0` signature at the offset in the header, machine `014C`/`8664` x86, `01C0` ARM, `01C2`/`01C4` Thumb, `AA64` ARM64)
get their instruction set's filter; everything else gets none. The choice is made for every file, and a file whose choice
differs from the open folder's starts a new folder, so interleaved executables and data make many folders; sort entries by
kind or set the filter explicitly to avoid that. With `Copy`, Auto means no filter.

## Levels

`Level` 0 is Copy whatever `Method` says. 1–9 mean the following, chosen by measurement against `7z a -mx=N` (Silesia text and
executables) and by listing 7-Zip's dictionary choices (LZMA2 at 256 KiB, 4 MiB and 32 MiB for -mx=1, 3 and 5):

| Level | LZMA dictionary | Encoder preset |
|---|---|---|
| 1 | 256 KiB | 3 (hash chain, fast mode) |
| 2 | 1 MiB | 3 |
| 3 | 4 MiB | 3 |
| 4 | 16 MiB | 4 |
| 5, 6 | 32 MiB | 5, 6 |
| 7–9 | 64 MiB | 7–9 |

`DictionarySize` overrides the dictionary and `Lzma` replaces the whole LZMA configuration. Deflate uses the level as is;
Zstandard maps 1–9 to levels 1, 2, 3, 5, 7, 9, 12, 16, 19.

**Dictionary and memory.** The LZMA family sizes its tables from the folder's size when it is known. A non-solid folder knows
it (the file's size). A solid folder's first 16 MiB are held back: if the folder ends within them the compressor is created
knowing the size, otherwise without; the output is a function of the data either way.

## Streaming

File content is read sequentially from the caller's stream (any `Stream`, seekable or not) in 64 KiB pieces and must be
exactly `Size` bytes; a shorter or longer stream raises `ArgumentException` and the writer refuses further writes. Each piece
goes through the CRC, the filter and the compressor straight to the output, so memory is bounded by the compressor's tables
(plus the first 16 MiB of a solid folder and the entry names and CRCs until `Finish`).

## Not written

AES encryption, PPMd, BCJ2 and BZip2 coders, archive properties, additional streams, start-position and
comment properties.
