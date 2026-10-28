using System.Buffers;
using System.Text;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;
using RecursiveAI.OneCompress.BZip2;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>The public parts of the decoder, one by one, and properties of the decoder as a whole.</summary>
public class BZip2ComponentTests
{
    [Fact]
    public void CrcCheckValue()
    {
        // The CRC-32/BZIP2 catalogue check value for "123456789".
        Assert.Equal(0xFC891918u, BZip2Crc.Compute("123456789"u8));
        Assert.Equal(0u, BZip2Crc.Compute([]));
    }

    [Fact]
    public void CrcMatchesBitByBitAtEveryLengthAndSplit()
    {
        byte[] data = BZip2TestData.Text(9, 300);
        for (int n = 0; n <= 100; n++)
        {
            uint reference = 0xFFFFFFFF;
            foreach (byte b in data.AsSpan(0, n))
            {
                reference ^= (uint)b << 24;
                for (int k = 0; k < 8; k++)
                {
                    reference = (reference & 0x80000000) != 0 ? (reference << 1) ^ 0x04C11DB7 : reference << 1;
                }
            }

            Assert.Equal(~reference, BZip2Crc.Compute(data.AsSpan(0, n)));
            uint split = BZip2Crc.Update(BZip2Crc.Initial, data.AsSpan(0, n / 3));
            Assert.Equal(~reference, ~BZip2Crc.Update(split, data.AsSpan(n / 3, n - (n / 3))));
        }
    }

    [Fact]
    public void CombinedCrcRotatesAndXors()
    {
        Assert.Equal(1u, BZip2Crc.Combine(0x80000000, 0));
        Assert.Equal(0xFu, BZip2Crc.Combine(0, 0xF));
        Assert.Equal(2u ^ 5u, BZip2Crc.Combine(1, 5));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("mississippi")]
    [InlineData("a")]
    [InlineData("abracadabra abracadabra")]
    public void InverseBwtUndoesTheNaiveForwardTransform(string text)
    {
        byte[] original = Encoding.ASCII.GetBytes(text);
        (byte[] last, int row) = BZip2TestBuilder.Bwt(original);
        byte[] output = new byte[original.Length];
        BZip2Bwt.Inverse(last, row, output);
        Assert.Equal(original, output);
    }

    [Fact]
    public void InverseBwtOfRandomBlocks()
    {
        var r = new Random(4);
        for (int round = 0; round < 20; round++)
        {
            byte[] original = new byte[1 + r.Next(300)];
            for (int i = 0; i < original.Length; i++)
            {
                original[i] = (byte)r.Next(round % 2 == 0 ? 3 : 256);
            }

            (byte[] last, int row) = BZip2TestBuilder.Bwt(original);
            byte[] output = new byte[original.Length];
            BZip2Bwt.Inverse(last, row, output);
            Assert.Equal(original, output);
        }
    }

    [Fact]
    public void InverseBwtRejectsBadArguments()
    {
        Assert.Throws<ArgumentException>(() => BZip2Bwt.Inverse([], 0, new byte[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => BZip2Bwt.Inverse(new byte[3], 3, new byte[3]));
        Assert.Throws<ArgumentException>(() => BZip2Bwt.Inverse(new byte[3], 0, new byte[2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => BZip2Bwt.BuildVector(new uint[2], 3));
    }

    [Fact]
    public void MoveToFrontRoundTrip()
    {
        var enc = new BZip2MoveToFront();
        var dec = new BZip2MoveToFront();
        byte[] used = [3, 9, 20, 77, 200];
        enc.Reset(used);
        dec.Reset(used);
        var r = new Random(1);
        for (int i = 0; i < 500; i++)
        {
            byte b = used[r.Next(used.Length)];
            int index = enc.Encode(b);
            Assert.Equal(b, dec.Decode(index));
            Assert.Equal(b, dec.Front);
        }

        var big = new BZip2MoveToFront();
        big.Reset(256);
        Assert.Equal(200, big.Decode(200));
        Assert.Equal(0, big.Decode(1)); // 200 moved to the front, so the old front 0 is at position 1
        Assert.Throws<ArgumentOutOfRangeException>(() => big.Decode(256));
        Assert.Throws<ArgumentOutOfRangeException>(() => big.Reset(257));
        Assert.Throws<ArgumentException>(() => enc.Encode(4));
    }

    [Fact]
    public void HuffmanTableAssignsCanonicalCodes()
    {
        var t = new BZip2HuffmanTable();
        // Lengths 2 1 3 3: symbol 1 gets 0, symbol 0 gets 10, symbols 2 and 3 get 110 and 111.
        t.Build([2, 1, 3, 3]);
        Assert.Equal(0u, t.GetCode(1));
        Assert.Equal(0b10u, t.GetCode(0));
        Assert.Equal(0b110u, t.GetCode(2));
        Assert.Equal(0b111u, t.GetCode(3));
        Assert.Equal(1, t.Decode(0u << 19, out int len));
        Assert.Equal(1, len);
        Assert.Equal(0, t.Decode(0b10u << 18, out len));
        Assert.Equal(2, len);
        Assert.Equal(3, t.Decode(0b111u << 17, out len));
        Assert.Equal(3, len);
    }

    [Fact]
    public void HuffmanTableDecodesLongCodesAndRejectsBadLengths()
    {
        // A chain code: lengths 1, 2, ... 19, 20, 20 uses the whole code space, with codes up to 20 bits.
        byte[] lengths = [.. Enumerable.Range(1, 20).Select(static i => (byte)i), 20];
        var t = new BZip2HuffmanTable();
        t.Build(lengths);
        for (int s = 0; s < lengths.Length; s++)
        {
            uint code = t.GetCode(s);
            int l = t.GetLength(s);
            Assert.Equal(s, t.Decode(code << (20 - l) | (uint)((1 << (20 - l)) - 1), out int len));
            Assert.Equal(l, len);
        }

        var bad = new BZip2HuffmanTable();
        Assert.False(bad.TryBuild([1, 1, 1]));
        Assert.False(bad.TryBuild([1, 2, 21]));
        Assert.False(bad.TryBuild([0, 2, 2]));
        Assert.Throws<ArgumentException>(() => bad.Build([1, 1, 1]));
        Assert.Throws<ArgumentException>(() => bad.TryBuild([1, 1]));
        Assert.True(bad.TryBuild([1, 2, 3]));
        Assert.Equal(-1, bad.Decode(0b111u << 17, out _));
    }

    [Fact]
    public void RunLengthDecoderInPiecesMatchesWhole()
    {
        byte[] original = [.. new byte[10], 1, 2, 2, 2, 2, 3, .. Enumerable.Repeat((byte)5, 300), 5, 5, 5, 5, 9, 9, 9];
        byte[] encoded = BZip2TestBuilder.RunLengthEncode(original);
        foreach (int inputPiece in new[] { 1, 2, 3, 1000 })
        {
            foreach (int outputPiece in new[] { 1, 5, 4096 })
            {
                var d = default(BZip2RunLengthDecoder);
                d.Reset();
                var result = new List<byte>();
                byte[] buffer = new byte[outputPiece];
                int at = 0;
                while (true)
                {
                    OperationStatus s = d.Decode(encoded.AsSpan(at, Math.Min(inputPiece, encoded.Length - at)), buffer, out int c, out int w);
                    at += c;
                    result.AddRange(buffer.AsSpan(0, w).ToArray());
                    if (s == OperationStatus.NeedMoreData && at >= encoded.Length)
                    {
                        break;
                    }
                }

                Assert.Equal(original, result);
                Assert.False(d.IsAwaitingCount);
            }
        }
    }

    [Fact]
    public void RandomizationIsItsOwnInverse()
    {
        byte[] data = BZip2TestData.Text(2, 5000);
        byte[] copy = data.ToArray();
        BZip2Randomization.Apply(copy);
        int flipped = 0;
        for (int i = 0; i < data.Length; i++)
        {
            flipped += data[i] != copy[i] ? 1 : 0;
        }

        Assert.InRange(flipped, 5, 12);
        BZip2Randomization.Apply(copy);
        Assert.Equal(data, copy);
        Assert.Equal(617, BZip2Randomization.FirstFlip);
        Assert.Equal(720, BZip2Randomization.GapAfter(0));
        Assert.Equal(619, BZip2Randomization.GapAfter(511));
    }

    [Fact]
    public void DecoderConsumesExactlyOneStreamPerDone()
    {
        BZip2Vector v = BZip2TestData.Vector("concat3-empty-middle");
        var decoder = new BZip2Decoder();
        byte[] buffer = new byte[10000];
        int pos = 0;
        int streams = 0;
        long total = 0;
        while (pos < v.Compressed.Length)
        {
            OperationStatus s = decoder.Decompress(v.Compressed.AsSpan(pos), buffer, out int c, out int w);
            pos += c;
            total += w;
            Assert.Equal(OperationStatus.Done, s);
            Assert.True(decoder.IsAtFrameBoundary);
            streams++;
        }

        Assert.Equal(3, streams);
        Assert.Equal(v.Size, total);
        Assert.Equal(v.Compressed.Length, decoder.TotalConsumed);
    }

    [Fact]
    public void DoneIsExactWhenInputArrivesInPieces()
    {
        // A stream's end must be reported with the right consumed count even when earlier calls staged bytes of its last header.
        BZip2Vector v = BZip2TestData.Vector("concat2");
        for (int piece = 1; piece < 40; piece++)
        {
            var decoder = new BZip2Decoder();
            byte[] buffer = new byte[10000];
            int pos = 0;
            int streams = 0;
            while (pos < v.Compressed.Length)
            {
                OperationStatus s = decoder.Decompress(v.Compressed.AsSpan(pos, Math.Min(piece, v.Compressed.Length - pos)), buffer, out int c, out _);
                pos += c;
                if (s == OperationStatus.Done)
                {
                    streams++;
                }
            }

            Assert.Equal(2, streams);
            Assert.Equal(v.Compressed.Length, pos);
        }
    }

    [Fact]
    public void SteadyStateAllocatesNothing()
    {
        BZip2Vector v = BZip2TestData.Vector("text300k-l1");
        var decoder = new BZip2Decoder();
        byte[] output = new byte[v.Size];
        for (int warm = 0; warm < 2; warm++)
        {
            decoder.Reset();
            decoder.Decompress(v.Compressed, output, out _, out _);
        }

        decoder.Reset();
        long before = GC.GetAllocatedBytesForCurrentThread();
        OperationStatus s = decoder.Decompress(v.Compressed, output, out int consumed, out int written);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(OperationStatus.Done, s);
        Assert.Equal(v.Size, written);
        Assert.Equal(v.Compressed.Length, consumed);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TrailingDataHandling()
    {
        BZip2Vector v = BZip2TestData.Vector("onebyte");
        byte[] withTail = [.. v.Compressed, 0, 0, 0, 0];
        Assert.Throws<CompressionFormatException>(() => BZ.Decompress(withTail));
        var allow = new BZip2DecompressionOptions { AllowTrailingData = true };
        Assert.Equal(v.Size, BZ.Decompress(withTail, allow).Length);
        Assert.Equal(v.Size, BZip2TestData.DecodeThroughStream(withTail, 3, 100, allow).Length);
        Assert.Throws<CompressionFormatException>(() => BZip2TestData.DecodeThroughStream(withTail, 3, 100));
        Assert.Throws<CompressionFormatException>(() => BZ.Decompress([]));
        Assert.Throws<CompressionFormatException>(() => BZip2TestData.DecodeThroughStream([], 3, 100));
    }

    [Fact]
    public void StreamLeavesInnerOpenOnRequestAndRefusesWrites()
    {
        var inner = new MemoryStream(BZip2TestData.Vector("onebyte").Compressed);
        var stream = new BZip2Stream(inner, leaveOpen: true);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        stream.Dispose();
        Assert.True(inner.CanRead);
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
        Assert.Throws<ArgumentException>(() => new BZip2Stream(new System.IO.Compression.GZipStream(new MemoryStream(), System.IO.Compression.CompressionMode.Compress)));
    }
}
