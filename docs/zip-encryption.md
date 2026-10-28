# Zip encryption

WinZip AES (AE-1 and AE-2) is read and written; traditional PKWARE encryption is read only. PKWARE's strong encryption (bit 6)
and central-directory encryption are not supported: they sit under PKWARE's licence terms. Sources: the WinZip document "AES
Encryption Information: Encryption Specification AE-1 and AE-2" (version 1.04; cited here by its section names) and PKWARE
APPNOTE 6.3.10 section 6.1 (traditional encryption), 4.4.4 (flag bits) and 4.4.5 (method 99).

## Reading

- `ZipEntry.Encryption` (`None`, `ZipCrypto`, `Aes128`, `Aes192`, `Aes256`, `Other`), `AesVersion` (1, 2 or 0), and
  `CompressionMethod` (the method inside the encryption; `Method` stays 99 for AES entries, as the headers say). Listing needs
  no password.
- `ZipReaderOptions.Password` (an `ArchivePassword`: a string, char array or `ReadOnlyMemory<char>` converts to it). Encoded as
  UTF-8, which is what 7-Zip decodes our archives with (it refuses to *create* a zip with a non-ASCII password, so there is no
  reference to compare against for creation).
- Opening an encrypted entry without a password raises `ArchivePasswordException` (`Required`, naming the entry). A wrong
  password raises it with `Incorrect`: AES entries compare the 2-byte verifier (1 in 65536 wrong passwords gets past it and
  then fails the authentication code, which is a `CompressionFormatException`); ZipCrypto entries compare the check byte (1 in
  256 gets past it and then fails the CRC-32 or the decoder). Messages never contain the password.
- A malformed AES field (missing, shorter than 7 bytes, vendor version other than 1 or 2, vendor id other than `AE`, strength
  other than 1 to 3, the real method 99, bit 0 clear) is a `CompressionFormatException` naming what was expected. The local header's
  method, name and bit 0 must agree with the central directory as for any entry.
- **AES data** (WinZip document, "Encrypted file storage format"): `salt | verifier (2) | data | authentication code (10)`; the
  compressed size in the headers covers all four. Salt is 8, 12 or 16 bytes for 128, 192 and 256-bit keys.
  **Key schedule** ("Key generation"): PBKDF2 with HMAC-SHA1, 1000 iterations, over the password and salt, producing the
  encryption key (key size), the authentication key (key size) and the verifier (2 bytes), in that order. **Cipher**: AES in counter
  mode with a 128-bit little-endian counter that starts at 1 and counts whole 16-byte blocks (the last may be partial). **Authentication
  code**: HMAC-SHA1 over the ciphertext with the authentication key, first 10 bytes. The code is compared in constant time.
- **AE-1 and AE-2** differ only in the CRC-32: AE-1 stores and checks it (`VerifyChecksum`), AE-2 stores 0 and the reader does not
  check it. The authentication code is checked in both and always, whatever `VerifyChecksum` says.
- **Streaming.** Decrypted bytes are handed to the caller before the authentication code at the end has been read, as for any
  streamed decryption; a mismatch raises `CompressionFormatException` ("an authentication code that matches the encrypted data") from
  the read that reaches the end of the entry. A modified ciphertext often fails in the decoder first, which is also a
  `CompressionFormatException`.
- **Traditional encryption** (APPNOTE 6.1): three 32-bit keys start at `0x12345678`, `0x23456789`, `0x34567890`, are updated with the
  password's bytes, and then with each decrypted byte (CRC-32 step, multiply by 134775813 plus 1, CRC-32 step); the stream byte is
  `(t * (t ^ 1)) >> 8` with `t = key2 | 2`. The 12-byte header's last decrypted byte must be the high byte of the CRC-32 or, when
  bit 3 is set, the high byte of the DOS time (6.1.6); with bit 3 set either is accepted, since writers differ.
- **Forward-only reads** decrypt entries whose sizes are in the local header. An encrypted entry with its sizes only in a data
  descriptor needs the central directory (the end of its data is not known), and raises `ArchiveUnsupportedException` saying so.
  Info-ZIP's streamed archives and our own descriptor-mode output are of this kind; a seekable stream reads them.
- Encrypted symbolic links are listed with an empty target when the password is missing or wrong.

## Writing

`ZipWriterOptions.Password` non-empty: every file and symbolic link entry, empty files included, is encrypted; directories are
not (the WinZip document recommends both). Defaults: `AesStrength = Aes256`, `AesVersion = Ae2`. ZipCrypto is never written.

- Headers: method 99, bit 0 set, the AES extra field `0x9901` (7 data bytes: version, `AE`, strength, real method) in the local
  header and the central directory, before the caller's own fields. Version-needed and version-made-by are what they would be
  without encryption (the document says so). The compressed size is data plus salt, verifier and 10-byte code. AE-2 writes CRC 0
  in the local header, the descriptor and the central directory.
- Order of work: compress, then encrypt. Data descriptors, patching and zip64 behave as for plain entries; the overhead counts
  in the compressed size.
- **Salt**: `RandomNumberGenerator`, one per entry, or `ZipWriterOptions.RandomBytes(Span<byte>)`, which fills the salt (the span
  is 8, 12 or 16 bytes). The hook exists so tests and the hash matrix can reproduce output; real archives need unpredictable salts.
- Passwords never reach messages, logs or `ToString` (`ArchivePassword.ToString` returns a fixed text). The password is
  copied into a byte array for the key derivation and cleared right after; derived keys are held by the cipher object and
  cleared when it is disposed.

## Platforms

On browser-wasm `System.Security.Cryptography.Aes` throws `PlatformNotSupportedException` (the BCL annotates it
`UnsupportedOSPlatform("browser")`), so AES zips and 7z archives cannot be read or written there. Traditional zip encryption is
plain managed code and works. SHA-256, HMAC-SHA1, PBKDF2 and `RandomNumberGenerator` do work in the browser (hash matrix
entries `encrypt-primitive/*`).
