using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Ppmd;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for PPMd variants H and I. The decoder target takes its parameters from the first three bytes (variant, order,
/// model size, restore method, whether the output size is known) so the fuzzer need not guess a header, decodes the rest through
/// the one-shot call and through the stream, and treats a disagreement or an exception outside the library's hierarchy as a
/// finding. The round-trip target compresses its data with chosen parameters and requires the decoder to return it.
/// </summary>
internal static class PpmdTargets
{
    public static FuzzTarget Decode { get; } = new("ppmd-decode", static data => RunDecode(data), static e => e is CompressionException);

    public static FuzzTarget RoundTrip { get; } = new("ppmd-roundtrip", static data => RunRoundTrip(data), static e => e is CompressionException);

    // Small models keep each execution cheap; variant I needs its minimum.
    private static int Memory(byte b, bool variantI) => Math.Max(variantI ? PpmdIModel.MinMemorySize : 2048, 2048 << (b % 9));

    private static void RunDecode(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        bool variantI = (input[0] & 1) != 0;
        bool known = (input[0] & 2) != 0;
        var restore = (input[0] & 4) != 0 ? PpmdRestoreMethod.CutOff : PpmdRestoreMethod.Restart;
        int order = variantI ? 2 + input[1] % 15 : 2 + input[1] % 63;
        int mem = Memory(input[2], variantI);
        byte[] data = input[3..].ToArray();
        long size = known ? Math.Min(data.Length * 8L + 16, 1 << 20) : -1;

        (byte[]? whole, string? wholeError) = Attempt(() => variantI
            ? PpmdI.Decompress(data, new PpmdIDecompressionOptions { Order = order, MemorySize = mem, RestoreMethod = restore, OutputSize = size, MaxOutputSize = 1 << 21 })
            : PpmdH.Decompress(data, new PpmdHDecompressionOptions { Order = order, MemorySize = mem, OutputSize = size, MaxOutputSize = 1 << 21 }));
        if (!known)
        {
            // Without a size the end of the data is the end of the stream; only the one-shot path defines that, so the stream is not compared.
            return;
        }

        (byte[]? streamed, string? streamError) = Attempt(() =>
        {
            using var src = new MemoryStream(data);
            using PpmdStream s = variantI
                ? new PpmdStream(src, new PpmdIDecompressionOptions { Order = order, MemorySize = mem, RestoreMethod = restore, OutputSize = size, MaxOutputSize = 1 << 21 })
                : new PpmdStream(src, new PpmdHDecompressionOptions { Order = order, MemorySize = mem, OutputSize = size, MaxOutputSize = 1 << 21 });
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        });
        if ((wholeError is null) != (streamError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, stream {streamError ?? "ok"}");
        }

        if (whole is not null && !whole.AsSpan().SequenceEqual(streamed))
        {
            throw new InvalidOperationException("stream produced different output");
        }
    }

    private static (byte[]?, string?) Attempt(Func<byte[]> decode)
    {
        try
        {
            return (decode(), null);
        }
        catch (CompressionException e)
        {
            return (null, e.GetType().Name);
        }
    }

    private static void RunRoundTrip(ReadOnlySpan<byte> input)
    {
        if (input.Length < 3)
        {
            return;
        }

        bool variantI = (input[0] & 1) != 0;
        var restore = (input[0] & 4) != 0 ? PpmdRestoreMethod.CutOff : PpmdRestoreMethod.Restart;
        bool marker = (input[0] & 8) != 0;
        int order = variantI ? 2 + input[1] % 15 : 2 + input[1] % 63;
        int mem = Memory(input[2], variantI);
        byte[] data = input[3..].ToArray();
        if (variantI)
        {
            byte[] c = PpmdI.Compress(data, new PpmdIOptions { Order = order, MemorySize = mem, RestoreMethod = restore, WriteEndMarker = marker });
            byte[] d = PpmdI.Decompress(c, new PpmdIDecompressionOptions { Order = order, MemorySize = mem, RestoreMethod = restore, OutputSize = data.Length });
            Check(data, d);
        }
        else
        {
            byte[] c = PpmdH.Compress(data, new PpmdHOptions { Order = order, MemorySize = mem, WriteEndMarker = marker });
            byte[] d = PpmdH.Decompress(c, new PpmdHDecompressionOptions { Order = order, MemorySize = mem, OutputSize = data.Length });
            Check(data, d);
        }
    }

    private static void Check(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException("round trip changed the data");
        }
    }

    private static byte[] RandomBytes(int n)
    {
        byte[] b = new byte[n];
        new Random(1).NextBytes(b);
        return b;
    }

    /// <summary>Writes decoder seeds (library output behind the three parameter bytes) into <c>DIR/ppmd-decode</c>.</summary>
    public static void WriteSeeds(string dir)
    {
        string target = Path.Combine(dir, "ppmd-decode");
        Directory.CreateDirectory(target);
        byte[][] payloads =
        [
            System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog\n", 60))),
            RandomBytes(3000),
            new byte[2000],
        ];
        int n = 0;
        foreach (byte[] data in payloads)
        {
            foreach (bool variantI in new[] { false, true })
            {
                n++;
                byte flags = (byte)((variantI ? 1 : 0) | 2 | ((n & 1) << 2));
                byte order = (byte)(4 + n % 6);
                byte memSel = (byte)(2 + n % 4);
                int mem = Memory(memSel, variantI);
                byte[] c = variantI
                    ? PpmdI.Compress(data, new PpmdIOptions { Order = 2 + order % 15, MemorySize = mem, RestoreMethod = (flags & 4) != 0 ? PpmdRestoreMethod.CutOff : PpmdRestoreMethod.Restart })
                    : PpmdH.Compress(data, new PpmdHOptions { Order = 2 + order % 63, MemorySize = mem });
                File.WriteAllBytes(Path.Combine(target, $"seed{n}"), [flags, order, memSel, .. c]);
            }
        }
    }
}
