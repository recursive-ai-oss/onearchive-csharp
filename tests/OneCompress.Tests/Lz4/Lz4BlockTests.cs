using RecursiveAI.OneCompress.Lz4;
using static RecursiveAI.OneCompress.Tests.Lz4Builder;

namespace RecursiveAI.OneCompress.Tests;

public class Lz4BlockTests
{
    private static byte[] Bytes(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    private static byte[] Filler(int n, int seed = 1)
    {
        var r = new Random(seed);
        byte[] b = new byte[n];
        r.NextBytes(b);
        return b;
    }

    [Fact]
    public void EmptyBlockIsASingleZeroToken()
    {
        Assert.Empty(Lz4Block.Decompress([0x00]));
    }

    [Fact]
    public void ABlockOfOnlyLiteralsMayBeShort()
    {
        Assert.Equal(Bytes("hey"), Lz4Block.Decompress([0x30, (byte)'h', (byte)'e', (byte)'y']));
    }

    [Fact]
    public void OverlappingMatchRepeatsTheLastBytes()
    {
        // "ab" then 30 bytes copied from 2 back: ababab..., then the last 5 literals; the match starts 35 bytes before the end.
        var seq = new[] { new Sequence(Bytes("ab"), 2, 30), new Sequence(Bytes("12345"), 0, 0) };
        Assert.Equal(Expand([], seq), Lz4Block.Decompress(Block(seq)));
        var one = new[] { new Sequence(Bytes("x"), 1, 300), new Sequence(Bytes("12345"), 0, 0) };
        Assert.Equal(Expand([], one), Lz4Block.Decompress(Block(one)));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(269)]
    [InlineData(270)]
    [InlineData(271)]
    [InlineData(525)]
    [InlineData(100_000)]
    public void LengthsAroundTheExtensionBoundariesDecode(int length)
    {
        byte[] literals = Filler(length);
        var seq = new[] { new Sequence(literals, 0, 0) };
        Assert.Equal(literals, Lz4Block.Decompress(Block(seq)));

        var withMatch = new[] { new Sequence(Bytes("0123456789abcdefg"), 17, length + 4), new Sequence(Filler(length, 2).Concat(Bytes("12345")).ToArray(), 0, 0) };
        Assert.Equal(Expand([], withMatch), Lz4Block.Decompress(Block(withMatch)));
    }

    [Fact]
    public void RandomBlocksDecodeToWhatTheSimpleRuleGives()
    {
        var r = new Random(77);
        for (int round = 0; round < 3000; round++)
        {
            int history = r.Next(3) == 0 ? r.Next(1, 300) : 0;
            byte[] hist = Filler(history, round);
            var seqs = new List<Sequence>();
            int produced = history;
            int count = r.Next(1, 8);
            for (int i = 0; i < count; i++)
            {
                int lit = r.Next(4) == 0 ? r.Next(0, 600) : r.Next(0, 20);
                if (produced + lit == 0)
                {
                    lit = 1;
                }

                int max = Math.Min(produced + lit, 65535);
                int offset = r.Next(3) == 0 ? r.Next(1, Math.Min(max, 20) + 1) : r.Next(1, max + 1);
                int len = r.Next(3) == 0 ? r.Next(4, 700) : r.Next(4, 24);
                seqs.Add(new Sequence(Filler(lit, r.Next()), offset, len));
                produced += lit + len;
            }

            seqs.Add(new Sequence(Filler(r.Next(5, 40), r.Next()), 0, 0));

            // The last match must start at least 12 bytes before the end: the final literals (5 or more) and the match
            // (4 or more) already add up to 9, so pad the last literals when needed.
            Sequence last = seqs[^1];
            Sequence prior = seqs[^2];
            if (prior.MatchLength + last.Literals.Length < 12)
            {
                seqs[^1] = last with { Literals = Filler(12 - prior.MatchLength, r.Next()) };
            }

            Sequence[] all = [.. seqs];
            byte[] expected = Expand(hist, all);
            byte[] block = Block(all);
            byte[] buffer = new byte[history + expected.Length];
            hist.CopyTo(buffer, 0);
            Assert.True(Lz4BlockDecoder.TryDecode(block, buffer, history, out int written), $"round {round}");
            Assert.Equal(expected.Length, written);
            Assert.True(expected.AsSpan().SequenceEqual(buffer.AsSpan(history)), $"round {round}");
            Assert.Equal(expected.Length, Lz4BlockDecoder.GetDecompressedSize(block, history));

            // The same block with one byte too little room must report it, not run over.
            if (expected.Length > 0)
            {
                byte[] tight = new byte[history + expected.Length - 1];
                hist.CopyTo(tight, 0);
                Assert.False(Lz4BlockDecoder.TryDecode(block, tight, history, out _), $"round {round}");
            }

            // And with room to spare, so the wide copies are used up to the end.
            byte[] roomy = new byte[history + expected.Length + 64];
            hist.CopyTo(roomy, 0);
            Assert.True(Lz4BlockDecoder.TryDecode(block, roomy, history, out written));
            Assert.True(expected.AsSpan().SequenceEqual(roomy.AsSpan(history, written)));
        }
    }

    [Fact]
    public void RandomLongBlocksOfShortSequencesTakeTheFastPathAndAgreeWithTheSimpleRule()
    {
        // Many sequences, mostly with literal and match lengths below 15 (some far longer) and offsets across the copy-width
        // boundaries (1 to 40, some to 400), so the wide-copy loop runs for most of the block and the general code finishes it.
        var r = new Random(2024);
        for (int round = 0; round < 400; round++)
        {
            int produced = 0;
            var seqs = new List<Sequence>();
            int count = r.Next(10, 400);
            for (int i = 0; i < count; i++)
            {
                int lit = r.Next(5) == 0 ? r.Next(15, 300) : r.Next(0, 15);
                if (produced + lit == 0)
                {
                    lit = 1;
                }

                int offset = r.Next(1, Math.Min(produced + lit, r.Next(6) == 0 ? 400 : 40) + 1);
                int len = r.Next(5) == 0 ? r.Next(19, 400) : r.Next(4, 19);
                seqs.Add(new Sequence(Filler(lit, r.Next()), offset, len));
                produced += lit + len;
            }

            seqs.Add(new Sequence(Filler(r.Next(12, 30), r.Next()), 0, 0));
            Sequence[] all = [.. seqs];
            byte[] expected = Expand([], all);
            byte[] block = Block(all);
            foreach (int spare in new[] { 0, 1, 16, 39, 40, 41, 100 })
            {
                byte[] buffer = new byte[expected.Length + spare];
                Assert.True(Lz4BlockDecoder.TryDecode(block, buffer, 0, out int written), $"round {round} spare {spare}");
                Assert.Equal(expected.Length, written);
                Assert.True(expected.AsSpan().SequenceEqual(buffer.AsSpan(0, written)), $"round {round} spare {spare}");
            }

            // One byte short: must be reported, never overrun.
            Assert.False(Lz4BlockDecoder.TryDecode(block, new byte[expected.Length - 1], 0, out _));

            // An offset error planted in the middle is found at the right input offset.
            byte[] bad = (byte[])block.Clone();
            int at = FindOffsetField(all, 5);
            bad[at] = 0;
            bad[at + 1] = 0;
            var e = Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress(bad));
            Assert.Equal(at, e.Offset);
        }
    }

    // Input position of the offset field of the n-th sequence.
    private static int FindOffsetField(Sequence[] sequences, int n)
    {
        static int ExtensionBytes(int length) => length < 15 ? 0 : ((length - 15) / 255) + 1;
        int position = 0;
        for (int i = 0; i < n; i++)
        {
            Sequence s = sequences[i];
            position += 1 + ExtensionBytes(s.Literals.Length) + s.Literals.Length + 2 + ExtensionBytes(s.MatchLength - 4);
        }

        return position + 1 + ExtensionBytes(sequences[n].Literals.Length) + sequences[n].Literals.Length;
    }

    [Fact]
    public void BlockCanReachIntoTheDictionary()
    {
        byte[] dictionary = Bytes("0123456789abcdefghijklmnopqrstuvwxyz");
        // The first match reaches back into the dictionary.
        byte[] block = Block(new Sequence(Bytes("Q"), 11, 20), new Sequence(Bytes("-----------"), 0, 0));
        byte[] expected = Expand(dictionary, new Sequence(Bytes("Q"), 11, 20), new Sequence(Bytes("-----------"), 0, 0));
        var options = new Lz4BlockOptions { Dictionary = new Lz4Dictionary(dictionary) };
        Assert.Equal(expected, Lz4Block.Decompress(block, options));
        var e = Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress(block));
        Assert.Contains("offset", e.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void DictionaryBlocksDecodeWhenSizesAndDictionariesAlternate()
    {
        var rng = new Random(11);
        var dicts = new Lz4Dictionary[2];
        for (int d = 0; d < 2; d++)
        {
            byte[] c = new byte[4000 + d * 3000];
            rng.NextBytes(c);
            dicts[d] = new Lz4Dictionary(c);
        }

        foreach (int size in new[] { 10, 5000, 300, 70_000, 2_000_000, 40, 1_500_000 })
        {
            for (int d = 0; d < 2; d++)
            {
                byte[] payload = new byte[size];
                for (int i = 0; i < size; i += 64)
                {
                    int n = Math.Min(64, size - i);
                    if (rng.Next(2) == 0) dicts[d].Content.Slice(rng.Next(dicts[d].Length - 64), n).CopyTo(payload.AsSpan(i));
                    else rng.NextBytes(payload.AsSpan(i, n));
                }

                byte[] block = Lz4Block.Compress(payload, new Lz4CompressionOptions { Level = 3, Dictionary = dicts[d] });
                var options = new Lz4BlockOptions { Dictionary = dicts[d] };
                Assert.Equal(payload, Lz4Block.Decompress(block, options));
                byte[] dest = new byte[size];
                Assert.Equal(size, Lz4Block.Decompress(block, dest, options));
                Assert.Equal(payload, dest);
            }
        }
    }

    [Fact]
    public void RandomSequencesReachingIntoTheDictionaryMatchTheModel()
    {
        var rng = new Random(23);
        byte[] dictionary = new byte[300];
        rng.NextBytes(dictionary);
        var options = new Lz4BlockOptions { Dictionary = new Lz4Dictionary(dictionary) };
        for (int round = 0; round < 3000; round++)
        {
            int count = 1 + rng.Next(8);
            var sequences = new List<Sequence>();
            int produced = dictionary.Length;
            for (int i = 0; i < count; i++)
            {
                byte[] literals = new byte[rng.Next(round % 3 == 0 ? 40 : 6)];
                rng.NextBytes(literals);
                produced += literals.Length;
                int offset = 1 + rng.Next(produced);
                int length = 4 + (rng.Next(4) == 0 ? rng.Next(60) : rng.Next(15));
                sequences.Add(new Sequence(literals, offset, length));
                produced += length;
            }

            // The last sequence holds only literals, at least five of them after a match that starts at least twelve bytes before the end.
            byte[] tail = new byte[12 + rng.Next(30)];
            rng.NextBytes(tail);
            sequences.Add(new Sequence(tail, 0, 0));
            byte[] block = Block([.. sequences]);
            byte[] expected = Expand(dictionary, [.. sequences]);
            Assert.Equal(expected, Lz4Block.Decompress(block, options));
            byte[] exact = new byte[expected.Length];
            Assert.Equal(expected.Length, Lz4Block.Decompress(block, exact, options));
            Assert.Equal(expected, exact);
        }
    }

    [Fact]
    public void OffsetZeroIsRejected()
    {
        byte[] block = [0x10, (byte)'a', 0, 0, 0x50, 1, 2, 3, 4, 5];
        var e = Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress(block));
        Assert.Equal(2, e.Offset);
        Assert.False(e.IsTruncated);
    }

    [Fact]
    public void OffsetBeyondTheOutputSoFarIsRejected()
    {
        byte[] block = Block(new Sequence(Bytes("abc"), 4, 4), new Sequence(Bytes("123456789012"), 0, 0));
        var e = Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress(block));
        Assert.Equal(4, e.Offset);
        // The same offset is fine once there are four bytes of history.
        Assert.Equal(19, Lz4BlockDecoder.GetDecompressedSize(block, 4));
    }

    [Fact]
    public void BlockEndRulesAreEnforced()
    {
        // Last sequence with fewer than 5 literals after a match.
        byte[] shortLast = Block(new Sequence(Bytes("0123456789ab"), 5, 8), new Sequence(Bytes("1234"), 0, 0));
        var e = Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress(shortLast));
        Assert.Contains("5 literals", e.Expected, StringComparison.Ordinal);

        // Last match starts only 10 bytes before the end of the block.
        byte[] lateMatch = Block(new Sequence(Bytes("0123456789ab"), 5, 4), new Sequence(Bytes("123456"), 0, 0));
        e = Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress(lateMatch));
        Assert.Contains("12 bytes", e.Expected, StringComparison.Ordinal);

        // Exactly 12 is fine.
        byte[] ok = Block(new Sequence(Bytes("0123456789ab"), 5, 4), new Sequence(Bytes("12345678"), 0, 0));
        Assert.Equal(12 + 4 + 8, Lz4Block.Decompress(ok).Length);
    }

    [Fact]
    public void BlockThatEndsAfterAMatchOrInsideASequenceIsTruncated()
    {
        byte[] good = Block(new Sequence(Bytes("0123456789ab"), 5, 8), new Sequence(Bytes("12345"), 0, 0));
        // Cut right after the match: no final literals-only sequence.
        byte[] afterMatch = good[..^6];
        var e = Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress(afterMatch));
        Assert.True(e.IsTruncated);
        Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress([]));
        Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress([0xF0]));
        Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress([0xF0, 0xFF]));
        Assert.Throws<CompressionFormatException>(() => Lz4Block.Decompress([0x1F, (byte)'a', 1]));
    }

    [Fact]
    public void EveryPrefixAndEveryBitFlipEndsInASuccessOrATypedError()
    {
        var seqs = new[]
        {
            new Sequence(Bytes("hello hello hello "), 6, 40),
            new Sequence(Filler(300), 17, 300),
            new Sequence(Filler(20), 100, 9),
            new Sequence(Filler(30), 0, 0),
        };
        byte[] block = Block(seqs);
        for (int length = 0; length < block.Length; length++)
        {
            Try(block[..length]);
        }

        for (int i = 0; i < block.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] copy = (byte[])block.Clone();
                copy[i] ^= (byte)(1 << bit);
                Try(copy);
            }
        }

        static void Try(byte[] data)
        {
            try
            {
                byte[] output = Lz4Block.Decompress(data);
                Assert.Equal(output.Length, Lz4BlockDecoder.GetDecompressedSize(data));
            }
            catch (CompressionFormatException)
            {
            }

            byte[] tiny = new byte[40];
            try
            {
                Lz4Block.TryDecompress(data, tiny, out _);
            }
            catch (CompressionFormatException)
            {
            }
        }
    }

    [Fact]
    public void OutputLimitIsCheckedBeforeAllocating()
    {
        // 'x' then a match of 255 * 40000 + 19 bytes: 10 MB from a 40 KB block.
        var block = new List<byte> { 0x1F, (byte)'x', 1, 0 };
        block.AddRange(Enumerable.Repeat((byte)255, 40000));
        block.Add(0);
        block.AddRange([0x50, 1, 2, 3, 4, 5]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<CompressionLimitException>(() => Lz4Block.Decompress([.. block], new Lz4BlockOptions { MaxOutputSize = 1 << 20 }));
        Assert.Equal("MaxOutputSize", e.LimitName);
        Assert.True(e.Requested > 10_000_000);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 16);

        // With room for it, it decodes.
        Assert.Equal(255 * 40000 + 19 + 1 + 5, Lz4Block.Decompress([.. block], new Lz4BlockOptions { MaxOutputSize = 20 << 20 }).Length);

        // Into a span that is too small: a typed limit error, not an overrun.
        var e2 = Assert.Throws<CompressionLimitException>(() => Lz4Block.Decompress([.. block], new byte[1000]));
        Assert.Equal("destination.Length", e2.LimitName);
    }

    [Fact]
    public void TierThreeRejectsBadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4BlockDecoder.TryDecode([0], new byte[4], 5, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4BlockDecoder.TryDecode([0], new byte[4], -1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Lz4BlockDecoder.GetDecompressedSize([0], -1));
    }
}
