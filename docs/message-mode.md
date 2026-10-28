# Streams of tiny messages

For a connection that carries many messages of tens to a few hundred bytes, there are two ways to compress them. Both keep the
encoder and its buffers for the life of the connection, so a steady state allocates nothing.

## Frame per message

`ZstdEncoder.TryCompressFrame(message, buffer, out n)` (or `Lz4Encoder.TryCompressFrame`, or the headerless
`Lz4BlockEncoder.TryCompressPrefixed`) turns each message into a self-contained unit that can be decoded alone, lost or reordered.
A zstd frame costs a 4-byte magic number, a descriptor, optionally the content size and dictionary ID, and a 3-byte block
header; an LZ4 frame costs 11 bytes. With a trained dictionary this is the best choice when messages arrive independently.

- Set `WriteContentSize = false` and `WriteDictionaryId = false` when both ends agree on the dictionary out of band (saves up to 8 bytes).
- Set `Checksum = false` (the zstd default) unless the transport does not already protect the bytes.
- LZ4 frames: `ContentChecksum = false` saves 4 bytes; `Lz4BlockEncoder.TryCompressPrefixed` removes the frame entirely (a varint
  length and the block, not an LZ4 frame, read back with `Lz4PrefixedBlock`).

## One stream, flushed per message

A single `ZstdEncoder` (or `ZstdStream`) calls `Compress(message, ..., isFinalBlock: false)` and then `Flush(...)` after every message.
The receiver's `ZstdDecoder` sees one frame and can decode each message as soon as its bytes arrive. Later messages can match
earlier ones, so the output is much smaller than frame-per-message when messages resemble each other: measured on 232 game
snapshot payloads of 256 bytes, with a 16 KiB dictionary at level 1, 111 bytes per message in a stream against 137 as frames, and
125 against 222 without a dictionary.

- **Cost of a flush.** Each `Flush` ends a block, so it writes a 3-byte block header and the block's literal and sequence
  headers, and the next message starts a new block (entropy tables may be repeated from the previous block). At 256-byte messages the
  work per message is the same few microseconds as a frame; it does not grow with the length of the stream. Do not set `JobSize`
  in this mode: with jobs every flush reloads the overlap, and a flush after every few bytes costs time quadratic in the stream.
- **Memory per stream.** The encoder holds a window buffer of `2^WindowLog + 2 * 128 KiB` bytes, plus the match finder's tables
  (`2^HashLog` four-byte entries, and a chain table at levels 5 and up). Left at 0, `WindowLog` follows the level's table, which is
  megabytes of window for an unsized stream; set it. The decoder needs the same window.
- **Recommended options.** `Level` 1 to 3, `WindowLog` 14 to 17 (16 to 128 KiB of history; messages older than the window are not matched anyway), `Checksum = false`, a trained `Dictionary`, and `ContentSize`
  unset. Set the decoder's `MaxWindowSize` to match.
- **LZ4** streams: `Lz4Encoder` with `BlocksIndependent = false` keeps 64 KiB of history across messages; its `Flush` ends a block, so the
  per-message cost is a 4-byte block size. Memory is 64 KiB of history plus one block (`BlockSize`, default 4 MiB), so
  set `BlockSize` to `Size64KiB` unless messages are larger.

A frame boundary or a lost message breaks the shared history of a stream, so use frame-per-message over unreliable or
unordered transports.
