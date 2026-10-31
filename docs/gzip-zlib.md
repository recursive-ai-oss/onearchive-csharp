# gzip and zlib

The gzip (RFC 1952) and zlib (RFC 1950) wrappers around Deflate (RFC 1951). Detail: `deflate-decoder.md`, `deflate-encoder.md`.

| | |
|---|---|
| Types | `GZip`, `ZLib`, `Deflate` (tier 1); `DeflateEncoder`, `DeflateDecoder` with `DeflateFormat` Raw, ZLib or GZip (tier 2); `DeflateEncoderStream`, `DeflateCodecStream` (streams); `DeflateOptions`, `DeflateDecompressionOptions` |
| Levels | 0 (store), 1 to 9 on the zlib scale, 10 to 12 optimal parsing; default 6 |
| gzip header | name, comment, extra field, header CRC, text flag, modification time and OS byte are options; `GZipHeader` exposes them on read |
| zlib | preset dictionary with FDICT and the dictionary's Adler-32 |
| Decoder | gzip files with several members are read as one stream; the Adler-32 and CRC-32 trailers are verified (`VerifyChecksum`); `MaxOutputSize` 1 GiB; a distance that reaches before the start is an error |
| Output | the same bytes on every platform; levels 1 to 9 are compared with zlib-ng and levels 1 to 12 with libdeflate in `bench-method.md` |

```csharp
byte[] gz = GZip.Compress(data, new DeflateOptions { Level = 9 });
byte[] fromGz = GZip.Decompress(gz);
```
