using System.Buffers;
using System.Diagnostics;
using RecursiveAI.OneCompress.Brotli;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Brotli compression: round trips through our decoder at every quality and window, streaming with flushes, determinism and the raw-layer parts.</summary>
public class BrotliEncoderTests
{
    private static readonly int[] s_windows = [10, 16, 22, 24];

    private static byte[] Compress(byte[] data, int quality, int window) =>
        BR.Compress(data, new BrotliOptions { Quality = quality, WindowBits = window });

    [Fact]
    public void RoundTripsGeneratedInputsAtEveryQualityAndWindow()
    {
        foreach ((string name, byte[] data) in BrotliInteropTests.GeneratedInputs())
        {
            bool big = data.Length > 1_000_000;
            foreach (int window in s_windows)
            {
                for (int q = 0; q <= 11; q++)
                {
                    if (big && (window != 22 || q is not (0 or 1 or 4 or 6)))
                    {
                        continue;
                    }

                    byte[] packed = Compress(data, q, window);
                    byte[] back = BR.Decompress(packed);
                    Assert.True(data.AsSpan().SequenceEqual(back), $"{name} q{q} w{window}: round trip differs");
                }
            }
        }
    }

    [Fact]
    public void EmptyInputIsAValidStream()
    {
        byte[] packed = Compress([], 5, 22);
        Assert.NotEmpty(packed);
        Assert.Empty(BR.Decompress(packed));
    }

    [Fact]
    public void OutputIsDeterministicAndIndependentOfCallSizes()
    {
        byte[] data = BZip2TestData.Text(5, 700_000);
        foreach (int q in new[] { 1, 4, 6 })
        {
            byte[] whole = Compress(data, q, 20);
            Assert.True(whole.AsSpan().SequenceEqual(Compress(data, q, 20)), $"q{q}: two runs differ");

            var encoder = new BrotliEncoder(new BrotliOptions { Quality = q, WindowBits = 20 });
            var output = new MemoryStream();
            byte[] buffer = new byte[777];
            var rng = new Random(q);
            int pos = 0;
            while (true)
            {
                int n = Math.Min(data.Length - pos, 1 + rng.Next(50_000));
                bool last = pos + n == data.Length;
                OperationStatus status = encoder.Compress(data.AsSpan(pos, n), buffer, out int consumed, out int written, last);
                output.Write(buffer, 0, written);
                pos += consumed;
                if (status == OperationStatus.Done)
                {
                    break;
                }

                if (status == OperationStatus.DestinationTooSmall)
                {
                    continue;
                }

                Assert.True(pos < data.Length || !last);
            }

            Assert.True(whole.AsSpan().SequenceEqual(output.ToArray()), $"q{q}: cutting the input changed the output");
        }
    }

    [Fact]
    public void FlushMakesEverythingWrittenDecodable()
    {
        byte[] data = BZip2TestData.Text(3, 400_000);
        foreach (int q in new[] { 0, 3, 5, 9 })
        {
            var encoder = new BrotliEncoder(new BrotliOptions { Quality = q, WindowBits = 18 });
            var output = new MemoryStream();
            byte[] buffer = new byte[4096];
            var rng = new Random(100 + q);
            int pos = 0;
            while (pos < data.Length)
            {
                int n = Math.Min(data.Length - pos, 1 + rng.Next(70_000));
                OperationStatus status;
                int offset = 0;
                do
                {
                    status = encoder.Compress(data.AsSpan(pos + offset, n - offset), buffer, out int consumed, out int written);
                    offset += consumed;
                    output.Write(buffer, 0, written);
                }
                while (status == OperationStatus.DestinationTooSmall || offset < n);
                pos += n;

                do
                {
                    status = encoder.Flush(buffer, out int written);
                    output.Write(buffer, 0, written);
                }
                while (status == OperationStatus.DestinationTooSmall);

                // Everything given so far must now come out of a decoder fed only what has been written.
                var decoder = new BrotliDecoder();
                byte[] got = new byte[pos];
                decoder.Decompress(output.ToArray(), got, out _, out int produced);
                Assert.Equal(pos, produced);
                Assert.True(data.AsSpan(0, pos).SequenceEqual(got), $"q{q}: flushed output differs at {pos}");
            }

            OperationStatus end;
            do
            {
                end = encoder.Compress([], buffer, out _, out int written, true);
                output.Write(buffer, 0, written);
            }
            while (end != OperationStatus.Done);
            Assert.True(data.AsSpan().SequenceEqual(BR.Decompress(output.ToArray())));
        }
    }

    [Fact]
    public void TryCompressRespectsTheDestination()
    {
        byte[] data = BZip2TestData.Text(8, 100_000);
        byte[] big = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        Assert.True(BR.TryCompress(data, big, out int n, new BrotliOptions { Quality = 4 }));
        Assert.True(data.AsSpan().SequenceEqual(BR.Decompress(big.AsSpan(0, n))));
        Assert.False(BR.TryCompress(data, new byte[n / 2], out int none, new BrotliOptions { Quality = 4 }));
        Assert.Equal(0, none);
    }

    [Fact]
    public void IncompressibleDataStaysWithinTheBound()
    {
        var random = new byte[1_000_000];
        new Random(5).NextBytes(random);
        foreach (int q in new[] { 0, 5, 9 })
        {
            byte[] packed = Compress(random, q, 22);
            Assert.True(packed.Length <= BrotliEncoder.GetMaxCompressedLength(random.Length), $"q{q}: {packed.Length}");
            Assert.True(packed.Length < random.Length + 200, $"q{q}: random data should be stored, got {packed.Length}");
            Assert.True(random.AsSpan().SequenceEqual(BR.Decompress(packed)));
        }
    }

    [Fact]
    public void StaticDictionaryHelpsSmallText()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog while thinking about international government information.");
        int plain = Compress(data, 4, 22).Length;
        int withDictionary = Compress(data, 9, 22).Length;
        Assert.True(withDictionary < plain, $"q9 {withDictionary} should beat q4 {plain} on text the dictionary knows");
    }

    [Fact]
    public void HuffmanLengthsAreCompleteAndLimited()
    {
        var rng = new Random(3);
        foreach (int size in new[] { 2, 3, 7, 64, 256, 704 })
        {
            for (int trial = 0; trial < 20; trial++)
            {
                int[] counts = new int[size];
                for (int i = 0; i < size; i++)
                {
                    counts[i] = rng.Next(3) == 0 ? 0 : 1 + (int)Math.Pow(2, rng.Next(24));
                }

                counts[0] = Math.Max(counts[0], 1);
                counts[size - 1] = Math.Max(counts[size - 1], 1);
                byte[] lengths = new byte[size];
                BrotliHuffmanCode.BuildLengths(counts, 15, lengths);
                double kraft = 0;
                for (int i = 0; i < size; i++)
                {
                    Assert.Equal(counts[i] > 0, lengths[i] > 0);
                    Assert.True(lengths[i] <= 15);
                    if (lengths[i] > 0)
                    {
                        kraft += Math.Pow(2, -lengths[i]);
                    }
                }

                Assert.Equal(1.0, kraft);
                var code = BrotliPrefixCode.FromCodeLengths(lengths);
                Assert.NotNull(code);
            }
        }
    }

    [Fact]
    public void OptionsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrotliOptions { Quality = 12 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrotliOptions { Quality = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrotliOptions { WindowBits = 9 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrotliOptions { WindowBits = 25 });
    }

    [Fact]
    public void ToolDecodesOurStreamsOfGeneratedInputs()
    {
        BrotliInteropTests.RequireEnabled();
        foreach ((string name, byte[] data) in BrotliInteropTests.GeneratedInputs())
        {
            bool big = data.Length > 1_000_000;
            foreach (int window in s_windows)
            {
                for (int q = 0; q <= 11; q++)
                {
                    if (big && (window != 22 || q is not (0 or 1 or 4 or 6 or 9 or 11)))
                    {
                        continue;
                    }

                    CheckWithTool(data, Compress(data, q, window), $"{name} q{q} w{window}");
                }
            }
        }
    }

    [Theory]
    [InlineData("dickens")]
    [InlineData("xml")]
    [InlineData("x-ray")]
    public void ToolDecodesOurStreamsOfSilesiaFiles(string file)
    {
        BrotliInteropTests.RequireEnabled();
        string corpora = BrotliInteropTests.Corpora;
        string path = new[] { Path.Combine(corpora, file), Path.Combine(corpora, "silesia", file), Path.Combine(corpora, "x", file) }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"corpus file {file} not found under {corpora}");
        byte[] data = File.ReadAllBytes(path);
        foreach (int window in new[] { 16, 22, 24 })
        {
            foreach (int q in new[] { 1, 5, 9 })
            {
                CheckWithTool(data, Compress(data, q, window), $"{file} q{q} w{window}");
            }
        }
    }

    [Fact]
    public void ToolDecodesOurFlushedStreams()
    {
        BrotliInteropTests.RequireEnabled();
        byte[] data = BZip2TestData.Text(4, 500_000);
        foreach (int q in new[] { 1, 5 })
        {
            var encoder = new BrotliEncoder(new BrotliOptions { Quality = q, WindowBits = 20 });
            var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            for (int pos = 0; pos < data.Length; pos += 100_000)
            {
                int n = Math.Min(100_000, data.Length - pos);
                int offset = 0;
                OperationStatus status;
                do
                {
                    status = encoder.Compress(data.AsSpan(pos + offset, n - offset), buffer, out int consumed, out int written);
                    offset += consumed;
                    output.Write(buffer, 0, written);
                }
                while (status == OperationStatus.DestinationTooSmall || offset < n);
                do
                {
                    status = encoder.Flush(buffer, out int written);
                    output.Write(buffer, 0, written);
                }
                while (status == OperationStatus.DestinationTooSmall);
            }

            OperationStatus end;
            do
            {
                end = encoder.Compress([], buffer, out _, out int written, true);
                output.Write(buffer, 0, written);
            }
            while (end != OperationStatus.Done);
            CheckWithTool(data, output.ToArray(), $"flushed q{q}");
        }
    }

    private static void CheckWithTool(byte[] original, byte[] compressed, string label)
    {
        Assert.True(original.AsSpan().SequenceEqual(BR.Decompress(compressed)), label + ": our decoder differs");
        string input = Path.Combine(BrotliInteropTests.Scratch, "ours.br");
        File.WriteAllBytes(input, compressed);
        var psi = new ProcessStartInfo(BrotliInteropTests.Tool()) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-d", "-c", input })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        var stdout = new MemoryStream();
        Task copy = p.StandardOutput.BaseStream.CopyToAsync(stdout);
        string err = p.StandardError.ReadToEnd();
        copy.Wait();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{label}: brotli -d failed: {err}");
        Assert.True(original.AsSpan().SequenceEqual(stdout.ToArray()), label + ": the tool's output differs");
    }
}
