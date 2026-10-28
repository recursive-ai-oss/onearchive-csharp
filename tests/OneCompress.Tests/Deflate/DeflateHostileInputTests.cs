using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Malformed input ends in a typed error, never another exception, and never in a successful short result.</summary>
public class DeflateHostileInputTests
{
    // A bit writer for building streams by hand (RFC 1951 section 3.1.1: least significant bit first).
    internal sealed class Bits
    {
        private readonly List<byte> _bytes = [];
        private int _used;

        public Bits Put(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_used == 0)
                {
                    _bytes.Add(0);
                }

                _bytes[^1] |= (byte)(((value >> i) & 1) << _used);
                _used = (_used + 1) & 7;
            }

            return this;
        }

        // A Huffman code goes out most significant bit first.
        public Bits Code(uint code, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                Put((code >> i) & 1, 1);
            }

            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    private static CompressionException? TryDecode(DeflateFormat format, byte[] data)
    {
        try
        {
            _ = format switch
            {
                DeflateFormat.Raw => Deflate.Deflate.Decompress(data),
                DeflateFormat.ZLib => ZLib.Decompress(data),
                _ => GZip.Decompress(data),
            };
            return null;
        }
        catch (CompressionException e)
        {
            return e;
        }
    }

    [Fact]
    public void ReservedBlockTypeIsRefused()
    {
        byte[] data = new Bits().Put(1, 1).Put(3, 2).ToArray();
        var e = Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data));
        Assert.Equal("deflate", e.Format);
        Assert.Contains("block type", e.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void StoredBlockLengthComplementIsChecked()
    {
        byte[] data = new Bits().Put(1, 1).Put(0, 2).Put(0, 5).Put(5, 16).Put(0xFFFA ^ 1, 16).ToArray();
        var e = Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data));
        Assert.Contains("NLEN", e.Expected, StringComparison.Ordinal);
        Assert.Equal(5, e.Offset);
        byte[] good = new Bits().Put(1, 1).Put(0, 2).Put(0, 5).Put(5, 16).Put(0xFFFA, 16).ToArray();
        var truncated = Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, [.. good, 1, 2, 3]));
        Assert.True(truncated.IsTruncated);
        Assert.Equal([1, 2, 3, 4, 5], Deflate.Deflate.Decompress([.. good, 1, 2, 3, 4, 5]));
    }

    [Fact]
    public void DistanceBeforeTheStartIsRefused()
    {
        // Fixed block: length code 257 (7 bits 0000001) then distance code 0 (5 bits) with nothing written yet.
        byte[] data = new Bits().Put(1, 1).Put(1, 2).Code(1, 7).Code(0, 5).Code(0, 7).ToArray();
        var e = Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data));
        Assert.Contains("distance", e.Expected, StringComparison.Ordinal);

        // One literal 'a' (0x30 + 0x61 = code 0x91, 8 bits), then distance 2 reaches before the start.
        data = new Bits().Put(1, 1).Put(1, 2).Code(0x91, 8).Code(1, 7).Code(1, 5).Code(0, 7).ToArray();
        Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data));

        // And distance 1 is fine: 'a' then a length-3 match at distance 1 gives "aaaa".
        data = new Bits().Put(1, 1).Put(1, 2).Code(0x91, 8).Code(1, 7).Code(0, 5).Code(0, 7).ToArray();
        Assert.Equal("aaaa"u8.ToArray(), Deflate.Deflate.Decompress(data));
    }

    [Fact]
    public void UnusedSymbolsAreRefused()
    {
        // Fixed literal/length symbol 286 is the 8-bit code 0xC6 (11000110); distance symbol 30 is 11110.
        byte[] data = new Bits().Put(1, 1).Put(1, 2).Code(0xC6, 8).ToArray();
        Assert.Contains("literal/length", Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data)).Expected, StringComparison.Ordinal);
        data = new Bits().Put(1, 1).Put(1, 2).Code(0x91, 8).Code(1, 7).Code(30, 5).Code(0, 7).ToArray();
        Assert.Contains("distance", Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data)).Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicHeaderIsValidated()
    {
        // HLIT 287 is above 286.
        byte[] data = new Bits().Put(1, 1).Put(2, 2).Put(30, 5).Put(0, 5).Put(0, 4).ToArray();
        Assert.Contains("286", Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data)).Expected, StringComparison.Ordinal);
        // HDIST 31 is above 30.
        data = new Bits().Put(1, 1).Put(2, 2).Put(0, 5).Put(30, 5).Put(0, 4).ToArray();
        Assert.Contains("30", Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data)).Expected, StringComparison.Ordinal);
        // An all-zero code-length code has no symbols: the first code-length symbol cannot be decoded.
        data = new Bits().Put(1, 1).Put(2, 2).Put(0, 5).Put(0, 5).Put(0, 4).Put(0, 12).Put(0, 16).ToArray();
        Assert.IsType<CompressionFormatException>(TryDecode(DeflateFormat.Raw, data));
    }

    private static byte[] RepairedPrefix(byte[] data, int length) => data[..length];

    // Every prefix of a stream is an error (single-member formats), and says it was truncated when it ended inside the stream.
    [Theory]
    [InlineData("text300-l6-gzip")]
    [InlineData("text300-l6-zlib")]
    [InlineData("text300-l6-raw")]
    [InlineData("text300-l0-raw")]
    [InlineData("bin150k-fixed-raw")]
    [InlineData("header-all-fields")]
    [InlineData("text300-dict-zlib")]
    public void EveryPrefixIsATruncationError(string name)
    {
        DeflateVector v = DeflateTestData.Vector(name);
        int step = Math.Max(1, v.Compressed.Length / 1500);
        for (int length = 0; length < v.Compressed.Length; length += (length < 400 ? 1 : step))
        {
            byte[] prefix = RepairedPrefix(v.Compressed, length);
            Exception? e = null;
            try
            {
                _ = v.Format switch
                {
                    DeflateFormat.Raw => Deflate.Deflate.Decompress(prefix, v.Options()),
                    DeflateFormat.ZLib => ZLib.Decompress(prefix, v.Options()),
                    _ => GZip.Decompress(prefix, v.Options()),
                };
            }
            catch (CompressionFormatException f)
            {
                e = f;
                Assert.True(f.IsTruncated, $"{name} cut at {length}: {f.Message}");
                Assert.Equal(length, f.Offset);
            }
            catch (CompressionDictionaryException)
            {
                // A cut inside the zlib header names no dictionary yet.
                e = new InvalidOperationException();
            }

            Assert.True(e is not null, $"{name} cut at {length} decoded without an error");
        }
    }

    // Every single-bit flip is either still valid or a typed error.
    [Theory]
    [InlineData("text300-l6-gzip")]
    [InlineData("text300-l6-zlib")]
    [InlineData("text300-l6-raw")]
    [InlineData("text300-l9-raw")]
    [InlineData("bin150k-fixed-raw")]
    [InlineData("text300-l0-raw")]
    [InlineData("text300-dict-raw")]
    [InlineData("header-all-fields")]
    public void BitFlipsEndInTypedErrors(string name)
    {
        DeflateVector v = DeflateTestData.Vector(name);
        int total = Math.Min(v.Compressed.Length, 600);
        for (int i = 0; i < total; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] data = (byte[])v.Compressed.Clone();
                data[i] ^= (byte)(1 << bit);
                try
                {
                    _ = v.Format switch
                    {
                        DeflateFormat.Raw => Deflate.Deflate.Decompress(data, new DeflateDecompressionOptions { Dictionary = v.Dictionary, MaxOutputSize = 1 << 22 }),
                        DeflateFormat.ZLib => ZLib.Decompress(data, new DeflateDecompressionOptions { Dictionary = v.Dictionary, MaxOutputSize = 1 << 22 }),
                        _ => GZip.Decompress(data, new DeflateDecompressionOptions { MaxOutputSize = 1 << 22 }),
                    };
                }
                catch (CompressionException)
                {
                }
            }
        }
    }

    // The same flips decoded in odd pieces behave exactly like the one-shot path.
    [Fact]
    public void PiecewiseDecodeAgreesWithOneShotOnCorruptInput()
    {
        DeflateVector v = DeflateTestData.Vector("text300-l6-raw");
        var rng = new Random(11);
        for (int n = 0; n < 400; n++)
        {
            byte[] data = (byte[])v.Compressed.Clone();
            int flips = 1 + rng.Next(3);
            for (int f = 0; f < flips; f++)
            {
                data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
            }

            string whole = Describe(() => Deflate.Deflate.Decompress(data));
            string pieces = Describe(() => DeflateVectorTests.Pieces(DeflateFormat.Raw, data, DeflateDecompressionOptions.Default, 1 + rng.Next(40), 1 + rng.Next(300)));
            Assert.Equal(whole, pieces);
        }
    }

    private static string Describe(Func<byte[]> decode)
    {
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(decode()));
        }
        catch (CompressionFormatException e)
        {
            return "format:" + e.Expected + ":" + e.IsTruncated;
        }
    }

    [Fact]
    public void RandomBytesNeverEscapeTheHierarchy()
    {
        var rng = new Random(5);
        for (int n = 0; n < 3000; n++)
        {
            byte[] data = new byte[rng.Next(4, 200)];
            rng.NextBytes(data);
            if (n % 3 == 1)
            {
                data[0] = 0x1F;
                data[1] = 0x8B;
                data[2] = 8;
                data[3] &= 0x1F;
            }
            else if (n % 3 == 2)
            {
                data[0] = 0x78;
                data[1] = 0x9C;
            }

            foreach (DeflateFormat format in new[] { DeflateFormat.Raw, DeflateFormat.ZLib, DeflateFormat.GZip })
            {
                try
                {
                    _ = format switch
                    {
                        DeflateFormat.Raw => Deflate.Deflate.Decompress(data, new DeflateDecompressionOptions { MaxOutputSize = 1 << 20 }),
                        DeflateFormat.ZLib => ZLib.Decompress(data, new DeflateDecompressionOptions { MaxOutputSize = 1 << 20 }),
                        _ => GZip.Decompress(data, new DeflateDecompressionOptions { MaxOutputSize = 1 << 20 }),
                    };
                }
                catch (CompressionException)
                {
                }
            }
        }
    }
}
