using System.Buffers;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Streams written by zlib, gzip and pigz (testdata/deflate) decode to the recorded output through every path.</summary>
public class DeflateVectorTests
{
    public static TheoryData<string> Names() => DeflateTestData.Names();

    [Theory]
    [MemberData(nameof(Names))]
    public void OneShotMatchesReference(string name)
    {
        DeflateVector v = DeflateTestData.Vector(name);
        v.AssertMatches(DeflateOneShotFor(v));
    }

    private static byte[] DeflateOneShotFor(DeflateVector v) => v.Format switch
    {
        DeflateFormat.Raw => Deflate.Deflate.Decompress(v.Compressed, v.Options()),
        DeflateFormat.ZLib => ZLib.Decompress(v.Compressed, v.Options()),
        _ => GZip.Decompress(v.Compressed, v.Options()),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void ExactBufferAndTooSmallBuffer(string name)
    {
        DeflateVector v = DeflateTestData.Vector(name);
        byte[] output = new byte[v.Size];
        Assert.True(TryOneShot(v, output, out int written));
        Assert.Equal(v.Size, written);
        v.AssertMatches(output);
        if (v.Size > 0)
        {
            Assert.False(TryOneShot(v, new byte[v.Size - 1], out written));
            Assert.Equal(0, written);
        }
    }

    private static bool TryOneShot(DeflateVector v, Span<byte> destination, out int written) => v.Format switch
    {
        DeflateFormat.Raw => Deflate.Deflate.TryDecompress(v.Compressed, destination, out written, v.Options()),
        DeflateFormat.ZLib => ZLib.TryDecompress(v.Compressed, destination, out written, v.Options()),
        _ => GZip.TryDecompress(v.Compressed, destination, out written, v.Options()),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void StreamMatchesReference(string name)
    {
        DeflateVector v = DeflateTestData.Vector(name);
        using var stream = new DeflateCodecStream(new OneByteStream(v.Compressed, 4093), v.Format, v.Options());
        var result = new MemoryStream();
        byte[] buffer = new byte[3001];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            result.Write(buffer, 0, n);
        }

        v.AssertMatches(result.ToArray());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task AsyncStreamMatchesReference(string name)
    {
        DeflateVector v = DeflateTestData.Vector(name);
        await using var stream = new DeflateCodecStream(new MemoryStream(v.Compressed), v.Format, v.Options());
        var result = new MemoryStream();
        await stream.CopyToAsync(result, TestContext.Current.CancellationToken);
        v.AssertMatches(result.ToArray());
    }

    // Any split of input and output gives the same result: feed in pieces of one size and drain through buffers of another.
    [Theory]
    [InlineData(1, 1 << 20)]
    [InlineData(7, 5)]
    [InlineData(300, 1)]
    [InlineData(1 << 20, 3)]
    [InlineData(4096, 40000)]
    [InlineData(65537, 70000)]
    public void PiecewiseDecodeMatchesReference(int inputStep, int outputStep)
    {
        foreach (string name in new[] { "text300-l6-gzip", "text100k-l9-zlib", "bin150k-fixed-raw", "text100k-l0-zlib", "period100k-l6-raw", "multi-3", "header-all-fields", "text300-dict-zlib", "text100k-flushes-raw", "far-dict-big-raw", "empty-l0-gzip" })
        {
            if (inputStep == 1 && outputStep > 1000 && name is "text100k-l9-zlib" or "period100k-l6-raw")
            {
                continue; // one input byte per call over 100 KB is slow in a unit test; the other rows cover it
            }

            if (outputStep <= 5 && name is "text100k-l9-zlib" or "bin150k-fixed-raw" or "text100k-l0-zlib" or "period100k-l6-raw" or "text100k-flushes-raw" or "far-dict-big-raw")
            {
                continue;
            }

            DeflateVector v = DeflateTestData.Vector(name);
            v.AssertMatches(Pieces(v.Format, v.Compressed, v.Options(), inputStep, outputStep));
        }
    }

    internal static byte[] Pieces(DeflateFormat format, byte[] data, DeflateDecompressionOptions options, int inputStep, int outputStep)
    {
        var decoder = new DeflateDecoder(format, options);
        var output = new MemoryStream();
        byte[] buffer = new byte[outputStep];
        int pos = 0;
        int inputEnd = Math.Min(inputStep, data.Length);
        while (true)
        {
            OperationStatus status = decoder.Decompress(data.AsSpan(pos, inputEnd - pos), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.NeedMoreData)
            {
                // The decoder took every byte it was given.
                Assert.Equal(inputEnd, pos);
                if (inputEnd == data.Length)
                {
                    break;
                }

                inputEnd = Math.Min(inputEnd + inputStep, data.Length);
            }
            else if (status == OperationStatus.Done)
            {
                if (pos == data.Length)
                {
                    break;
                }

                if (format != DeflateFormat.GZip)
                {
                    throw new CompressionFormatException("deflate", pos, "the input to end with the stream");
                }
            }
        }

        decoder.ThrowIfIncomplete();
        Assert.True(decoder.IsAtFrameBoundary);
        return output.ToArray();
    }
}

/// <summary>Hands out at most <c>step</c> bytes per read, to exercise the stream's refill logic.</summary>
internal sealed class OneByteStream(byte[] data, int step) : Stream
{
    private int _pos;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _pos;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = Math.Min(Math.Min(count, step), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
