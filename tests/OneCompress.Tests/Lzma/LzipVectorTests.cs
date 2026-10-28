using RecursiveAI.OneCompress.Lzip;
using LzipApi = RecursiveAI.OneCompress.Lzip.Lzip;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Lzip files written by the lzip tool decode to the recorded output through every path.</summary>
public class LzipVectorTests
{
    public static TheoryData<string> Names() => LzmaTestData.Names(".lz");

    // Files that end with data lzip does not read as a member.
    private static LzipDecompressionOptions OptionsFor(string name) => new() { AllowTrailingData = name.Contains("trailing", StringComparison.Ordinal) };

    [Theory]
    [MemberData(nameof(Names))]
    public void OneShotMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        v.AssertMatches(LzipApi.Decompress(v.Compressed, OptionsFor(name)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ExactBufferAndTooSmallBuffer(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        byte[] output = new byte[v.Size];
        Assert.True(LzipApi.TryDecompress(v.Compressed, output, out int written, OptionsFor(name)));
        Assert.Equal(v.Size, written);
        v.AssertMatches(output);
        if (v.Size > 0)
        {
            Assert.False(LzipApi.TryDecompress(v.Compressed, new byte[v.Size - 1], out written, OptionsFor(name)));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void PiecesGiveTheSameOutput(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        foreach ((int i, int o) in new[] { (1, 4096), (7, 1), (997, 1013), (40, 65536), (100000, 3) })
        {
            if (v.Size > 20000 && (i < 40 || o < 100))
            {
                continue;
            }

            var d = new LzipDecoder(OptionsFor(name));
            v.AssertMatches(LzmaTestData.Pieces((ReadOnlySpan<byte> s, Span<byte> t, out int c, out int w) => d.Decompress(s, t, out c, out w), d.ThrowIfIncomplete, v.Compressed, i, o));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        using var stream = new LzipStream(new LzmaTestData.SlowStream(v.Compressed, 333), OptionsFor(name));
        v.AssertMatches(LzmaTestData.ReadAll(stream, 1009));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task AsyncStreamMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        await using var stream = new LzipStream(new MemoryStream(v.Compressed), OptionsFor(name));
        var result = new MemoryStream();
        await stream.CopyToAsync(result, TestContext.Current.CancellationToken);
        v.AssertMatches(result.ToArray());
    }

    [Fact]
    public void TrailingDataIsAnErrorByDefault()
    {
        LzmaVector v = LzmaTestData.Vector("members3-trailing.lz");
        var e = Assert.Throws<CompressionFormatException>(() => LzipApi.Decompress(v.Compressed));
        Assert.Equal("lzip", e.Format);
    }
}
