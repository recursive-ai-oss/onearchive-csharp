using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Vectors written by the reference tools (testdata/deflate/generate.py) and generated inputs.</summary>
internal sealed record DeflateVector(string Name, DeflateFormat Format, int Size, string Sha256, byte[] Compressed, byte[]? Dictionary)
{
    public DeflateDecompressionOptions Options() => new() { Dictionary = Dictionary };

    public void AssertMatches(byte[] output)
    {
        Assert.Equal(Size, output.Length);
        Assert.Equal(Sha256, Convert.ToHexStringLower(SHA256.HashData(output)));
    }
}

internal static class DeflateTestData
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "testdata", "deflate");

    private static readonly Lazy<List<string[]>> s_rows = new(() =>
        File.ReadAllLines(Path.Combine(Dir, "manifest.tsv")).Where(static l => !l.StartsWith('#')).Select(static l => l.Split('\t')).ToList());

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (string[] row in s_rows.Value)
        {
            data.Add(row[0]);
        }

        return data;
    }

    public static DeflateVector Vector(string name)
    {
        string[] row = s_rows.Value.Single(r => r[0] == name);
        DeflateFormat format = row[1] switch { "raw" => DeflateFormat.Raw, "zlib" => DeflateFormat.ZLib, _ => DeflateFormat.GZip };
        string ext = format switch { DeflateFormat.Raw => "deflate", DeflateFormat.ZLib => "zz", _ => "gz" };
        byte[] compressed = File.ReadAllBytes(Path.Combine(Dir, "vectors", name + "." + ext));
        byte[]? dictionary = row[5].Length > 0 ? File.ReadAllBytes(Path.Combine(Dir, row[5])) : null;
        return new DeflateVector(name, format, int.Parse(row[2], CultureInfo.InvariantCulture), row[3], compressed, dictionary);
    }

    // Inputs of several shapes, from a fixed generator.
    public static byte[] Text(int seed, int size)
    {
        string[] words = ["the", "of", "and", "archive", "window", "sequence", "literal", "offset", "match", "block", "frame", "table", "state", "entropy", "symbol"];
        var r = new Random(seed);
        var output = new byte[size];
        int at = 0;
        while (at < size)
        {
            string w = words[r.Next(words.Length)] + (r.Next(11) == 0 ? "\n" : " ");
            for (int i = 0; i < w.Length && at < size; i++)
            {
                output[at++] = (byte)w[i];
            }
        }

        return output;
    }

    public static byte[] RandomBytes(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static byte[] Runs(int seed, int size)
    {
        var r = new Random(seed);
        var output = new byte[size];
        int at = 0;
        while (at < size)
        {
            byte b = (byte)r.Next(256);
            int n = 1 + r.Next(3000);
            for (int i = 0; i < n && at < size; i++)
            {
                output[at++] = b;
            }
        }

        return output;
    }

    public static IEnumerable<(string Name, byte[] Data)> Inputs()
    {
        yield return ("empty", []);
        yield return ("one", [42]);
        yield return ("text1k", Text(1, 1000));
        yield return ("text200k", Text(2, 200_000));
        yield return ("random100k", RandomBytes(3, 100_000));
        yield return ("runs500k", Runs(4, 500_000));
        var mixed = new byte[300_000];
        Text(5, 100_000).CopyTo(mixed, 0);
        RandomBytes(6, 50_000).CopyTo(mixed, 100_000);
        Runs(7, 100_000).CopyTo(mixed, 150_000);
        Text(5, 50_000).CopyTo(mixed, 250_000); // a far repeat of the start
        yield return ("mixed300k", mixed);
    }

    // The BCL's codecs, run as a black box, write the streams.
    public static byte[] BclCompress(DeflateFormat format, byte[] data, CompressionLevel level)
    {
        var ms = new MemoryStream();
        using (Stream s = format switch
        {
            DeflateFormat.Raw => new System.IO.Compression.DeflateStream(ms, level, leaveOpen: true),
            DeflateFormat.ZLib => new ZLibStream(ms, level, leaveOpen: true),
            _ => new GZipStream(ms, level, leaveOpen: true),
        })
        {
            s.Write(data);
        }

        return ms.ToArray();
    }
}
