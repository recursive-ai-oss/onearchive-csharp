# Choosing codecs for a small binary

`Archive` and the per-format readers and writers reach codecs only through a registry. By default nothing needs
configuring: the first time an archive needs a codec, every codec is registered, so every format and method works, and
the app carries every codec.

An app that needs a small NativeAOT or trimmed binary turns the default off and registers what it uses:

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="RecursiveAI.OneArchive.AllCodecs" Value="false" Trim="true" />
</ItemGroup>
```

```csharp
ArchiveCodecs.AddZstd();   // .zst and .tar.zst; AddLz4, AddDeflate, AddDeflate64, AddBZip2, AddLzma, AddXz,
                           // AddLzip, AddBrotli, AddLzx and AddFilters register the others
```

Without the trimmer the switch can be set before the first archive is touched with
`AppContext.SetSwitch("RecursiveAI.OneArchive.AllCodecs", false)`.

With the default off, an archive that needs a codec the app did not register fails with an `ArchiveUnsupportedException`
naming the format, the method and the `ArchiveCodecs.Add...` call that fixes it. It is never skipped or truncated.

Which call serves what:

| Need | Register |
|---|---|
| `.zst`, `.tar.zst`, zip method 93, 7z Zstandard | `AddZstd` |
| `.lz4`, `.tar.lz4` | `AddLz4` |
| `.gz`, `.tar.gz`, zip method 8 (the zip writer's default), 7z Deflate, cabinet MSZIP | `AddDeflate` |
| zip method 9, 7z Deflate64 | `AddDeflate64` |
| `.bz2`, `.tar.bz2`, zip method 12, 7z BZip2 | `AddBZip2` |
| zip method 14, 7z LZMA and LZMA2, 7z compressed headers | `AddLzma` |
| `.xz`, `.tar.xz`, zip method 95 | `AddXz` |
| `.lz`, `.tar.lz` | `AddLzip` |
| `.br`, `.tar.br` (and brotli's dictionary) | `AddBrotli` |
| cabinet LZX folders | `AddLzx` |
| 7z branch converters and delta | `AddFilters` |

The smoke app `smoke/aot-trim-slim` reads and writes only `.tar.zst` this way; `build/ci.sh aot` publishes it with
NativeAOT and checks that no other codec's types and not brotli's dictionary are in the binary.
