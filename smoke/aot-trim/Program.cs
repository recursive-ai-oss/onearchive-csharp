using System.Security.Cryptography;
using RecursiveAI.OneArchive;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.BZip2;
using RecursiveAI.OneCompress.Brotli;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Deflate64;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;
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

// Compression: one-liner at a negative, a fast and a double-fast level, with a dictionary, and through the compressing stream.
foreach (int level in new[] { -5, 1, 3 })
{
    byte[] again = Zstd.Compress(text, new ZstdCompressionOptions { Level = level, Checksum = true });
    if (!Zstd.Decompress(again).AsSpan().SequenceEqual(text))
    {
        Console.Error.WriteLine($"zstd compress level {level} failed");
        return 1;
    }
}

var smokeDictionary = ZstdCompressionDictionary.FromContent(text, 7);
byte[] withDictionary = Zstd.Compress(text, new ZstdCompressionOptions { Level = 3, Dictionary = smokeDictionary });
if (withDictionary.Length >= 40 || !Zstd.Decompress(withDictionary, new ZstdDecompressionOptions { Dictionary = ZstdDictionary.FromContent(text, 7) }).AsSpan().SequenceEqual(text))
{
    Console.Error.WriteLine("zstd dictionary compress failed");
    return 1;
}

// Dictionary training: small records in, a dictionary that loads in both directions and helps, the same bytes on a second run.
var trainingSamples = new List<ReadOnlyMemory<byte>>();
for (int i = 0; i < 300; i++)
{
    trainingSamples.Add(System.Text.Encoding.ASCII.GetBytes($"{{\"id\":{(i * 7919) % 1000},\"kind\":\"lantern\",\"pos\":[{i % 17},{i % 29}],\"state\":\"{(i % 3 == 0 ? "active" : "idle")}\"}}"));
}

var trainingOptions = new ZstdDictionaryTrainingOptions { DictionarySize = 2048, MaxDegreeOfParallelism = 2 };
byte[] trained = ZstdDictionary.Train(trainingSamples, trainingOptions);
if (!trained.AsSpan().SequenceEqual(ZstdDictionary.Train(trainingSamples, trainingOptions)))
{
    Console.Error.WriteLine("zstd dictionary training is not repeatable");
    return 1;
}

var trainedForCompression = ZstdCompressionDictionary.Parse(trained);
var trainedForDecoding = ZstdDictionary.Parse(trained);
byte[] recordFrame = Zstd.Compress(trainingSamples[5].Span, new ZstdCompressionOptions { Level = 3, Dictionary = trainedForCompression });
if (recordFrame.Length >= Zstd.Compress(trainingSamples[5].Span).Length || !Zstd.Decompress(recordFrame, new ZstdDecompressionOptions { Dictionary = trainedForDecoding }).AsSpan().SequenceEqual(trainingSamples[5].Span))
{
    Console.Error.WriteLine("zstd trained dictionary failed");
    return 1;
}

var written = new MemoryStream();
using (var compressing = new ZstdStream(written, new ZstdCompressionOptions { Level = 1 }, leaveOpen: true))
{
    compressing.Write(text, 0, text.Length);
}

if (!Zstd.Decompress(written.ToArray()).AsSpan().SequenceEqual(text))
{
    Console.Error.WriteLine("zstd compress stream failed");
    return 1;
}

try
{
    Zstd.Decompress(compressed.AsSpan(0, 100));
    return 1;
}
catch (CompressionFormatException e) when (e.IsTruncated)
{
}

// An LZ4 frame (content checksum on) through the one-liner, the stream and the raw block layer; a truncated frame must fail typed.
byte[] lz4Frame = Convert.FromHexString("04224d186440a738000000ff1e54686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e202d00145020646f672e0000000087752026");
string lz4Text = "The quick brown fox jumps over the lazy dog. The quick brown fox jumps over the lazy dog.";
if (System.Text.Encoding.ASCII.GetString(Lz4.Decompress(lz4Frame)) != lz4Text)
{
    Console.Error.WriteLine("lz4 frame failed");
    return 1;
}

using (var lz4Stream = new Lz4Stream(new MemoryStream(lz4Frame)))
{
    var lz4Result = new MemoryStream();
    lz4Stream.CopyTo(lz4Result);
    if (System.Text.Encoding.ASCII.GetString(lz4Result.ToArray()) != lz4Text)
    {
        Console.Error.WriteLine("lz4 stream failed");
        return 1;
    }
}

if (System.Text.Encoding.ASCII.GetString(Lz4Block.Decompress(lz4Frame.AsSpan(11, 0x38))) != lz4Text)
{
    Console.Error.WriteLine("lz4 block failed");
    return 1;
}

try
{
    Lz4.Decompress(lz4Frame.AsSpan(0, 30));
    return 1;
}
catch (CompressionFormatException e) when (e.IsTruncated)
{
}

// LZ4 compression at a fast and a high level: frames through the one-liner, a stream, and a block with a dictionary.
byte[] lz4Input = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(lz4Text + " ", 50)));
foreach (int level in new[] { 1, -3, 9, 12 })
{
    byte[] packed = Lz4.Compress(lz4Input, new Lz4CompressionOptions { Level = level });
    if (packed.Length >= lz4Input.Length / 4 || !Lz4.Decompress(packed).AsSpan().SequenceEqual(lz4Input))
    {
        Console.Error.WriteLine($"lz4 compress failed at level {level}");
        return 1;
    }
}

var lz4Compressed = new MemoryStream();
using (var writer = new Lz4Stream(lz4Compressed, new Lz4CompressionOptions { BlocksIndependent = false, BlockSize = Lz4BlockSize.Size64KiB }, leaveOpen: true))
{
    writer.Write(lz4Input);
}

var lz4Dictionary = new Lz4Dictionary(System.Text.Encoding.ASCII.GetBytes(lz4Text));
byte[] lz4Block = Lz4Block.Compress(lz4Input, new Lz4CompressionOptions { Level = 5, Dictionary = lz4Dictionary });
if (!Lz4.Decompress(lz4Compressed.ToArray()).AsSpan().SequenceEqual(lz4Input)
    || !Lz4Block.Decompress(lz4Block, new Lz4BlockOptions { Dictionary = lz4Dictionary }).AsSpan().SequenceEqual(lz4Input))
{
    Console.Error.WriteLine("lz4 stream or block compress failed");
    return 1;
}

// The same 300 bytes of text as raw Deflate, zlib and gzip (a dynamic-Huffman block): one-liners, the stream, and the span decoder.
byte[] deflateRaw = Convert.FromHexString("458f418ec4300804ef79457f8d78b040434cd6c663f9f7ebcc26da2320aabb34637a47d06e8ce1dd5e08617861e44a0763672ea002a186210e2ea131711a4daeeb8add3cbd611a5cc9206be9e562cc07d0735ecb218bb3f7c04191e4c2543fe7d6e6b1bb2174315e6c410f99cc9056ae571c5e796bfcd3b92406d524fa6168fe86ddf887b70afdb9dc18e1ed79181a72773abd69e8ea791979fe9f353096e86df1fd10bac2daf2fc05");
byte[] deflateZlib = [0x78, 0xDA, .. deflateRaw, 0xFE, 0x9B, 0x6C, 0xE0];
byte[] deflateGzip = [0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 2, 3, .. deflateRaw, 0x44, 0xA6, 0xE7, 0xEA, 0x2C, 1, 0, 0];
foreach (byte[] decoded in new[] { Deflate.Decompress(deflateRaw), ZLib.Decompress(deflateZlib), GZip.Decompress(deflateGzip) })
{
    if (!decoded.AsSpan().SequenceEqual(text))
    {
        Console.Error.WriteLine("deflate one-liner failed");
        return 1;
    }
}

using (var inflate = new DeflateCodecStream(new MemoryStream(deflateGzip), DeflateFormat.GZip))
{
    var inflated = new MemoryStream();
    inflate.CopyTo(inflated);
    if (!inflated.ToArray().AsSpan().SequenceEqual(text))
    {
        Console.Error.WriteLine("deflate stream failed");
        return 1;
    }
}

var inflater = new DeflateDecoder(DeflateFormat.ZLib);
byte[] inflateBuffer = new byte[64];
var inflateResult = new MemoryStream();
for (int i = 0; ;)
{
    var status = inflater.Decompress(deflateZlib.AsSpan(i, Math.Min(7, deflateZlib.Length - i)), inflateBuffer, out int used, out int made);
    i += used;
    inflateResult.Write(inflateBuffer, 0, made);
    if (status == System.Buffers.OperationStatus.Done)
    {
        break;
    }
}

if (!inflateResult.ToArray().AsSpan().SequenceEqual(text))
{
    Console.Error.WriteLine("deflate decoder failed");
    return 1;
}

try
{
    GZip.Decompress(deflateGzip.AsSpan(0, 40));
    return 1;
}
catch (CompressionFormatException e) when (e.IsTruncated)
{
}

// bzip2: "hello hello hello" written by the bzip2 tool, through the one-liner, the stream and the span decoder; truncation is a typed error.
byte[] bzip2Data = Convert.FromBase64String("QlpoMTFBWSZTWZ5iW/4AAAKRAEAAAkSgACEUYGaCke8jRwu5IpwoSE8xLf8A");
if (System.Text.Encoding.ASCII.GetString(BZip2.Decompress(bzip2Data)) != "hello hello hello")
{
    Console.Error.WriteLine("bzip2 failed");
    return 1;
}

using (var bzip2Stream = new BZip2Stream(new MemoryStream(bzip2Data)))
{
    var bzip2Result = new MemoryStream();
    bzip2Stream.CopyTo(bzip2Result);
    var bzip2Decoder = new BZip2Decoder();
    byte[] bzip2Buffer = new byte[64];
    bzip2Decoder.Decompress(bzip2Data, bzip2Buffer, out _, out int bzip2Written);
    if (bzip2Result.Length != 17 || bzip2Written != 17 || BZip2Crc.Compute("123456789"u8) != 0xFC891918u)
    {
        Console.Error.WriteLine("bzip2 stream failed");
        return 1;
    }
}

try
{
    BZip2.Decompress(bzip2Data.AsSpan(0, 20));
    return 1;
}
catch (CompressionFormatException e) when (e.IsTruncated)
{
}

// bzip2 compression: the one-liner, the span encoder and the write-mode stream agree and decode back.
byte[] bzip2Text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("block sorting, move to front, huffman. ", 500)));
byte[] bzip2Packed = BZip2.Compress(bzip2Text, new BZip2Options { Level = 2 });
var bzip2Sink = new MemoryStream();
using (var bzip2Writer = new BZip2Stream(bzip2Sink, new BZip2Options { Level = 2 }, leaveOpen: true))
{
    bzip2Writer.Write(bzip2Text);
}

byte[] bzip2Span = new byte[bzip2Packed.Length + 16];
var bzip2Encoder = new BZip2Encoder(new BZip2Options { Level = 2 });
bzip2Encoder.Compress(bzip2Text, bzip2Span, out _, out int bzip2SpanWritten, isFinalBlock: true);
if (!BZip2.Decompress(bzip2Packed).AsSpan().SequenceEqual(bzip2Text) || !bzip2Sink.ToArray().AsSpan().SequenceEqual(bzip2Packed)
    || !bzip2Span.AsSpan(0, bzip2SpanWritten).SequenceEqual(bzip2Packed))
{
    Console.Error.WriteLine("bzip2 compression failed");
    return 1;
}

// One checksum from each family, so the trimmer keeps (and analyses) the vector paths.
ReadOnlySpan<byte> check = "123456789"u8;
if (Crc32.Compute(check) != 0xCBF43926u || Crc64.Compute(check) != 0x995DC9BBDF1939FAUL || Adler32.Compute(check) != 0x091E01DEu)
{
    Console.Error.WriteLine("checksum smoke failed");
    return 1;
}

// Archives without a file system: tar written and read back, then the Archive layer over .tar.zst in memory,
// sparse-free but with pax fields, a safe-extraction refusal and a limit.
var tarBytes = new MemoryStream();
using (var tarWriter = new RecursiveAI.OneArchive.Tar.TarWriter(tarBytes, leaveOpen: true))
{
    tarWriter.WriteEntry(new ArchiveEntry { Name = new string('n', 150) + "/f.txt", Size = 5, ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(1234567) }, "hello"u8.ToArray());
    tarWriter.WriteEntry(new ArchiveEntry { Name = "../evil", Size = 1 }, "x"u8.ToArray());
}

tarBytes.Position = 0;
using (var tarReader = new RecursiveAI.OneArchive.Tar.TarReader(tarBytes))
{
    var first = tarReader.GetNextEntry();
    if (first is null || first.Name.Length != 156 || first.Size != 5 || first.ModifiedTime!.Value.Ticks % 10_000_000 != 1234567)
    {
        Console.Error.WriteLine("tar entry failed");
        return 1;
    }
}

// cpio and ar written and read back through the Archive layer and the format types.
var cpioBytes = new MemoryStream();
using (var cpioWriter = new RecursiveAI.OneArchive.Cpio.CpioWriter(cpioBytes, leaveOpen: true))
{
    cpioWriter.WriteEntry(new ArchiveEntry { Name = "dir/a.txt", Size = 5, ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000) }, "hello"u8.ToArray());
}

using (var cpioReader = new RecursiveAI.OneArchive.Cpio.CpioReader(new MemoryStream(cpioBytes.ToArray())))
{
    var cpioEntry = cpioReader.GetNextEntry();
    if (cpioEntry is null || cpioEntry.Name != "dir/a.txt" || cpioEntry.Size != 5 || cpioReader.GetNextEntry() is not null || Archive.Open(new MemoryStream(cpioBytes.ToArray())).Format != ArchiveFormat.Cpio)
    {
        Console.Error.WriteLine("cpio round trip failed");
        return 1;
    }
}

var arBytes = new MemoryStream();
using (var arWriter = new RecursiveAI.OneArchive.Ar.ArWriter(arBytes, leaveOpen: true))
{
    arWriter.WriteEntry(new ArchiveEntry { Name = new string('m', 40) + ".o", Size = 3 }, "abc"u8.ToArray());
}

using (var arReader = new RecursiveAI.OneArchive.Ar.ArReader(new MemoryStream(arBytes.ToArray())))
{
    var arEntry = arReader.GetNextEntry();
    if (arEntry is null || arEntry.Name.Length != 42 || arEntry.Size != 3 || arReader.GetNextEntry() is not null || Archive.Open(new MemoryStream(arBytes.ToArray())).Format != ArchiveFormat.Ar)
    {
        Console.Error.WriteLine("ar round trip failed");
        return 1;
    }
}

// A 619-byte cabinet with a stored, an MSZIP and an LZX folder, each holding one 315-byte text file: read through CabReader and Archive.
byte[] cabBytes = Convert.FromHexString("4d534346000000006b020000000000003c000000000000000301030003000000341200008d00000001000000d0010000010001000d0200000100030f3b0100000000000000006958aa6d20006e6f6e655c742e747874003b0100000000000001006958aa6d20006d737a69705c742e747874003b0100000000000002006958aa6d20006c7a785c742e747874000e655d773b013b0154686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e2054686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672e206996c82a35003b01434b0bc94855282ccd4cce56482aca2fcf5348cbaf50c82acd2d2856c82f4b2d5228c94855c849acaa5448c94fd75318558cac18000f6cfa7f56003b010010b01300000000220000553942e99f9d282118fbaa5b5de5f7008000000000001008013e678c15effb000000000000000008113e61effb72a707731ecd7728ab02c5479960d1b143054b40be4271b17fe9dfc000f2");
using (var cabReader = new RecursiveAI.OneArchive.Cab.CabReader(new MemoryStream(cabBytes)))
{
    int cabCount = 0;
    while (cabReader.GetNextEntry() is { } cabEntry)
    {
        using var cabContent = new MemoryStream();
        cabReader.OpenEntryStream().CopyTo(cabContent);
        if (cabEntry.Size != 315 || !cabEntry.Name.EndsWith("/t.txt", StringComparison.Ordinal) || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(cabContent.ToArray())) != "dababee8658b645e01725be3eab108b2f506b7220e19ef399a18a11ae9e78e66")
        {
            Console.Error.WriteLine("cab entry failed: " + cabEntry.Name);
            return 1;
        }

        cabCount++;
    }

    if (cabCount != 3 || Archive.Open(new MemoryStream(cabBytes)).Format != ArchiveFormat.Cab)
    {
        Console.Error.WriteLine("cab read failed");
        return 1;
    }
}

// Deflate64: a level-6 stream of 70,000 bytes with a far repeat, through the one-liners, the streams and the span decoder.
byte[] deflate64Input = new byte[70_000];
new Random(3).NextBytes(deflate64Input.AsSpan(0, 40_000));
deflate64Input.AsSpan(0, 20_000).CopyTo(deflate64Input.AsSpan(50_000));
byte[] deflate64Data = Deflate64.Compress(deflate64Input);
using (var deflate64Stream = new Deflate64Stream(new MemoryStream(deflate64Data)))
{
    var result = new MemoryStream();
    deflate64Stream.CopyTo(result);
    if (!Deflate64.Decompress(deflate64Data).AsSpan().SequenceEqual(deflate64Input) || !result.ToArray().AsSpan().SequenceEqual(deflate64Input))
    {
        Console.Error.WriteLine("deflate64 round trip failed");
        return 1;
    }
}

// 7z written (LZMA2 with an x86 filter, then Deflate non-solid) and read back.
foreach (var sevenZipOptions in new[] { new RecursiveAI.OneArchive.SevenZip.SevenZipWriterOptions { Filter = RecursiveAI.OneArchive.SevenZip.SevenZipFilter.X86 }, new RecursiveAI.OneArchive.SevenZip.SevenZipWriterOptions { Method = RecursiveAI.OneArchive.SevenZip.SevenZipMethod.Deflate, Solid = false } })
{
    var sevenZip = new MemoryStream();
    using (var sevenZipWriter = new RecursiveAI.OneArchive.SevenZip.SevenZipWriter(sevenZip, sevenZipOptions, leaveOpen: true))
    {
        sevenZipWriter.WriteEntry(new ArchiveEntry { Name = "a/b.txt", Size = 11 }, "hello hello"u8.ToArray());
        sevenZipWriter.WriteEntry(new ArchiveEntry { Name = "empty", Size = 0 }, (Stream?)null);
    }

    using var sevenZipReader = new RecursiveAI.OneArchive.SevenZip.SevenZipReader(new MemoryStream(sevenZip.ToArray()));
    using var sevenZipText = new StreamReader(sevenZipReader.OpenEntryStream(sevenZipReader.Items[0]));
    if (sevenZipReader.Items.Count != 2 || sevenZipText.ReadToEnd() != "hello hello")
    {
        Console.Error.WriteLine("7z round trip failed");
        return 1;
    }
}

var zstTar = new MemoryStream();
Archive.Create(zstTar, new ArchiveCreateOptions { Compression = ArchiveCompression.Zstd, LeaveOpen = true })
    .Add(new ArchiveEntry { Name = "a.txt", Size = 3 }, "abc"u8.ToArray()).Save();
using (var opened = Archive.Open(new MemoryStream(zstTar.ToArray())))
{
    using var content = new StreamReader(opened.OpenEntry(opened.Entries[0]));
    if (opened.Compression != ArchiveCompression.Zstd || opened.Entries.Count != 1 || content.ReadToEnd() != "abc")
    {
        Console.Error.WriteLine("archive round trip failed");
        return 1;
    }
}

try
{
    var lz4Tar = new MemoryStream();
    Archive.Create(lz4Tar, new ArchiveCreateOptions { Compression = ArchiveCompression.Lz4, LeaveOpen = true }).Save();
    using var emptyArchive = Archive.Open(new MemoryStream(lz4Tar.ToArray()));
    if (emptyArchive.Entries.Count != 0)
    {
        Console.Error.WriteLine("empty lz4 tar failed");
        return 1;
    }

    using var hostile = Archive.Open(new MemoryStream(tarBytes.ToArray()));
    hostile.Extract(Path.Combine(Path.GetTempPath(), "onearchive-smoke-never"));
    Console.Error.WriteLine("traversal was not refused");
    return 1;
}
catch (ArchiveSafetyException)
{
}
catch (IOException)
{
    // No writable file system (the browser host): the refusal is not reachable there, the stream-only API above is.
}
catch (UnauthorizedAccessException)
{
}
catch (PlatformNotSupportedException)
{
}

// Brotli: a 41-byte stream written by the brotli tool that refers to the static dictionary, through the one-liner, the stream and the span decoder.
byte[] brotliData = Convert.FromBase64String("oTgDACtNVYaWBjbgwOFt8Il72BhbP7dgJV0il0gDpRJ/AKvlmXBJ6GI=");
const string BrotliExpected = "The internet is where people share information about education, development and entertainment worldwide.";
using (var brotliStream = new BrotliStream(new MemoryStream(brotliData)))
{
    var brotliResult = new MemoryStream();
    brotliStream.CopyTo(brotliResult);
    var brotliDecoder = new BrotliDecoder();
    byte[] brotliBuffer = new byte[16];
    var brotliPieces = new MemoryStream();
    int brotliPos = 0;
    while (true)
    {
        var brotliStatus = brotliDecoder.Decompress(brotliData.AsSpan(brotliPos, Math.Min(5, brotliData.Length - brotliPos)), brotliBuffer, out int brotliConsumed, out int brotliWritten);
        brotliPos += brotliConsumed;
        brotliPieces.Write(brotliBuffer, 0, brotliWritten);
        if (brotliStatus == System.Buffers.OperationStatus.Done)
        {
            break;
        }
    }

    if (System.Text.Encoding.ASCII.GetString(Brotli.Decompress(brotliData)) != BrotliExpected
        || System.Text.Encoding.ASCII.GetString(brotliResult.ToArray()) != BrotliExpected
        || System.Text.Encoding.ASCII.GetString(brotliPieces.ToArray()) != BrotliExpected
        || BrotliTransforms.Count != 121 || BrotliWords.GetWord(4, 0).Length != 4)
    {
        Console.Error.WriteLine("brotli failed");
        return 1;
    }

    // The encoder: one-liner at a fast, a middle and the optimal quality, and the stream with a flush.
    byte[] brotliInput = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(BrotliExpected + " ", 40)));
    foreach (int quality in new[] { 1, 5, 11 })
    {
        byte[] packed = Brotli.Compress(brotliInput, new BrotliOptions { Quality = quality, WindowBits = 16 });
        if (!Brotli.Decompress(packed).AsSpan().SequenceEqual(brotliInput))
        {
            Console.Error.WriteLine("brotli encoder round trip failed");
            return 1;
        }
    }

    var brotliPacked = new MemoryStream();
    using (var writer = new BrotliStream(brotliPacked, new BrotliOptions { Quality = 6 }, leaveOpen: true))
    {
        writer.Write(brotliInput, 0, 100);
        writer.Flush();
        writer.Write(brotliInput, 100, brotliInput.Length - 100);
    }

    if (!Brotli.Decompress(brotliPacked.ToArray()).AsSpan().SequenceEqual(brotliInput))
    {
        Console.Error.WriteLine("brotli stream round trip failed");
        return 1;
    }
}

// The LZMA family: each encoder at a fast and a normal preset, read back by this library's decoders (300 bytes of text, repeated).
byte[] lzmaInput = [.. text, .. text, .. text, .. text];
foreach (int preset in new[] { 1, 6 })
{
    var lzmaOptions = new LzmaOptions { Preset = preset };
    byte[] lzma2 = Lzma2.Compress(lzmaInput, out byte lzma2Dictionary, lzmaOptions);
    if (!Lzma.Decompress(Lzma.Compress(lzmaInput, lzmaOptions)).AsSpan().SequenceEqual(lzmaInput)
        || !Lzma2.Decompress(lzma2, lzma2Dictionary).AsSpan().SequenceEqual(lzmaInput)
        || !Xz.Decompress(Xz.Compress(lzmaInput, new XzCompressionOptions { Lzma = lzmaOptions, Check = XzCheck.Sha256 })).AsSpan().SequenceEqual(lzmaInput)
        || !Lzip.Decompress(Lzip.Compress(lzmaInput, new LzipOptions { Level = preset })).AsSpan().SequenceEqual(lzmaInput))
    {
        Console.Error.WriteLine("lzma family round trip failed");
        return 1;
    }
}

// The archive happy path with no configuration: every format and codec works without registering anything.
foreach (var (format, compression) in new[]
{
    (ArchiveFormat.Zip, ArchiveCompression.None), (ArchiveFormat.SevenZip, ArchiveCompression.None), (ArchiveFormat.Tar, ArchiveCompression.Zstd),
    (ArchiveFormat.Tar, ArchiveCompression.Xz), (ArchiveFormat.Tar, ArchiveCompression.Brotli), (ArchiveFormat.Tar, ArchiveCompression.Bzip2),
    (ArchiveFormat.Tar, ArchiveCompression.Gzip), (ArchiveFormat.Tar, ArchiveCompression.Lz4), (ArchiveFormat.Tar, ArchiveCompression.Lzip),
})
{
    var packedArchive = new MemoryStream();
    Archive.Create(packedArchive, new ArchiveCreateOptions { Format = format, Compression = compression, LeaveOpen = true })
        .Add(new ArchiveEntry { Name = "dir/t.txt", Size = lzmaInput.Length }, lzmaInput)
        .Save();
    packedArchive.Position = 0;
    using Archive opened = Archive.Open(packedArchive, new ArchiveOpenOptions { Format = format, Compression = compression, LeaveOpen = true });
    using Stream entryStream = opened.OpenEntry(opened.Entries[0]);
    var back = new MemoryStream();
    entryStream.CopyTo(back);
    if (!back.ToArray().AsSpan().SequenceEqual(lzmaInput))
    {
        Console.Error.WriteLine($"archive round trip failed: {format} {compression}");
        return 1;
    }
}

Console.WriteLine("ok: " + archive);
return 0;
