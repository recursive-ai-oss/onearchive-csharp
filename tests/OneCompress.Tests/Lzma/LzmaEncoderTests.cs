using System.Buffers;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Round trips of the LZMA and LZMA2 encoders through this library's decoders, over inputs that exercise every symbol kind.</summary>
public class LzmaEncoderTests
{
    internal static byte[] Text(int size, int seed)
    {
        string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "compression", "window", "dictionary", "literal", "match", "range", "coder", "\n", ", ", ". "];
        var rng = new Random(seed);
        var sb = new List<byte>(size + 16);
        while (sb.Count < size)
        {
            sb.AddRange(System.Text.Encoding.ASCII.GetBytes(words[rng.Next(words.Length)] + " "));
        }

        return [.. sb.Take(size)];
    }

    internal static byte[] Random(int size, int seed)
    {
        byte[] data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    // Records with a repeating structure, near repeats and long runs: reps at several distances, short matches, and far matches.
    internal static byte[] Structured(int size, int seed)
    {
        var rng = new Random(seed);
        byte[] data = new byte[size];
        int i = 0;
        while (i < size)
        {
            int kind = rng.Next(5);
            int n = Math.Min(size - i, 1 + rng.Next(300));
            switch (kind)
            {
                case 0: rng.NextBytes(data.AsSpan(i, n)); break;
                case 1: data.AsSpan(i, n).Fill((byte)rng.Next(4)); break;
                default:
                    if (i > 8)
                    {
                        int dist = kind == 2 ? 1 + rng.Next(Math.Min(i, 16)) : 1 + rng.Next(i);
                        for (int k = 0; k < n; k++)
                        {
                            data[i + k] = data[i + k - dist];
                        }

                        if (rng.Next(3) == 0)
                        {
                            data[i + (n / 2)] ^= 0x55;
                        }
                    }
                    else
                    {
                        rng.NextBytes(data.AsSpan(i, n));
                    }

                    break;
            }

            i += n;
        }

        return data;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (int preset in new[] { 0, 1, 3, 4, 6, 9 })
        {
            foreach (int size in new[] { 0, 1, 2, 3, 17, 1000, 70_000, 400_000 })
            {
                yield return [preset, size, "text"];
                yield return [preset, size, "random"];
                yield return [preset, size, "structured"];
                yield return [preset, size, "zeros"];
            }
        }
    }

    internal static byte[] Make(string kind, int size) => kind switch
    {
        "text" => Text(size, size),
        "random" => Random(size, size + 1),
        "structured" => Structured(size, size + 2),
        _ => new byte[size],
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void LzmaRoundTrips(int preset, int size, string kind)
    {
        byte[] input = Make(kind, size);
        byte[] packed = Lzma.Lzma.Compress(input, new LzmaOptions { Preset = preset });
        Assert.Equal(input, Lzma.Lzma.Decompress(packed));
        byte[] withMarker = Lzma.Lzma.Compress(input, new LzmaOptions { Preset = preset, WriteEndMarker = true });
        Assert.Equal(input, Lzma.Lzma.Decompress(withMarker));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Lzma2RoundTrips(int preset, int size, string kind)
    {
        byte[] input = Make(kind, size);
        byte[] packed = Lzma.Lzma2.Compress(input, out byte dict, new LzmaOptions { Preset = preset });
        Assert.Equal(input, Lzma.Lzma2.Decompress(packed, dict));
    }

    [Fact]
    public void Lzma2StoresIncompressibleData()
    {
        byte[] input = Random(300_000, 5);
        byte[] packed = Lzma.Lzma2.Compress(input, out byte dict);
        Assert.True(packed.Length <= input.Length + 3 * 5 + 1, $"{packed.Length}");
        Assert.Equal(input, Lzma.Lzma2.Decompress(packed, dict));
    }

    [Fact]
    public void Lzma2MixesStoredAndCodedChunks()
    {
        // Compressible, incompressible, compressible: exercises the state reset after a stored chunk.
        byte[] input = [.. Text(200_000, 1), .. Random(150_000, 2), .. Text(200_000, 3), .. Random(70_000, 4), .. new byte[100_000]];
        foreach (int preset in new[] { 1, 6 })
        {
            byte[] packed = Lzma.Lzma2.Compress(input, out byte dict, new LzmaOptions { Preset = preset });
            Assert.Equal(input, Lzma.Lzma2.Decompress(packed, dict));
        }
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(3, 0, 0)]
    [InlineData(0, 4, 4)]
    [InlineData(4, 0, 4)]
    [InlineData(2, 2, 3)]
    public void LiteralAndPositionBitsRoundTrip(int lc, int lp, int pb)
    {
        byte[] input = Structured(150_000, 9);
        var options = new LzmaOptions { Preset = 5, Lc = lc, Lp = lp, Pb = pb };
        Assert.Equal(input, Lzma.Lzma.Decompress(Lzma.Lzma.Compress(input, options)));
        byte[] packed = Lzma.Lzma2.Compress(input, out byte dict, options);
        Assert.Equal(input, Lzma.Lzma2.Decompress(packed, dict));
    }

    [Theory]
    [InlineData(LzmaMatchFinderKind.HashChain3)]
    [InlineData(LzmaMatchFinderKind.HashChain4)]
    [InlineData(LzmaMatchFinderKind.BinaryTree2)]
    [InlineData(LzmaMatchFinderKind.BinaryTree3)]
    [InlineData(LzmaMatchFinderKind.BinaryTree4)]
    public void EveryMatchFinderAndModeRoundTrips(LzmaMatchFinderKind kind)
    {
        byte[] input = [.. Text(120_000, 4), .. Structured(120_000, 5)];
        foreach (LzmaMode mode in new[] { LzmaMode.Fast, LzmaMode.Normal })
        {
            var options = new LzmaOptions { Preset = 5, MatchFinder = kind, Mode = mode, DictionarySize = 1 << 16, NiceLength = 40 };
            Assert.Equal(input, Lzma.Lzma.Decompress(Lzma.Lzma.Compress(input, options)));
        }
    }

    [Fact]
    public void SmallDictionaryAndSlidingWindowRoundTrip()
    {
        // Far more input than window: the window slides many times and matches must never reach past the dictionary.
        byte[] input = [.. Structured(900_000, 11), .. Text(900_000, 12)];
        var options = new LzmaOptions { Preset = 4, DictionarySize = 1 << 12 };
        using var encoder = new MemoryStream();
        var enc = new Lzma2Encoder(options);
        byte[] outBuf = new byte[1000];
        int pos = 0;
        while (true)
        {
            int take = Math.Min(777, input.Length - pos);
            OperationStatus s = enc.Compress(input.AsSpan(pos, take), outBuf, out int c, out int w, pos + take == input.Length);
            pos += c;
            encoder.Write(outBuf, 0, w);
            if (s == OperationStatus.Done)
            {
                break;
            }
        }

        Assert.Equal(input, Lzma.Lzma2.Decompress(encoder.ToArray(), enc.DictionaryProperty));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 13)]
    [InlineData(1000, 100)]
    [InlineData(65_536, 1 << 20)]
    public void StreamingMatchesOneCall(int inputPiece, int outputPiece)
    {
        byte[] input = [.. Text(300_000, 21), .. Structured(100_000, 22), .. Random(30_000, 23)];
        foreach (int preset in new[] { 1, 6 })
        {
            var options = new LzmaOptions { Preset = preset, DictionarySize = 1 << 17 };
            byte[] expected = Lzma.Lzma2.Compress(input, out _, options);
            byte[] expectedAlone = Lzma.Lzma.Compress(input, options);
            var enc = new Lzma2Encoder(options);
            Assert.Equal(expected, Drive(enc.Compress, input, inputPiece, outputPiece));
            var alone = new LzmaEncoder(options, input.Length);
            Assert.Equal(expectedAlone, Drive(alone.Compress, input, inputPiece, outputPiece));
        }

    }

    private delegate OperationStatus CompressStep(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinal);

    private static byte[] Drive(CompressStep step, byte[] input, int inputPiece, int outputPiece)
    {
        var result = new MemoryStream();
        byte[] buffer = new byte[outputPiece];
        int pos = 0;
        while (true)
        {
            int take = Math.Min(inputPiece, input.Length - pos);
            OperationStatus s = step(input.AsSpan(pos, take), buffer, out int c, out int w, pos + take == input.Length);
            pos += c;
            result.Write(buffer, 0, w);
            if (s == OperationStatus.Done)
            {
                return result.ToArray();
            }
        }
    }
}
