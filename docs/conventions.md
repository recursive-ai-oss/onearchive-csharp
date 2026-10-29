# Conventions for codecs

How every OneCompress codec is shaped, so the codecs look alike and a port can follow them. The Zstandard
decoder (`src/OneCompress/Zstd/`) is the reference implementation of each point.

## Namespaces and tiers

- One namespace per codec: `RecursiveAI.OneCompress.<Codec>`. Shared types (the exceptions below) live in
  `RecursiveAI.OneCompress`.
- Three tiers, each built only on the one below:
  1. **One-liners**: a static class named for the codec (`Zstd.Decompress(bytes, options)`).
  2. **Decoder / encoder / stream**: `XxxDecoder`, `XxxEncoder` (sealed classes that own their buffers and tables and
     reuse them) and `XxxStream`.
  3. **Raw parts**, public and documented: bit readers, entropy tables, sequence decoders, dictionaries.
- Inside a namespace named like the codec, the static class has the same name (`RecursiveAI.OneCompress.Zstd.Zstd`).
  Code that lives under `RecursiveAI.OneCompress` therefore writes `Zstd.Zstd.Decompress`; code elsewhere writes
  `Zstd.Decompress` after `using RecursiveAI.OneCompress.Zstd;`.

## Errors

All errors a codec raises on its input derive from `CompressionException`:

| Type | Raised when | Properties |
|---|---|---|
| `CompressionFormatException` | the data is not valid for the format: truncated, corrupt, a forbidden value, a checksum mismatch | `Format`, `Offset` (byte offset in the compressed input), `Expected` (what the format requires there), `IsTruncated` |
| `CompressionLimitException` | a configured limit is exceeded (window size, output size, entry size...). Raised **before** the memory is allocated | `Format`, `LimitName` (the option's name), `Limit`, `Requested` |
| `CompressionDictionaryException` | a frame names a dictionary that was not supplied | `Format`, `DictionaryId` |

Rules:

- Messages read `<format>: invalid data at byte offset <n>: expected <what>`. `Expected` is a phrase that completes
  "expected ...", naming the value the format allows, not the value found.
- `Offset` counts bytes of the compressed input from the start of what the caller gave the decoder (across calls for a
  streaming decoder). For a problem inside a block, it is the offset of the block or of the field being read.
- A decoder never lets `IndexOutOfRangeException`, `NullReferenceException`, `OverflowException` or
  `OutOfMemoryException` (from sizes the input chose) escape for malformed input. Argument errors
  (`ArgumentException` and subclasses), `OperationCanceledException` and I/O errors from the caller's own streams are
  the only other exceptions.
- Truncated input is reported with `IsTruncated = true` and the offset where the input ended.
- Never return a truncated result as a success: a decoder that cannot finish throws.

## Options

- Every tunable is a property on a sealed options class: `ZstdDecompressionOptions`, `ZstdCompressionOptions`.
  No tunable is an overload or an extra parameter.
- Properties are `init`-only, with the default in the property's XML doc and as a `Default...` constant when it is a
  number. The class has a shared `Default` instance. Options are read once, when a decoder or stream is constructed.
- Limit properties are named `Max<Thing>` (`MaxWindowSize`, `MaxOutputSize`). They are checked against the sizes the
  input *declares* before allocating, and against the real sizes as output is produced. A hit throws
  `CompressionLimitException` with `LimitName` set to the property name.
- Safe defaults: memory a decoder may reserve is bounded by the declared window and a configured maximum
  (8 MiB for Zstandard); total output is bounded (1 GiB). Callers raise them knowingly.

## Tier 2: the decoder shape

```csharp
public sealed class XxxDecoder
{
    public XxxDecoder(XxxDecompressionOptions? options = null);
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written);
    public bool IsAtFrameBoundary { get; }
    public void ThrowIfIncomplete();
    public void Reset();
}
```

- `Decompress` returns `System.Buffers.OperationStatus`: `Done` (a unit of the format, such as one frame, is complete and
  fully written), `DestinationTooSmall` (output is waiting), `NeedMoreData` (all input used, unit incomplete).
  It never returns `InvalidData`: malformed input throws, so a corrupt stream is never mistaken for a short one.
- Callers pass the bytes not consumed by earlier calls, plus any new ones. `consumed` and `written` are always set,
  also when the call throws. Any split of input and output must give the same result as no split.
- `ThrowIfIncomplete` is how a caller says "no more input will come": it throws a truncated `CompressionFormatException`
  when the decoder stopped inside a unit.
- After an exception the decoder refuses further calls (`InvalidOperationException`) until `Reset()`.
- A decoder instance is not thread-safe; immutable parts such as dictionaries are, and can be shared.
- Steady state allocates nothing: buffers are created on first need, grow only, and are reused across units.
  A test with `GC.GetAllocatedBytesForCurrentThread` guards this.
- Where the caller's buffer can hold the whole output of a unit that states its size, the decoder writes into it
  directly instead of through its own window.

## Streams

- `XxxStream : Stream`, sync and async paths, `CancellationToken` on `ReadAsync`/`WriteAsync`.
- Input is read into a pooled buffer. `leaveOpen` as in the BCL. End of data is reported only at a unit boundary;
  an inner stream that ends early throws the truncated format error.

## Hot paths and `Unsafe`

- Write loops on spans first. `Unsafe`/`MemoryMarshal` appear only behind small internal accessors whose bounds hold
  by construction, each with a comment stating the bound; in the `Checked` build configuration they are bounds-checked.
- No `Vector<T>`; fixed-width `Vector128/256/512` only, with a scalar path giving the same result.

## Tests and data

- Unit tests live in `tests/OneCompress.Tests/<Codec>/`. Test vectors written by reference tools are generated by a
  script in `testdata/<codec>/` (the tool and its version are recorded in the manifest) and committed when small.
- Tests that need the reference tool or large corpora are tagged `[Trait("Category", "Interop")]` and run only with
  `ONEARCHIVE_INTEROP=1`; with it set, a missing tool or corpus fails the run.
- Every decoder has hostile-input tests: every prefix of a frame, bit flips, and each limit.
- Hash-matrix entries for a codec live in `tools/hashmatrix/Host/Entries/<Codec>*.cs`.
