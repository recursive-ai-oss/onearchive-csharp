using System.Buffers;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Brotli;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// The decoder's fast command loop must do exactly what the general path does: the same bytes for valid streams cut into pieces any
/// way, and the same exception, at the same offset, for damaged ones.
/// </summary>
public class BrotliFastPathTests
{
    private static byte[] Text(int size, int seed)
    {
        string[] words = ["the", "of", "and", "compression", "window", "stream", "brotli", "literal", "distance", "meta-block", "dictionary", "context", "prefix", "code", "Zürich", "naïve"];
        var random = new Random(seed);
        var data = new List<byte>(size + 32);
        while (data.Count < size)
        {
            data.AddRange(System.Text.Encoding.UTF8.GetBytes(words[random.Next(words.Length)] + (random.Next(9) == 0 ? ".\n" : " ")));
            if (random.Next(40) == 0)
            {
                // A far repeat: copy an earlier stretch, so distances reach back across the whole input.
                int length = random.Next(8, 300);
                int from = random.Next(Math.Max(1, data.Count - length));
                for (int i = 0; i < length && from + i < data.Count; i++)
                {
                    data.Add(data[from + i]);
                }
            }
        }

        return data.ToArray();
    }

    private static byte[] Records(int size, int seed)
    {
        var random = new Random(seed);
        byte[] data = new byte[size];
        for (int i = 0; i + 16 <= size; i += 16)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i), i / 16);
            data[i + 4] = (byte)random.Next(4);
            data[i + 5] = (byte)(random.Next(3) * 50);
            random.NextBytes(data.AsSpan(i + 8, random.Next(8) == 0 ? 8 : 2));
        }

        return data;
    }

    private static byte[] RunsAndNoise(int size, int seed)
    {
        var random = new Random(seed);
        byte[] data = new byte[size];
        int i = 0;
        while (i < size)
        {
            int length = Math.Min(size - i, random.Next(1, 200));
            if (random.Next(3) == 0)
            {
                random.NextBytes(data.AsSpan(i, length));
            }
            else
            {
                byte period = (byte)random.Next(1, 9);
                for (int k = 0; k < length; k++)
                {
                    data[i + k] = (byte)(97 + (k % period));
                }
            }

            i += length;
        }

        return data;
    }

    public static TheoryData<string, int, int> Cases()
    {
        var cases = new TheoryData<string, int, int>();
        foreach (string kind in new[] { "text", "records", "runs" })
        {
            foreach (int quality in new[] { 1, 5, 9, 11 })
            {
                cases.Add(kind, quality, 22);
            }

            // Small windows make the ring wrap many times, which sends copies and dictionary words through the ring-end cases.
            foreach (int window in new[] { 10, 12, 16, 18 })
            {
                cases.Add(kind, 5, window);
            }
        }

        return cases;
    }

    private static byte[] Decode(byte[] compressed, bool fast, int inputPiece, int outputPiece)
    {
        var decoder = new BrotliDecoder { FastPathDisabled = !fast };
        var output = new MemoryStream();
        byte[] buffer = new byte[outputPiece];
        int pos = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(compressed.AsSpan(pos, Math.Min(inputPiece, compressed.Length - pos)), buffer, out int consumed, out int written);
            pos += consumed;
            output.Write(buffer, 0, written);
            if (status == OperationStatus.Done || (pos >= compressed.Length && status != OperationStatus.DestinationTooSmall))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FastAndGeneralPathsDecodeEveryCutTheSame(string kind, int quality, int windowBits)
    {
        int size = quality == 11 ? 120_000 : 400_000;
        byte[] data = kind switch
        {
            "text" => Text(size, 1),
            "records" => Records(size, 2),
            _ => RunsAndNoise(size, 3),
        };
        byte[] compressed = BR.Compress(data, new BrotliOptions { Quality = quality, WindowBits = windowBits });

        Assert.Equal(data, Decode(compressed, fast: true, compressed.Length, data.Length));
        Assert.Equal(data, Decode(compressed, fast: false, compressed.Length, data.Length));
        Assert.Equal(data, Decode(compressed, fast: true, 4093, 100_000));
        Assert.Equal(data, Decode(compressed, fast: true, compressed.Length, 1013));
        Assert.Equal(data, Decode(compressed, fast: true, 997, 7));
    }

    private static string Outcome(byte[] compressed, bool fast)
    {
        try
        {
            return "ok " + Convert.ToHexString(SHA256.HashData(Decode(compressed, fast, compressed.Length, 1 << 20)));
        }
        catch (CompressionFormatException e)
        {
            return $"format {e.Offset} {e.IsTruncated} {e.Message}";
        }
        catch (CompressionLimitException e)
        {
            return $"limit {e.Message}";
        }
    }

    [Theory]
    [InlineData(5, 22)]
    [InlineData(11, 22)]
    [InlineData(9, 12)]
    public void DamagedStreamsFailTheSameWayOnBothPaths(int quality, int windowBits)
    {
        byte[] data = Text(60_000, 7);
        byte[] compressed = BR.Compress(data, new BrotliOptions { Quality = quality, WindowBits = windowBits });
        var random = new Random(quality * 100 + windowBits);
        for (int round = 0; round < 250; round++)
        {
            byte[] damaged = (byte[])compressed.Clone();
            switch (round % 3)
            {
                case 0:
                    damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    Array.Resize(ref damaged, random.Next(1, damaged.Length));
                    break;
                default:
                    // Several flips near the start, where the tree descriptions and the first commands are.
                    for (int k = 0; k < 4; k++)
                    {
                        damaged[random.Next(Math.Min(damaged.Length, 600))] ^= (byte)(1 << random.Next(8));
                    }

                    break;
            }

            Assert.Equal(Outcome(damaged, fast: false), Outcome(damaged, fast: true));
        }
    }

    /// <summary>
    /// Found by the fuzzer: a static dictionary reference whose transformation leaves fewer than two bytes (it omits letters) once made the
    /// fast loop read the literal context from before the start of the ring. The file is the fuzz target's input: an options byte, then the stream.
    /// </summary>
    [Fact]
    public void DictionaryWordShorterThanTwoBytesIsDecodedLikeTheGeneralPath()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "brotli", "regress", "dictionary-word-under-two-bytes.bin"));
        byte[] stream = input[1..];
        Assert.Equal(Outcome(stream, fast: false), Outcome(stream, fast: true));
    }

    [Theory]
    [InlineData("mixed530k-q11", false)]
    [InlineData("mixed530k-q11", true)]
    [InlineData("dictwords60k-q5", false)]
    [InlineData("structured60k-q9", false)]
    public void ReusedDecoderAllocatesNothingInSteadyState(string name, bool general)
    {
        BrotliVector v = BrotliTestData.Vector(name);
        var decoder = new BrotliDecoder { FastPathDisabled = general };
        byte[] buffer = new byte[v.Size];

        // The runtime's tiered compilation allocates a little while it warms up, so wait for three clean calls in a row; an allocation
        // made by every call never gets there.
        var allocations = new List<long>();
        int clean = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clean < 3 && clock.ElapsedMilliseconds < 20_000)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            decoder.Reset();
            OperationStatus status = decoder.Decompress(v.Compressed, buffer, out _, out int written);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(OperationStatus.Done, status);
            Assert.Equal(v.Size, written);
            allocations.Add(allocated);
            clean = allocated < 512 ? clean + 1 : 0;
        }

        Assert.True(clean >= 3, $"{name} kept allocating: {string.Join(",", allocations.TakeLast(10))} bytes per call");
    }
}
