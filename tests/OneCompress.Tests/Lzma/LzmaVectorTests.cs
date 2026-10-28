using RecursiveAI.OneCompress.Lzma;
using LzmaApi = RecursiveAI.OneCompress.Lzma.Lzma;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>.lzma files written by the xz tool decode to the recorded output through every path.</summary>
public class LzmaVectorTests
{
    public static TheoryData<string> Names() => LzmaTestData.Names(".lzma");

    [Theory]
    [MemberData(nameof(Names))]
    public void OneShotMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        v.AssertMatches(LzmaApi.Decompress(v.Compressed));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ExactBufferAndTooSmallBuffer(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        byte[] output = new byte[v.Size];
        Assert.True(LzmaApi.TryDecompress(v.Compressed, output, out int written));
        Assert.Equal(v.Size, written);
        v.AssertMatches(output);
        if (v.Size > 0)
        {
            Assert.False(LzmaApi.TryDecompress(v.Compressed, new byte[v.Size - 1], out written));
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

            var d = new LzmaDecoder();
            v.AssertMatches(LzmaTestData.Pieces((ReadOnlySpan<byte> s, Span<byte> t, out int c, out int w) => d.Decompress(s, t, out c, out w), d.ThrowIfIncomplete, v.Compressed, i, o));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        using var stream = new LzmaStream(new LzmaTestData.SlowStream(v.Compressed, 333));
        v.AssertMatches(LzmaTestData.ReadAll(stream, 1009));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task AsyncStreamMatchesReference(string name)
    {
        LzmaVector v = LzmaTestData.Vector(name);
        await using var stream = new LzmaStream(new MemoryStream(v.Compressed));
        var result = new MemoryStream();
        await stream.CopyToAsync(result, TestContext.Current.CancellationToken);
        v.AssertMatches(result.ToArray());
    }

    [Fact]
    public void RawStreamDecodesWithPropertiesFromOutside()
    {
        LzmaVector v = LzmaTestData.Vector("text150k.lzma");
        var props = new LzmaProperties(v.Compressed[0] % 9, v.Compressed[0] / 9 % 5, v.Compressed[0] / 45, BitConverter.ToUInt32(v.Compressed, 1));
        var decoder = LzmaDecoder.CreateRaw(props, v.Size);
        byte[] output = new byte[v.Size];
        System.Buffers.OperationStatus status = decoder.Decompress(v.Compressed.AsSpan(13), output, out int consumed, out int written);
        Assert.Equal(System.Buffers.OperationStatus.Done, status);
        Assert.Equal(v.Size, written);
        v.AssertMatches(output);
    }
}
