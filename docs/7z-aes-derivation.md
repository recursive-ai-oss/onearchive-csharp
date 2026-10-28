# 7z AES-256 (method 06 F1 07 01)

`7zFormat.txt` and `Methods.txt` give the method ID and say nothing about its properties, key derivation or padding. Those were
pinned by experiment against 7-Zip 26.00 (`7z`, used as a black box), written down here before the code that uses them.
A value enters the code only through one of the experiments below. Scripts: `testdata/encryption/generate.py` makes the
reference archives; the experiments themselves used a small Python script with `hashlib` and the OpenSSL-backed
`cryptography` package as an independent implementation of the hypothesis.

## What the code does

- **Coder properties** (experiments 1 to 3): byte 0 holds the key-derivation power in bits 0-5, and bits 6 and 7 are the extra
  one of the IV size and salt size. Byte 1 holds the salt size's remaining four bits in its high nibble and the IV size's in its
  low nibble. So `saltSize = bit7 + (b1 >> 4)` and `ivSize = bit6 + (b1 & 15)`. The salt bytes follow, then the IV bytes.
  The reader requires `ivSize = 16` and rejects other sizes with a typed error (7-Zip itself refuses them). A writer uses no
  salt, a 16-byte IV, and so writes `0x40 | power`, `0x0F`, IV.
- **Key** (experiment 1): SHA-256 over 2^power repetitions of `salt || password as UTF-16LE || round counter`, where the
  counter is the round number as an 8-byte little-endian integer starting at 0, all fed to one running hash. The 32-byte digest
  is the AES-256 key. 7-Zip writes power 19. The reader refuses a power above `SevenZipReaderOptions.MaxKeyDerivationPower`
  (default 24) with `CompressionLimitException` before any hashing, since 2^power rounds is the work a hostile archive asks for.
- **Cipher** (experiment 1): AES-256 in CBC mode without padding, with the 16-byte IV, over the whole packed stream. The packed
  size is a multiple of 16; the coder's unpack size (in the folder's unpack-size list) is the true length and the rest is padding.
- **Padding** (experiment 4): the encoder pads the last block with zero bytes.
- **Wrong password**: there is no verifier. A wrong key decrypts to noise, which fails the CRC or the next coder. The reader
  reports that as `ArchivePasswordException` (`Problem = Incorrect`) for folders that have an AES coder, and says in the message
  that 7z cannot tell it from damage.
- **Encrypted header** (experiment 5): the signature header's next-header record is an EncodedHeader (0x17) whose single folder
  is `[AES, LZMA]`: the AES coder first, its output bound to the LZMA coder's input; unpack sizes `[LZMA packed size, header size]`.

## Experiments

All with password `test123` (and, for 6, a Unicode one), source file `a.txt` of 1200 bytes, `7z a -t7z -mx0 -mhe=off`.

1. **Layout and key derivation.** The coder in the archive read `24 06 F1 07 01 12 | 53 0F | 16 bytes`: flag byte, ID, property
   size 18, then `53 0F` and 16 random-looking bytes. Hypothesis: power 0x13 (19) from `0x53 & 0x3F`; 16-byte IV (bit 6 plus nibble 15);
   no salt; key as above. Decrypting the 1200 packed bytes with that key and IV reproduced `a.txt` and its CRC-32. Result: confirmed.
2. **A salt goes before the IV.** An archive was rebuilt in Python with a 4-byte salt `01 02 03 04` (properties: byte 0 `0x40|19`, byte 1
   `(4<<4)|15`, the salt, the IV), the data re-encrypted with the salted key, and both header CRCs recomputed. `7z t -ptest123`:
   `Everything is Ok`; `-pwrong`: `CRC Failed in encrypted file. Wrong password?`. Confirms the nibble layout and the position of the salt.
3. **Extra bits and sizes.** Same construction with a 5-byte salt (flag bit 7 set, nibble 4) and a 16-byte salt (bit 7, nibble 15):
   both `Everything is Ok`. An 8-byte IV (no bit 6, nibble 8, zero-padded to 16 for the cipher): `Unsupported Method`. Powers 10 and 0:
   `Everything is Ok` (the power field means 2^power rounds).
4. **Padding.** A 1001-byte file: packed size 1008, unpack size 1001. Decrypting and looking at bytes 1001-1007: all zero.
5. **Encrypted header.** `-mhe=on` archives open only with the password and decode with the construction above. The reader's
   test vectors in `testdata/encryption/vectors/` come from these runs.
6. **Password encoding.** A password with `ä ö €` and a non-BMP character produced an archive whose key matched UTF-16LE of the
   string (surrogate pairs included) in the key derivation above.

The round trip the other way is part of the test suite: archives from `SevenZipWriter` pass `7z t -p...` and extract byte-exact,
with and without header encryption, and `7z t` with a wrong password fails.

## Limits and behaviour

- A missing password for a folder names the entry (`ArchivePasswordException.EntryName`, `Problem = Required`); for an
  encrypted header the name is empty and the exception comes from the first call that needs the header.
- Listing an archive whose header is not encrypted works without a password. Links in encrypted folders are listed with an
  empty target in that case.
- Memory: one 64 KiB buffer per open decryptor; key material is cleared on dispose where the BCL allows (the key array held by
  the reader's cache is zeroed when the reader is disposed; `Aes` copies the key it is given).
