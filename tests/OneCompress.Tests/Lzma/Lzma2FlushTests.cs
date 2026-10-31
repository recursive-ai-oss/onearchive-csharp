using System.Buffers;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// A flush ends the LZMA2 chunk in progress and the stream goes on. Whatever the flushes do to the encoder's match finder, the stream
/// must decode to exactly the bytes that were written.
/// </summary>
public class Lzma2FlushTests
{
    // 69 bytes of short repeats ("ab", "abc", runs of one letter), found by shrinking a fuzz input. Flushed every 15 bytes, the tree match
    // finder used to record positions near the end of the data whose comparisons were cut short by the flush; once the next bytes arrived,
    // a search through those nodes reported a match that was not there and the stream decoded to different bytes without any error.
    private const string ShortRepeats =
        "616263616263616261626162636162636162636161626361626361626361626361626361626361626361626361626361626361626363636361626361626361626361626362";

    private static byte[] FlushEvery(LzmaOptions options, byte[] data, int piece, int outputSize, out byte dictionaryProperty)
    {
        var encoder = new Lzma2Encoder(options);
        dictionaryProperty = encoder.DictionaryProperty;
        var result = new List<byte>();
        byte[] buffer = new byte[outputSize];
        for (int offset = 0; offset < data.Length; offset += piece)
        {
            int take = Math.Min(piece, data.Length - offset);
            bool last = offset + take == data.Length;
            ReadOnlySpan<byte> source = data.AsSpan(offset, take);
            OperationStatus status;
            do
            {
                status = encoder.Compress(source, buffer, out int consumed, out int written, last);
                result.AddRange(buffer.AsSpan(0, written).ToArray());
                source = source[consumed..];
            }
            while (status == OperationStatus.DestinationTooSmall || (!source.IsEmpty && status != OperationStatus.Done));
            if (!last)
            {
                do
                {
                    status = encoder.Flush(buffer, out int written);
                    result.AddRange(buffer.AsSpan(0, written).ToArray());
                }
                while (status == OperationStatus.DestinationTooSmall);
            }
        }

        return [.. result];
    }

    [Theory]
    [InlineData(LzmaMatchFinderKind.HashChain3)]
    [InlineData(LzmaMatchFinderKind.HashChain4)]
    [InlineData(LzmaMatchFinderKind.BinaryTree2)]
    [InlineData(LzmaMatchFinderKind.BinaryTree3)]
    [InlineData(LzmaMatchFinderKind.BinaryTree4)]
    public void ShortRepeatsSurviveFlushes(LzmaMatchFinderKind finder)
    {
        byte[] data = Convert.FromHexString(ShortRepeats);
        foreach (LzmaMode mode in new[] { LzmaMode.Fast, LzmaMode.Normal })
        {
            for (int piece = 1; piece <= 40; piece++)
            {
                var options = new LzmaOptions { Preset = 2, Extreme = true, Lc = 3, Lp = 0, Pb = 2, DictionarySize = 1 << 19, MatchFinder = finder, Mode = mode };
                byte[] stream = FlushEvery(options, data, piece, 1, out byte property);
                Assert.Equal(data, Lzma.Lzma2.Decompress(stream, property));
            }
        }
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(7, 300)]
    [InlineData(300, 4000)]
    [InlineData(5000, 65_536)]
    public void FlushesAnywhereKeepTheData(int piece, int outputSize)
    {
        // Text and structured data have repeats at every distance, so the finders are exercised right at the flush points.
        byte[] data = [.. LzmaEncoderTests.Structured(40_000, 31), .. LzmaEncoderTests.Text(40_000, 32), .. LzmaEncoderTests.Structured(20_000, 33)];
        foreach (LzmaMatchFinderKind finder in Enum.GetValues<LzmaMatchFinderKind>())
        {
            foreach (int niceLength in new[] { 0, 8, 273 })
            {
                var options = new LzmaOptions { Preset = 5, DictionarySize = 1 << 16, MatchFinder = finder, NiceLength = niceLength == 0 ? 0 : Math.Max(niceLength, LzmaEncoderSettings.GetMinimumNiceLength(finder)) };
                byte[] stream = FlushEvery(options, data, piece, outputSize, out byte property);
                Assert.Equal(data, Lzma.Lzma2.Decompress(stream, property));
            }
        }
    }
}
