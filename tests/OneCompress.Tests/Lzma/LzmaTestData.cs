using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>A step function over a decoder: the shape of every OneCompress span decoder.</summary>
internal delegate OperationStatus DecodeStep(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written);

/// <summary>Vectors written by the xz, lzip and 7-Zip tools (testdata/lzma/generate.py).</summary>
internal sealed record LzmaVector(string Name, int Size, string Sha256, byte[] Compressed)
{
    public string Extension => Path.GetExtension(Name);

    public void AssertMatches(byte[] output)
    {
        Assert.Equal(Size, output.Length);
        Assert.Equal(Sha256, Convert.ToHexStringLower(SHA256.HashData(output)));
    }
}

internal static class LzmaTestData
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "testdata", "lzma");

    private static readonly Lazy<List<string[]>> s_rows = new(() =>
        File.ReadAllLines(Path.Combine(Dir, "manifest.tsv")).Where(static l => !l.StartsWith('#')).Select(static l => l.Split('\t')).ToList());

    public static TheoryData<string> Names(string extension)
    {
        var data = new TheoryData<string>();
        foreach (string[] row in s_rows.Value.Where(r => r[0].EndsWith(extension, StringComparison.Ordinal)))
        {
            data.Add(row[0]);
        }

        return data;
    }

    public static LzmaVector Vector(string name)
    {
        string[] row = s_rows.Value.Single(r => r[0] == name);
        return new LzmaVector(name, int.Parse(row[1], CultureInfo.InvariantCulture), row[2], File.ReadAllBytes(Path.Combine(Dir, "vectors", name)));
    }

    /// <summary>Decodes through a step decoder with input and output cut into pieces of the given sizes.</summary>
    public static byte[] Pieces(DecodeStep step, Action finish, byte[] compressed, int inputPiece, int outputPiece)
    {
        var output = new MemoryStream();
        byte[] buffer = new byte[outputPiece];
        int pos = 0;
        int stalls = 0;
        while (true)
        {
            OperationStatus status = step(compressed.AsSpan(pos, Math.Min(inputPiece, compressed.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done && c == 0 && w == 0)
            {
                break;
            }

            stalls = c == 0 && w == 0 ? stalls + 1 : 0;
            Assert.True(stalls < 3 || pos + inputPiece <= compressed.Length, "no progress");
            if (pos >= compressed.Length && status != OperationStatus.DestinationTooSmall && !(status == OperationStatus.Done && pos < compressed.Length))
            {
                break;
            }

            if (c == 0 && w == 0 && status == OperationStatus.NeedMoreData && inputPiece < compressed.Length - pos)
            {
                // The decoder holds nothing it can use: give it the next piece by growing the window of input it sees.
                inputPiece += 1;
            }
        }

        finish();
        return output.ToArray();
    }

    public sealed class SlowStream(byte[] data, int max) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, max));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, max)]);
    }

    public static byte[] ReadAll(Stream stream, int bufferSize)
    {
        var result = new MemoryStream();
        byte[] buffer = new byte[bufferSize];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.Write(buffer, 0, n);
        }

        return result.ToArray();
    }
}
