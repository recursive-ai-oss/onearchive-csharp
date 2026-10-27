using System.Buffers;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Round-trip target for long-distance matching and jobs. The first four bytes choose the options (level, checksum, content size,
/// one-shot or streamed, flush, a dictionary, long-distance matching, job size, workers, window, overlap and matcher knobs); the rest
/// is a recipe that builds up to 3 MB of data out of literal runs, repeats at any distance, runs and near-repeats, so a few dozen bytes
/// reach inputs several jobs long with repeats across job and window boundaries. The frame must decode, with this library's decoder,
/// to the data, and the same options on another number of workers must give the same bytes. Any exception at all is a finding.
/// </summary>
internal static class ZstdLongTargets
{
    private const int MaxData = 3 << 20;

    private static readonly ZstdCompressionDictionary s_dictionary = ZstdCompressionDictionary.FromContent(Pattern(6000, 99), 5);
    private static readonly ZstdDictionary s_decodeDictionary = ZstdDictionary.Parse(Pattern(6000, 99));

    public static FuzzTarget RoundTrip { get; } = new("zstd-encode-long-roundtrip", static data => Run(data), static _ => false);

    private static byte[] Pattern(int length, ulong seed)
    {
        byte[] data = new byte[length];
        ulong s = seed;
        for (int i = 0; i < length; i++)
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            data[i] = (byte)(s >> 24);
        }

        return data;
    }

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 5)
        {
            return;
        }

        int level = (input[0] % 14) - 9;
        int flags = input[1];
        int more = input[2];
        int tuning = input[3];
        byte[] data = Build(input[4..].ToArray());

        bool longDistance = (more & 1) != 0;
        int jobSelector = (more >> 1) & 7;
        int jobSize = jobSelector == 0 ? 0 : jobSelector == 7 ? ZstdCompressionOptions.AutoJobSize : (128 << 10) << (jobSelector - 1);
        int[] workerChoices = [1, 2, 3, 0];
        int windowLog = 12 + (3 * ((more >> 6) & 3));
        bool streamed = (flags & 4) != 0;
        bool flush = (flags & 8) != 0;
        int piece = 1 << (tuning & 15);
        if (streamed && flush)
        {
            // Every flush ends a job, and each job starts by loading its overlap into the match finder, so a flush costs time
            // in proportion to the window. A flush after every few bytes of a megabyte is slow by design, not a bug to find.
            piece = Math.Max(piece, 1 << 12);
        }
        ZstdCompressionOptions Make(int workers) => new()
        {
            Level = level,
            Checksum = (flags & 1) != 0,
            WriteContentSize = (flags & 2) != 0,
            ContentSize = streamed && (flags & 64) != 0 ? data.Length : null,
            Dictionary = (flags & 16) != 0 ? s_dictionary : null,
            WindowLog = windowLog,
            LongDistanceMatching = longDistance,
            LongDistanceMinMatch = (tuning & 0x40) != 0 ? 16 : 0,
            LongDistanceHashRateLog = (tuning & 0x80) != 0 ? 2 : 0,
            JobSize = jobSize,
            Workers = workers,
            OverlapSize = ((tuning >> 4) & 3) switch { 0 => -1, 1 => 0, 2 => 4096, _ => 100_000 },
        };

        int workers = workerChoices[(more >> 4) & 3];
        byte[] frame = Encode(data, Make(workers), streamed, piece, flush);
        long flushAllowance = streamed && flush ? 4L * ((data.Length / piece) + 2) : 0;
        if (frame.Length > ZstdEncoder.GetMaxCompressedLength(data.Length) + 16 + flushAllowance)
        {
            throw new InvalidOperationException($"frame of {frame.Length} bytes exceeds the bound for {data.Length}");
        }

        var decompression = new ZstdDecompressionOptions { Dictionary = (flags & 16) != 0 ? s_decodeDictionary : null, MaxWindowSize = 1L << 22 };
        byte[] back = RecursiveAI.OneCompress.Zstd.Zstd.Decompress(frame, decompression);
        if (!back.AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException($"round trip mismatch at level {level}, flags {flags:x2}/{more:x2}/{tuning:x2}, {data.Length} bytes");
        }

        if (jobSize != 0)
        {
            int other = workers == 1 ? 3 : 1;
            byte[] again = Encode(data, Make(other), streamed, piece, flush);
            if (!again.AsSpan().SequenceEqual(frame))
            {
                throw new InvalidOperationException($"bytes depend on the worker count ({workers} versus {other}) at level {level}, flags {flags:x2}/{more:x2}/{tuning:x2}");
            }
        }
    }

    private static byte[] Encode(byte[] data, ZstdCompressionOptions options, bool streamed, int piece, bool flush)
    {
        if (!streamed)
        {
            return RecursiveAI.OneCompress.Zstd.Zstd.Compress(data, options);
        }

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
                OperationStatus status = encoder.Compress(data.AsSpan(position + taken, n - taken), buffer, out int consumed, out int written, last);
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

    // The recipe: each step is a tag byte (its low two bits pick the kind) and some argument bytes; missing bytes read as zero.
    private static byte[] Build(byte[] recipe)
    {
        byte[] data = new byte[1 << 16];
        int count = 0;
        int p = 0;

        int Take(int bytes)
        {
            int value = 0;
            for (int i = 0; i < bytes && p < recipe.Length; i++)
            {
                value |= recipe[p++] << (8 * i);
            }

            return value;
        }

        void Ensure(int extra)
        {
            if (count + extra > data.Length)
            {
                Array.Resize(ref data, Math.Max(data.Length * 2, count + extra));
            }
        }

        while (p < recipe.Length && count < MaxData)
        {
            int tag = recipe[p++];
            switch (tag & 3)
            {
                case 0:
                    {
                        int length = Math.Min((Take(2) % 70000) + 1, MaxData - count);
                        byte[] random = Pattern(length, (ulong)(Take(1) + 1) * 0x9E3779B97F4A7C15UL);
                        Ensure(length);
                        random.CopyTo(data, count);
                        count += length;
                        break;
                    }

                case 1 when count > 0:
                case 3 when count > 0:
                    {
                        int distance = 1 + (Take(3) % count);
                        int length = Math.Min(1 + (Take(2) * (1 + (((tag >> 2) & 7) * 8)) % 700_000), MaxData - count);
                        int every = (tag & 3) == 3 ? 1 + (Take(1) % 200) : 0;
                        Ensure(length);
                        for (int i = 0; i < length; i++)
                        {
                            byte b = data[count - distance];
                            data[count++] = every != 0 && i % every == every - 1 ? (byte)(b + 1) : b;
                        }

                        break;
                    }

                case 2:
                    {
                        int length = Math.Min(1 + (Take(2) % 200_000), MaxData - count);
                        byte value = (byte)Take(1);
                        Ensure(length);
                        data.AsSpan(count, length).Fill(value);
                        count += length;
                        break;
                    }
            }
        }

        return data.AsSpan(0, count).ToArray();
    }
}
