using System.Buffers;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Round-trip target for the Zstandard encoder. The first bytes of the input choose the options (level, checksum, content size,
/// dictionary, one-shot or streamed in pieces, a flush in the middle); the rest is the data. The frame must decode, with this
/// library's decoder, to the data. The encoder never rejects valid options, so any exception at all is a finding.
/// </summary>
internal static class ZstdEncoderTargets
{
    private static readonly string? s_testData = Environment.GetEnvironmentVariable("ZSTD_FUZZ_TESTDATA");
    private static readonly byte[]? s_formattedBytes = Load("dict4.zdict");
    private static readonly byte[]? s_rawBytes = Load("rawdict-small.bin");
    private static readonly ZstdCompressionDictionary? s_formatted = s_formattedBytes is null ? null : ZstdCompressionDictionary.Parse(s_formattedBytes);
    private static readonly ZstdCompressionDictionary? s_raw = s_rawBytes is null ? null : ZstdCompressionDictionary.Parse(s_rawBytes);
    private static readonly ZstdDictionary? s_formattedDecode = s_formattedBytes is null ? null : ZstdDictionary.Parse(s_formattedBytes);
    private static readonly ZstdDictionary? s_rawDecode = s_rawBytes is null ? null : ZstdDictionary.Parse(s_rawBytes);

    public static FuzzTarget RoundTrip { get; } = new("zstd-encode-roundtrip", static data => Run(data), static _ => false);

    private static byte[]? Load(string name)
    {
        if (s_testData is null)
        {
            return null;
        }

        string path = Path.Combine(s_testData, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        int level = (input[0] % 14) - 9; // -9 to 4, so level 0 and the negative levels all occur
        int flags = input[1];
        int piece = 1 << (input[2] % 19);
        ReadOnlySpan<byte> data = input[3..];
        if (data.Length > 1 << 21)
        {
            data = data[..(1 << 21)];
        }

        ZstdCompressionDictionary? dictionary = (flags >> 4 & 3) switch { 1 => s_formatted, 2 => s_raw, _ => null };
        ZstdDictionary? decodeDictionary = (flags >> 4 & 3) switch { 1 => s_formattedDecode, 2 => s_rawDecode, _ => null };
        bool streamed = (flags & 4) != 0;
        var options = new ZstdCompressionOptions
        {
            Level = level,
            Checksum = (flags & 1) != 0,
            WriteContentSize = (flags & 2) != 0,
            ContentSize = streamed && (flags & 64) != 0 ? data.Length : null,
            Dictionary = dictionary,
            WriteDictionaryId = (flags & 128) == 0,
        };

        byte[] frame = streamed ? Stream(data, options, piece, (flags & 8) != 0) : Zstd.Compress(data, options);
        if (frame.Length > ZstdEncoder.GetMaxCompressedLength(data.Length) + 16)
        {
            throw new InvalidOperationException($"frame of {frame.Length} bytes exceeds the bound for {data.Length}");
        }

        var decompression = new ZstdDecompressionOptions { Dictionary = decodeDictionary };
        byte[] back = Zstd.Decompress(frame, decompression);
        if (!back.AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException($"round trip mismatch at level {level}, flags {flags:x2}, {data.Length} bytes");
        }
    }

    private static byte[] Stream(ReadOnlySpan<byte> data, ZstdCompressionOptions options, int piece, bool flush)
    {
        var encoder = new ZstdEncoder(options);
        var result = new MemoryStream();
        byte[] buffer = new byte[Math.Max(1, piece % 5000) + 13];
        int position = 0;
        while (true)
        {
            int n = Math.Min(piece, data.Length - position);
            bool last = position + n == data.Length;
            int taken = 0;
            while (true)
            {
                OperationStatus status = encoder.Compress(data.Slice(position + taken, n - taken), buffer, out int consumed, out int written, last);
                taken += consumed;
                result.Write(buffer, 0, written);
                if (status == OperationStatus.Done)
                {
                    break;
                }
            }

            position += n;
            if (last)
            {
                return result.ToArray();
            }

            if (flush)
            {
                OperationStatus flushStatus;
                do
                {
                    flushStatus = encoder.Flush(buffer, out int flushed);
                    result.Write(buffer, 0, flushed);
                }
                while (flushStatus == OperationStatus.DestinationTooSmall);
            }
        }
    }
}
