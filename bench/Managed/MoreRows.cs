using System.Buffers;
using System.Diagnostics;
using K4os.Compression.LZ4;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Deflate64;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Ppmd;
using RecursiveAI.OneCompress.Zstd;
using OCrc = RecursiveAI.OneCompress.Checksums;
using SC = SharpCompress.Compressors;
using SIH = System.IO.Hashing;

namespace RecursiveAI.ManagedBench;

/// <summary>Rows for the codecs and directions beyond the first set: Deflate64 and lzip decode, PPMd H, LZMA and LZMA2, checksums, small payloads with dictionaries, and streaming Deflate.</summary>
internal static class MoreRows
{
    private const string ScV = "SharpCompress 1.0.0";
    private const string SzlV = "SharpZipLib 1.4.2";
    private const string DnzV = "DotNetZip 1.16.0";
    private const string K4osV = "K4os.Compression.LZ4 1.3.8";
    private const string ZstdSharpV = "ZstdSharp.Port 0.8.8";
    private const string HashingV = "System.IO.Hashing 10.0.0";
    private const string LzmaNetV = "Lzma.Net 5.8.4.3";
    private const string SdkV = "LZMA-SDK 22.1.1";

    // The dictionary-size byte of the stream the last LZMA2 candidate wrote, for the round-trip check.
    private static byte _lzma2Prop;

    private static Cand Mine(string label, Func<Ctx, int> run) => new("ours", label, Kind.Ours, run);
    private static Cand Man(string lib, Func<Ctx, int> run) => new(lib[..lib.LastIndexOf(' ')], lib[(lib.LastIndexOf(' ') + 1)..], Kind.Managed, run);
    private static Cand Nat(string lib, string v, Func<Ctx, int> run) => new(lib, v, Kind.Native, run);

    private static IEnumerable<Cand> Sw(IEnumerable<int> levels, string prefix, Func<int, Func<Ctx, int>> make) =>
        levels.Select(l => new Cand("ours", prefix + l, Kind.Sweep, make(l)));

    public static void Add(List<Row> rows, string corpora)
    {
        Decoders(rows);
        Ppmd(rows);
        Lzma(rows);
        Checksums(rows);
        SmallPayloads(rows, corpora);
        DeflateStreams(rows);
    }

    // ---- Deflate64 and lzip decode -------------------------------------------------------------
    private static void Decoders(List<Row> rows)
    {
        rows.Add(new Row
        {
            Id = "deflate64-decode/7zz", Data = "silesia.tar@64", Decode = true,
            MakePacked = c => Deflate64FromZip(c.Src),
            Cands =
            [
                Mine("7zz", c => Deflate64.TryDecompress(c.Packed, c.Dst, out int n, new Deflate64DecompressionOptions { MaxOutputSize = long.MaxValue }) ? n : -1),
                Man(ScV, c => Io.Read(c, s => new SC.Deflate64.Deflate64Stream(s, SC.CompressionMode.Decompress))),
            ],
        });

        rows.Add(new Row
        {
            Id = "lzip-decode/L6", Data = "silesia.tar@64", Decode = true,
            MakePacked = c => Tool.Pipe("lzip", "-6 -c", c.Src),
            Cands =
            [
                Mine("L6", c => Lzip.TryDecompress(c.Packed, c.Dst, out int n, Rows.LzipDec) ? n : -1),
                Man(ScV, c => Io.Read(c, s => new SC.LZMA.LZipStream(s, SC.CompressionMode.Decompress))),
            ],
        });
    }

    /// <summary>Makes a raw Deflate64 stream with the 7-Zip tool as a black box: writes a one-entry zip and cuts the entry's data out of it.</summary>
    private static byte[] Deflate64FromZip(byte[] data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "managed-bench-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "in.bin"), data);
            var psi = new ProcessStartInfo("7zz", "a -tzip -mm=Deflate64 -mx=6 -bd -y out.zip in.bin") { WorkingDirectory = dir, RedirectStandardOutput = true, UseShellExecute = false };
            using (var p = Process.Start(psi)!)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException("7zz exited with " + p.ExitCode);
            }

            var zip = File.ReadAllBytes(Path.Combine(dir, "out.zip"));
            if (BitConverter.ToUInt32(zip, 0) != 0x04034b50 || BitConverter.ToUInt16(zip, 8) != 9) throw new InvalidOperationException("not a Deflate64 zip entry");
            long csize = BitConverter.ToUInt32(zip, 18);
            int start = 30 + BitConverter.ToUInt16(zip, 26) + BitConverter.ToUInt16(zip, 28);
            if (csize == 0xFFFFFFFF || csize == 0) throw new InvalidOperationException("sizes are in a data descriptor or zip64 record");
            return zip.AsSpan(start, (int)csize).ToArray();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- PPMd variant H (the 7z method) ------------------------------------------------------
    private static void Ppmd(List<Row> rows)
    {
        const int Order = 6, Mem = 16 << 20;
        var hOpts = new PpmdHOptions { Order = Order, MemorySize = Mem };
        // The 7z coder properties: the order, then the memory size, little endian.
        byte[] props = [Order, 0, 0, 0, 1];
        rows.Add(new Row
        {
            Id = "ppmd-h-compress/o6m16", Data = "silesia.tar@16", Runs = 2, MakePacked = null,
            VerifyParts = (c, n) => PpmdH.Decompress(c.Dst.AsSpan(0, n), new PpmdHDecompressionOptions { Order = Order, MemorySize = Mem, OutputSize = c.Src.Length }).AsSpan().SequenceEqual(c.Src) ? null : "round trip through our decoder differs",
            Cands =
            [
                Mine("o6m16", c => PpmdH.TryCompress(c.Src, c.Dst, out int n, hOpts) ? n : -1),
                Man(ScV, c => Io.Write(c, s => new SC.PPMd.PpmdStream(new SC.PPMd.PpmdProperties(props), s, true))),
            ],
        });
        rows.Add(new Row
        {
            Id = "ppmd-h-decode/o6m16", Data = "silesia.tar@16", Decode = true, Runs = 2,
            MakePacked = c => PpmdH.Compress(c.Src, hOpts),
            Cands =
            [
                Mine("o6m16", c => PpmdH.Decompress(c.Packed, new PpmdHDecompressionOptions { Order = Order, MemorySize = Mem, OutputSize = c.Src.Length }).AsSpan().TryCopyTo(c.Dst) ? c.Src.Length : -1),
                Man(ScV, c => Io.Read(c, s => new SC.PPMd.PpmdStream(new SC.PPMd.PpmdProperties(props), s, false), c.Src.Length)),
            ],
        });
    }

    // ---- LZMA and LZMA2 -------------------------------------------------------------------------
    private static void Lzma(List<Row> rows)
    {
        rows.Add(new Row
        {
            Id = "lzma-compress/p6", Data = "silesia.tar@16", Runs = 2, MakePacked = null,
            Verify = (d, n) => RecursiveAI.OneCompress.Lzma.Lzma.Decompress(d.AsSpan(0, n), Rows.LzmaDec),
            Cands =
            [
                Mine("p6", c => RecursiveAI.OneCompress.Lzma.Lzma.TryCompress(c.Src, c.Dst, out int n, new LzmaOptions { Preset = 6 }) ? n : -1),
                ..Sw([2, 3, 4, 5, 7], "p", p => c => RecursiveAI.OneCompress.Lzma.Lzma.TryCompress(c.Src, c.Dst, out int n, new LzmaOptions { Preset = p }) ? n : -1),
                Man(LzmaNetV, c => Io.Write(c, s => new LzmaNet.LzmaAloneCompressStream(s, 6, true))),
                Man(ScV, ScLzma1),
                Man(SdkV, SdkEncode),
            ],
        });

        rows.Add(new Row
        {
            Id = "lzma2-compress/p6", Data = "silesia.tar@16", Runs = 2, MakePacked = null,
            VerifyParts = (c, n) => Lzma2.Decompress(c.Dst.AsSpan(0, n), _lzma2Prop, Rows.LzmaDec).AsSpan().SequenceEqual(c.Src) ? null : "round trip through our decoder differs",
            Cands =
            [
                Mine("p6", c => OursLzma2(c, 6)),
                ..Sw([2, 3, 4, 5, 7], "p", p => c => OursLzma2(c, p)),
                Man(ScV, c => ScLzma2(c, 8 << 20, 64)),
            ],
        });

        rows.Add(new Row
        {
            Id = "lzma2-decode/p6", Data = "silesia.tar@64", Decode = true,
            MakePacked = c => Tool.Pipe("xz", "--format=raw --lzma2=preset=6,dict=8MiB -T1 -c", c.Src),
            Cands =
            [
                Mine("p6", c => new Lzma2Decoder(22, Rows.LzmaDec).Decompress(c.Packed, c.Dst, out _, out int w) == OperationStatus.Done ? w : -1),
                Man(ScV, c => Io.Read(c, s => new SC.LZMA.LzmaStream([22], s, c.Packed.Length, c.Src.Length, null, true))),
            ],
        });
    }

    private static int OursLzma2(Ctx c, int preset)
    {
        var b = Lzma2.Compress(c.Src, out byte prop, new LzmaOptions { Preset = preset });
        _lzma2Prop = prop;
        b.CopyTo(c.Dst, 0);
        return b.Length;
    }

    /// <summary>SharpCompress's raw LZMA writer, with the 13-byte header of a .lzma file put in front by hand.</summary>
    private static int ScLzma1(Ctx c)
    {
        var sink = new Sink(c.Dst);
        using (var s = new SC.LZMA.LzmaStream(new SC.LZMA.LzmaEncoderProperties(false, 8 << 20, 64), false, sink))
        {
            sink.Write(s.Properties);
            sink.Write(BitConverter.GetBytes((long)c.Src.Length));
            for (int p = 0; p < c.Src.Length; p += 1 << 18) s.Write(c.Src, p, Math.Min(1 << 18, c.Src.Length - p));
        }

        return sink.Pos;
    }

    private static int ScLzma2(Ctx c, int dict, int fastBytes)
    {
        var sink = new Sink(c.Dst);
        using (var s = new SC.LZMA.LzmaStream(new SC.LZMA.LzmaEncoderProperties(false, dict, fastBytes), true, sink))
        {
            _lzma2Prop = s.Properties[0];
            for (int p = 0; p < c.Src.Length; p += 1 << 18) s.Write(c.Src, p, Math.Min(1 << 18, c.Src.Length - p));
        }

        return sink.Pos;
    }

    /// <summary>Classic LZMA through the SDK package: 8 MiB window, binary tree 4, 64 fast bytes (the xz preset 6 values), with the 13-byte header of a .lzma file.</summary>
    private static int SdkEncode(Ctx c)
    {
        var enc = new SevenZip.Compression.LZMA.Encoder();
        enc.SetCoderProperties(
            [SevenZip.CoderPropID.DictionarySize, SevenZip.CoderPropID.PosStateBits, SevenZip.CoderPropID.LitContextBits, SevenZip.CoderPropID.LitPosBits, SevenZip.CoderPropID.Algorithm, SevenZip.CoderPropID.NumFastBytes, SevenZip.CoderPropID.MatchFinder, SevenZip.CoderPropID.EndMarker],
            [8 << 20, 2, 3, 0, 2, 64, "bt4", false]);
        var sink = new Sink(c.Dst);
        enc.WriteCoderProperties(sink);
        sink.Write(BitConverter.GetBytes((long)c.Src.Length));
        using var input = new MemoryStream(c.Src, writable: false);
        enc.Code(input, sink, -1, -1, null);
        return sink.Pos;
    }

    // ---- Checksums ---------------------------------------------------------------------------------
    private static void Checksums(List<Row> rows)
    {
        // CRC-32 (IEEE) and xxHash64 give the same values in every candidate, which the row checks. System.IO.Hashing's CRC-64 is a different
        // polynomial and reflection from the xz one, so its value is not compared; the work per byte is the same kind.
        rows.Add(new Row
        {
            Id = "crc32/full", Data = "silesia.tar", Hash = true, Decode = false, MakePacked = null,
            Cands =
            [
                Mine("", c => Same(OCrc.Crc32.Compute(c.Src), 0, c)),
                Man(HashingV, c => Same(SIH.Crc32.HashToUInt32(c.Src), 0, c)),
                Man(SzlV, c => { var h = new ICSharpCode.SharpZipLib.Checksum.Crc32(); h.Update(new ArraySegment<byte>(c.Src)); return Same((uint)h.Value, 0, c); }),
                Man(DnzV, c => { var h = new Ionic.Crc.CRC32(); h.SlurpBlock(c.Src, 0, c.Src.Length); return Same((uint)h.Crc32Result, 0, c); }),
            ],
        });
        rows.Add(new Row
        {
            Id = "crc64/full", Data = "silesia.tar", Hash = true, MakePacked = null,
            Cands =
            [
                Mine("", c => { _ = OCrc.Crc64.Compute(c.Src); return c.Src.Length; }),
                Man(HashingV, c => { _ = SIH.Crc64.HashToUInt64(c.Src); return c.Src.Length; }),
            ],
        });
        rows.Add(new Row
        {
            Id = "xxhash64/full", Data = "silesia.tar", Hash = true, MakePacked = null,
            Cands =
            [
                Mine("", c => Same64(OCrc.XxHash64.Compute(c.Src), 1, c)),
                Man(HashingV, c => Same64(SIH.XxHash64.HashToUInt64(c.Src, 0), 1, c)),
            ],
        });
        rows.Add(new Row
        {
            Id = "adler32/full", Data = "silesia.tar", Hash = true, MakePacked = null,
            Cands =
            [
                Mine("", c => Same(OCrc.Adler32.Compute(c.Src), 2, c)),
                Man(SzlV, c => { var h = new ICSharpCode.SharpZipLib.Checksum.Adler32(); h.Update(new ArraySegment<byte>(c.Src)); return Same((uint)h.Value, 2, c); }),
                Man(DnzV, c => Same(Ionic.Zlib.Adler.Adler32(1, c.Src, 0, c.Src.Length), 2, c)),
            ],
        });
        // CRC-32C: none of the pinned packages offers one (System.IO.Hashing has CRC-32 and CRC-64 only), so ours stands alone.
        rows.Add(new Row
        {
            Id = "crc32c/full", Data = "silesia.tar", Hash = true, MakePacked = null,
            Cands = [Mine("", c => { _ = OCrc.Crc32C.Compute(c.Src); return c.Src.Length; })],
        });
    }

    private static readonly ulong[] Expected = new ulong[3];

    private static int Same(uint value, int slot, Ctx c) => Same64(value, slot, c);

    /// <summary>The first candidate of a row (ours) stores its value; the others must match it.</summary>
    private static int Same64(ulong value, int slot, Ctx c)
    {
        if (Expected[slot] == 0 || value == Expected[slot]) { Expected[slot] = value; return c.Src.Length; }
        throw new InvalidOperationException($"checksum differs: {value:x} vs {Expected[slot]:x}");
    }

    // ---- Small payloads with trained dictionaries ------------------------------------------------
    private static void SmallPayloads(List<Row> rows, string corpora)
    {
        foreach (var kind in new[] { "frames", "snapshots" })
        {
            string k = kind;
            foreach (int lvl in new[] { 1, 3 })
            {
                int level = lvl;
                rows.Add(new Row
                {
                    Id = $"zstd-dict-compress/{k}-L{level}", Data = "game:" + k, MakePacked = null, Runs = 3,
                    VerifyParts = (c, n) => VerifyZstdParts(c, corpora, k),
                    Cands =
                    [
                        Mine($"L{level}", c => OursZstdSmall(c, level, Dict(corpora, k))),
                        ..Sw(level == 1 ? [2] : [2, 4], "L", l => c => OursZstdSmall(c, l, Dict(corpora, k))),
                        Man(ZstdSharpV, c => SharpZstdSmall(c, level, Dict(corpora, k))),
                    ],
                });
            }

            rows.Add(new Row
            {
                Id = $"zstd-dict-decode/{k}", Data = "game:" + k, Decode = true, Runs = 3,
                MakePacked = c => { OursZstdSmall(c, 3, Dict(corpora, k)); var packed = c.Dst.AsSpan(0, c.PackedParts.Sum()).ToArray(); return packed; },
                Cands =
                [
                    Mine("L3", c =>
                    {
                        var dec = new ZstdDecoder(new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(Dict(corpora, k)) });
                        int ip = 0, op = 0;
                        foreach (int len in c.PackedParts)
                        {
                            if (!Zstd.TryDecompress(dec, c.Packed.AsSpan(ip, len), c.Dst.AsSpan(op), out int w)) return -1;
                            ip += len; op += w;
                        }

                        return op;
                    }),
                    Man(ZstdSharpV, c =>
                    {
                        using var d = new ZstdSharp.Decompressor();
                        d.LoadDictionary(Dict(corpora, k));
                        int ip = 0, op = 0;
                        foreach (int len in c.PackedParts)
                        {
                            op += d.Unwrap(c.Packed.AsSpan(ip, len), c.Dst.AsSpan(op));
                            ip += len;
                        }

                        return op;
                    }),
                ],
            });
        }

        // LZ4: K4os offers block decoding with a dictionary but no encoder that takes one, so compression rows are ours alone
        // and the decode row has a managed candidate.
        foreach (var kind in new[] { "frames", "snapshots" })
        {
            string k = kind;
            foreach (int lvl in new[] { 1, 9 })
            {
                int level = lvl;
                rows.Add(new Row
                {
                    Id = $"lz4-dict-compress/{k}-L{level}", Data = "game:" + k, MakePacked = null, Runs = 3,
                    VerifyParts = (c, n) => VerifyLz4Parts(c, corpora, k),
                    Cands = [Mine($"L{level}", c => OursLz4Small(c, level, Lz4Dict(corpora, k)))],
                });
            }

            rows.Add(new Row
            {
                Id = $"lz4-dict-decode/{k}", Data = "game:" + k, Decode = true, Runs = 3,
                MakePacked = c => { OursLz4Small(c, 9, Lz4Dict(corpora, k)); return c.Dst.AsSpan(0, c.PackedParts.Sum()).ToArray(); },
                Cands =
                [
                    Mine("L9", c =>
                    {
                        var opts = new Lz4BlockOptions { Dictionary = Lz4Dict(corpora, k), MaxOutputSize = 1 << 30 };
                        int ip = 0, op = 0;
                        for (int i = 0; i < c.PackedParts.Length; i++)
                        {
                            op += Lz4Block.Decompress(c.Packed.AsSpan(ip, c.PackedParts[i]), c.Dst.AsSpan(op, c.Parts[i]), opts);
                            ip += c.PackedParts[i];
                        }

                        return op;
                    }),
                    Man(K4osV, c =>
                    {
                        var dict = Lz4Dict(corpora, k).Content.ToArray();
                        int ip = 0, op = 0;
                        for (int i = 0; i < c.PackedParts.Length; i++)
                        {
                            op += LZ4Codec.Decode(c.Packed.AsSpan(ip, c.PackedParts[i]), c.Dst.AsSpan(op, c.Parts[i]), dict);
                            ip += c.PackedParts[i];
                        }

                        return op;
                    }),
                ],
            });
        }
    }

    private static byte[] Dict(string corpora, string kind) => GameData.Dictionary(corpora, kind);

    // The raw LZ4 dictionary is the last 64 KiB of the zstd dictionary's content: trained content puts the most useful strings last.
    private static readonly Dictionary<string, Lz4Dictionary> Lz4Dicts = [];

    private static Lz4Dictionary Lz4Dict(string corpora, string kind)
    {
        lock (Lz4Dicts)
        {
            if (!Lz4Dicts.TryGetValue(kind, out var d))
            {
                var content = ZstdCompressionDictionary.Parse(Dict(corpora, kind)).Content.Span;
                d = new Lz4Dictionary(content.Length > Lz4Dictionary.MaxLength ? content[^Lz4Dictionary.MaxLength..] : content);
                Lz4Dicts[kind] = d;
            }

            return d;
        }
    }

    private static int OursZstdSmall(Ctx c, int level, byte[] dict)
    {
        var enc = new ZstdEncoder(new ZstdCompressionOptions { Level = level, Dictionary = ZstdCompressionDictionary.Parse(dict) });
        return Frames(c, (src, dst) => enc.CompressFrame(src, dst));
    }

    private static int SharpZstdSmall(Ctx c, int level, byte[] dict)
    {
        using var z = new ZstdSharp.Compressor(level);
        z.LoadDictionary(dict);
        return Frames(c, (src, dst) => z.Wrap(src, dst));
    }

    private static int OursLz4Small(Ctx c, int level, Lz4Dictionary dict)
    {
        var opts = new Lz4CompressionOptions { Level = level, Dictionary = dict };
        return Frames(c, (src, dst) => Lz4Block.TryCompress(src, dst, out int w, opts) ? w : throw new InvalidOperationException("lz4 failed"));
    }

    private delegate int Packer(ReadOnlySpan<byte> src, Span<byte> dst);

    /// <summary>Compresses each payload on its own into consecutive places of <see cref="Ctx.Dst"/>, noting every length.</summary>
    private static int Frames(Ctx c, Packer pack)
    {
        if (c.PackedParts.Length != c.Parts.Length) c.PackedParts = new int[c.Parts.Length];
        int ip = 0, op = 0;
        for (int i = 0; i < c.Parts.Length; i++)
        {
            int w = pack(c.Src.AsSpan(ip, c.Parts[i]), c.Dst.AsSpan(op));
            c.PackedParts[i] = w;
            ip += c.Parts[i]; op += w;
        }

        return op;
    }

    private static string? VerifyZstdParts(Ctx c, string corpora, string kind)
    {
        var dec = new ZstdDecoder(new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(Dict(corpora, kind)) });
        var back = new byte[c.Parts.Max()];
        int ip = 0, op = 0;
        for (int i = 0; i < c.Parts.Length; i++)
        {
            if (!Zstd.TryDecompress(dec, c.Dst.AsSpan(op, c.PackedParts[i]), back, out int w) || w != c.Parts[i] || !back.AsSpan(0, w).SequenceEqual(c.Src.AsSpan(ip, w)))
            {
                return $"payload {i} does not decode to the input";
            }

            ip += c.Parts[i]; op += c.PackedParts[i];
        }

        return null;
    }

    private static string? VerifyLz4Parts(Ctx c, string corpora, string kind)
    {
        var opts = new Lz4BlockOptions { Dictionary = Lz4Dict(corpora, kind), MaxOutputSize = 1 << 30 };
        var back = new byte[c.Parts.Max()];
        int ip = 0, op = 0;
        for (int i = 0; i < c.Parts.Length; i++)
        {
            int w = Lz4Block.Decompress(c.Dst.AsSpan(op, c.PackedParts[i]), back.AsSpan(0, c.Parts[i]), opts);
            if (w != c.Parts[i] || !back.AsSpan(0, w).SequenceEqual(c.Src.AsSpan(ip, w))) return $"payload {i} does not decode to the input";
            ip += c.Parts[i]; op += c.PackedParts[i];
        }

        return null;
    }

    // ---- Streaming Deflate ----------------------------------------------------------------------------
    private static void DeflateStreams(List<Row> rows)
    {
        foreach (var (lvl, data) in new[] { (1, "silesia.tar"), (6, "silesia.tar@32") })
        {
            int level = lvl;
            rows.Add(new Row
            {
                Id = $"deflate-stream-compress/L{level}", Data = data, MakePacked = null,
                Verify = (d, n) => Deflate.Decompress(d.AsSpan(0, n), Rows.DeflateDec),
                Cands =
                [
                    Mine($"L{level}", c => Io.Write(c, s => new DeflateEncoderStream(s, DeflateFormat.Raw, new DeflateOptions { Level = level }, true))),
                    ..Sw(level == 1 ? [2] : [5, 7], "L", l => c => Io.Write(c, s => new DeflateEncoderStream(s, DeflateFormat.Raw, new DeflateOptions { Level = l }, true))),
                    Man(SzlV, c => Io.Write(c, s => new ICSharpCode.SharpZipLib.Zip.Compression.Streams.DeflaterOutputStream(s, new ICSharpCode.SharpZipLib.Zip.Compression.Deflater(level, true)) { IsStreamOwner = false })),
                    Man(DnzV, c => Io.Write(c, s => new Ionic.Zlib.DeflateStream(s, Ionic.Zlib.CompressionMode.Compress, (Ionic.Zlib.CompressionLevel)level, true))),
                    Man(ScV, c => Io.Write(c, s => new SC.Deflate.DeflateStream(s, SC.CompressionMode.Compress, (SC.Deflate.CompressionLevel)level))),
                    Nat("BCL (zlib-ng)", "net10", c => Io.Write(c, s => new System.IO.Compression.DeflateStream(s, new System.IO.Compression.ZLibCompressionOptions { CompressionLevel = level }, true))),
                ],
            });
        }

        rows.Add(new Row
        {
            Id = "deflate-stream-decode/L6", Data = "silesia.tar", Decode = true,
            MakePacked = c => Deflate.Compress(c.Src, new DeflateOptions { Level = 6 }),
            Cands =
            [
                Mine("L6", c => Io.Read(c, s => new DeflateCodecStream(s, DeflateFormat.Raw, Rows.DeflateDec, true))),
                Man(SzlV, c => Io.Read(c, s => new ICSharpCode.SharpZipLib.Zip.Compression.Streams.InflaterInputStream(s, new ICSharpCode.SharpZipLib.Zip.Compression.Inflater(true)))),
                Man(DnzV, c => Io.Read(c, s => new Ionic.Zlib.DeflateStream(s, Ionic.Zlib.CompressionMode.Decompress))),
                Man(ScV, c => Io.Read(c, s => new SC.Deflate.DeflateStream(s, SC.CompressionMode.Decompress))),
                Nat("BCL (zlib-ng)", "net10", c => Io.Read(c, s => new System.IO.Compression.DeflateStream(s, System.IO.Compression.CompressionMode.Decompress))),
            ],
        });
    }
}
