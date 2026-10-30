using System.IO.Compression;
using K4os.Compression.LZ4;
using RecursiveAI.OneCompress.BZip2;
using RecursiveAI.OneCompress.Brotli;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Ppmd;
using RecursiveAI.OneCompress.Xz;
using RecursiveAI.OneCompress.Zstd;
using ZstdSharp;
using SC = SharpCompress.Compressors;
using OC = RecursiveAI.OneCompress;

namespace RecursiveAI.ManagedBench;

/// <summary>The rows. Versions are the pinned package versions in Managed.csproj.</summary>
internal static class Rows
{
    private const string ZstdSharpV = "ZstdSharp.Port 0.8.8";
    private const string K4osV = "K4os.Compression.LZ4 1.3.8";
    private const string ScV = "SharpCompress 1.0.0";
    private const string SzlV = "SharpZipLib 1.4.2";
    private const string DnzV = "DotNetZip 1.16.0";
    private const string BslV = "BrotliSharpLib 0.3.3";
    private const string BmfV = "BrotliManagedFast 1.1.0";
    private const string LzmaNetV = "Lzma.Net 5.8.4.3";
    private const string SdkV = "LZMA-SDK 22.1.1";

    private const string Full = "silesia.tar";
    private const string S64 = "silesia.tar@64";
    private const string S32 = "silesia.tar@32";
    private const string S16 = "silesia.tar@16";
    private const string S10 = "silesia.tar@10";

    private static Cand Mine(string v, Func<Ctx, int> run) => new("ours", "main", Kind.Ours, run);
    private static Cand Man(string lib, Func<Ctx, int> run) => new(lib[..lib.LastIndexOf(' ')], lib[(lib.LastIndexOf(' ') + 1)..], Kind.Managed, run);
    private static Cand Nat(string lib, string v, Func<Ctx, int> run) => new(lib, v, Kind.Native, run);

    public static List<Row> All(string corpora)
    {
        var rows = new List<Row>();

        // 1. xz / LZMA decode ------------------------------------------------------------------
        rows.Add(new Row
        {
            Id = "xz-decode/p6", Data = Full, Decode = true,
            MakePacked = c => Tool.Pipe("xz", "-6 -T1 -c", c.Src),
            Cands =
            [
                Mine("", c => Xz.TryDecompress(c.Packed, c.Dst, out int n, XzDec) ? n : -1),
                Man(LzmaNetV, c => Io.Read(c, s => new LzmaNet.XzDecompressStream(s))),
                Man(ScV, c => Io.Read(c, s => new SC.Xz.XZStream(s))),
            ],
        });
        rows.Add(new Row
        {
            Id = "lzma-decode/p6", Data = Full, Decode = true,
            MakePacked = c => Tool.Pipe("xz", "--format=lzma -6 -T1 -c", c.Src),
            Cands =
            [
                Mine("", c => Lzma.TryDecompress(c.Packed, c.Dst, out int n, new LzmaDecompressionOptions { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 }) ? n : -1),
                Man(LzmaNetV, c => Io.Read(c, s => new LzmaNet.LzmaAloneDecompressStream(s))),
                Man(SdkV, SdkDecode),
            ],
        });

        // 2. zstd ---------------------------------------------------------------------------------
        foreach (var (lvl, data, runs) in new[] { (1, Full, 3), (2, Full, 3), (3, Full, 3), (4, Full, 3), (5, Full, 3), (7, S64, 3), (9, S32, 3), (12, S32, 2), (13, S32, 2), (14, S32, 2), (15, S32, 2), (17, S16, 2), (19, S16, 2), (22, S10, 2) })
        {
            int level = lvl;
            rows.Add(new Row
            {
                Id = $"zstd-compress/L{level}", Data = data, Runs = runs,
                MakePacked = null,
                Verify = (d, n) => Zstd.Decompress(d.AsSpan(0, n), ZstdDec),
                Cands =
                [
                    Mine("", c => Zstd.TryCompress(c.Src, c.Dst, out int n, new ZstdCompressionOptions { Level = level, WriteContentSize = true }) ? n : -1),
                    Man(ZstdSharpV, c => { using var z = new Compressor(level); return z.Wrap(c.Src, c.Dst); }),
                ],
            });
        }

        foreach (int dl in new[] { 1, 3, 19 })
        {
            int level = dl;
            rows.Add(new Row
            {
                Id = $"zstd-decode/L{level}", Data = Full, Decode = true,
                MakePacked = c => Tool.Pipe("zstd", $"-{level} -q -c", c.Src),
                Cands =
                [
                    Mine("", c => Zstd.TryDecompress(c.Packed, c.Dst, out int n, ZstdDec) ? n : -1),
                    Man(ZstdSharpV, c => { using var d = new Decompressor(); return d.Unwrap(c.Packed, c.Dst); }),
                ],
            });
        }
        rows.Add(new Row
        {
            Id = "zstd-ldm/L3", Data = Full,
            Verify = (d, n) => Zstd.Decompress(d.AsSpan(0, n), ZstdDec),
            Cands =
            [
                Mine("", c => Zstd.TryCompress(c.Src, c.Dst, out int n, new ZstdCompressionOptions { Level = 3, LongDistanceMatching = true, WindowLog = 27 }) ? n : -1),
                Man(ZstdSharpV, c =>
                {
                    using var z = new Compressor(3);
                    z.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_windowLog, 27);
                    z.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
                    return z.Wrap(c.Src, c.Dst);
                }),
            ],
            MakePacked = null,
        });
        rows.Add(new Row
        {
            Id = "zstd-mt2/L3", Data = Full,
            Verify = (d, n) => Zstd.Decompress(d.AsSpan(0, n), ZstdDec),
            Cands =
            [
                Mine("", c => Zstd.TryCompress(c.Src, c.Dst, out int n, new ZstdCompressionOptions { Level = 3, Workers = 2, JobSize = 8 << 20 }) ? n : -1),
                Man(ZstdSharpV, c =>
                {
                    using var z = new Compressor(3);
                    z.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers, 2);
                    return z.Wrap(c.Src, c.Dst);
                }),
            ],
            MakePacked = null,
        });

        // 3. Deflate (raw) ----------------------------------------------------------------------
        foreach (var (lvl, data) in new[] { (1, Full), (3, S64), (6, S32), (9, S32) })
        {
            int level = lvl;
            rows.Add(new Row
            {
                Id = $"deflate-compress/L{level}", Data = data, MakePacked = null,
                Verify = (d, n) => Deflate.Decompress(d.AsSpan(0, n), DeflateDec),
                Cands =
                [
                    Mine("", c => Deflate.TryCompress(c.Src, c.Dst, out int n, new DeflateOptions { Level = level }) ? n : -1),
                    Man(SzlV, c => SzlDeflate(c, level)),
                    Man(DnzV, c => Io.Write(c, s => new Ionic.Zlib.DeflateStream(s, Ionic.Zlib.CompressionMode.Compress, (Ionic.Zlib.CompressionLevel)level, true))),
                    Man(ScV, c => Io.Write(c, s => new SC.Deflate.DeflateStream(s, SC.CompressionMode.Compress, (SC.Deflate.CompressionLevel)level))),
                    Nat("BCL (zlib-ng)", "net10", c => Io.Write(c, s => new DeflateStream(s, new ZLibCompressionOptions { CompressionLevel = level }, true))),
                ],
            });
        }

        rows.Add(new Row
        {
            Id = "deflate-decode/L6", Data = Full, Decode = true,
            MakePacked = c => Deflate.Compress(c.Src, new DeflateOptions { Level = 6 }),
            Cands =
            [
                Mine("", c => Deflate.TryDecompress(c.Packed, c.Dst, out int n, DeflateDec) ? n : -1),
                Man(SzlV, c => SzlInflate(c)),
                Man(DnzV, c => Io.Read(c, s => new Ionic.Zlib.DeflateStream(s, Ionic.Zlib.CompressionMode.Decompress))),
                Man(ScV, c => Io.Read(c, s => new SC.Deflate.DeflateStream(s, SC.CompressionMode.Decompress))),
                Nat("BCL (zlib-ng)", "net10", c => Io.Read(c, s => new DeflateStream(s, CompressionMode.Decompress))),
            ],
        });

        // 4. brotli -----------------------------------------------------------------------------
        foreach (var (q, data, runs) in new[] { (0, S64, 3), (1, S64, 3), (2, S64, 3), (4, S32, 3), (5, S32, 3), (7, S16, 2), (9, S16, 2), (11, S10, 2) })
        {
            int quality = q;
            rows.Add(new Row
            {
                Id = $"brotli-compress/q{quality}", Data = data, Runs = runs, MakePacked = null,
                Verify = (d, n) => OC.Brotli.Brotli.Decompress(d.AsSpan(0, n)),
                Cands =
                [
                    Mine("", c => OC.Brotli.Brotli.TryCompress(c.Src, c.Dst, out int n, new BrotliOptions { Quality = quality, WindowBits = 22 }) ? n : -1),
                    Man(BmfV, c => BrotliManagedFast.BrotliEncoder.TryCompress(c.Src, c.Dst, out int n, new BrotliManagedFast.BrotliCompressionOptions { Quality = quality, WindowLog = 22 }) ? n : -1),
                    Man(BslV, c => Io.Copy(BrotliSharpLib.Brotli.CompressBuffer(c.Src, 0, c.Src.Length, quality, 22), c.Dst)),
                    Nat("BCL (brotli)", "net10", c => System.IO.Compression.BrotliEncoder.TryCompress(c.Src, c.Dst, out int n, quality, 22) ? n : -1),
                ],
            });
        }

        rows.Add(new Row
        {
            Id = "brotli-decode/q5", Data = S32, Decode = true,
            MakePacked = c => Tool.Pipe("brotli", "-q 5 -c", c.Src),
            Cands =
            [
                Mine("", c => OC.Brotli.Brotli.TryDecompress(c.Packed, c.Dst, out int n) ? n : -1),
                Man(BmfV, c => BrotliManagedFast.BrotliDecoder.TryDecompress(c.Packed, c.Dst, out int n) ? n : -1),
                // BrotliSharpLib is left out of the decode row: it did not finish 32 MiB of quality-5 output within the 300 s timeout.
                Nat("BCL (brotli)", "net10", c => System.IO.Compression.BrotliDecoder.TryDecompress(c.Packed, c.Dst, out int n) ? n : -1),
            ],
        });

        // 5. PPMd -------------------------------------------------------------------------------
        PpmdRows.Add(rows);

        // 6. LZ4 block ----------------------------------------------------------------------------
        foreach (var (lvl, data, runs, k4) in new[] { (1, Full, 3, LZ4Level.L00_FAST), (3, S64, 3, LZ4Level.L03_HC), (9, S32, 3, LZ4Level.L09_HC), (12, S16, 2, LZ4Level.L12_MAX) })
        {
            int level = lvl;
            var k4level = k4;
            rows.Add(new Row
            {
                Id = $"lz4-compress/L{level}", Data = data, Runs = runs, MakePacked = null,
                Verify = (d, n) => Lz4Block.Decompress(d.AsSpan(0, n), new Lz4BlockOptions { MaxOutputSize = 1 << 30 }),
                Cands =
                [
                    Mine("", c => { return Lz4Block.TryCompress(c.Src, c.Dst, out int n, new Lz4CompressionOptions { Level = level }) ? n : -1; }),
                    Man(K4osV, c => { return LZ4Codec.Encode(c.Src, c.Dst, k4level); }),
                ],
            });
        }

        rows.Add(new Row
        {
            Id = "lz4-decode/L1", Data = Full, Decode = true,
            MakePacked = c => Lz4Block.Compress(c.Src, new Lz4CompressionOptions { Level = 1 }),
            Cands =
            [
                Mine("", c => Lz4Block.TryDecompress(c.Packed, c.Dst, out int n, new Lz4BlockOptions { MaxOutputSize = 1 << 30 }) ? n : -1),
                Man(K4osV, c => LZ4Codec.Decode(c.Packed, c.Dst)),
            ],
        });

        // 7. xz and lzip compress ---------------------------------------------------------------
        foreach (var (p, data, runs) in new[] { (0, S32, 3), (1, S32, 3), (3, S16, 3), (6, S16, 2), (9, S16, 2) })
        {
            int preset = p;
            rows.Add(new Row
            {
                Id = $"xz-compress/p{preset}", Data = data, Runs = runs, MakePacked = null,
                Verify = (d, n) => Xz.Decompress(d.AsSpan(0, n), XzDec),
                Cands =
                [
                    Mine("", c => Xz.TryCompress(c.Src, c.Dst, out int n, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = preset } }) ? n : -1),
                    Man(LzmaNetV, c => Io.Copy(LzmaNet.XzCompressor.Compress(c.Src, new LzmaNet.XzCompressOptions { Preset = preset, Threads = 1 }), c.Dst)),
                    Nat("xz CLI (pipe)", "system", c => Io.Copy(Tool.Pipe("xz", $"-{preset} -T1 -c", c.Src), c.Dst)),
                ],
            });
        }

        foreach (var (lv, data) in new[] { (0, S32), (3, S16) })
        {
            int level = lv;
            rows.Add(new Row
            {
                Id = $"lzip-compress/L{level}", Data = data, Runs = 2, MakePacked = null,
                Verify = (d, n) => Lzip.Decompress(d.AsSpan(0, n), LzipDec),
                Cands =
                [
                    Mine("", c => Lzip.TryCompress(c.Src, c.Dst, out int n, new LzipOptions { Level = level }) ? n : -1),
                    Man(ScV, c => Io.Write(c, s => new SC.LZMA.LZipStream(s, SC.CompressionMode.Compress))),
                ],
            });
        }

        // 8. bzip2 -------------------------------------------------------------------------------
        rows.Add(new Row
        {
            Id = "bzip2-compress/L9", Data = S16, Runs = 2, MakePacked = null,
            Verify = (d, n) => OC.BZip2.BZip2.Decompress(d.AsSpan(0, n)),
            Cands =
            [
                Mine("", c => OC.BZip2.BZip2.TryCompress(c.Src, c.Dst, out int n, new BZip2Options { Level = 9 }) ? n : -1),
                Man(ScV, c => Io.Write(c, s => new SC.BZip2.BZip2Stream(s, SC.CompressionMode.Compress, false))),
                Man(SzlV, c => Io.Write(c, s => new ICSharpCode.SharpZipLib.BZip2.BZip2OutputStream(s, 9) { IsStreamOwner = false })),
                Nat("bzip2 CLI (pipe)", "system", c => Io.Copy(Tool.Pipe("bzip2", "-9 -c", c.Src), c.Dst)),
            ],
        });
        rows.Add(new Row
        {
            Id = "bzip2-decode/L9", Data = S64, Decode = true,
            MakePacked = c => Tool.Pipe("bzip2", "-9 -c", c.Src),
            Cands =
            [
                Mine("", c => OC.BZip2.BZip2.TryDecompress(c.Packed, c.Dst, out int n) ? n : -1),
                Man(ScV, c => Io.Read(c, s => new SC.BZip2.BZip2Stream(s, SC.CompressionMode.Decompress, false))),
                Man(SzlV, c => Io.Read(c, s => new ICSharpCode.SharpZipLib.BZip2.BZip2InputStream(s))),
            ],
        });

        return rows;
    }

    internal static readonly XzDecompressionOptions XzDec = new() { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 };
    internal static readonly ZstdDecompressionOptions ZstdDec = new() { MaxOutputSize = long.MaxValue, MaxWindowSize = 1L << 30 };
    internal static readonly DeflateDecompressionOptions DeflateDec = new() { MaxOutputSize = long.MaxValue };
    internal static readonly LzipDecompressionOptions LzipDec = new() { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 };

    private static int SzlDeflate(Ctx c, int level)
    {
        var d = new ICSharpCode.SharpZipLib.Zip.Compression.Deflater(level, true);
        d.SetInput(c.Src, 0, c.Src.Length);
        d.Finish();
        int pos = 0;
        while (!d.IsFinished)
        {
            pos += d.Deflate(c.Dst, pos, Math.Min(1 << 20, c.Dst.Length - pos));
        }

        return pos;
    }

    private static int SzlInflate(Ctx c)
    {
        var inf = new ICSharpCode.SharpZipLib.Zip.Compression.Inflater(true);
        inf.SetInput(c.Packed);
        int pos = 0;
        while (!inf.IsFinished)
        {
            int n = inf.Inflate(c.Dst, pos, Math.Min(1 << 20, c.Dst.Length - pos));
            if (n == 0 && (inf.IsNeedingInput || inf.IsNeedingDictionary)) break;
            pos += n;
        }

        return pos;
    }

    private static int SdkDecode(Ctx c)
    {
        var dec = new SevenZip.Compression.LZMA.Decoder();
        dec.SetDecoderProperties(c.Packed.AsSpan(0, 5).ToArray());
        long size = BitConverter.ToInt64(c.Packed, 5);
        using var input = new MemoryStream(c.Packed, 13, c.Packed.Length - 13, writable: false);
        var sink = new Sink(c.Dst);
        dec.Code(input, sink, input.Length, size, null);
        return sink.Pos;
    }
}
