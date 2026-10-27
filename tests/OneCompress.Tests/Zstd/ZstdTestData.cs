using System.Security.Cryptography;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>One row of testdata/zstd/manifest.tsv: a frame written by the zstd tool and what it must decode to.</summary>
public sealed record ZstdVector(string Name, int Size, string Sha256, long MaxWindow, string Dictionary, string Command)
{
    public byte[] Compressed => File.ReadAllBytes(ZstdTestData.Path("vectors", Name + ".zst"));

    public ZstdDecompressionOptions Options()
    {
        long window = Math.Max(ZstdDecompressionOptions.DefaultMaxWindowSize, MaxWindow);
        ZstdDictionary? dictionary = Dictionary.Length == 0 ? null : ZstdDictionary.Parse(File.ReadAllBytes(ZstdTestData.Path(Dictionary)));
        return new ZstdDecompressionOptions { MaxWindowSize = window, Dictionary = dictionary };
    }

    public void AssertMatches(ReadOnlySpan<byte> output)
    {
        Assert.Equal(Size, output.Length);
        Assert.Equal(Sha256, Convert.ToHexStringLower(SHA256.HashData(output)));
    }

    public override string ToString() => Name;
}

public static class ZstdTestData
{
    public static string Root { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "zstd");

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public static IReadOnlyList<ZstdVector> Vectors { get; } = Load();

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (ZstdVector v in Vectors)
        {
            data.Add(v.Name);
        }

        return data;
    }

    public static ZstdVector Vector(string name) => Vectors.First(v => v.Name == name);

    private static List<ZstdVector> Load()
    {
        var list = new List<ZstdVector>();
        foreach (string line in File.ReadAllLines(Path("manifest.tsv")))
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            string[] f = line.Split('\t');
            list.Add(new ZstdVector(f[0], int.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture), f[2], long.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture), f[5], f[6]));
        }

        return list;
    }
}
