using RecursiveAI.OneCompress.Lz4;
using System.Buffers;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

public class Lz4BlockEncoderTests
{
    private static byte[] Payload(int n, int seed)
    {
        var r = new Random(seed);
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (byte)(r.Next(4) == 0 ? r.Next(256) : "snapshot tick="[i % 14]);
        }

        return b;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(-5)]
    public void ReusedEncoderMatchesOneShotAndRoundTrips(int level)
    {
        var options = new Lz4CompressionOptions { Level = level };
        var encoder = new Lz4BlockEncoder(options);
        byte[] buffer = new byte[Lz4BlockEncoder.GetMaxCompressedLength(5000)];
        foreach (int n in new[] { 0, 1, 12, 100, 4999, 17, 2000 })
        {
            byte[] data = Payload(n, n);
            Assert.True(encoder.TryCompress(data, buffer, out int written));
            Assert.Equal(Lz4Block.Compress(data, options), buffer.AsSpan(0, written).ToArray());
            Assert.Equal(data, Lz4Block.Decompress(buffer.AsSpan(0, written)));
        }
    }

    [Fact]
    public void TooSmallDestinationReturnsFalse()
    {
        var encoder = new Lz4BlockEncoder();
        byte[] data = new byte[300];
        new Random(5).NextBytes(data);
        Assert.False(encoder.TryCompress(data, new byte[10], out int written));
        Assert.Equal(0, written);
        Assert.False(encoder.TryCompressPrefixed(data, new byte[10], out _));
        Assert.False(encoder.TryCompressPrefixed(data, Span<byte>.Empty, out _));
        // Room for the result but not for the worst case still succeeds.
        byte[] repetitive = new byte[300];
        Assert.True(encoder.TryCompress(repetitive, new byte[30], out written));
        Assert.True(written <= 30);
    }

    [Fact]
    public void DictionaryIsUsedAndDecoded()
    {
        byte[] content = Payload(4000, 1);
        var dictionary = new Lz4Dictionary(content);
        var encoder = new Lz4BlockEncoder(new Lz4CompressionOptions { Dictionary = dictionary });
        byte[] data = content.AsSpan(500, 600).ToArray();
        byte[] buffer = new byte[Lz4BlockEncoder.GetMaxPrefixedLength(data.Length)];
        Assert.True(encoder.TryCompressPrefixed(data, buffer, out int written));
        Assert.True(written < 60);
        var decodeOptions = new Lz4BlockOptions { Dictionary = dictionary };
        Assert.Equal(data, Lz4PrefixedBlock.Decompress(buffer.AsSpan(0, written), decodeOptions));
        byte[] output = new byte[data.Length];
        Assert.True(Lz4PrefixedBlock.TryDecompress(buffer.AsSpan(0, written), output, out int n, decodeOptions));
        Assert.Equal(data.Length, n);
        Assert.Equal(data, output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(70000)]
    public void PrefixedRoundTripAndLengthBytes(int n)
    {
        byte[] data = Payload(n, 3);
        var encoder = new Lz4BlockEncoder();
        byte[] buffer = new byte[Lz4BlockEncoder.GetMaxPrefixedLength(n)];
        Assert.True(encoder.TryCompressPrefixed(data, buffer, out int written));
        Assert.True(Lz4PrefixedBlock.TryGetDecompressedSize(buffer.AsSpan(0, written), out int size, out int prefix));
        Assert.Equal(n, size);
        Assert.Equal(n < 128 ? 1 : n < 16384 ? 2 : 3, prefix);
        Assert.Equal(data, Lz4PrefixedBlock.Decompress(buffer.AsSpan(0, written)));
        if (n > 0)
        {
            Assert.False(Lz4PrefixedBlock.TryDecompress(buffer.AsSpan(0, written), new byte[n - 1], out _));
        }
    }

    [Fact]
    public void HostilePrefixedInputGivesTypedErrors()
    {
        var encoder = new Lz4BlockEncoder();
        byte[] data = Payload(200, 9);
        byte[] buffer = new byte[Lz4BlockEncoder.GetMaxPrefixedLength(200)];
        Assert.True(encoder.TryCompressPrefixed(data, buffer, out int written));
        byte[] good = buffer.AsSpan(0, written).ToArray();

        Assert.Throws<CompressionFormatException>(() => Lz4PrefixedBlock.Decompress([]));
        Assert.Throws<CompressionFormatException>(() => Lz4PrefixedBlock.Decompress([0x80]));
        Assert.Throws<CompressionFormatException>(() => Lz4PrefixedBlock.Decompress([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01]));
        Assert.Throws<CompressionFormatException>(() => Lz4PrefixedBlock.Decompress([0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x00]));

        // A declared length that disagrees with the block, in both directions.
        byte[] shorter = (byte[])good.Clone();
        shorter[0] = 100;
        Assert.Throws<CompressionFormatException>(() => Lz4PrefixedBlock.Decompress(shorter));

        // A huge declared length is refused before anything is allocated for it.
        Assert.Throws<CompressionLimitException>(() => Lz4PrefixedBlock.Decompress([0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x00]));

        // Every truncation is a typed error or (for the cut that leaves a smaller valid block) a length mismatch.
        for (int cut = 0; cut < good.Length; cut++)
        {
            Assert.Throws<CompressionFormatException>(() => Lz4PrefixedBlock.Decompress(good.AsSpan(0, cut)));
        }
    }

    [Fact]
    public void TrainedDictionaryWorksForLz4AndGivesBothZstdSides()
    {
        var samples = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < 400; i++)
        {
            samples.Add(Payload(200 + (i % 50), i));
        }

        Lz4Dictionary lz4 = Lz4Dictionary.Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 8192, Optimize = false }, id: 7, cancellationToken: TestContext.Current.CancellationToken);
        Assert.InRange(lz4.Length, 1, 8192);
        Assert.Equal(7u, lz4.Id);
        var encoder = new Lz4BlockEncoder(new Lz4CompressionOptions { Dictionary = lz4 });
        byte[] data = Payload(230, 1000);
        byte[] buffer = new byte[Lz4BlockEncoder.GetMaxPrefixedLength(data.Length)];
        Assert.True(encoder.TryCompressPrefixed(data, buffer, out int written));
        Assert.Equal(data, Lz4PrefixedBlock.Decompress(buffer.AsSpan(0, written), new Lz4BlockOptions { Dictionary = lz4 }));

        ZstdCompressionDictionary z = ZstdCompressionDictionary.Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 8192, Optimize = false }, TestContext.Current.CancellationToken);
        Assert.Same(z.ForDecompression, z.ForDecompression);
        Assert.Equal(z.Id, z.ForDecompression.Id);
        Assert.True(z.Content.Span.SequenceEqual(z.ForDecompression.Content.Span));
        byte[] file = z.ToArray();
        Assert.Equal(file, ZstdCompressionDictionary.Parse(file).ToArray());
        var zEncoder = new ZstdEncoder(new ZstdCompressionOptions { Level = 1, Dictionary = z });
        byte[] frame = new byte[ZstdEncoder.GetMaxCompressedLength(data.Length)];
        int n = zEncoder.CompressFrame(data, frame);
        var zDecoder = new ZstdDecoder(new ZstdDecompressionOptions { Dictionary = z.ForDecompression });
        byte[] back = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, zDecoder.Decompress(frame.AsSpan(0, n), back, out int consumed, out int produced));
        Assert.Equal(data, back.AsSpan(0, produced).ToArray());

        ZstdCompressionDictionary raw = ZstdCompressionDictionary.FromContent(Payload(100, 2), 40000);
        Assert.Equal(40000u, raw.ForDecompression.Id);
        Assert.Equal(raw.Content.ToArray(), raw.ToArray());
    }
}
