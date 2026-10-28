using System.Buffers;
using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;
using LzipApi = RecursiveAI.OneCompress.Lzip.Lzip;
using LzmaApi = RecursiveAI.OneCompress.Lzma.Lzma;
using XzApi = RecursiveAI.OneCompress.Xz.Xz;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Round-trip fuzz targets for the LZMA family encoders. The first bytes of the input choose the format, options, how the data is cut
/// into writes and how small the output buffer is; the rest is a program that expands into compressible data. Output that our
/// decoders do not turn back into the input, or that differs between the one-call path and the streaming path (when no flush is
/// involved), is a finding.
/// </summary>
internal static class LzmaEncoderTargets
{
    public static FuzzTarget LzmaRoundTrip { get; } = new("lzma-encode-roundtrip", static data => RunLzma(data), static _ => false);

    public static FuzzTarget XzRoundTrip { get; } = new("xz-encode-roundtrip", static data => RunXz(data), static _ => false);

    // Bytes below 0x80 start a literal run taken from the input; the rest are copies at a distance, some long enough for the longest match.
    private static byte[] Expand(ReadOnlySpan<byte> program, int limit)
    {
        var output = new List<byte>(Math.Min(limit, program.Length * 4));
        int i = 0;
        while (i < program.Length && output.Count < limit)
        {
            byte op = program[i++];
            if (op < 0x80 || output.Count == 0)
            {
                int run = (op & 0x3F) + 1;
                for (int k = 0; k < run && i < program.Length; k++)
                {
                    output.Add(program[i++]);
                }
            }
            else
            {
                if (i + 1 >= program.Length)
                {
                    break;
                }

                int length = 2 + (op & 0x1F) + ((op & 0x60) == 0x60 ? program[i] * 2 : 0);
                int distance = 1 + (((program[i] << 8) | program[i + 1]) % output.Count);
                if ((op & 0x40) != 0 && output.Count > 4)
                {
                    distance = 1 + (program[i + 1] % Math.Min(output.Count, 8));
                }

                i += 2;
                for (int k = 0; k < length && output.Count < limit; k++)
                {
                    output.Add(output[^distance]);
                }
            }
        }

        return [.. output];
    }

    private delegate OperationStatus Step(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinal);

    private static byte[] Stream(Step step, byte[] data, int piece, int outSize)
    {
        var compressed = new List<byte>();
        byte[] buffer = new byte[outSize];
        int offset = 0;
        while (true)
        {
            int take = Math.Min(piece, data.Length - offset);
            bool last = offset + take == data.Length;
            ReadOnlySpan<byte> source = data.AsSpan(offset, take);
            while (true)
            {
                OperationStatus status = step(source, buffer, out int consumed, out int written, last);
                compressed.AddRange(buffer.AsSpan(0, written).ToArray());
                source = source[consumed..];
                if (status == OperationStatus.Done || (status == OperationStatus.NeedMoreData && source.IsEmpty))
                {
                    break;
                }
            }

            offset += take;
            if (last)
            {
                return [.. compressed];
            }
        }
    }

    private static void Same(byte[] a, byte[] b, string what)
    {
        if (!a.AsSpan().SequenceEqual(b))
        {
            throw new InvalidOperationException($"streaming output differs from the one-call output: {what}");
        }
    }

    private static void Equal(byte[] expected, byte[] actual, string what)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"round trip mismatch: {what}");
        }
    }

    // Dictionaries up to 1 MiB: the presets above that allocate hundreds of megabytes per encoder, which says nothing about the code.
    private static uint FuzzDictionary(byte b) => 4096u << ((b & 0x0F) % 9);

    private static LzmaOptions MakeOptions(ReadOnlySpan<byte> h, bool lzma2)
    {
        int lc = h[3] % 5;
        int lp = lzma2 ? h[3] / 5 % (5 - lc) : h[3] / 5 % 5;
        LzmaMatchFinderKind kind = (LzmaMatchFinderKind)(1 + (h[5] % 5));
        int minNice = LzmaEncoderSettings.GetMinimumNiceLength(kind);
        return new LzmaOptions
        {
            Preset = h[1] % 10,
            Extreme = (h[1] & 0x80) != 0,
            Lc = lc,
            Lp = lp,
            Pb = h[2] % 5,
            DictionarySize = FuzzDictionary(h[4]),
            NiceLength = (h[4] & 0xF0) == 0 ? 0 : minNice + ((h[4] >> 4) * 17 % (274 - minNice)),
            MatchFinder = (h[5] & 0x80) != 0 ? kind : null,
            Mode = (h[5] & 0x40) != 0 ? ((h[5] & 0x20) != 0 ? LzmaMode.Fast : LzmaMode.Normal) : null,
            Depth = (h[5] & 0x10) != 0 ? 1 + (h[6] % 40) : 0,
            WriteEndMarker = (h[5] & 0x08) != 0,
        };
    }

    // Header: [0] format, [1] preset, [2] pb, [3] lc/lp, [4] dictionary and nice, [5] match finder, mode, depth, [6] depth/piece, [7] output size.
    private static void RunLzma(ReadOnlySpan<byte> input)
    {
        if (input.Length < 10)
        {
            return;
        }

        int format = input[0] % 3;
        LzmaOptions options = MakeOptions(input, format == 1);
        int piece = 1 + (input[6] * 509 % 70_000);
        int outSize = 1 + (input[7] * 31 % 3000);
        byte[] data = Expand(input[8..], 300_000);
        switch (format)
        {
            case 0:
                {
                    byte[] whole = LzmaApi.Compress(data, options);
                    Equal(data, LzmaApi.Decompress(whole), "lzma one call");
                    var encoder = new LzmaEncoder(options, data.Length);
                    Same(whole, Stream(encoder.Compress, data, piece, outSize), "lzma");
                    var unknown = new LzmaEncoder(options);
                    Equal(data, LzmaApi.Decompress(Stream(unknown.Compress, data, piece, outSize)), "lzma streamed, unknown length");
                    break;
                }

            case 1:
                {
                    byte[] whole = Lzma2.Compress(data, out byte prop, options);
                    Equal(data, Lzma2.Decompress(whole, prop), "lzma2 one call");
                    var encoder = new Lzma2Encoder(options);
                    Same(whole, Stream(encoder.Compress, data, piece, outSize), "lzma2");

                    // Flushes between writes end chunks; the result must still decode to the same data.
                    var flushing = new Lzma2Encoder(options);
                    var compressed = new List<byte>();
                    byte[] buffer = new byte[outSize];
                    for (int offset = 0; offset < data.Length; offset += piece)
                    {
                        int take = Math.Min(piece, data.Length - offset);
                        bool last = offset + take == data.Length;
                        ReadOnlySpan<byte> source = data.AsSpan(offset, take);
                        OperationStatus status;
                        do
                        {
                            status = flushing.Compress(source, buffer, out int c, out int w, last);
                            compressed.AddRange(buffer.AsSpan(0, w).ToArray());
                            source = source[c..];
                        }
                        while (status == OperationStatus.DestinationTooSmall || (!source.IsEmpty && status != OperationStatus.Done));
                        if (!last)
                        {
                            do
                            {
                                status = flushing.Flush(buffer, out int w);
                                compressed.AddRange(buffer.AsSpan(0, w).ToArray());
                            }
                            while (status == OperationStatus.DestinationTooSmall);
                        }
                    }

                    if (data.Length == 0)
                    {
                        OperationStatus s;
                        do
                        {
                            s = flushing.Compress([], buffer, out _, out int w, true);
                            compressed.AddRange(buffer.AsSpan(0, w).ToArray());
                        }
                        while (s != OperationStatus.Done);
                    }

                    Equal(data, Lzma2.Decompress([.. compressed], prop), "lzma2 with flushes");
                    break;
                }

            default:
                {
                    var lz = new LzipOptions
                    {
                        Level = input[1] % 10,
                        DictionarySize = FuzzDictionary(input[4]),
                        MatchLength = (input[4] & 0xF0) == 0 ? 0 : 5 + (input[4] >> 4) * 17 % 269,
                        MemberSize = (input[5] & 0x80) != 0 ? 1 + (input[6] * 997 % 100_000) : 0,
                    };
                    byte[] whole = LzipApi.Compress(data, lz);
                    Equal(data, LzipApi.Decompress(whole), "lzip one call");
                    var encoder = new LzipEncoder(lz);
                    Same(whole, Stream(encoder.Compress, data, piece, outSize), "lzip");
                    break;
                }
        }
    }

    private static void RunXz(ReadOnlySpan<byte> input)
    {
        if (input.Length < 10)
        {
            return;
        }

        LzmaOptions lzma = MakeOptions(input, lzma2: true);
        int plan = input[0];
        XzCheck[] checks = [XzCheck.None, XzCheck.Crc32, XzCheck.Crc64, XzCheck.Sha256];
        var filters = new List<XzFilter>();
        BranchArchitecture[] archs = Enum.GetValues<BranchArchitecture>();
        if ((plan & 4) != 0)
        {
            filters.Add(XzFilter.Branch(archs[input[6] % archs.Length]));
        }

        if ((plan & 8) != 0)
        {
            filters.Add(XzFilter.Delta(1 + (input[7] % 16)));
        }

        var options = new XzCompressionOptions
        {
            Lzma = lzma,
            Check = checks[plan & 3],
            BlockSize = (plan & 16) != 0 ? 1 + (input[6] * 331 % 150_000) : 0,
            Filters = filters,
        };
        int piece = 1 + (input[7] * 509 % 70_000);
        int outSize = 1 + ((input[6] ^ input[7]) * 31 % 3000);
        byte[] data = Expand(input[8..], 300_000);
        byte[] whole = XzApi.Compress(data, options);
        Equal(data, XzApi.Decompress(whole), "xz one call");
        var encoder = new XzEncoder(options);
        Same(whole, Stream(encoder.Compress, data, piece, outSize), "xz");
    }
}
