using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>One file written by the lz4 tool and what it must decode to.</summary>
public sealed record Lz4Vector(string Name, int Size, string Sha256, string Dictionary, string Command)
{
    public byte[] Compressed => File.ReadAllBytes(Lz4TestData.Path("vectors", Name + ".lz4"));

    public Lz4DecompressionOptions Options() => new()
    {
        Dictionary = Dictionary.Length == 0 ? null : new Lz4Dictionary(File.ReadAllBytes(Lz4TestData.Path(Dictionary))),
    };

    public void AssertMatches(ReadOnlySpan<byte> output)
    {
        Assert.Equal(Size, output.Length);
        Assert.Equal(Sha256, Convert.ToHexStringLower(SHA256.HashData(output)));
    }

    public override string ToString() => Name;
}

public static class Lz4TestData
{
    public static string Root { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "lz4");

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public static IReadOnlyList<Lz4Vector> Vectors { get; } = Load();

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (Lz4Vector v in Vectors)
        {
            data.Add(v.Name);
        }

        return data;
    }

    public static Lz4Vector Vector(string name) => Vectors.First(v => v.Name == name);

    private static List<Lz4Vector> Load()
    {
        var list = new List<Lz4Vector>();
        foreach (string line in File.ReadAllLines(Path("manifest.tsv")))
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            string[] f = line.Split('\t');
            list.Add(new Lz4Vector(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), f[2], f[4], f[5]));
        }

        return list;
    }
}
