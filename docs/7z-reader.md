# 7z reader

How `SevenZipReader` (`src/OneArchive/SevenZip/`) reads 7z archives and the choices the format document leaves open.
Sources: `7zFormat.txt` (header structures) and `Methods.txt` (method IDs) from the 7-Zip documentation; every point the
documents do not settle was fixed by a black-box run of 7-Zip 26.00 (`testdata/7z/generate.py` writes the vectors).

## What is read

- **Signature header** (32 bytes): signature `37 7A BC AF 27 1C`, version (major must be 0), the start-header CRC over
  bytes 12–31, then the next header's offset, size and CRC. A next-header size of 0 is an empty archive (7-Zip writes
  one for an empty input).
- **Header**: plain (`0x01`) or encoded (`0x17`: a streams info whose single folder decodes to the real header; up to four
  levels). Archive properties are skipped. Additional-streams info is parsed and ignored; names, times or attributes that
  point into it (`External != 0`) raise `ArchiveUnsupportedException`.
- **Streams info**: pack info, folders (coders, bond pairs, packed stream indices, unpack sizes, folder CRC), substreams
  (counts, sizes, CRCs).
- **Files info**: empty-stream, empty-file and anti bit vectors, names (UTF-16LE), creation, access and modification
  times (100 ns ticks since 1601-01-01 UTC), Windows attributes. Dummy padding, start positions, comments and unknown
  properties are skipped by their size.

## Choices where the document is silent (all checked against 7-Zip output)

| Point | What the reader does |
|---|---|
| Substream sizes | A folder with n files stores n−1 sizes; the last is the folder's unpack size minus the others. With no `kSize`, only folders with one file are allowed. |
| Substream CRCs | A folder with exactly one file and a folder CRC takes that CRC; digests are stored only for the other files, in folder order. |
| Directory | An empty-stream entry that is not marked empty-file, or any entry whose attributes carry `0x10`. Its size is 0 and any data stream is ignored. |
| Unix mode | Attribute bit `0x8000` says the high 16 bits are `st_mode`. The permission bits (`& 0xFFF`) become `Mode`; the type bits choose symlink (`0xA000`), directory, device or FIFO. Without the bit, `Mode` is null. |
| Symbolic link | A file of type symlink whose content is the target; `LinkTarget` is read when the header is read (at most 4 × `MaxPathLength` bytes). |
| Anti-items | Listed in `Items` with `IsAnti`; `GetNextEntry` skips them, so extraction never sees a deletion marker. |
| Names | Kept exactly as stored, without a trailing `/`. Extraction applies the shared path rules. |
| Coder order | Coders are kept in header order. BCJ2 (four inputs) is parsed and its graph checked, but not decoded. |
| Stream directions | `NumInStreams` are the coder's packed-side inputs when decoding, `NumOutStreams` its outputs; a bond feeds a coder input from another coder's output; unbound inputs are the folder's packed streams, in the listed order; the one unbound output is the result. |

## Coders

Decoded: Copy (`00`), Deflate (`04 01 08`), BZip2 (`04 02 02`), LZMA (`03 01 01`, 5 property bytes: lc/lp/pb and the
dictionary size; the unpack size comes from the header, an end marker is optional), LZMA2 (`21`, one property byte, the
dictionary size code, at most 40), Delta (`03`, one property byte: distance minus one), the branch filters x86, PowerPC,
IA-64, ARM, ARM Thumb, SPARC, ARM64 and RISC-V (short IDs `04`–`0B`; the older long IDs `03 03 xx xx` for the first six;
an optional 4-byte start offset as property), and Zstandard under the ID `F7 11 01` that `Methods.txt` lists for external
codecs (the frame is self-describing, so the coder's properties are ignored). Any other coder, or one with several
streams (BCJ2), raises `ArchiveUnsupportedException` naming the method ID, when the content is opened;
listing works. Each coder's output is cut to the unpack size the header gives; a coder that ends early is a truncated-data
error naming the folder's first packed stream offset.

## Limits and safety

- `SevenZipReaderOptions.MaxHeaderSize` (default 64 MiB) bounds the next header and any encoded header, checked on the
  declared size before allocating.
- `MaxDictionarySize` (default 64 MiB) bounds an LZMA or LZMA2 dictionary, checked on the header's property bytes before
  the decoder allocates (a typed limit error otherwise). It is separate from `ArchiveLimits.MaxWindowSize` because 7-Zip's
  own default dictionary is 16 MiB. The zip LZMA and xz entries use `MaxWindowSize`.
- `MaxCodersPerFolder` (default 32) and 64 streams per folder; every count read from the header is checked against the
  remaining header bytes or the limit before an array is allocated.
- File count against `ArchiveLimits.MaxEntryCount`; each folder's unpack size against `MaxTotalSize` before any decoding;
  the declared entry sizes through the shared `Admit` check; the expansion ratio over the bytes read from packed streams
  and bytes produced.
- Coder graphs: bonds and packed indices must use each stream once, and every coder must be reachable from the result
  (a loop is refused).
- Packed stream positions are checked against the file length when the header is read.
- A file's CRC-32 is checked when it has been read to its end (`VerifyChecksum`, default on). The header CRCs always are.
- The 7z header is at the end of the file, so the source must seek. `Archive.Open` copies a non-seekable source into
  memory first, up to `MaxTotalSize`.

## Random access and streaming

`OpenEntryStream(entry)` leases a decoder for the entry's folder and gives it back when the entry stream is disposed; the
reader keeps the two most recently used idle decoders, so opening a solid folder's entries in archive order decodes the
folder once, while going back to an earlier entry decodes from the folder's start. A decoder that threw is discarded and
the next use starts the folder over. In sequential reading the reader keeps the folder's decoder open, so an
archive read in order decodes each folder once. An entry stream can seek; seeking backwards re-decodes, and a seek turns
off the CRC check for that stream.

## Not done yet

PPMd, BCJ2, AES and Deflate64 coders; writing.

## Zip entries (methods 14 and 95)

Method 14 is read as APPNOTE 5.8 describes: a 4-byte header (LZMA SDK major and minor version, then the properties size,
which must be 5), the 5 property bytes, then raw LZMA data. The uncompressed size comes from the zip headers; a streamed
entry (size unknown) must end with an end marker. Method 95 is one xz stream. Both use `ArchiveLimits.MaxWindowSize` for
the dictionary. Both are read only.
