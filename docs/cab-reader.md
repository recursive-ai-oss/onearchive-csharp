# Cabinet reader

Normative notes for `CabReader` in `RecursiveAI.OneArchive.Cab`: read only. Source: Microsoft's [MS-CAB] (Cabinet File Format) 2.1 to
2.4 and 3.1; LZX is in `lzx-decoder.md`. Gaps were closed with cabextract 1.11 and gcab 1.6 run as black boxes; findings are marked
**[tool]**.

## Layout read

All integers are little-endian.

- **CFHEADER** (36 bytes): `MSCF`, reserved, `cbCabinet`, reserved, `coffFiles`, reserved, version minor and major, `cFolders`, `cFiles`,
  `flags`, `setID`, `iCabinet`. Then, if the reserve flag (4) is set, `cbCFHeader` (u16, at most 60000), `cbCFFolder` (u8), `cbCFData` (u8) and
  `cbCFHeader` reserved bytes; then the NUL-terminated `szCabinetPrev`, `szDiskPrev` (flag 1) and `szCabinetNext`, `szDiskNext` (flag 2), each
  at most 255 bytes. Reserved fields are not checked; major version must be 1 (minor is exposed, not checked); unknown flag bits are ignored.
  `coffFiles` must lie after the folder table and inside the file.
- **CFFOLDER** (8 bytes + `cbCFFolder`): `coffCabStart`, `cCFData`, `typeCompress`. Method = `typeCompress & 15` (0 none, 1 MSZIP, 2
  Quantum, 3 LZX); LZX window = `typeCompress >> 8 & 31`, which must be 15 to 21 (other values raise `ArchiveUnsupportedException`).
- **CFFILE** (16 bytes + name): `cbFile`, `uoffFolderStart`, `iFolder`, DOS date, DOS time, `attribs`, NUL-terminated name of at most 256
  bytes. `iFolder` 0xFFFD, 0xFFFE and 0xFFFF mark files continued from the previous cabinet, into the next one, or both; they are listed
  (`CabEntry.Continuation`) but cannot be opened (`ArchiveUnsupportedException` naming the neighbouring cabinet). A file that is not continued
  must satisfy `uoffFolderStart + cbFile <= min(0x7FFF8000, cCFData * 32768)`.
- **CFDATA**: `csum` (u32), `cbData` (u16), `cbUncomp` (u16), `cbCFData` reserved bytes, `cbData` bytes. Blocks of a folder are contiguous
  from `coffCabStart`. `cbUncomp` 0 means the block continues in the next cabinet (`ArchiveUnsupportedException`); more than 32768 is an error.

## Names, times, attributes

- A name with `_A_NAME_IS_UTF` (0x80) is UTF-8 (invalid sequences become U+FFFD); otherwise it is ISO 8859-1 unless
  `CabReaderOptions.LegacyNameEncoding` says otherwise. `\` becomes `/` in `ArchiveEntry.Name`; the stored bytes are in `RawName`.
- DOS date and time are read as UTC, like zip's. A date or time that is not valid gives no `ModifiedTime`.
- `Mode` is 0444, plus owner write unless `_A_RDONLY`, plus execute for everyone with `_A_EXEC`. Hidden and system are only in `Attributes`.

## Data blocks

- **Checksum** ([MS-CAB] 3.1): XOR of the little-endian 32-bit words of the data, then the same function continued over `cbData` and
  `cbUncomp` (four bytes), where a trailing partial word is built from the last byte down. **[tool]** The data reserve is not included,
  although the specification calls the second part the "header area": with reserve bytes present, only this reading extracts under
  cabextract. A zero `csum` is not checked. A mismatch raises `CompressionFormatException` at the block's offset unless
  `VerifyChecksum` is false.
- **Stored**: `cbData` must equal `cbUncomp`.
- **MSZIP**: the block is `CK` then a raw deflate stream that ends with a final block and produces exactly `cbUncomp` bytes. Matches may
  reach into the previous 32 KiB of the folder's output (which is the history of the folder, not of the block); the reader keeps that
  history across blocks and uses the deflate decoder's preset dictionary for it. **[tool]** Reference tools assume every block but the last
  has 32768 bytes; this reader accepts any size.
- **LZX**: one frame per block with `cbUncomp` 32768 (the last block may be shorter; any other short block is an error); a cabinet is
  decoded with one `LzxDecoder` per folder.

## Access and limits

- The source must be seekable (`Archive` copies a forward-only source into memory first, subject to `MaxTotalSize`).
- A folder is decoded from its first block. Reading files in table order costs one pass; going back decodes from the start again. At
  most two idle decoders are kept. Entries are listed in table order.
- Limits: `MaxEntryCount`, `MaxPathLength`, `MaxPathDepth`, `MaxTotalSize` (declared sizes, checked while listing),
  `MaxExpansionRatio` (compressed bytes counted per block including headers, output per block), `MaxWindowSize` (the LZX window,
  checked when an entry is opened). Errors name the format `cab` and the offset of the field or block at fault; errors from the
  deflate and LZX decoders are re-raised with the cabinet offset of the block and their own description.
- Not supported: Quantum folders (`ArchiveUnsupportedException`), files and blocks that span cabinets, LZX windows beyond 2^21, writing.
