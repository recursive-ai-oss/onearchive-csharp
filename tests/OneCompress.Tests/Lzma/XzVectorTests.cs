using RecursiveAI.OneCompress.Xz;
using XzApi = RecursiveAI.OneCompress.Xz.Xz;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>.xz files written by the xz and 7-Zip tools decode to the recorded output through every path.</summary>
public class XzVectorTests
{
    public static TheoryData<string> Names() => LzmaTestData.Names(".xz");

    [Theory]
    [MemberData(nameof(Names))]
    public void OneShotMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        v.AssertMatches(XzApi.Decompress(v.Compressed));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ExactBufferAndTooSmallBuffer(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        byte[] output = new byte[v.Size];
        Assert.True(XzApi.TryDecompress(v.Compressed, output, out int written));
        Assert.Equal(v.Size, written);
        v.AssertMatches(output);
        if (v.Size > 0)
        {
            Assert.False(XzApi.TryDecompress(v.Compressed, new byte[v.Size - 1], out written));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void PiecesGiveTheSameOutput(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        foreach ((int i, int o) in new[] { (1, 4096), (7, 1), (997, 1013), (40, 65536), (100000, 3), (65536, 70000) })
        {
            if (v.Size > 20000 && (i < 40 || o < 100))
            {
                continue;
            }

            var d = new XzDecoder();
            v.AssertMatches(LzmaTestData.Pieces((ReadOnlySpan<byte> s, Span<byte> t, out int c, out int w) => d.Decompress(s, t, out c, out w), d.ThrowIfIncomplete, v.Compressed, i, o));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        using var stream = new XzStream(new LzmaTestData.SlowStream(v.Compressed, 333));
        v.AssertMatches(LzmaTestData.ReadAll(stream, 1009));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task AsyncStreamMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        await using var stream = new XzStream(new MemoryStream(v.Compressed));
        var result = new MemoryStream();
        await stream.CopyToAsync(result, TestContext.Current.CancellationToken);
        v.AssertMatches(result.ToArray());
    }
}
