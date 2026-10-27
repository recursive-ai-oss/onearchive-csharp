using System.Security.Cryptography;
using RecursiveAI.OneArchive;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Zstd;
using RecursiveAI.OneCompress.Checksums;

// Calls public APIs from each package so the trimmer and the AOT compiler analyse them.
string compress = OneCompressInfo.Name;
string archive = OneArchiveInfo.Name;
if (compress != "OneCompress" || !archive.Contains(compress, StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke failed");
    return 1;
}

// A Zstandard frame with a raw block, and one with compressed blocks (300 bytes of text), through the one-liner,
// the decoder and the stream.
byte[] raw = [0x28, 0xB5, 0x2F, 0xFD, 0x20, 0x05, 0x29, 0x00, 0x00, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'];
byte[] compressed = Convert.FromHexString("28b52ffd642c00650500720c2015a0a56dc0be97b4b7bdf657292d9facaaaad1af9e2b387cce1f949d2abc5734f5f68e12b65708b78f72fee08c98f2491e097e841b1d6cd459c488558b0738406e8d3a86748444a831b23578d170de0a9b978252aacdcd58e67be800511cfeb0792a249cf6366fb5c2a7cc59ce7ff84399e2a7fab60620d2c2c7516ba4c9e5020f005505481a52661657bff18b7184ea1b9239f0348753bc10c381c1219e2ea85122301174d611843341934ec6");
if (System.Text.Encoding.ASCII.GetString(Zstd.Decompress(raw)) != "hello")
{
    Console.Error.WriteLine("zstd raw frame failed");
    return 1;
}

byte[] text = Zstd.Decompress(compressed);
if (text.Length != 300 || Convert.ToHexStringLower(SHA256.HashData(text)) != "64072fa480af494b1d73e0c2907148f34fa2f1fc029b9456e838235826bc52ef")
{
    Console.Error.WriteLine("zstd compressed frame failed");
    return 1;
}

using (var stream = new ZstdStream(new MemoryStream(compressed)))
{
    var result = new MemoryStream();
    stream.CopyTo(result);
    if (!result.ToArray().AsSpan().SequenceEqual(text))
    {
        Console.Error.WriteLine("zstd stream failed");
        return 1;
    }
}

try
{
    Zstd.Decompress(compressed.AsSpan(0, 100));
    return 1;
}
catch (CompressionFormatException e) when (e.IsTruncated)
{
}

// One checksum from each family, so the trimmer keeps (and analyses) the vector paths.
ReadOnlySpan<byte> check = "123456789"u8;
if (Crc32.Compute(check) != 0xCBF43926u || Crc64.Compute(check) != 0x995DC9BBDF1939FAUL || Adler32.Compute(check) != 0x091E01DEu)
{
    Console.Error.WriteLine("checksum smoke failed");
    return 1;
}

Console.WriteLine("ok: " + archive);
return 0;
