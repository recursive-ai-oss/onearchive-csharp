using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// LZMA, LZMA2, xz and lzip compression at every preset and level, with the extreme variants, every parser and match finder, odd
/// lc/lp/pb, every xz check and filter, blocks and lzip members. The compressed bytes must not depend on the platform, vector support
/// or runtime: the price tables are integers, the match finders use fixed hashes, and nothing here reads the processor count.
/// </summary>
internal static class LzmaEncoderEntries
{
    private static readonly string[] Small = ["empty", "one-byte", "text-small", "text-64k"];
    private static readonly string[] Medium = ["text-64k", "binary-1m", "runs-1m"];
    private static readonly string[] Wide = ["empty", "one-byte", "text-small", "text-64k", "binary-1m", "runs-1m", "random-1m", "zeros-4m"];

    [ModuleInitializer]
    internal static void Register()
    {
        for (int preset = 0; preset <= 9; preset++)
        {
            int p = preset;
            string[] inputs = p is 1 or 6 ? Wide : [.. Small, "binary-1m", "runs-1m"];
            Matrix.Add("lzma-enc", $"alone-p{p}", (input, ctx) => Lzma.Compress(input, new LzmaOptions { Preset = p }), inputs);
            Matrix.Add("lzma-enc", $"lzma2-p{p}", (input, ctx) => Lzma2.Compress(input, out _, new LzmaOptions { Preset = p }), inputs);
            Matrix.Add("lzma-enc", $"lzip-l{p}", (input, ctx) => Lzip.Compress(input, new LzipOptions { Level = p }), [.. Small, "binary-1m"]);
            if (p is 0 or 3 or 6 or 9)
            {
                Matrix.Add("lzma-enc", $"alone-p{p}e", (input, ctx) => Lzma.Compress(input, new LzmaOptions { Preset = p, Extreme = true }), Medium);
                Matrix.Add("lzma-enc", $"lzma2-p{p}e", (input, ctx) => Lzma2.Compress(input, out _, new LzmaOptions { Preset = p, Extreme = true }), Medium);
            }
        }

        Matrix.Add("lzma-enc", "alone-p6-text3m", (input, ctx) => Lzma.Compress(input, new LzmaOptions { Preset = 6 }), "text-3m");
        Matrix.Add("lzma-enc", "lzma2-p3-text3m", (input, ctx) => Lzma2.Compress(input, out _, new LzmaOptions { Preset = 3 }), "text-3m");
        Matrix.Add("lzma-enc", "alone-marker-p4", (input, ctx) => Lzma.Compress(input, new LzmaOptions { Preset = 4, WriteEndMarker = true }), Small);
        Matrix.Add("lzma-enc", "alone-streamed-p4", (input, ctx) => StreamedAlone(input), Small);

        foreach (LzmaMatchFinderKind kind in Enum.GetValues<LzmaMatchFinderKind>())
        {
            LzmaMatchFinderKind k = kind;
            foreach (LzmaMode mode in Enum.GetValues<LzmaMode>())
            {
                LzmaMode m = mode;
                Matrix.Add("lzma-enc", $"mf-{k}-{m}", (input, ctx) => Lzma2.Compress(input, out _, new LzmaOptions { Preset = 5, MatchFinder = k, Mode = m, NiceLength = 48, DictionarySize = 1 << 18 }), Medium);
            }
        }

        foreach ((int lc, int lp, int pb) in new[] { (0, 0, 0), (4, 0, 4), (0, 4, 0), (2, 2, 2), (8, 4, 4) })
        {
            (int lc2, int lp2, int pb2) = (lc, lp, pb);
            Matrix.Add("lzma-enc", $"alone-lc{lc2}-lp{lp2}-pb{pb2}", (input, ctx) => Lzma.Compress(input, new LzmaOptions { Preset = 4, Lc = lc2, Lp = lp2, Pb = pb2, DictionarySize = 1 << 16 }), Medium);
        }

        Matrix.Add("lzma-enc", "lzma2-dict4k-depth1", (input, ctx) => Lzma2.Compress(input, out _, new LzmaOptions { Preset = 6, DictionarySize = 4096, Depth = 1 }), Medium);
        Matrix.Add("lzma-enc", "lzma2-flushes", (input, ctx) => Lzma2Flushes(input), Medium);

        foreach (XzCheck check in new[] { XzCheck.None, XzCheck.Crc32, XzCheck.Crc64, XzCheck.Sha256 })
        {
            XzCheck c = check;
            Matrix.Add("xz-enc", $"check-{c}", (input, ctx) => Xz.Compress(input, new XzCompressionOptions { Check = c, Lzma = new LzmaOptions { Preset = 2 } }), Small);
        }

        for (int preset = 0; preset <= 9; preset++)
        {
            int p = preset;
            Matrix.Add("xz-enc", $"preset{p}", (input, ctx) => Xz.Compress(input, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = p } }), Small);
            Matrix.Add("xz-enc", $"preset{p}e", (input, ctx) => Xz.Compress(input, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = p, Extreme = true } }), "text-64k");
        }

        foreach (BranchArchitecture arch in Enum.GetValues<BranchArchitecture>())
        {
            BranchArchitecture a = arch;
            Matrix.Add("xz-enc", $"filter-{a}", (input, ctx) => Xz.Compress(input, new XzCompressionOptions { Filters = [XzFilter.Branch(a)], Lzma = new LzmaOptions { Preset = 3 } }), "binary-1m", "text-64k");
        }

        Matrix.Add("xz-enc", "filter-delta4", (input, ctx) => Xz.Compress(input, new XzCompressionOptions { Filters = [XzFilter.Delta(4)], Lzma = new LzmaOptions { Preset = 3 } }), "binary-1m", "text-64k");
        Matrix.Add("xz-enc", "filter-chain-blocks", (input, ctx) => Xz.Compress(input, new XzCompressionOptions
        {
            Filters = [XzFilter.Branch(BranchArchitecture.X86, 8), XzFilter.Delta(2)],
            BlockSize = 100_000,
            Check = XzCheck.Sha256,
            Lzma = new LzmaOptions { Preset = 3 },
        }), "binary-1m", "text-64k");
        Matrix.Add("xz-enc", "blocks-64k", (input, ctx) => Xz.Compress(input, new XzCompressionOptions { BlockSize = 65_536, Lzma = new LzmaOptions { Preset = 4 } }), Medium);
        Matrix.Add("xz-enc", "blocks-300k-stored", (input, ctx) => Xz.Compress(input, new XzCompressionOptions { BlockSize = 300_000, Lzma = new LzmaOptions { Preset = 1 } }), "random-1m");
        Matrix.Add("lzip-enc", "members-100k", (input, ctx) => Lzip.Compress(input, new LzipOptions { Level = 3, MemberSize = 100_000 }), Medium);
        Matrix.Add("lzip-enc", "dict-100k-match-12", (input, ctx) => Lzip.Compress(input, new LzipOptions { Level = 5, DictionarySize = 100_000, MatchLength = 12 }), Medium);
    }

    private static byte[] StreamedAlone(ReadOnlySpan<byte> input)
    {
        var sink = new MemoryStream();
        using (var stream = new LzmaEncoderStream(sink, new LzmaOptions { Preset = 4 }, leaveOpen: true))
        {
            for (int i = 0; i < input.Length; i += 4099)
            {
                stream.Write(input.Slice(i, Math.Min(4099, input.Length - i)));
            }
        }

        return sink.ToArray();
    }

    // Writes in odd pieces with a flush between them; the chunk layout depends on where the flushes fall, which is part of the output.
    private static byte[] Lzma2Flushes(ReadOnlySpan<byte> input)
    {
        var encoder = new Lzma2Encoder(new LzmaOptions { Preset = 3 });
        var output = new List<byte>();
        byte[] buffer = new byte[1000];
        int offset = 0;
        int step = 0;
        int[] pieces = [1, 5000, 33333, 70001, 250000];
        while (true)
        {
            int take = Math.Min(pieces[step++ % pieces.Length], input.Length - offset);
            bool last = offset + take == input.Length;
            ReadOnlySpan<byte> source = input.Slice(offset, take);
            while (true)
            {
                var status = encoder.Compress(source, buffer, out int consumed, out int written, last);
                output.AddRange(buffer.AsSpan(0, written).ToArray());
                source = source[consumed..];
                if (status == System.Buffers.OperationStatus.Done || (status == System.Buffers.OperationStatus.NeedMoreData && source.IsEmpty))
                {
                    break;
                }
            }

            offset += take;
            if (last)
            {
                return [.. output];
            }

            System.Buffers.OperationStatus flush;
            do
            {
                flush = encoder.Flush(buffer, out int written);
                output.AddRange(buffer.AsSpan(0, written).ToArray());
            }
            while (flush == System.Buffers.OperationStatus.DestinationTooSmall);
        }
    }
}
