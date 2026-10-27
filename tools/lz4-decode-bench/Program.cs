using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Lz4;

// Times the LZ4 decoder, memory to memory and warm.
//
//   Lz4DecodeBench big <file.lz4> [--seconds 4]
//       Decodes the whole frame (independent blocks, one frame) repeatedly into a buffer that is reused and prints the
//       best time as MB/s of output. Three rows: "frame" (Lz4Decoder.TryDecompress, which decodes straight into the
//       buffer), "stream" (a 128 KiB output buffer, so the window is used, as in an Lz4Stream) and "blocks" (the raw
//       block decoder over the frame's blocks, the same work `lz4 -b` times).
//   Lz4DecodeBench small <dictionary> <dir of .lz4 frames> [--seconds 4]
//       Decodes every frame in the directory with one decoder that has the dictionary loaded, and prints the best pass as
//       frames per second and MB/s of output. A second row decodes the frames' blocks with the raw block decoder.
//
// Compare with `lz4 -b<level> -i<seconds> file` (decompression speed) and `lz4 -b1 -D dict files...`.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Lz4DecodeBench big <file.lz4> | small <dictionary> <dir>");
    return 2;
}

double seconds = 4;
for (int i = 2; i + 1 < args.Length; i++)
{
    if (args[i] == "--seconds")
    {
        seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
    }
}

double Best(Action run)
{
    run();
    double best = double.MaxValue;
    var total = Stopwatch.StartNew();
    while (total.Elapsed.TotalSeconds < seconds)
    {
        long t = Stopwatch.GetTimestamp();
        run();
        best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
    }

    return best;
}

// The blocks of a single standard frame, as (offset, length, uncompressed) triples, and the frame header.
static (Lz4FrameHeader Header, List<(int Offset, int Length, bool Raw)> Blocks) ParseBlocks(byte[] frame)
{
    Lz4FrameHeader.TryParse(frame, out Lz4FrameHeader header);
    var blocks = new List<(int, int, bool)>();
    int pos = header.Length;
    while (true)
    {
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(pos));
        pos += 4;
        if (size == 0)
        {
            return (header, blocks);
        }

        blocks.Add((pos, (int)(size & 0x7FFFFFFF), (size & 0x80000000) != 0));
        pos += (int)(size & 0x7FFFFFFF) + (header.HasBlockChecksum ? 4 : 0);
    }
}

// Decodes every block of an independent-block frame into `whole`, each block after the previous one's output.
static int DecodeBlocks(byte[] frame, List<(int Offset, int Length, bool Raw)> blocks, byte[] whole)
{
    int at = 0;
    foreach ((int offset, int length, bool raw) in blocks)
    {
        ReadOnlySpan<byte> block = frame.AsSpan(offset, length);
        if (raw)
        {
            block.CopyTo(whole.AsSpan(at));
            at += length;
        }
        else
        {
            if (!Lz4BlockDecoder.TryDecode(block, whole.AsSpan(at), 0, out int written))
            {
                throw new InvalidOperationException("decode failed");
            }

            at += written;
        }
    }

    return at;
}

if (args[0] == "big")
{
    byte[] compressed = File.ReadAllBytes(args[1]);
    var options = new Lz4DecompressionOptions { MaxOutputSize = long.MaxValue };
    var decoder = new Lz4Decoder(options);
    byte[] chunk = new byte[128 * 1024];
    if (!Lz4Decoder.TryGetDecompressedSize(compressed, out long size))
    {
        // No content size in the frame: measure by streaming through a small buffer. Decoding the whole frame into a
        // fresh array here would add a second output-sized allocation to the process's peak memory.
        size = 0;
        int at = 0;
        while (true)
        {
            OperationStatus s = decoder.Decompress(compressed.AsSpan(at), chunk, out int c, out int w);
            at += c;
            size += w;
            if (s == OperationStatus.Done && at == compressed.Length)
            {
                break;
            }
        }

        decoder.Reset();
    }

    byte[] whole = new byte[size];
    (Lz4FrameHeader header, var blocks) = ParseBlocks(compressed);
    if (!header.BlocksIndependent)
    {
        Console.Error.WriteLine("the raw block row needs independent blocks (no -BD)");
        return 2;
    }

    double frame = Best(() =>
    {
        if (!Lz4.TryDecompress(decoder, compressed, whole, out int w) || w != size)
        {
            throw new InvalidOperationException("decode failed");
        }
    });
    double stream = Best(() =>
    {
        decoder.Reset();
        int pos = 0;
        long total = 0;
        while (true)
        {
            OperationStatus s = decoder.Decompress(compressed.AsSpan(pos), chunk, out int c, out int w);
            pos += c;
            total += w;
            if (s == OperationStatus.Done && pos == compressed.Length)
            {
                break;
            }
        }

        if (total != size)
        {
            throw new InvalidOperationException("decode failed");
        }
    });
    double raw = Best(() =>
    {
        if (DecodeBlocks(compressed, blocks, whole) != size)
        {
            throw new InvalidOperationException("decode failed");
        }
    });
    Console.WriteLine($"big\t{Path.GetFileName(args[1])}\tsize={size}\tcompressed={compressed.Length}\tframe={size / frame / 1e6:F0} MB/s\tstream={size / stream / 1e6:F0} MB/s\tblocks={size / raw / 1e6:F0} MB/s");
    return 0;
}

{
    var dictionary = new Lz4Dictionary(File.ReadAllBytes(args[1]));
    var frames = Directory.GetFiles(args[2], "*.lz4").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
    var parsed = frames.Select(ParseBlocks).ToList();
    var decoder = new Lz4Decoder(new Lz4DecompressionOptions { Dictionary = dictionary });
    byte[] output = new byte[1 << 20];
    byte[] history = new byte[dictionary.Length + (1 << 16)];
    long bytes = 0;

    void FramePass()
    {
        bytes = 0;
        foreach (byte[] f in frames)
        {
            if (!Lz4.TryDecompress(decoder, f, output, out int w))
            {
                throw new InvalidOperationException("decode failed");
            }

            bytes += w;
        }
    }

    // The raw layer with the dictionary already in place, as a caller that lays out its own buffer would use it.
    void BlockPass()
    {
        dictionary.Content.CopyTo(history);
        for (int i = 0; i < frames.Count; i++)
        {
            foreach ((int offset, int length, bool raw) in parsed[i].Blocks)
            {
                if (raw)
                {
                    continue;
                }

                if (!Lz4BlockDecoder.TryDecode(frames[i].AsSpan(offset, length), history, dictionary.Length, out _))
                {
                    throw new InvalidOperationException("decode failed");
                }
            }
        }
    }

    double framePass = Best(FramePass);
    double blockPass = Best(BlockPass);
    Console.WriteLine($"small\tframes={frames.Count}\toutput={bytes}\tframe: {frames.Count / framePass:F0} frames/s {bytes / framePass / 1e6:F0} MB/s\tblocks: {frames.Count / blockPass:F0} frames/s {bytes / blockPass / 1e6:F0} MB/s");
    return 0;
}
