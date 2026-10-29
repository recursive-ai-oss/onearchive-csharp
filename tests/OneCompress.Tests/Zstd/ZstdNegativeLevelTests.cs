using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Negative levels (the fast strategy with an acceleration) on inputs of several blocks. The first guard is the bit container of the
/// sequences section: a sequence whose three state transitions need 26 bits after 7 left-over bits and whose extra bits add up to
/// 31 fills the 64-bit container exactly, and a writer that shifted the container down by 64 wrote those bits twice, which made the
/// rest of the block decode as garbage. It showed only in the larger blocks of real data, where many rare codes make 26-bit
/// transitions possible.
/// </summary>
public class ZstdNegativeLevelTests
{
    private const int BlockSize = 1 << 17;

    // Mixed data of the kinds a compressor meets: words, records, far repeats, runs and noise.
    internal static byte[] MixedData(int size, int seed)
    {
        var random = new Random(seed);
        string[] words = new string[300];
        for (int i = 0; i < words.Length; i++)
        {
            var chars = new char[random.Next(2, 11)];
            for (int j = 0; j < chars.Length; j++)
            {
                chars[j] = (char)('a' + random.Next(26));
            }

            words[i] = new string(chars);
        }

        var data = new byte[size];
        int n = 0;
        while (n < size)
        {
            int length = Math.Min(random.Next(64, 6000), size - n);
            switch (random.Next(6))
            {
                case 0: // words, a few of them far more common than the rest
                    for (int i = 0; i < length;)
                    {
                        string word = words[(int)(Math.Pow(random.NextDouble(), 3) * words.Length)];
                        for (int j = 0; j < word.Length && i < length; j++)
                        {
                            data[n + i++] = (byte)word[j];
                        }

                        if (i < length)
                        {
                            data[n + i++] = (byte)' ';
                        }
                    }

                    break;
                case 1: // records with a counter
                    for (int i = 0; i < length; i++)
                    {
                        data[n + i] = (i & 15) < 4 ? (byte)((n + i) >> (8 * (i & 3))) : (byte)(i & 15);
                    }

                    break;
                case 2: // a repeat from anywhere earlier, with a few edits so the match ends
                    if (n > 100)
                    {
                        int distance = random.Next(1, n);
                        for (int i = 0; i < length; i++)
                        {
                            data[n + i] = data[n + i - distance];
                        }

                        for (int e = 0; e < 4 && length > 8; e++)
                        {
                            data[n + random.Next(length)] = (byte)random.Next(256);
                        }
                    }
                    else
                    {
                        random.NextBytes(data.AsSpan(n, length));
                    }

                    break;
                case 3: // a run
                    Array.Fill(data, (byte)random.Next(256), n, length);
                    break;
                case 4: // noise
                    random.NextBytes(data.AsSpan(n, length));
                    break;
                default: // a few distinct byte values
                    for (int i = 0; i < length; i++)
                    {
                        data[n + i] = (byte)((random.Next(4) * 17) + random.Next(2));
                    }

                    break;
            }

            n += length;
        }

        return data;
    }

    // Every level the options accept below zero: all of them down to -1024, then the powers of two, the values between them that
    // change the step's size class, and the lowest. Beyond a few thousand the step is longer than a block's useful span, so what
    // changes from level to level is only how few positions are tried.
    internal static IEnumerable<int> NegativeLevels()
    {
        for (int level = -1; level >= -1024; level--)
        {
            yield return level;
        }

        for (int level = -2048; level >= ZstdCompressionParameters.MinLevel; level *= 2)
        {
            yield return level;
            yield return level + 1;
        }

        yield return ZstdCompressionParameters.MinLevel;
    }

    [Fact]
    public void EveryNegativeLevelRoundTripsAMultiBlockInput()
    {
        byte[] data = MixedData(3 << 20, 11);
        foreach (int level in NegativeLevels())
        {
            byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level });
            byte[] back = Zstd.Zstd.Decompress(frame);
            Assert.True(data.AsSpan().SequenceEqual(back), $"level {level} did not round-trip");
        }
    }

    [Fact]
    public void NegativeLevelVariantsRoundTripAMultiBlockInput()
    {
        byte[] data = MixedData(3 << 20, 12);
        foreach (int level in new[] { -1, -3, -7, -100 })
        {
            foreach (var options in new[]
            {
                new ZstdCompressionOptions { Level = level, Checksum = true },
                new ZstdCompressionOptions { Level = level, WriteContentSize = false },
            })
            {
                Assert.True(data.AsSpan().SequenceEqual(Zstd.Zstd.Decompress(Zstd.Zstd.Compress(data, options))), $"level {level} did not round-trip");
            }
        }
    }

    [Fact]
    public void ASequenceThatFillsTheBitContainerExactlyIsWrittenOnce()
    {
        // Rare codes for all three kinds make the states of a sequence take their full width (a code with a single state in the
        // table always costs the whole accuracy log), and the codes are chosen so that the extra bits of every special sequence
        // add up to exactly 31. Some of them land after 7 left-over bits, whatever the rest of the stream is. The block has history
        // of three raw blocks so that large offsets are possible.
        const int HistorySize = 3 * BlockSize;
        var random = new Random(5);
        uint[] llBase = ZstdFormat.LiteralLengthBase;
        byte[] llBits = ZstdFormat.LiteralLengthBits;
        uint[] mlBase = ZstdFormat.MatchLengthBase;
        byte[] mlBits = ZstdFormat.MatchLengthBits;

        var combinations = new List<(int Ll, int Ml, int Offset, long Cost)>();
        for (int a = 16; a < llBits.Length; a++)
        {
            for (int b = 32; b < mlBits.Length; b++)
            {
                int g = 31 - llBits[a] - mlBits[b];
                if (g >= 6 && g <= 17)
                {
                    combinations.Add((a, b, g, (long)llBase[a] + mlBase[b]));
                }
            }
        }

        combinations.Sort((x, y) => x.Cost.CompareTo(y.Cost));
        var useLl = new int[64];
        var useMl = new int[64];
        var useOffset = new int[64];
        var specials = new List<(uint Ll, uint Ml, uint Offset)>();
        long specialBytes = 0;
        foreach (var c in combinations)
        {
            if (useLl[c.Ll] >= 6 || useMl[c.Ml] >= 6 || useOffset[c.Offset] >= 20 || specialBytes + c.Cost > 60_000)
            {
                continue;
            }

            useLl[c.Ll]++;
            useMl[c.Ml]++;
            useOffset[c.Offset]++;
            specialBytes += c.Cost;

            // Offsets of this size class that differ from each other, so that none is a repeat of an earlier one.
            specials.Add((llBase[c.Ll], mlBase[c.Ml], (uint)((1 << c.Offset) + (1000 * specials.Count) % (1 << (c.Offset - 1)))));
        }

        Assert.True(specials.Count >= 30, "the table of codes leaves too few special sequences");

        // Ordinary sequences: short literals and matches at short distances, enough of them for a table of the full accuracy log.
        var sequences = new List<(uint Ll, uint Ml, uint Offset)>();
        long bytes = specialBytes;
        while (sequences.Count < 8000)
        {
            uint ll = (uint)random.Next(0, 3);
            uint ml = (uint)random.Next(3, 8);
            sequences.Add((ll, ml, (uint)random.Next(8, 256)));
            bytes += ll + ml;
        }

        Assert.True(bytes < BlockSize - 64, "the sequences do not fit in a block");
        foreach (var s in specials)
        {
            sequences.Insert(random.Next(sequences.Count + 1), s);
        }

        byte[] all = new byte[HistorySize + BlockSize];
        random.NextBytes(all.AsSpan(0, HistorySize));
        var store = new ZstdSequenceStore();
        int position = HistorySize;
        foreach (var s in sequences)
        {
            random.NextBytes(all.AsSpan(position, (int)s.Ll));
            store.Append(all.AsSpan(position), s.Ll, s.Ml, s.Offset);
            position += (int)s.Ll;
            for (uint i = 0; i < s.Ml; i++)
            {
                all[position] = all[position - (int)s.Offset];
                position++;
            }
        }

        random.NextBytes(all.AsSpan(position));
        store.AppendLiterals(all.AsSpan(position));

        var writer = new ZstdBlockWriter();
        writer.BeginFrame(null, compressLiterals: false, estimatedPricing: true);
        byte[] frame = new byte[ZstdFrameWriter.MaxHeaderSize + (4 * (BlockSize + 3)) + ZstdBlockWriter.DestinationSlack + 64];
        int length = ZstdFrameWriter.WriteHeader(frame, 19, all.Length, 0, false);
        for (int b = 0; b < 3; b++)
        {
            length += ZstdBlockWriter.WriteRawBlock(all.AsSpan(b * BlockSize, BlockSize), false, frame.AsSpan(length));
        }

        int blockStart = length;
        length += writer.WriteBlock(all.AsSpan(HistorySize, BlockSize), store, true, frame.AsSpan(length));
        Assert.Equal(2, (frame[blockStart] >> 1) & 3); // a compressed block, not a stored one

        byte[] decoded = Zstd.Zstd.Decompress(frame.AsSpan(0, length));
        Assert.True(all.AsSpan().SequenceEqual(decoded), "the block did not decode to its input");
    }

    [Fact]
    [Trait("Category", "Interop")]
    public void EveryNegativeLevelIsAcceptedByTheZstdTool()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string tool = Environment.GetEnvironmentVariable("ONEARCHIVE_ZSTD") ?? "zstd";
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string directory = Path.Combine(root, "zstd-negative-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        try
        {
            byte[] data = MixedData(3 << 20, 13);
            string path = Path.Combine(directory, "frame.zst");
            var failures = new List<string>();
            foreach (int level in NegativeLevels())
            {
                File.WriteAllBytes(path, Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level }));
                var psi = new ProcessStartInfo(tool, ["-t", "-q", path]) { RedirectStandardError = true, RedirectStandardOutput = true };
                using Process p = Process.Start(psi)!;
                string error = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture, $"level {level}: {error.Trim()}"));
                }
            }

            Assert.True(failures.Count == 0, string.Join("; ", failures.Take(5)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
