using System.Buffers;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;
using RecursiveAI.OneCompress.BZip2;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Hostile and broken input: every prefix, every flipped bit, hand-built bad streams, limits.</summary>
public class BZip2HostileInputTests
{
    public static TheoryData<string> PrefixVectors() => new() { "onebyte", "allbytes-l9", "concat2", "level1-small-l1", "text20k-l1", "randomized20k" };

    [Theory]
    [MemberData(nameof(PrefixVectors))]
    public void EveryPrefixIsATypedTruncationError(string name)
    {
        BZip2Vector v = BZip2TestData.Vector(name);
        int firstEnd = name == "concat2" ? FirstStreamLength(v.Compressed) : -1;
        for (int cut = 0; cut < v.Compressed.Length; cut++)
        {
            byte[] prefix = v.Compressed.AsSpan(0, cut).ToArray();
            if (cut == firstEnd)
            {
                Assert.NotEmpty(BZ.Decompress(prefix)); // a complete first stream is a valid input
                continue;
            }

            var e = Assert.ThrowsAny<CompressionFormatException>(() => BZ.Decompress(prefix));
            Assert.True(e.IsTruncated, $"{name} cut at {cut}: {e.Message}");
            Assert.InRange(e.Offset, 0, cut);
            Assert.Equal("bzip2", e.Format);

            // The span decoder, fed in pieces, agrees (no input at all is a boundary, not an error, for the span decoder).
            if (cut == 0)
            {
                continue;
            }

            var piece = Assert.ThrowsAny<CompressionFormatException>(() => BZip2TestData.DecodeInPieces(prefix, 61, 4096));
            Assert.True(piece.IsTruncated);
        }
    }

    private static int FirstStreamLength(byte[] data)
    {
        var d = new BZip2Decoder();
        d.Decompress(data, new byte[100000], out int consumed, out _);
        return consumed;
    }

    [Theory]
    [InlineData("onebyte")]
    [InlineData("level1-small-l1")]
    [InlineData("allbytes-l9")]
    [InlineData("zeros259-l9")]
    public void EveryFlippedBitIsATypedErrorOrTheSameOutput(string name)
    {
        BZip2Vector v = BZip2TestData.Vector(name);
        byte[] copy = v.Compressed.ToArray();
        var options = new BZip2DecompressionOptions { MaxOutputSize = 1 << 24 };
        int errors = 0;
        for (int i = 0; i < copy.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                copy[i] ^= (byte)(1 << bit);
                try
                {
                    byte[] output = BZ.Decompress(copy, options);
                    v.AssertMatches(output);
                }
                catch (CompressionException)
                {
                    errors++;
                }

                copy[i] ^= (byte)(1 << bit);
            }
        }

        Assert.True(errors > copy.Length * 4);
    }

    [Fact]
    public void EveryFlippedBitOfAMultiBlockStream()
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, 9, 150);
        for (int i = 0; i < stream.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                stream[i] ^= (byte)(1 << bit);
                try
                {
                    Assert.Equal(s_sample, BZ.Decompress(stream, new BZip2DecompressionOptions { MaxOutputSize = 1 << 22 }));
                }
                catch (CompressionException)
                {
                }

                stream[i] ^= (byte)(1 << bit);
            }
        }
    }

    private static void AssertBad(byte[] stream, string expectedPhrase, BZip2DecompressionOptions? options = null)
    {
        var e = Assert.ThrowsAny<CompressionFormatException>(() => BZ.Decompress(stream, options));
        Assert.False(e.IsTruncated, e.Message);
        Assert.Contains(expectedPhrase, e.Expected, StringComparison.Ordinal);
        Assert.InRange(e.Offset, 0, stream.Length);

        // Fed in pieces, the same failure.
        var piece = Assert.ThrowsAny<CompressionFormatException>(() => BZip2TestData.DecodeInPieces(stream, 7, 1000, options));
        Assert.Equal(e.Expected, piece.Expected);
        Assert.Equal(e.Offset, piece.Offset);
    }

    private static readonly byte[] s_sample = BZip2TestData.Text(3, 400);

    [Fact]
    public void ValidBuilderStreamsDecode()
    {
        foreach (int blockInput in new[] { int.MaxValue, 150 })
        {
            Assert.Equal(s_sample, BZ.Decompress(BZip2TestBuilder.Stream(s_sample, 9, blockInput)));
        }

        byte[] runs = [.. new byte[300], .. Enumerable.Repeat((byte)7, 4), 1, 1, 1, 1, 1, .. Enumerable.Repeat((byte)9, 259), .. Enumerable.Repeat((byte)9, 100)];
        Assert.Equal(runs, BZ.Decompress(BZip2TestBuilder.Stream(runs)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData(":")]
    [InlineData("x")]
    public void BadLevelDigit(string digit)
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample);
        stream[3] = (byte)digit[0];
        AssertBad(stream, "block size level");
    }

    [Fact]
    public void BadSignatures()
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample);
        byte[] bad = stream.ToArray();
        bad[0] = (byte)'C';
        AssertBad(bad, "stream signature");
        bad = stream.ToArray();
        bad[4] ^= 0x10;
        AssertBad(bad, "block signature");
        bad = [.. stream, (byte)'x'];
        AssertBad(bad, "stream signature");
        Assert.Equal(s_sample, BZ.Decompress(bad, new BZip2DecompressionOptions { AllowTrailingData = true }));
    }

    [Fact]
    public void BadOriginalRow()
    {
        foreach (int row in new[] { 400, 401, 0xFFFFFF })
        {
            byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) => m.OriginalRow = row);
            AssertBad(stream, "original row number");
        }
    }

    [Fact]
    public void SelectorCountZeroIsRejected()
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) => m.SelectorCountField = 0);
        AssertBad(stream, "table selector");
    }

    [Theory]
    [InlineData(18002)]
    [InlineData(18003)]
    [InlineData(32767)]
    public void SelectorsPastTheLimitAreReadAndIgnored(int count)
    {
        // bzip2 1.0.8 decodes these to the original; extra selectors must be consumed so the symbols stay aligned.
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) =>
        {
            while (m.Selectors.Count < count)
            {
                m.Selectors.Add(1);
            }
        });
        Assert.Equal(s_sample, BZ.Decompress(stream));
        Assert.Equal(s_sample, BZip2TestData.DecodeInPieces(stream, 7, 1000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void BadTableCount(int tables)
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) => m.TableCount = tables);
        AssertBad(stream, "Huffman tables");
    }

    [Fact]
    public void SelectorAboveTableCount()
    {
        // Three tables declared, a selector value of 3 (unary 1110) that no table has.
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) =>
        {
            m.TableCount = 3;
            m.Lengths = [m.Lengths[0], m.Lengths[0], m.Lengths[0]];
            m.Selectors[0] = 3;
        });
        AssertBad(stream, "table selector below the table count");
    }

    [Fact]
    public void EmptySymbolMap()
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) => m.UsedBytes = []);
        AssertBad(stream, "byte value in use");
    }

    [Theory]
    [InlineData(21)]
    [InlineData(25)]
    [InlineData(0)]
    public void CodeLengthOutOfRange(int length)
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) => m.Lengths[1][3] = (byte)length);
        AssertBad(stream, "code length from 1 to 20");
    }

    [Fact]
    public void OversubscribedCodeLengths()
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) => m.Lengths[0] = Enumerable.Repeat((byte)1, m.AlphabetSize).ToArray());
        AssertBad(stream, "oversubscribe");
    }

    [Fact]
    public void LengthWalkThatNeverEnds()
    {
        // Start at 10, then +1 and -1 forever: every step stays in range, so only a step limit stops it.
        var w = new BZip2BitWriter();
        BZip2TestBuilder.WriteStreamHeader(w, 9);
        BZip2BlockModel m = BZip2TestBuilder.Model(s_sample);
        m.Lengths = [m.Lengths[0], m.Lengths[0]];
        m.Write(w); // a valid block first, to take its header bits; rewrite the walk below
        byte[] valid = w.ToArray();
        Assert.NotEmpty(valid);

        var bad = new BZip2BitWriter();
        BZip2TestBuilder.WriteStreamHeader(bad, 9);
        bad.Write(0x314159265359, 48);
        bad.Write(0, 32);
        bad.Write(0, 1);
        bad.Write(0, 24);
        bad.Write(0x8000, 16);
        bad.Write(0x8000, 16);
        bad.Write(2, 3);
        bad.Write(1, 15);
        bad.Write(0, 1);
        bad.Write(10, 5);
        for (int i = 0; i < 200; i++)
        {
            bad.Write(0b10, 2);
            bad.Write(0b11, 2);
        }

        AssertBad(bad.ToArray(), "at most 40 steps");
    }

    [Fact]
    public void IncompleteCodeIsAcceptedUntilAnUnassignedCodeAppears()
    {
        // One symbol used, so the alphabet is RUNA, RUNB and the end of block. Lengths 1, 2, 3 leave the code 111 unassigned;
        // the stream spells the end of block as 110, which is fine, then a second block uses 111.
        var model = new BZip2BlockModel
        {
            Crc = BZip2Crc.Compute([0x61]),
            OriginalRow = 0,
            UsedBytes = [0x61],
            Selectors = [0],
            Lengths = [[1, 2, 3], [1, 2, 3]],
            Symbols = [0, 2],
        };
        var w = new BZip2BitWriter();
        BZip2TestBuilder.WriteStreamHeader(w, 9);
        model.Write(w);
        BZip2TestBuilder.WriteEnd(w, model.Crc);
        Assert.Equal([0x61], BZ.Decompress(w.ToArray()));
    }

    [Fact]
    public void UnassignedCode()
    {
        var w = new BZip2BitWriter();
        BZip2TestBuilder.WriteStreamHeader(w, 9);
        var model = new BZip2BlockModel { UsedBytes = [0x61], Selectors = [0], Lengths = [[1, 2, 3], [1, 2, 3]], Symbols = [] };
        model.Write(w);
        w.Write(0b1111111, 7); // 111 is not a code of the table
        w.Write(0, 100);
        AssertBad(w.ToArray(), "code assigned");
    }

    private static byte[] OneBlock(int level, int[] symbols, byte[] used, byte[][] lengths, int selectors = 1)
    {
        var w = new BZip2BitWriter();
        BZip2TestBuilder.WriteStreamHeader(w, level);
        var model = new BZip2BlockModel
        {
            Crc = 0,
            UsedBytes = used,
            Selectors = Enumerable.Range(0, selectors).Select(static i => i % 2).ToList(),
            Lengths = lengths,
            Symbols = [.. symbols],
        };
        model.Write(w);
        BZip2TestBuilder.WriteEnd(w, 0);
        return w.ToArray();
    }

    [Fact]
    public void RunOfZerosPastTheBlockSize()
    {
        // 40 RUNB digits spell a run of about 2^41; so does 17 of them at level 1 (limit 100000).
        byte[][] lengths = [[1, 2, 2], [1, 2, 2]];
        int[] many = [.. Enumerable.Repeat(1, 40), 2];
        AssertBad(OneBlock(9, many, [0x61], lengths, 1), "run of zeros");
        int[] some = [.. Enumerable.Repeat(1, 17), 2];
        AssertBad(OneBlock(1, some, [0x61], lengths, 1), "run of zeros that fits the block size");
        AssertBad(OneBlock(9, [.. Enumerable.Repeat(0, 100), 2], [0x61], lengths, 1), "run of zeros");
    }

    [Fact]
    public void MoreSymbolsThanTheBlockSizeAllows()
    {
        // Level 1 holds 100000 symbols: 100001 of them overflow. Two used bytes: RUNA, RUNB, 2 (the byte at position 1), end of block 3.
        byte[][] lengths = [[2, 2, 2, 2], [2, 2, 2, 2]];
        int[] symbols = [.. Enumerable.Repeat(2, 100001), 3];
        AssertBad(OneBlock(1, symbols, [0x61, 0x62], lengths, 100001 / 50 + 2), "at most 100000 symbols");
    }

    [Fact]
    public void RunOverflowAfterLiteralsAtLevelLimit()
    {
        // 99999 literals, then a run of 2 (RUNB) passes 100000.
        byte[][] lengths = [[2, 2, 2, 2], [2, 2, 2, 2]];
        int[] symbols = [.. Enumerable.Repeat(2, 99999), 1, 3];
        AssertBad(OneBlock(1, symbols, [0x61, 0x62], lengths, 100001 / 50 + 2), "run of zeros that fits");
    }

    [Fact]
    public void BlockRunsOutOfSelectors()
    {
        byte[][] lengths = [[2, 2, 2, 2], [2, 2, 2, 2]];
        int[] symbols = [.. Enumerable.Repeat(2, 120), 3];
        AssertBad(OneBlock(9, symbols, [0x61, 0x62], lengths, 2), "last table selector");
    }

    [Fact]
    public void EmptyBlock()
    {
        byte[][] lengths = [[1, 2, 2], [1, 2, 2]];
        AssertBad(OneBlock(9, [2], [0x61], lengths), "at least one symbol");
    }

    [Fact]
    public void BlockEndingAfterFourEqualBytesWithoutACount()
    {
        // The tool rejects this (checked against bzip2 1.0.8); with a count of zero it is fine.
        foreach ((byte[] rle, bool valid) in new[] { (new byte[] { 97, 97, 97, 97 }, false), (new byte[] { 97, 97, 97, 97, 0 }, true) })
        {
            BZip2BlockModel m = BZip2TestBuilder.ModelFromRunLengthEncoded(rle, BZip2Crc.Compute(new byte[] { 97, 97, 97, 97 }));
            var w = new BZip2BitWriter();
            BZip2TestBuilder.WriteStreamHeader(w, 9);
            m.Write(w);
            BZip2TestBuilder.WriteEnd(w, m.Crc);
            if (valid)
            {
                Assert.Equal(new byte[] { 97, 97, 97, 97 }, BZ.Decompress(w.ToArray()));
            }
            else
            {
                AssertBad(w.ToArray(), "repeat count byte");
            }
        }
    }

    [Fact]
    public void BlockCrcMismatch()
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) => m.Crc ^= 1);
        var e = Assert.ThrowsAny<CompressionFormatException>(() => BZ.Decompress(stream));
        Assert.Contains("block CRC", e.Expected, StringComparison.Ordinal);
        Assert.Equal(4, e.Offset); // the error points at the block, which starts right after the 4-byte stream header
        Assert.Equal(s_sample, BZ.Decompress(stream, new BZip2DecompressionOptions { VerifyChecksum = false }));
    }

    [Fact]
    public void CombinedCrcMismatch()
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, blockInput: 150, combinedCrcOverride: 12345);
        var e = Assert.ThrowsAny<CompressionFormatException>(() => BZ.Decompress(stream));
        Assert.Contains("combined CRC", e.Expected, StringComparison.Ordinal);
        Assert.Equal(s_sample, BZ.Decompress(stream, new BZip2DecompressionOptions { VerifyChecksum = false }));

        // Block CRCs in the wrong order or swapped between blocks are caught by the combined CRC alone.
        byte[] swapped = BZip2TestBuilder.Stream(s_sample, blockInput: 150, tamper: (m, i) => { });
        Assert.Equal(s_sample, BZ.Decompress(swapped));
    }

    [Fact]
    public void RandomizedBlockIsDerandomized()
    {
        // Data without runs, flipped at the scheduled positions, written with the flag set and the CRC of the unflipped data.
        byte[] data = new byte[2000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 7 * 2 + ((i / 7) % 3 == 0 ? 40 : 100));
        }

        byte[] flipped = data.ToArray();
        BZip2Randomization.Apply(flipped);
        Assert.NotEqual(data, flipped);
        byte[] stream = BZip2TestBuilder.Stream(flipped, tamper: (m, _) =>
        {
            m.Randomized = true;
            m.Crc = BZip2Crc.Compute(data);
        }, combinedCrcOverride: BZip2Crc.Combine(0, BZip2Crc.Compute(data)));
        Assert.Equal(data, BZ.Decompress(stream));
    }

    // ---- Limits ----

    [Fact]
    public void OutputLimitStopsAtTheLimit()
    {
        BZip2Vector v = BZip2TestData.Vector("zeros1m-l9");
        foreach (long limit in new long[] { 0, 1, 999, 1_000_000 - 1 })
        {
            var e = Assert.Throws<CompressionLimitException>(() => BZ.Decompress(v.Compressed, new BZip2DecompressionOptions { MaxOutputSize = limit }));
            Assert.Equal(nameof(BZip2DecompressionOptions.MaxOutputSize), e.LimitName);
            Assert.Equal(limit, e.Limit);
            Assert.Equal("bzip2", e.Format);

            var decoder = new BZip2Decoder(new BZip2DecompressionOptions { MaxOutputSize = limit });
            byte[] buffer = new byte[4096];
            long total = 0;
            int pos = 0;
            Action drain = () =>
            {
                while (true)
                {
                    decoder.Decompress(v.Compressed.AsSpan(pos), buffer, out int c, out int w);
                    pos += c;
                    total += w;
                }
            };
            Assert.Throws<CompressionLimitException>(drain);
            Assert.True(decoder.TotalWritten <= limit);
            Assert.Equal(limit, decoder.TotalWritten);
        }

        // Exactly at the limit is fine, and a destination of exactly the limit is not "too small".
        Assert.Equal(1_000_000, BZ.Decompress(v.Compressed, new BZip2DecompressionOptions { MaxOutputSize = 1_000_000 }).Length);
        byte[] exact = new byte[1_000_000];
        Assert.True(BZ.TryDecompress(v.Compressed, exact, out int written, new BZip2DecompressionOptions { MaxOutputSize = 1_000_000 }));
        Assert.Equal(1_000_000, written);
        Assert.Throws<CompressionLimitException>(() => BZ.TryDecompress(v.Compressed, new byte[1_000_000], out _, new BZip2DecompressionOptions { MaxOutputSize = 999_999 }));
    }

    [Fact]
    public void LimitAppliesToTheStreamAndAcrossStreams()
    {
        BZip2Vector v = BZip2TestData.Vector("concat2");
        Assert.Throws<CompressionLimitException>(() => BZip2TestData.DecodeThroughStream(v.Compressed, 100, 100, new BZip2DecompressionOptions { MaxOutputSize = 5999 }));
        Assert.Equal(6000, BZip2TestData.DecodeThroughStream(v.Compressed, 100, 100, new BZip2DecompressionOptions { MaxOutputSize = 6000 }).Length);
    }

    [Fact]
    public void DecoderRefusesCallsAfterAFailureUntilReset()
    {
        byte[] stream = BZip2TestBuilder.Stream(s_sample, tamper: (m, _) => m.Crc ^= 1);
        var decoder = new BZip2Decoder();
        byte[] buffer = new byte[4096];
        Assert.Throws<CompressionFormatException>(() => decoder.Decompress(stream, buffer, out _, out _));
        Assert.Throws<InvalidOperationException>(() => decoder.Decompress(stream, buffer, out _, out _));
        decoder.Reset();
        BZip2Vector v = BZip2TestData.Vector("onebyte");
        Assert.Equal(OperationStatus.Done, decoder.Decompress(v.Compressed, buffer, out int consumed, out int written));
        Assert.Equal(v.Compressed.Length, consumed);
        Assert.Equal(1, written);
    }
}
