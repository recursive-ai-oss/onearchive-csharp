using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>The fast strategies' search of a dictionary's index, whose entries carry a tag for dictionaries under 16 MiB.</summary>
public class ZstdDictionarySearchTests
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // A payload made of pieces from the end of the dictionary with a fresh byte between them. The index keeps the latest position
    // of each hash, so the last few thousand positions of a large dictionary are the ones it still holds.
    private static byte[] PayloadFrom(byte[] content, int seed)
    {
        var random = new Random(seed);
        var payload = new List<byte>();
        for (int piece = 0; piece < 12; piece++)
        {
            int start = content.Length - 3000 + random.Next(2600);
            payload.AddRange(content.AsSpan(start, 200 + random.Next(100)).ToArray());
            payload.Add((byte)random.Next(256));
        }

        return payload.ToArray();
    }

    [Theory]
    [InlineData(300_000, 1)]
    [InlineData(300_000, 2)]
    [InlineData(300_000, 3)]
    [InlineData(300_000, 4)]
    [InlineData(17 << 20, 1)]
    [InlineData(17 << 20, 2)]
    [InlineData(17 << 20, 3)]
    public void DictionaryMatchesAreFoundWithAndWithoutTaggedEntries(int dictionarySize, int level)
    {
        byte[] content = RandomBytes(dictionarySize, 5);
        byte[] payload = PayloadFrom(content, 9);
        var dictionary = ZstdCompressionDictionary.FromContent(content, 31);
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = level, Dictionary = dictionary });
        byte[] frame = new byte[ZstdEncoder.GetMaxCompressedLength(payload.Length)];
        int written = encoder.CompressFrame(payload, frame);

        // Random bytes cannot be coded without the dictionary; with it most of each piece is a match.
        Assert.True(written < payload.Length / 2, $"{written} bytes for {payload.Length}");
        byte[] back = Zstd.Zstd.Decompress(frame.AsSpan(0, written), new ZstdDecompressionOptions { Dictionary = ZstdDictionary.FromContent(content, 31), MaxWindowSize = 64 << 20 });
        Assert.Equal(payload, back);
    }
}
