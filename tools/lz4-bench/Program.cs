using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Lz4;

// Times the LZ4 encoder, memory to memory and warm.
//
//   Lz4Bench big <file> <level> [--seconds 4] [--block 4194304] [--out frame.lz4]
//       Compresses the file as independent blocks (the layout `lz4 -b` times) with one reused compressor and prints the best
//       compression speed, the size of the blocks, the size of the equivalent frame (written by Lz4.Compress) and the speed
//       of decoding the blocks with our decoder. --out writes the frame so the reference tool can decode it.
//   Lz4Bench small <dictionary|-> <dir> <level> [--seconds 4]
//       Compresses every file in the directory as its own frame with one reused encoder (with the dictionary, if given) and
//       prints payloads per second, MB/s of input, and the total sizes.
//
// Compare with `lz4 -b<level> -i<seconds> file` and `lz4 -b<level> -i<seconds> -D dict files...`.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Lz4Bench big <file> <level> | small <dictionary|-> <dir> <level>");
    return 2;
}

double seconds = 4;
int blockSize = 4 << 20;
string? outPath = null;
for (int i = 2; i + 1 < args.Length; i++)
{
    switch (args[i])
    {
        case "--seconds":
            seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--block":
            blockSize = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--out":
            outPath = args[i + 1];
            break;
    }
}

double Best(Action run)
{
    run();
    double best = double.MaxValue;
    var total = Stopwatch.StartNew();
    int runs = 0;
    while (total.Elapsed.TotalSeconds < seconds || runs < 3)
    {
        long t = Stopwatch.GetTimestamp();
        run();
        best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
        runs++;
    }

    return best;
}

if (args[0] == "dbg")
{
    byte[] dict = File.ReadAllBytes(args[1]);
    byte[] data = File.ReadAllBytes(args[2]);
    var d = new Lz4Dictionary(dict);
    for (int level = 2; level <= 12; level++)
    {
        byte[] block = Lz4Block.Compress(data, new Lz4CompressionOptions { Level = level, Dictionary = d });
        File.WriteAllBytes(args[2] + "." + level + ".blk", block);
        try
        {
            byte[] back = Lz4Block.Decompress(block, new Lz4BlockOptions { Dictionary = d });
            Console.WriteLine($"level {level} ok={back.AsSpan().SequenceEqual(data)} {block.Length}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"level {level} FAIL {e.Message}");
        }
    }

    return 0;
}

if (args[0] == "sweep")
{
    // sweep <files> <tune,tune,...>: per tune string (see the encoder's tuning hook) the frame size and speed of each file.
    foreach (string tune in args[2].Split(','))
    {
        Environment.SetEnvironmentVariable("LZ4HC_TUNE", tune);
        int lv = int.Parse(tune.Split(':')[0], CultureInfo.InvariantCulture);
        foreach (string file in args[1].Split(','))
        {
            byte[] bytes = File.ReadAllBytes(file);
            var sw = Stopwatch.StartNew();
            int n = Lz4.Compress(bytes, new Lz4CompressionOptions { Level = lv, ContentChecksum = false, IncludeContentSize = false }).Length;
            Console.WriteLine($"{tune}\t{Path.GetFileName(file)}\t{n}\t{bytes.Length / sw.Elapsed.TotalSeconds / 1e6:F1}");
        }
    }

    return 0;
}

if (args[0] == "size")
{
    // Sizes only, for ratio tuning: frame size of each file at each level in args[2..], one line per pair.
    foreach (string file in args[1].Split(','))
    {
        byte[] bytes = File.ReadAllBytes(file);
        foreach (string l in args[2].Split(','))
        {
            int lv = int.Parse(l, CultureInfo.InvariantCulture);
            var sw = Stopwatch.StartNew();
            int n = Lz4.Compress(bytes, new Lz4CompressionOptions { Level = lv, ContentChecksum = false, IncludeContentSize = false }).Length;
            Console.WriteLine($"{Path.GetFileName(file)}\t{lv}\t{n}\t{bytes.Length / sw.Elapsed.TotalSeconds / 1e6:F1}");
        }
    }

    return 0;
}

if (args[0] == "enc")
{
    // enc <file> <level,level,...> [--seconds s] [--block n]: compressed size, best compression speed and a hash of the output
    // blocks per level, for comparing two builds of the encoder (equal hashes mean equal bytes).
    byte[] data = File.ReadAllBytes(args[1]);
    foreach (string l in args[2].Split(','))
    {
        int level = int.Parse(l, CultureInfo.InvariantCulture);
        Lz4BlockCompressor c = Lz4BlockCompressor.Create(level);
        byte[] scratch = new byte[Lz4BlockWriter.GetMaxCompressedLength(blockSize)];
        long total = 0;
        double t = Best(() =>
        {
            total = 0;
            for (int at = 0; at < data.Length; at += blockSize)
            {
                total += c.Compress(data.AsSpan(at, Math.Min(blockSize, data.Length - at)), 0, scratch);
            }
        });
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        c.Reset();
        for (int at = 0; at < data.Length; at += blockSize)
        {
            int n = c.Compress(data.AsSpan(at, Math.Min(blockSize, data.Length - at)), 0, scratch);
            sha.AppendData(scratch.AsSpan(0, n));
        }

        Console.WriteLine($"{level}\t{total}\t{data.Length / t / 1e6:F1}\t{Convert.ToHexString(sha.GetHashAndReset())[..12]}");
    }

    return 0;
}

if (args[0] == "big")
{
    byte[] data = File.ReadAllBytes(args[1]);
    int level = int.Parse(args[2], CultureInfo.InvariantCulture);
    Lz4BlockCompressor compressor = Lz4BlockCompressor.Create(level);
    byte[] scratch = new byte[Lz4BlockWriter.GetMaxCompressedLength(blockSize)];
    int blocks = (data.Length + blockSize - 1) / blockSize;
    byte[] packed = new byte[blocks * (scratch.Length + 4)];
    int[] sizes = new int[blocks];

    long CompressAll()
    {
        long total = 0;
        for (int b = 0; b < blocks; b++)
        {
            int at = b * blockSize;
            ReadOnlySpan<byte> block = data.AsSpan(at, Math.Min(blockSize, data.Length - at));
            int n = compressor.Compress(block, 0, scratch);
            if (n >= block.Length)
            {
                n = block.Length;
            }

            sizes[b] = n;
            total += n;
        }

        return total;
    }

    long blockBytes = 0;
    double compress = Best(() => blockBytes = CompressAll());

    // Decode the blocks as the decoder bench does: each into its place in one output buffer.
    var blobs = new List<byte[]>();
    for (int b = 0; b < blocks; b++)
    {
        int at = b * blockSize;
        ReadOnlySpan<byte> block = data.AsSpan(at, Math.Min(blockSize, data.Length - at));
        blobs.Add(Lz4Block.Compress(block, new Lz4CompressionOptions { Level = level }));
    }

    byte[] whole = new byte[data.Length];
    double decode = Best(() =>
    {
        int at = 0;
        foreach (byte[] blob in blobs)
        {
            if (!Lz4BlockDecoder.TryDecode(blob, whole.AsSpan(at), 0, out int w))
            {
                throw new InvalidOperationException("decode failed");
            }

            at += w;
        }
    });
    if (!whole.AsSpan().SequenceEqual(data))
    {
        Console.Error.WriteLine("ROUND TRIP MISMATCH");
        return 1;
    }

    byte[] frame = Lz4.Compress(data, new Lz4CompressionOptions { Level = level, BlockSize = blockSize switch { 65536 => Lz4BlockSize.Size64KiB, 262144 => Lz4BlockSize.Size256KiB, 1048576 => Lz4BlockSize.Size1MiB, _ => Lz4BlockSize.Size4MiB }, IncludeContentSize = false });
    if (outPath is not null)
    {
        File.WriteAllBytes(outPath, frame);
    }

    Console.WriteLine($"big\t{Path.GetFileName(args[1])}\tlevel={level}\tsize={data.Length}\tblocks={blockBytes}\tframe={frame.Length}\tcompress={data.Length / compress / 1e6:F1} MB/s\tdecode={data.Length / decode / 1e6:F0} MB/s");
    return 0;
}

{
    Lz4Dictionary? dictionary = args[1] == "-" ? null : new Lz4Dictionary(File.ReadAllBytes(args[1]));
    int level = int.Parse(args[3], CultureInfo.InvariantCulture);
    var payloads = Directory.GetFiles(args[2]).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
    var options = new Lz4CompressionOptions { Level = level, Dictionary = dictionary, ContentChecksum = false, IncludeContentSize = false };
    var encoder = new Lz4Encoder(options);
    var blockOptions = new Lz4CompressionOptions { Level = level, Dictionary = dictionary };
    Lz4BlockCompressor blocks = Lz4BlockCompressor.Create(level);
    blocks.SetDictionary(dictionary);
    byte[] output = new byte[1 << 20];
    long inBytes = payloads.Sum(p => (long)p.Length);
    long frameBytes = 0;
    long blockBytes = 0;

    double framePass = Best(() =>
    {
        frameBytes = 0;
        foreach (byte[] p in payloads)
        {
            if (!encoder.TryCompressFrame(p, output, out int w))
            {
                throw new InvalidOperationException("frame did not fit");
            }

            frameBytes += w;
        }
    });
    double blockPass = Best(() =>
    {
        blockBytes = 0;
        foreach (byte[] p in payloads)
        {
            blockBytes += blocks.Compress(p, 0, output);
        }
    });

    // Check the frames decode with the dictionary.
    var decoder = new Lz4Decoder(new Lz4DecompressionOptions { Dictionary = dictionary });
    foreach (byte[] p in payloads.Take(200))
    {
        encoder.TryCompressFrame(p, output, out int w);
        byte[] back = new byte[p.Length + 16];
        if (!Lz4.TryDecompress(decoder, output.AsSpan(0, w), back, out int bw) || !back.AsSpan(0, bw).SequenceEqual(p))
        {
            Console.Error.WriteLine("ROUND TRIP MISMATCH");
            return 1;
        }
    }

    Console.WriteLine($"small\tlevel={level}\tpayloads={payloads.Count}\tin={inBytes}\tframes={frameBytes}\tblocks={blockBytes}\tframe/s={payloads.Count / framePass:F0}\tblock/s={payloads.Count / blockPass:F0}\tframe MB/s={inBytes / framePass / 1e6:F0}\tblock MB/s={inBytes / blockPass / 1e6:F0}");
    _ = blockOptions;
    return 0;
}
