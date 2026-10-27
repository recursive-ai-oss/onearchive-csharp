# Checksums

Normative description of the checksums in `RecursiveAI.OneCompress.Checksums`. A port must reproduce every value from this text alone.

| Type | Width | Definition | Check value of `123456789` | Empty input |
|---|---|---|---|---|
| `Crc32` | 32 | reflected polynomial `0xEDB88320`, init and final xor `0xFFFFFFFF` | `0xCBF43926` | `0` |
| `Crc32C` | 32 | reflected polynomial `0x82F63B78`, init and final xor `0xFFFFFFFF` | `0xE3069283` | `0` |
| `Crc64` | 64 | reflected polynomial `0xC96C5795D7870F42` (ECMA-182 as the xz format uses it), init and final xor all ones | `0x995DC9BBDF1939FA` | `0` |
| `Adler32` | 32 | RFC 1950 section 8.2: sums modulo 65521, initial value 1, second sum in the high half | `0x091E01DE` | `1` |
| `XxHash32` | 32 | xxHash specification, XXH32, 32-bit seed (default 0) | — | `0x02CC5D05` (seed 0) |
| `XxHash64` | 64 | xxHash specification, XXH64, 64-bit seed (default 0) | — | `0xEF46DB3751D8E999` (seed 0) |

## API shape (identical for all six)

- `Compute(data)` returns the value for a buffer. The xxHash types also take `Compute(data, seed)`.
- The CRCs and Adler-32 have `Update(value, data)`, which continues a finished value: `Update(Compute(a), b) == Compute(a ++ b)`. A value is the externally visible one, with the final xor applied, so values written into container formats can be fed straight back in.
- The struct (`Crc32`, …) is the incremental form: `Append(data)`, `GetCurrentValue()` (does not disturb the state), `Reset()`. Any split of the input, including empty pieces, gives the same value. `default(T)` is the empty checksum. `XxHash32(seed)` / `XxHash64(seed)` set the seed; `Reset()` keeps it.
- `Crc32.Combine(crcA, crcB, lengthB)` (also `Crc32C`, `Crc64`) returns the checksum of A followed by B. `lengthB` outside `[0, 2^60]` throws `ArgumentOutOfRangeException`.

## CRC combine

With finished values (init and final xor equal), `crc(A ++ B) = crc(A) * x^(8·len(B)) xor crc(B)` in GF(2)[x] modulo the CRC polynomial. `x^n` is computed by square-and-multiply with the bit-reflected representation (bit `w-1-i` holds the coefficient of `x^i`). Cost is O(log n) multiplications of w iterations each.

## Folding constants

The carry-less-multiply kernels fold 128-bit blocks. For a width-`w` CRC, folding a register forward by `d` bits uses the pair `(x^(d+w-1) mod P, x^(d+w-65) mod P)` (low, high), each in the reflected `w`-bit form. The `-1` compensates for the one-bit offset of a reflected 64×64 carry-less product. Distances used: 1024, 512, 256, 128. When the data runs out, the 16-byte remainder is fed through the table-driven update from state zero, which completes the reduction. Every path (tables, instructions, folding with 128-bit or 256-bit multiplies, 4 or 8 accumulators) computes the same function; only speed differs.

## Adler-32 deferral

Sums are reduced modulo 65521 at least every 5552 bytes (the largest run in which the second sum stays below 2^32 for byte input).
