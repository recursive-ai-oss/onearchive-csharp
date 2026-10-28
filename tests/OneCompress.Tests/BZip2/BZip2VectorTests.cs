using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;
using RecursiveAI.OneCompress.BZip2;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Vectors written by the bzip2 tool (testdata/bzip2/generate.py).</summary>
internal sealed record BZip2Vector(string Name, int Size, string Sha256, byte[] Compressed)
{
    public void AssertMatches(byte[] output)
    {
        Assert.Equal(Size, output.Length);
        Assert.Equal(Sha256, Convert.ToHexStringLower(SHA256.HashData(output)));
    }
}

internal static class BZip2TestData
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "testdata", "bzip2");

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

    public static BZip2Vector Vector(string name)
    {
        string[] row = s_rows.Value.Single(r => r[0] == name);
        return new BZip2Vector(name, int.Parse(row[1], CultureInfo.InvariantCulture), row[2], File.ReadAllBytes(Path.Combine(Dir, "vectors", name + ".bz2")));
    }

    /// <summary>Decodes through the span decoder with input and output cut into pieces of the given sizes.</summary>
    public static byte[] DecodeInPieces(byte[] compressed, int inputPiece, int outputPiece, BZip2DecompressionOptions? options = null)
    {
        var decoder = new BZip2Decoder(options);
        var output = new MemoryStream();
        byte[] buffer = new byte[outputPiece];
        int pos = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(compressed.AsSpan(pos, Math.Min(inputPiece, compressed.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (pos >= compressed.Length && status != OperationStatus.DestinationTooSmall && !(status == OperationStatus.Done && pos < compressed.Length))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }

    public static byte[] DecodeThroughStream(byte[] compressed, int readSize, int bufferSize, BZip2DecompressionOptions? options = null)
    {
        using var stream = new BZip2Stream(new SlowStream(compressed, readSize), options);
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

    public static byte[] RandomBytes(int size)
    {
        var data = new byte[size];
        new Random(11).NextBytes(data);
        return data;
    }

    public static byte[] Text(int seed, int size)
    {
        string[] words = ["the", "of", "and", "archive", "block", "sort", "table", "run", "length", "symbol", "entropy", "state"];
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
}

/// <summary>Streams written by the bzip2 tool decode to the recorded output through every path.</summary>
public class BZip2VectorTests
{
    public static TheoryData<string> Names() => BZip2TestData.Names();

    [Theory]
    [MemberData(nameof(Names))]
    public void OneShotMatchesReference(string name)
    {
        BZip2Vector v = BZip2TestData.Vector(name);
        v.AssertMatches(BZ.Decompress(v.Compressed));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ExactBufferAndTooSmallBuffer(string name)
    {
        BZip2Vector v = BZip2TestData.Vector(name);
        byte[] output = new byte[v.Size];
        Assert.True(BZ.TryDecompress(v.Compressed, output, out int written));
        Assert.Equal(v.Size, written);
        v.AssertMatches(output);
        if (v.Size > 0)
        {
            Assert.False(BZ.TryDecompress(v.Compressed, new byte[v.Size - 1], out written));
            Assert.Equal(0, written);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamMatchesReference(string name)
    {
        BZip2Vector v = BZip2TestData.Vector(name);
        v.AssertMatches(BZip2TestData.DecodeThroughStream(v.Compressed, 4093, 3001));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void PiecewiseMatchesReference(string name)
    {
        BZip2Vector v = BZip2TestData.Vector(name);
        v.AssertMatches(BZip2TestData.DecodeInPieces(v.Compressed, 997, 1013));
        v.AssertMatches(BZip2TestData.DecodeInPieces(v.Compressed, 1, 65536));
        v.AssertMatches(BZip2TestData.DecodeInPieces(v.Compressed, 100000, 7));
    }

    [Fact]
    public async Task AsyncStreamMatchesReference()
    {
        BZip2Vector v = BZip2TestData.Vector("concat2");
        await using var stream = new BZip2Stream(new MemoryStream(v.Compressed));
        var result = new MemoryStream();
        await stream.CopyToAsync(result, TestContext.Current.CancellationToken);
        v.AssertMatches(result.ToArray());
    }
}
