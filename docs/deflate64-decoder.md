# Deflate64 decoder

Normative notes for `Deflate64Decoder`. The format facts (symbols, extra bits, window, header limits) are in
`deflate64-derivation.md`, with the experiment behind each. This file records what the decoder does with them.

- **Input:** one raw stream (zip method 9 stores nothing around it). No wrapper, no checksum; the zip layer checks the CRC-32.
- **Window:** 65536 bytes of history, kept in a 128 KiB ring. A match whose distance exceeds the output produced so far is a
  format error naming the offset. Distance 65536 is accepted (fact F1).
- **Symbols:** RFC 1951 for everything not listed in the derivation. Literal/length 285 = 3 plus 16 extra bits; distance
  symbols 30 and 31 = bases 32769 and 49153 with 14 extra bits. Literal/length 286 and 287 fail only when decoded.
- **Dynamic headers:** HLIT up to 288 and HDIST up to 32 are accepted (fact F9); the code lengths must still form a complete
  or single-symbol code per RFC 1951, otherwise a format error.
- **Block type 3** is a format error. A stored block checks LEN against its complement.
- **Output limit:** `MaxOutputSize` (default 1 GiB) is checked before bytes are written; reaching it throws
  `CompressionLimitException` and writes nothing past the limit. The decoder allocates only its fixed window and tables.
- **End:** `Done` is returned after the final block; bytes after it are left unconsumed. The one-call and stream readers refuse
  them unless `AllowTrailingData` is set. A truncated stream is a `CompressionFormatException` with `IsTruncated` set,
  raised by `ThrowIfIncomplete` or by the readers.
- **Speed:** a fast loop (`Deflate64FastLoop`, built like Deflate's) runs while 24 input and 280 output bytes remain. A second bit refill follows a length whose code and extra bits leave fewer than 29 bits (symbol 285: 15 + 16 bits, then a distance code and 14 extra bits do not fit one 56-bit refill). Matches longer than 258 bytes, or reaching into the window, leave the loop and are copied by the per-symbol path, which also handles the ends of the buffers.
