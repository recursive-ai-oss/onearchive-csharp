# Zstandard seekable format

Reading and writing seekable Zstandard files: ordinary frames, each compressed on its own, followed by one
skippable frame (the seek table) that maps content offsets to frames. A seekable file is a valid Zstandard file;
any decoder reads it start to end and ignores the table. The format is the Zstandard seekable format document,
version 0.1.0. Code: `src/OneCompress/Zstd/Seekable/`.

## Layout

```
frame 0 | frame 1 | ... | frame n-1 | seek table frame
```

Seek table frame (all fields little-endian):

| field | size | value |
|---|---|---|
| Skippable_Magic_Number | 4 | `0x184D2A5E` |
| Frame_Size | 4 | `entries * entry size + 9` |
| entry × n: Compressed_Size | 4 | the frame's size in the file |
| entry × n: Decompressed_Size | 4 | the frame's content size; 0 for a skippable or empty frame |
| entry × n: Checksum | 4 | only with the checksum flag: low 32 bits of XXH64 (seed 0) of the frame's content |
| Number_Of_Frames | 4 | n |
| Seek_Table_Descriptor | 1 | bit 7 = checksum flag; bits 6-2 reserved; bits 1-0 unused |
| Seekable_Magic_Number | 4 | `0x8F92EAB1` |

## What the writer does

- The content is cut every `FrameSize` bytes (default 1 MiB, 1 to 2^30). The last frame holds the rest. An input of
  length 0 gives no frames and a table with zero entries. An input that is an exact multiple of the frame size
  gets no extra empty frame.
- Each frame is compressed by one `ZstdEncoder` with the options in `ZstdSeekableOptions.Compression`, as a
  complete frame: nothing carries over between frames, so no frame depends on another. The header declares the
  frame's content size when `WriteContentSize` is set (the default). `ContentSize` must be null.
- The frame's own optional 32-bit checksum (`ZstdCompressionOptions.Checksum`, default off) is independent of the
  seek table checksums (`ZstdSeekableOptions.Checksums`, default on).
- The bytes depend only on the content, the options, and `EndFrame` calls (which cut a frame early at the caller's
  choice). They do not depend on how the content arrives across `Write` calls, on threads or on vector paths.
- The descriptor's unused bits are written as zero.
- Frames are compressed one after another; the writer does not use worker threads across frames. (Options that
  split a single frame into jobs apply inside each frame as usual.)

## What the reader checks

On open (`ZstdSeekTable.Read`):

1. The file is at least 17 bytes; its last four bytes are `0x8F92EAB1`.
2. The descriptor's reserved bits 6-2 are zero.
3. `Number_Of_Frames` is at most `MaxFrames` (default 4,194,304; `CompressionLimitException`, before anything is
   allocated), and a table of that many entries fits in the file.
4. The table frame starts with `0x184D2A5E` and its `Frame_Size` equals `entries * entry size + 9`.
5. Every `Compressed_Size` is at least 8 (the smallest frame, an empty skippable frame).
6. The sizes add up: the frames fill the file exactly up to the table frame.
7. No frame declares more than `MaxFrameSize` bytes of content (default 256 MiB), so the buffer for one decoded frame
   is bounded before it exists.

Every failure is a `CompressionFormatException` (format `zstd-seekable`) with the byte offset and what was
expected. On a read, the frame is decoded with `ZstdDecoder` and must produce exactly `Decompressed_Size` bytes,
and match the table checksum when the table has them (`VerifyChecksums`, default on); otherwise
`CompressionFormatException`. Decoder errors inside a frame are re-raised with the offset relative to the file.
Frames whose `Decompressed_Size` is 0 are never read by a range read. `Verify()` decodes every frame and also
checks that a zero-content frame is a skippable frame of the listed size or an empty Zstandard frame.

The table does not describe frames inside the file beyond their sizes, so a file whose table is consistent but
whose frames are not frames fails when those frames are read (or by `Verify()`), not when it opens.

## Random access

`FindFrame(offset)` binary-searches the cumulative content offsets for the frame whose end lies beyond the
offset. A read decodes each frame it touches once: a read of `L` bytes touches `ceil(L / F)` frames, or one more
when it straddles a frame boundary, for frame size `F`. The reader caches the last
decoded frame, so sequential small reads decode each frame once, and a read that covers a whole frame decodes
straight into the caller's buffer. `ZstdSeekableReader.FramesDecoded` and `CompressedBytesDecoded` count the work.

## Non-seekable input

A seekable reader needs to read the end of the file and then jump around, so it takes a readable stream that
can seek (or a memory block). A stream that cannot seek is rejected with `ArgumentException`. To read a seekable
file from such a stream, use `ZstdStream`, which decodes the frames in order and skips the seek table.

## Tiers

| Tier | Types |
|---|---|
| 1 | `ZstdSeekable.Compress`, `Decompress`, `DecompressRange`, `ReadTable` |
| 2 | `ZstdSeekableWriter` (to a `Stream` or `IBufferWriter<byte>`, reusable through `Reset`), `ZstdSeekableReader` (a stream or memory, positional `Read`/`ReadAsync`), `ZstdSeekableStream` (read-only, seekable `Stream`) |
| 3 | `ZstdSeekTable` (parse, index, `FindFrame`), `ZstdSeekTableFooter`, `ZstdSeekFrame`, `ZstdSeekTableBuilder` (encode) |

## Testing

- Unit and property tests: `tests/OneCompress.Tests/Zstd/ZstdSeekableTests.cs`.
- Interop (`ONEARCHIVE_INTEROP=1`): our files decode with the `zstd` tool and read with the seekable-format
  example reader; files from the example producer open in our reader. The example programs are built by
  `tools/build-zstd-seekable-examples.sh` from the pinned release tarball as black boxes.
- Fuzz targets `zstd-seekable-read` (memory and stream readers must agree) and `zstd-seekable-roundtrip`;
  seeds from `fuzz/seeds-seekable.sh`.
- Hash matrix entries `zstd-seekable/*`.

## Size overhead

silesia.tar (211,957,760 bytes), compressed size against one frame at the same level; the table is 8 + 9 bytes plus
8 or 12 bytes per frame. The cost is the frames, not the table.

| level | single frame | frame 64 KiB | frame 1 MiB | frame 8 MiB |
|---|---|---|---|---|
| 3 | 66,748,199 | +10.43% (3,235 frames) | +1.70% (203 frames) | +0.15% (26 frames) |
| 15 | 56,938,984 | +18.74% | +7.28% | +1.34% |

With table checksums on, the table is 38,837 / 2,453 / 329 bytes at the three frame sizes; off, 25,897 / 1,641 / 225.

Frames touched by a read at level 3 (1,000 random reads each, last-frame cache on): reads of 1 byte and 4 KiB touch
1.0 frame at every frame size; 64 KiB reads touch 2.0 frames at F = 64 KiB and 1.05 at F = 1 MiB; 1 MiB reads touch
17.0 frames at F = 64 KiB and 2.0 at F = 1 MiB.
