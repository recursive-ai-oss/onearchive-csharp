using System.Buffers;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for the LZ4 decoders. The frame targets decode the input two ways and treat any disagreement between
/// them, or any exception outside the library's own hierarchy, as a finding. The block target checks that measuring,
/// decoding into an exact buffer and decoding with room to spare agree.
/// </summary>
internal static class Lz4Targets
{
    private static readonly Lz4Dictionary s_dictionary = MakeDictionary();

    // Small limits keep each execution cheap and still exercise every limit path.
    private static Lz4DecompressionOptions Options(bool dictionary, bool verify) => new()
    {
        MaxBlockSize = 1 << 21,
        MaxOutputSize = 1 << 24,
        VerifyChecksum = verify,
        Dictionary = dictionary ? s_dictionary : null,
        SkippableFrameHandler = static (_, _) => { },
        MaxSkippableFrameSize = 1 << 16,
    };

    private static Lz4Dictionary MakeDictionary()
    {
        byte[] data = new byte[8000];
        var r = new Random(5);
        string[] words = ["tick", "entity", "pos", "health", "state", "item", "count", "chunk"];
        int at = 0;
        while (at < data.Length)
        {
            string w = words[r.Next(words.Length)] + " ";
            for (int i = 0; i < w.Length && at < data.Length; i++)
            {
                data[at++] = (byte)w[i];
            }
        }

        return new Lz4Dictionary(data);
    }

    public static FuzzTarget Frame { get; } = new("lz4-decode", static data => Check(data.ToArray(), Options(false, true)), IsExpected);

    public static FuzzTarget FrameWithDictionary { get; } = new("lz4-decode-dict", static data =>
    {
        // The first byte picks the dictionary and whether checksums are verified (unverified, the fuzzer reaches the
        // block decoder without having to guess xxHash32 values).
        if (data.IsEmpty)
        {
            return;
        }

        Check(data[1..].ToArray(), Options((data[0] & 1) != 0, (data[0] & 2) == 0));
    }, IsExpected);

    public static FuzzTarget Block { get; } = new("lz4-block", static data =>
    {
        if (data.IsEmpty)
        {
            return;
        }

        int history = (data[0] & 3) == 0 ? 0 : Math.Min(s_dictionary.Length, 1 << (data[0] & 3) * 3);
        ReadOnlySpan<byte> block = data[1..];
        long size;
        try
        {
            size = Lz4BlockDecoder.GetDecompressedSize(block, history);
        }
        catch (CompressionFormatException)
        {
            // Whatever the measure refuses, the decoder must refuse or run out of room, never succeed.
            byte[] buffer = new byte[history + (1 << 16)];
            s_dictionary.Content[..history].CopyTo(buffer);
            try
            {
                if (Lz4BlockDecoder.TryDecode(block, buffer, history, out _))
                {
                    throw new InvalidOperationException("decode succeeded where measuring failed");
                }

                if (history > 0 && Lz4Block.TryDecompress(block, new byte[1 << 16], out _, new Lz4BlockOptions { Dictionary = new Lz4Dictionary(s_dictionary.Content[..history]) }))
                {
                    throw new InvalidOperationException("dictionary decode succeeded where measuring failed");
                }
            }
            catch (CompressionFormatException)
            {
            }

            return;
        }

        if (size > 1 << 24)
        {
            return;
        }

        byte[] exact = new byte[history + (int)size];
        s_dictionary.Content[..history].CopyTo(exact);
        if (!Lz4BlockDecoder.TryDecode(block, exact, history, out int written) || written != size)
        {
            throw new InvalidOperationException("exact-size decode disagrees with measuring");
        }

        byte[] roomy = new byte[history + (int)size + 70];
        s_dictionary.Content[..history].CopyTo(roomy);
        if (!Lz4BlockDecoder.TryDecode(block, roomy, history, out written) || written != size || !roomy.AsSpan(history, written).SequenceEqual(exact.AsSpan(history)))
        {
            throw new InvalidOperationException("decode with spare room differs");
        }

        if (size > 0 && Lz4BlockDecoder.TryDecode(block, new byte[history + (int)size - 1], history, out _))
        {
            throw new InvalidOperationException("decode succeeded in a buffer one byte too small");
        }

        // The public block API reads the dictionary in place instead of next to the output; it must give the same bytes.
        if (history > 0)
        {
            var options = new Lz4BlockOptions { Dictionary = new Lz4Dictionary(s_dictionary.Content[..history]) };
            byte[] direct = new byte[(int)size];
            if (!Lz4Block.TryDecompress(block, direct, out written, options) || written != size || !direct.AsSpan().SequenceEqual(exact.AsSpan(history)))
            {
                throw new InvalidOperationException("decode with the dictionary in place differs");
            }

            if (size > 0 && Lz4Block.TryDecompress(block, new byte[(int)size - 1], out _, options))
            {
                throw new InvalidOperationException("dictionary decode succeeded in a buffer one byte too small");
            }
        }
    }, IsExpected);

    private static bool IsExpected(Exception e) => e is CompressionException;

    // Mutated headers would nearly always fail the one-byte header checksum; repairing it lets the fuzzer reach the rest
    // of the header checks (the checksum's own failure is covered by unit tests).
    private static byte[] RepairHeaderChecksum(byte[] d)
    {
        if (d.Length >= 7 && d[0] == 0x04 && d[1] == 0x22 && d[2] == 0x4D && d[3] == 0x18)
        {
            int length = 4 + 2 + ((d[4] & 0x08) != 0 ? 8 : 0) + ((d[4] & 0x01) != 0 ? 4 : 0) + 1;
            if (d.Length >= length)
            {
                d[length - 1] = (byte)(RecursiveAI.OneCompress.Checksums.XxHash32.Compute(d.AsSpan(4, length - 5)) >> 8);
            }
        }

        return d;
    }

    private static void Check(byte[] data, Lz4DecompressionOptions options)
    {
        data = RepairHeaderChecksum(data);
        // Way one: all at once. Way two: the streaming decoder fed and drained in odd pieces.
        (byte[]? whole, string? wholeError) = Run(() => Lz4.Decompress(data, options));
        (byte[]? pieces, string? piecesError) = Run(() => Chunked(data, options));
        if ((wholeError is null) != (piecesError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, chunked {piecesError ?? "ok"}");
        }

        if (whole is not null && !whole.AsSpan().SequenceEqual(pieces))
        {
            throw new InvalidOperationException("paths produced different output");
        }
    }

    private static (byte[]?, string?) Run(Func<byte[]> decode)
    {
        try
        {
            return (decode(), null);
        }
        catch (CompressionException e)
        {
            return (null, e.GetType().Name);
        }
    }

    private static byte[] Chunked(byte[] data, Lz4DecompressionOptions options)
    {
        if (data.Length == 0)
        {
            throw new CompressionFormatException("lz4", 0, "a frame", isTruncated: true);
        }

        var decoder = new Lz4Decoder(options);
        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data[0] * 37 % 5000)];
        int pos = 0;
        int step = 1 + (data[^1] * 11 % 700);
        while (true)
        {
            OperationStatus status = decoder.Decompress(data.AsSpan(pos, Math.Min(step, data.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (pos >= data.Length && status != OperationStatus.DestinationTooSmall && !(status == OperationStatus.Done && pos < data.Length))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }
}
