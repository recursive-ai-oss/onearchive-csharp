using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Brotli;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Vectors written by the brotli tool (testdata/brotli/generate.py).</summary>
internal sealed record BrotliVector(string Name, int Size, string Sha256, byte[] Compressed)
{
    public void AssertMatches(byte[] output)
    {
        Assert.Equal(Size, output.Length);
        Assert.Equal(Sha256, Convert.ToHexStringLower(SHA256.HashData(output)));
    }
}

internal static class BrotliTestData
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "testdata", "brotli");

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

    public static BrotliVector Vector(string name)
    {
        string[] row = s_rows.Value.Single(r => r[0] == name);
        return new BrotliVector(name, int.Parse(row[1], CultureInfo.InvariantCulture), row[2], File.ReadAllBytes(Path.Combine(Dir, "vectors", name + ".br")));
    }

    /// <summary>Decodes through the span decoder with input and output cut into pieces of the given sizes.</summary>
    public static byte[] DecodeInPieces(byte[] compressed, int inputPiece, int outputPiece, BrotliDecompressionOptions? options = null)
    {
        var decoder = new BrotliDecoder(options);
        var output = new MemoryStream();
        byte[] buffer = new byte[outputPiece];
        int pos = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(compressed.AsSpan(pos, Math.Min(inputPiece, compressed.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done || (pos >= compressed.Length && status != OperationStatus.DestinationTooSmall))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }

    public static byte[] DecodeThroughStream(byte[] compressed, int readSize, int bufferSize, BrotliDecompressionOptions? options = null)
    {
        using var stream = new BrotliStream(new SlowStream(compressed, readSize), options);
        var result = new MemoryStream();
        byte[] buffer = new byte[bufferSize];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.Write(buffer, 0, n);
        }

        return result.ToArray();
    }

    internal sealed class SlowStream(byte[] data, int max) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, max));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, max)]);
    }
}

/// <summary>Streams written by the brotli tool decode to the recorded output through every path.</summary>
public class BrotliVectorTests
{
    public static TheoryData<string> Names() => BrotliTestData.Names();

    [Theory]
    [MemberData(nameof(Names))]
    public void OneShotMatchesReference(string name)
    {
        BrotliVector v = BrotliTestData.Vector(name);
        v.AssertMatches(BR.Decompress(v.Compressed));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ExactBufferAndTooSmallBuffer(string name)
    {
        BrotliVector v = BrotliTestData.Vector(name);
        byte[] output = new byte[v.Size];
        Assert.True(BR.TryDecompress(v.Compressed, output, out int written));
        Assert.Equal(v.Size, written);
        v.AssertMatches(output);
        if (v.Size > 0)
        {
            Assert.False(BR.TryDecompress(v.Compressed, new byte[v.Size - 1], out written));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamMatchesReference(string name)
    {
        BrotliVector v = BrotliTestData.Vector(name);
        v.AssertMatches(BrotliTestData.DecodeThroughStream(v.Compressed, 4093, 3001));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void PiecewiseMatchesReference(string name)
    {
        BrotliVector v = BrotliTestData.Vector(name);
        v.AssertMatches(BrotliTestData.DecodeInPieces(v.Compressed, 997, 1013));
        v.AssertMatches(BrotliTestData.DecodeInPieces(v.Compressed, 1, 65536));
        v.AssertMatches(BrotliTestData.DecodeInPieces(v.Compressed, 100000, 7));
    }

    [Fact]
    public async Task AsyncStreamMatchesReference()
    {
        BrotliVector v = BrotliTestData.Vector("mixed530k-q11");
        await using var stream = new BrotliStream(new MemoryStream(v.Compressed));
        var result = new MemoryStream();
        await stream.CopyToAsync(result, TestContext.Current.CancellationToken);
        v.AssertMatches(result.ToArray());
    }
}
