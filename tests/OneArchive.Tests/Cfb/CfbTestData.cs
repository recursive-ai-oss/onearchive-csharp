using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace RecursiveAI.OneArchive.Tests.Cfb;

/// <summary>The compound file vectors written by tools/cfb/gen_cfb.py and their manifest.</summary>
internal static class CfbTestData
{
    private static readonly string s_dir = Path.Combine(AppContext.BaseDirectory, "testdata", "cfb");

    public static byte[] Read(string file)
    {
        byte[] raw = File.ReadAllBytes(Path.Combine(s_dir, file));
        if (!file.EndsWith(".gz", StringComparison.Ordinal))
        {
            return raw;
        }

        using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
        var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    public static JsonElement Manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(s_dir, "manifest.json")));
        return doc.RootElement.Clone();
    }

    public static IEnumerable<object[]> Good() =>
        Manifest().GetProperty("good").EnumerateArray().Select(e => e.GetProperty("file").GetString()!).Select(f => new object[] { f });

    public static IEnumerable<object[]> Bad() =>
        Manifest().GetProperty("bad").EnumerateArray().Select(e => e.GetProperty("file").GetString()!).Select(f => new object[] { f });

    public static JsonElement GoodEntry(string file) => Manifest().GetProperty("good").EnumerateArray().First(e => e.GetProperty("file").GetString() == file);

    public static JsonElement BadEntry(string file) => Manifest().GetProperty("bad").EnumerateArray().First(e => e.GetProperty("file").GetString() == file);

    public static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
