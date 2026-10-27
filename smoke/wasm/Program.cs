using RecursiveAI.OneArchive;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;

// Runs under Node. The hash-matrix runner will grow out of this host.
string compress = OneCompressInfo.Name;
string archive = OneArchiveInfo.Name;
if (compress != "OneCompress" || !archive.Contains(compress, StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke failed");
    return 1;
}

// Published check values, then vector-sized input against the same input fed in pieces too small for any vector path.
ReadOnlySpan<byte> check = "123456789"u8;
if (Crc32.Compute(check) != 0xCBF43926u || Crc32C.Compute(check) != 0xE3069283u ||
    Crc64.Compute(check) != 0x995DC9BBDF1939FAUL || Adler32.Compute(check) != 0x091E01DEu)
{
    Console.Error.WriteLine("checksum vectors failed");
    return 1;
}

var big = new byte[100_003];
new Random(1).NextBytes(big);
var crc = default(Crc32);
var crcC = default(Crc32C);
var crc64 = default(Crc64);
var adler = default(Adler32);
var xx32 = default(XxHash32);
for (int i = 0; i < big.Length; i += 7)
{
    ReadOnlySpan<byte> piece = big.AsSpan(i, Math.Min(7, big.Length - i));
    crc.Append(piece);
    crcC.Append(piece);
    crc64.Append(piece);
    adler.Append(piece);
    xx32.Append(piece);
}

if (crc.GetCurrentValue() != Crc32.Compute(big) || crcC.GetCurrentValue() != Crc32C.Compute(big) ||
    crc64.GetCurrentValue() != Crc64.Compute(big) || adler.GetCurrentValue() != Adler32.Compute(big) ||
    xx32.GetCurrentValue() != XxHash32.Compute(big))
{
    Console.Error.WriteLine("checksum split mismatch");
    return 1;
}

Console.WriteLine("ok: " + archive);
return 0;
