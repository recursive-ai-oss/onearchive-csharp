using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Cab;
using RecursiveAI.OneArchive.Tests.Cab;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Lzx;
using RecursiveAI.OneCompress.Tests.Lzx;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for LZX and cabinets.
/// <list type="bullet">
/// <item><c>lzx-decode</c>: byte 0 chooses the window (15 to 21), bytes 1 and 2 the output size (times 3); the rest is the stream. It is
/// decoded in one call and, when that works, again frame by frame with each frame given exactly the bytes it used; both must agree.</item>
/// <item><c>cab-read</c>: the input is a cabinet. It is read in order and again by random access, backwards; every entry must give the
/// same bytes (or the same error type) both ways.</item>
/// </list>
/// <c>fuzz seeds-cab DIR</c> writes seeds for both.
/// </summary>
internal static class LzxCabTargets
{
    private static readonly ArchiveLimits s_limits = new()
    {
        MaxTotalSize = 1 << 24,
        MaxEntryCount = 2000,
        MaxPathLength = 1024,
        MaxPathDepth = 32,
    };

    public static FuzzTarget Lzx { get; } = new("lzx-decode", static data => RunLzx(data), static ex => ex is CompressionException);

    public static FuzzTarget Cab { get; } = new("cab-read", static data => RunCab(data.ToArray()), static ex => ex is CompressionException);

    private static void RunLzx(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        int window = 15 + (input[0] % 7);
        int length = (input[1] | (input[2] << 8)) * 3;
        byte[] stream = input[3..].ToArray();
        byte[] whole = RecursiveAI.OneCompress.Lzx.Lzx.Decompress(stream, window, length);

        // Frame by frame, then again with each frame given only the bytes it used: the decoder must not need more.
        var d1 = new LzxDecoder(window);
        var sizes = new List<int>();
        byte[] out1 = new byte[length];
        for (int pos = 0, src = 0; pos < length;)
        {
            int n = Math.Min(LzxDecoder.FrameSize, length - pos);
            d1.DecodeFrame(stream.AsSpan(src), out1.AsSpan(pos, n), out int used);
            sizes.Add(used);
            src += used;
            pos += n;
        }

        var d2 = new LzxDecoder(window);
        byte[] out2 = new byte[length];
        for (int pos = 0, src = 0, k = 0; pos < length; k++)
        {
            int n = Math.Min(LzxDecoder.FrameSize, length - pos);
            d2.DecodeFrame(stream.AsSpan(src, sizes[k]), out2.AsSpan(pos, n), out int used);
            if (used != sizes[k])
            {
                throw new InvalidOperationException("a frame used a different number of bytes on the second pass");
            }

            src += used;
            pos += n;
        }

        if (!out1.AsSpan().SequenceEqual(whole) || !out2.AsSpan().SequenceEqual(whole))
        {
            throw new InvalidOperationException("frame-by-frame decoding differs from the one-call result");
        }
    }

    private static void RunCab(byte[] cab)
    {
        string sequential = Sequential(cab);
        string random = RandomAccess(cab);
        if (sequential != random)
        {
            throw new InvalidOperationException($"sequential and random access disagree:{Environment.NewLine}{sequential}{Environment.NewLine}{random}");
        }
    }

    private static string Describe(CabReader reader, CabEntry e)
    {
        try
        {
            using Stream s = reader.OpenEntryStream(e);
            return Hash(s, e);
        }
        catch (CompressionException ex)
        {
            return ex.GetType().Name;
        }
    }

    private static string Hash(Stream s, CabEntry e)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[8192];
        long total = 0;
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            total += n;
        }

        if (total != e.Size)
        {
            throw new InvalidOperationException($"entry '{e.Name}' read {total} bytes but declares {e.Size}");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Sequential(byte[] cab)
    {
        var sb = new StringBuilder();
        try
        {
            using var reader = new CabReader(new MemoryStream(cab), new CabReaderOptions { Limits = s_limits });
            while (reader.GetNextEntry() is { } e)
            {
                sb.Append(e.Index).Append('|').Append(e.Name).Append('|').Append(e.Size).Append('|');
                try
                {
                    using Stream s = reader.OpenEntryStream();
                    sb.Append(Hash(s, e));
                }
                catch (CompressionException ex)
                {
                    sb.Append(ex.GetType().Name);
                }

                sb.Append('\n');
            }
        }
        catch (CompressionException ex)
        {
            sb.Append("ERR ").Append(ex.GetType().Name);
        }

        return sb.ToString();
    }

    private static string RandomAccess(byte[] cab)
    {
        var lines = new SortedDictionary<int, string>();
        var sb = new StringBuilder();
        try
        {
            using var reader = new CabReader(new MemoryStream(cab), new CabReaderOptions { Limits = s_limits });
            // Admission limits apply while listing, so list the same way the sequential path does.
            var entries = new List<CabEntry>();
            while (reader.GetNextEntry() is { } e)
            {
                entries.Add(e);
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                lines[i] = $"{entries[i].Index}|{entries[i].Name}|{entries[i].Size}|{Describe(reader, entries[i])}";
            }

            foreach (string line in lines.Values)
            {
                sb.Append(line).Append('\n');
            }
        }
        catch (CompressionException ex)
        {
            // The listing failed part-way: the sequential path reported the entries before the failure, then the error.
            sb.Clear();
            sb.Append(Sequential(cab));
            _ = ex;
        }

        return sb.ToString();
    }

    /// <summary>Writes seeds for both targets: cabinets with every method and valid LZX streams.</summary>
    public static void WriteSeeds(string dir)
    {
        string cabDir = Directory.CreateDirectory(Path.Combine(dir, "cab-read")).FullName;
        string lzxDir = Directory.CreateDirectory(Path.Combine(dir, "lzx-decode")).FullName;
        byte[] text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("seed text for the cabinet readers, with calls \u00e8 and repeats. ", 400)));
        byte[] noise = new byte[3000];
        new Random(4).NextBytes(noise);
        int i = 0;
        foreach (CabTestBuilder.Method method in new[] { CabTestBuilder.Method.None, CabTestBuilder.Method.Mszip, CabTestBuilder.Method.MszipNoHistory, CabTestBuilder.Method.Lzx })
        {
            foreach (bool reserve in new[] { false, true })
            {
                var b = new CabTestBuilder();
                if (reserve)
                {
                    b.HeaderReserve = [1, 2, 3];
                    b.FolderReserveSize = 2;
                    b.DataReserveSize = 3;
                }

                var f = new CabTestBuilder.FolderSpec { Method = method, WindowBits = 15 + (i % 7), BlockSize = 32768 };
                f.Files.Add(new CabTestBuilder.FileSpec { Name = "a.txt", Data = text });
                f.Files.Add(new CabTestBuilder.FileSpec { Name = "dir\\n.bin", Data = noise });
                b.Folders.Add(f);
                var g = new CabTestBuilder.FolderSpec { Method = method, WindowBits = 16 };
                g.Files.Add(new CabTestBuilder.FileSpec { Name = "\u00fc.txt", Data = text[..500], Utf8Name = true });
                g.Files.Add(new CabTestBuilder.FileSpec { Name = "tail", Data = [1], RawFolderIndex = 0xFFFE });
                b.Folders.Add(g);
                if (i % 3 == 0)
                {
                    b.NextCabinet = "next.cab";
                    b.NextDisk = "disk 2";
                }

                File.WriteAllBytes(Path.Combine(cabDir, $"seed-{i++}.cab"), b.Build());
            }
        }

        i = 0;
        foreach (int window in new[] { 15, 16, 18, 21 })
        {
            foreach ((int blockSize, uint? translation) in new (int, uint?)[] { (40_000, null), (9_000, 100_000) })
            {
                byte[] data = [.. text, .. noise, .. text];
                data = data[..(data.Length / 3 * 3)];
                var frames = LzxTestEncoder.Encode(data, new LzxTestEncoder.Settings
                {
                    WindowBits = window,
                    BlockSize = blockSize,
                    TranslationSize = translation,
                    Kinds = [LzxTestEncoder.Kind.Verbatim, LzxTestEncoder.Kind.Aligned, LzxTestEncoder.Kind.Uncompressed],
                });
                int units = data.Length / 3;
                byte[] header = [(byte)(window - 15), (byte)(units & 0xFF), (byte)(units >> 8)];
                File.WriteAllBytes(Path.Combine(lzxDir, $"seed-{i++}"), [.. header, .. frames.SelectMany(f => f)]);
            }
        }
    }
}
