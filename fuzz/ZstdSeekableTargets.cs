using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for the seekable Zstandard format. The reader target treats the input as a whole seekable file, opens it from
/// memory and from a stream, and reads the same ranges both ways: the two must agree byte for byte, or both throw the
/// library's own exceptions. The round-trip target builds data, frame size and options from the input, writes a seekable file
/// in chunks, and checks that random ranges equal slices of the data.
/// </summary>
internal static class ZstdSeekableTargets
{
    private static readonly ZstdSeekableReaderOptions s_options = new()
    {
        MaxFrames = 1 << 16,
        MaxFrameSize = 1 << 22,
        Decompression = new ZstdDecompressionOptions { MaxWindowSize = 1 << 22, MaxOutputSize = 1 << 24 },
    };

    public static FuzzTarget Read { get; } = new("zstd-seekable-read", static data => CheckRead(data.ToArray()), static ex => ex is CompressionException);

    public static FuzzTarget RoundTrip { get; } = new("zstd-seekable-roundtrip", static data => CheckRoundTrip(data), static _ => false);

    private static byte[]? TryRead(Func<ZstdSeekableReader> open, long offset, int length, bool verify)
    {
        try
        {
            using ZstdSeekableReader reader = open();
            if (verify)
            {
                reader.Verify();
            }

            byte[] buffer = new byte[length];
            int n = reader.Read(offset, buffer);
            return buffer.AsSpan(0, n).ToArray();
        }
        catch (CompressionException)
        {
            return null;
        }
    }

    private static void CheckRead(byte[] file)
    {
        // The table parser on its own, then the reader on top of it.
        try
        {
            ZstdSeekTable table = ZstdSeekTable.Read(file, s_options.MaxFrames);
            for (int i = 0; i < table.Count; i += Math.Max(1, table.Count / 64))
            {
                ZstdSeekFrame f = table.GetFrame(i);
                if (f.CompressedOffset < 0 || f.CompressedSize < 8 || f.CompressedOffset + f.CompressedSize > file.Length)
                {
                    throw new InvalidOperationException("seek table entry outside the file");
                }
            }
        }
        catch (CompressionException)
        {
        }

        ulong h = 1469598103934665603UL;
        foreach (byte b in file.AsSpan(0, Math.Min(file.Length, 64)))
        {
            h = (h ^ b) * 1099511628211UL;
        }

        for (int probe = 0; probe < 4; probe++)
        {
            h = (h * 6364136223846793005UL) + 1442695040888963407UL;
            long offset = (long)((h >> 20) % 100_000);
            int length = (int)((h >> 8) % 70_000);
            bool verify = probe == 0;
            byte[]? a = TryRead(() => new ZstdSeekableReader(file, s_options), offset, length, verify);
            byte[]? b = TryRead(() => new ZstdSeekableReader(new MemoryStream(file), s_options), offset, length, verify);
            if ((a is null) != (b is null) || (a is not null && !a.AsSpan().SequenceEqual(b)))
            {
                throw new InvalidOperationException("memory and stream readers disagree");
            }
        }
    }

    private static void CheckRoundTrip(ReadOnlySpan<byte> input)
    {
        if (input.Length < 4)
        {
            return;
        }

        int level = (input[0] % 12) - 3;
        int frameSize = 1 + (((input[1] << 8) | input[2]) % 40_000);
        bool checksums = (input[3] & 1) != 0;
        bool frameChecksum = (input[3] & 2) != 0;
        int chunk = 1 + ((input[3] >> 2) * 97);
        // The rest is a recipe: runs and repeats are built from it so a short input makes a long, compressible one.
        byte[] data = Build(input[4..]);
        var options = new ZstdSeekableOptions { FrameSize = frameSize, Checksums = checksums, Compression = new ZstdCompressionOptions { Level = level, Checksum = frameChecksum } };
        var ms = new MemoryStream();
        using (var writer = new ZstdSeekableWriter(ms, options, leaveOpen: true))
        {
            for (int p = 0; p < data.Length; p += chunk)
            {
                writer.Write(data.AsSpan(p, Math.Min(chunk, data.Length - p)));
            }
        }

        byte[] file = ms.ToArray();
        byte[] whole = ZstdSeekable.Compress(data, options);
        if (!file.AsSpan().SequenceEqual(whole))
        {
            throw new InvalidOperationException("chunked and one-shot writers differ");
        }

        using var reader = new ZstdSeekableReader(new MemoryStream(file));
        if (reader.Length != data.Length)
        {
            throw new InvalidOperationException("length mismatch");
        }

        reader.Verify();
        ulong h = (ulong)data.Length + 7;
        for (int i = 0; i < 12; i++)
        {
            h = (h * 6364136223846793005UL) + 1442695040888963407UL;
            int offset = data.Length == 0 ? 0 : (int)((h >> 16) % (ulong)data.Length);
            int length = (int)((h >> 40) % (ulong)(frameSize * 3 + 1));
            byte[] got = new byte[length];
            int n = reader.Read(offset, got);
            int expected = Math.Min(length, data.Length - offset);
            if (n != expected || !got.AsSpan(0, n).SequenceEqual(data.AsSpan(offset, n)))
            {
                throw new InvalidOperationException($"range {offset}+{length} differs");
            }
        }
    }

    private static byte[] Build(ReadOnlySpan<byte> recipe)
    {
        var data = new List<byte>();
        for (int i = 0; i + 3 < recipe.Length && data.Count < (1 << 20); i += 4)
        {
            int kind = recipe[i] & 3;
            int length = 1 + (recipe[i + 1] * 8);
            int arg = (recipe[i + 2] << 8) | recipe[i + 3];
            switch (kind)
            {
                case 0:
                    ulong s = (ulong)arg + 1;
                    for (int k = 0; k < length; k++)
                    {
                        s ^= s << 13;
                        s ^= s >> 7;
                        s ^= s << 17;
                        data.Add((byte)(s >> 24));
                    }

                    break;
                case 1:
                    for (int k = 0; k < length; k++)
                    {
                        data.Add((byte)arg);
                    }

                    break;
                default:
                    int distance = Math.Min(data.Count, 1 + arg);
                    for (int k = 0; k < length && distance > 0; k++)
                    {
                        data.Add(data[data.Count - distance]);
                    }

                    break;
            }
        }

        return [.. data];
    }
}
