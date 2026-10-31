# lzip

The lzip container (`.lz`) around LZMA. Source: the lzip manual's file format chapter. Detail: `lzma-decoder.md` and
`lzma-encoder.md`.

| | |
|---|---|
| Types | `Lzip` (tier 1), `LzipEncoder`, `LzipDecoder`, `LzipStream` (tier 2), `LzipOptions`, `LzipDecompressionOptions` |
| Levels | 0 to 9 as the reference tool numbers them; default 6 |
| Members | one member by default; `MemberSize` cuts the input into members of that many uncompressed bytes; the reader accepts any number of members |
| Checks | the CRC-32, data size and member size of every member trailer are verified; `VerifyChecksum` can skip the CRC only |
| Decoder limits | dictionary 4 KiB to 512 MiB by the format and `MaxDictionarySize` (64 MiB) by default; `MaxOutputSize` 1 GiB; version 0 members are rejected as unsupported |

```csharp
byte[] lz = Lzip.Compress(data, new LzipOptions { Level = 9 });
byte[] fromLz = Lzip.Decompress(lz);
```
