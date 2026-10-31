using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Buffers;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cab;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Tests.Cab;
using RecursiveAI.OneCompress.Brotli;
using RecursiveAI.OneCompress.BZip2;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Deflate64;
using RecursiveAI.OneCompress.Ppmd;
using RecursiveAI.OneCompress.Tests.Lzx;

// Bar04Bench <mode> ... [--reps N] [--out <file>]
//   brotli-dec <file.br>              decode into a preallocated buffer; prints the best, median and slowest rate in MB/s
//   brotli-enc <file> <quality>       compress (window 22), untimed unless --reps > 0; --out writes the stream (always checked)
//   bz2-enc <file> <level>            compress in memory with one call; prints size and rates; --out writes the stream (checked)
//   d64-enc <file> <level>            Deflate64 compress in memory with one call; --out writes the raw stream (checked)
//   d64-dec <raw>                     Deflate64 decode of a raw stream (the payload of a zip entry); needs --size <uncompressed bytes>
//   d64-zip <file> <level> --out <z>  writes a one-entry zip (method 9) around our Deflate64 stream, for 7-Zip to read
//   d64-payload <zip> --out <raw>     extracts the raw stream of a one-entry zip's entry; prints method, sizes and CRC-32
//   ppmd-enc <file> <level>           PPMd variant H compress in memory with one call, at the order and memory 7-Zip uses for -mx<level> (docs/ppmd.md); --out writes the raw stream (checked)
//   ppmd-dec <raw> <level>            PPMd H decode of a raw stream written at that level; needs --size <uncompressed bytes>;
//                                     --order N --memlog M (model memory 2^M MiB) replace the level's parameters
//   ppmd-7z <file> <level> --out <z>  writes a one-entry 7z (PPMD coder) with our writer at that level's order and memory, for 7-Zip to read
//   lzx-mkcab <file> --out <cab>      builds a one-file cabinet with an LZX folder (window 2^21) using the test compressor
//   lzx-dec <cab>                     reads the cabinet's file through CabReader into a null sink; prints rates
// Timing: one warm-up pass (kept as a rep when it takes 8 s or more), then --reps passes; rates are in MB of uncompressed data.
static int Usage()
{
    Console.Error.WriteLine("usage: Bar04Bench brotli-dec|brotli-enc|bz2-enc|d64-enc|d64-dec|d64-zip|d64-payload|ppmd-enc|ppmd-dec|ppmd-7z|lzx-mkcab|lzx-dec <file> [level] [--reps N] [--out f] [--size n]");
    return 2;
}

if (args.Length < 2)
{
    return Usage();
}

string mode = args[0];
string input = args[1];
int reps = 5;
long size = 0;
string? outPath = null;
string? expectPath = null;
int level = 0;
int ppmdOrder = 0;
int ppmdMemLog = 0;
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--reps": reps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--out": outPath = args[++i]; break;
        case "--expect": expectPath = args[++i]; break;
        case "--order": ppmdOrder = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--memlog": ppmdMemLog = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--size": size = long.Parse(args[++i], CultureInfo.InvariantCulture); break;
        default: level = int.Parse(args[i], CultureInfo.InvariantCulture); break;
    }
}

switch (mode)
{
    case "brotli-dec": return Decode(File.ReadAllBytes(input), BrotliDecode);
    case "brotli-enc":
        return Encode(File.ReadAllBytes(input), (d, b) => Brotli.TryCompress(d, b, out int n, new BrotliOptions { Quality = level, WindowBits = 22 }) ? n : -1,
            (p) => Brotli.Decompress(p), reps, outPath);
    case "bz2-enc":
        return Encode(File.ReadAllBytes(input), (d, b) => BZip2.TryCompress(d, b, out int n, new BZip2Options { Level = level }) ? n : -1,
            (p) => BZip2.Decompress(p), reps, outPath);
    case "d64-enc":
        return Encode(File.ReadAllBytes(input), (d, b) => Deflate64.TryCompress(d, b, out int n, new Deflate64Options { Level = level }) ? n : -1,
            (p) => Deflate64.Decompress(p), reps, outPath);
    case "d64-dec":
        return Decode(File.ReadAllBytes(input), (p, b) => Deflate64.TryDecompress(p, b, out int n) ? n : -1, size);
    case "d64-zip": return D64Zip(File.ReadAllBytes(input), level, outPath);
    case "d64-payload": return D64Payload(File.ReadAllBytes(input), outPath);
    case "ppmd-enc":
    {
        // 7z data has no end marker, so the decoder is told the size.
        byte[] original = File.ReadAllBytes(input);
        return Encode(original, (d, b) => PpmdH.TryCompress(d, b, out int n, PpmdLevel(level)) ? n : -1,
            (p) => PpmdH.Decompress(p, PpmdDecodeOptions(PpmdParams(level), original.Length)), reps, outPath);
    }
    case "ppmd-dec":
        return Decode(File.ReadAllBytes(input), (p, b) => PpmdDecode(p, b, ppmdOrder > 0 ? (ppmdOrder, 1 << ppmdMemLog) : PpmdParams(level)), size);
    case "ppmd-7z": return Ppmd7z(File.ReadAllBytes(input), level, outPath);
    case "lzx-mkcab": return LzxMakeCab(File.ReadAllBytes(input), outPath);
    case "lzx-dec": return LzxDecode(File.ReadAllBytes(input), reps);
    default: return Usage();
}

// docs/ppmd.md "Levels": the order and model memory 7-Zip uses for -mx in a 7z archive on a large input. Levels above 9 behave as 9.
static (int Order, int MemoryMiB) PpmdParams(int level) => Math.Clamp(level, 1, 9) switch
{
    1 => (4, 1), 2 => (4, 2), 3 => (5, 4), 4 => (5, 8), 5 => (6, 16), 6 => (8, 32), 7 => (16, 64), 8 => (24, 128), _ => (32, 256),
};

static PpmdHOptions PpmdLevel(int level)
{
    var (order, mib) = PpmdParams(level);
    return new PpmdHOptions { Order = order, MemorySize = mib << 20 };
}

static PpmdHDecompressionOptions PpmdDecodeOptions((int Order, int MemoryMiB) p, long outputSize) =>
    new() { Order = p.Order, MemorySize = p.MemoryMiB << 20, OutputSize = outputSize };

// One decoder per call, so the model allocation is inside the timing, as it is for the tool's process.
static int PpmdDecode(byte[] packed, byte[] buffer, (int Order, int MemoryMiB) p)
{
    var decoder = new PpmdHDecoder(PpmdDecodeOptions(p, buffer.Length));
    OperationStatus status = decoder.Decompress(packed, buffer, out _, out int written, isFinalBlock: true);
    return status == OperationStatus.Done ? written : -1;
}

static int Ppmd7z(byte[] data, int level, string? path)
{
    if (path is null)
    {
        return Usage();
    }

    using var file = File.Create(path);
    using (var writer = new SevenZipWriter(file, new SevenZipWriterOptions { Method = SevenZipMethod.Ppmd, Ppmd = PpmdLevel(level) }, leaveOpen: true))
    {
        writer.WriteEntry(new ArchiveEntry { Name = "data", Size = data.Length, ModifiedTime = DateTimeOffset.UnixEpoch }, data);
        writer.Finish();
    }

    return 0;
}

static int BrotliDecode(byte[] packed, byte[] buffer) => Brotli.TryDecompress(packed, buffer, out int n) ? n : -1;

// Times a decode into a preallocated buffer. The size is learnt from one untimed decode unless it is given.
int Decode(byte[] packed, Func<byte[], byte[], int> decode, long knownSize = 0)
{
    long outSize = knownSize;
    if (outSize <= 0)
    {
        outSize = Brotli.Decompress(packed).Length;
    }

    byte[] buffer = new byte[outSize];
    var times = new List<double>();
    int n = 0;
    for (int i = 0; i <= reps; i++)
    {
        long t = Stopwatch.GetTimestamp();
        n = decode(packed, buffer);
        double s = Stopwatch.GetElapsedTime(t).TotalSeconds;
        if (n != outSize)
        {
            throw new InvalidOperationException($"decode failed: got {n}, expected {outSize}");
        }

        if (i > 0 || s >= 8 || reps == 0)
        {
            times.Add(s);
        }
    }

    if (expectPath is not null && !buffer.AsSpan().SequenceEqual(File.ReadAllBytes(expectPath)))
    {
        Console.Error.WriteLine("DECODED BYTES DIFFER FROM --expect");
        return 1;
    }

    times.Sort();
    Console.WriteLine($"dec out={n} dec_MBps={n / 1e6 / times[0]:F1} dec_MBps_med={n / 1e6 / times[times.Count / 2]:F1} dec_MBps_min={n / 1e6 / times[^1]:F1}");
    return 0;
}

int Encode(byte[] data, Func<byte[], byte[], int> compress, Func<byte[], byte[]> decompress, int repeats, string? path)
{
    byte[] buffer = new byte[data.Length + (data.Length >> 2) + 65536];
    var times = new List<double>();
    int compressed = 0;
    for (int i = 0; i <= repeats; i++)
    {
        long t = Stopwatch.GetTimestamp();
        compressed = compress(data, buffer);
        double s = Stopwatch.GetElapsedTime(t).TotalSeconds;
        if (compressed < 0)
        {
            throw new InvalidOperationException("output buffer too small");
        }

        if (i > 0 || s >= 8 || repeats == 0)
        {
            times.Add(s);
        }

        if (i == 0 && s >= 8 && repeats > 0)
        {
            repeats--;
        }
    }

    if (path is not null)
    {
        byte[] packed = buffer.AsSpan(0, compressed).ToArray();
        File.WriteAllBytes(path, packed);
        if (!decompress(packed).AsSpan().SequenceEqual(data))
        {
            Console.Error.WriteLine("ROUND TRIP MISMATCH");
            return 1;
        }
    }

    times.Sort();
    double mb = data.Length / 1e6;
    Console.WriteLine($"enc size={compressed} comp_MBps={mb / times[0]:F2} comp_MBps_med={mb / times[times.Count / 2]:F2} comp_MBps_min={mb / times[^1]:F2}");
    return 0;
}

static int D64Zip(byte[] data, int level, string? path)
{
    if (path is null)
    {
        return Usage();
    }

    byte[] packed = Deflate64.Compress(data, new Deflate64Options { Level = level });
    uint crc = Crc32.Compute(data);
    byte[] name = "data"u8.ToArray();
    using var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    // APPNOTE 4.3.7 local header, 4.3.12 central directory header, 4.3.16 end of central directory.
    w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)9); w.Write((ushort)0); w.Write((ushort)0x21);
    w.Write(crc); w.Write((uint)packed.Length); w.Write((uint)data.Length); w.Write((ushort)name.Length); w.Write((ushort)0);
    w.Write(name); w.Write(packed);
    long cd = ms.Position;
    w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)9); w.Write((ushort)0); w.Write((ushort)0x21);
    w.Write(crc); w.Write((uint)packed.Length); w.Write((uint)data.Length); w.Write((ushort)name.Length);
    w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(0u); w.Write(name);
    long cdSize = ms.Position - cd;
    w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
    w.Write((uint)cdSize); w.Write((uint)cd); w.Write((ushort)0);
    File.WriteAllBytes(path, ms.ToArray());
    Console.WriteLine($"zip size={ms.Length} payload={packed.Length}");
    return 0;
}

static int D64Payload(byte[] zip, string? path)
{
    // The central directory of a one-entry archive without a comment: the end record is the last 22 bytes.
    int eocd = zip.Length - 22;
    if (eocd < 0 || BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd)) != 0x06054b50u)
    {
        throw new InvalidOperationException("no end-of-central-directory record in the last 22 bytes");
    }

    int cd = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(eocd + 16));
    ushort method = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cd + 10));
    uint crc = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cd + 16));
    uint csize = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cd + 20));
    uint usize = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cd + 24));
    int local = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cd + 42));
    int start = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 26)) + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 28));
    if (csize == 0xFFFFFFFF || usize == 0xFFFFFFFF)
    {
        throw new InvalidOperationException("zip64 entry; the bar inputs are below 4 GiB");
    }

    if (path is not null)
    {
        File.WriteAllBytes(path, zip.AsSpan(start, (int)csize).ToArray());
    }

    Console.WriteLine($"method={method} csize={csize} usize={usize} crc={crc:x8}");
    return 0;
}

static int LzxMakeCab(byte[] data, string? path)
{
    if (path is null)
    {
        return Usage();
    }

    var builder = new CabTestBuilder();
    var folder = new CabTestBuilder.FolderSpec
    {
        Method = CabTestBuilder.Method.Lzx,
        WindowBits = 21,
        Lzx = new LzxTestEncoder.Settings { WindowBits = 21, BlockSize = 1 << 18 },
    };
    folder.Files.Add(new CabTestBuilder.FileSpec { Name = "data", Data = data });
    builder.Folders.Add(folder);
    byte[] cab = builder.Build();
    File.WriteAllBytes(path, cab);
    Console.WriteLine($"cab size={cab.Length} data={data.Length}");
    return 0;
}

int LzxDecode(byte[] cab, int repeats)
{
    var times = new List<double>();
    long total = 0;
    for (int i = 0; i <= repeats; i++)
    {
        long t = Stopwatch.GetTimestamp();
        total = 0;
        using var reader = new CabReader(new MemoryStream(cab, writable: false), new CabReaderOptions { VerifyChecksum = true });
        CabEntry? entry;
        byte[] chunk = new byte[1 << 16];
        while ((entry = reader.GetNextEntry()) is not null)
        {
            using Stream s = reader.OpenEntryStream();
            int n;
            while ((n = s.Read(chunk)) > 0)
            {
                total += n;
            }
        }

        double sec = Stopwatch.GetElapsedTime(t).TotalSeconds;
        if (i > 0 || sec >= 8)
        {
            times.Add(sec);
        }
    }

    if (expectPath is not null)
    {
        // One untimed pass that compares the cabinet's bytes with the original file.
        byte[] expected = File.ReadAllBytes(expectPath);
        using var check = new CabReader(new MemoryStream(cab, writable: false));
        _ = check.GetNextEntry() ?? throw new InvalidOperationException("cabinet has no entry");
        byte[] got = new byte[expected.Length];
        using Stream cs = check.OpenEntryStream();
        cs.ReadExactly(got);
        if (cs.Read(new byte[1]) != 0 || !got.AsSpan().SequenceEqual(expected))
        {
            Console.Error.WriteLine("DECODED BYTES DIFFER FROM --expect");
            return 1;
        }
    }

    times.Sort();
    Console.WriteLine($"dec out={total} dec_MBps={total / 1e6 / times[0]:F1} dec_MBps_med={total / 1e6 / times[times.Count / 2]:F1} dec_MBps_min={total / 1e6 / times[^1]:F1}");
    return 0;
}
