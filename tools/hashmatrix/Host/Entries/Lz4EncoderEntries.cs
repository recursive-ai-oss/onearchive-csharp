using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// LZ4 compression at every level and with the options that change the stream: blocks, frames of both block layouts and
/// several block sizes and checksums, a dictionary (with and without an ID), and the streaming encoder fed in odd pieces
/// with small output buffers. The bytes must be the same in every column: the encoders use integer arithmetic and fixed
/// hashes only, and vector code never changes which candidates are chosen.
/// </summary>
internal static class Lz4EncoderEntries
{
    // The levels the manifest covers: the fast levels at a few accelerations, then every high-compression level.
    private static readonly int[] s_levels = [-65536, -100, -9, -4, -2, -1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    [ModuleInitializer]
    internal static void Register()
    {
        foreach (int level in s_levels)
        {
            int l = level;
            string name = level < 0 ? $"fast{-level}" : $"level{level}";
            Matrix.Add("lz4-enc", $"block-{name}", (input, _) => Lz4Block.Compress(input, new Lz4CompressionOptions { Level = l }));
            Matrix.Add("lz4-enc", $"frame-{name}", (input, _) => Lz4.Compress(input, new Lz4CompressionOptions { Level = l }));
        }

        foreach (int level in new[] { -3, 1, 4, 9, 12 })
        {
            int l = level;
            string name = level < 0 ? $"fast{-level}" : $"level{level}";

            // Linked 64 KiB blocks with both checksums and no content size: history crosses block boundaries.
            Matrix.Add("lz4-enc", $"frame-linked-64k-{name}", (input, _) => Lz4.Compress(input, new Lz4CompressionOptions
            {
                Level = l,
                BlocksIndependent = false,
                BlockSize = Lz4BlockSize.Size64KiB,
                BlockChecksum = true,
                IncludeContentSize = false,
            }));
            Matrix.Add("lz4-enc", $"frame-independent-256k-{name}", (input, _) => Lz4.Compress(input, new Lz4CompressionOptions
            {
                Level = l,
                BlockSize = Lz4BlockSize.Size256KiB,
                ContentChecksum = false,
            }));

            // A dictionary, with an ID in the frame and without; the block form too.
            Matrix.Add("lz4-enc", $"block-dict-{name}", (input, _) => Lz4Block.Compress(input, new Lz4CompressionOptions { Level = l, Dictionary = Dictionary(0) }));
            Matrix.Add("lz4-enc", $"frame-dict-{name}", (input, _) => Lz4.Compress(input, new Lz4CompressionOptions { Level = l, Dictionary = Dictionary(0) }));
            Matrix.Add("lz4-enc", $"frame-dict-id-linked-{name}", (input, _) => Lz4.Compress(input, new Lz4CompressionOptions
            {
                Level = l,
                Dictionary = Dictionary(0x1234ABCD),
                BlocksIndependent = false,
                BlockSize = Lz4BlockSize.Size64KiB,
            }));

            // The streaming encoder: input in odd pieces, output through a small buffer, one flush in the middle.
            Matrix.Add("lz4-enc", $"stream-pieces-{name}", (input, _) => Pieces(input, l, linked: false));
            Matrix.Add("lz4-enc", $"stream-pieces-linked-{name}", (input, _) => Pieces(input, l, linked: true));
        }

        // Many small payloads through one reused encoder with a dictionary (the per-message use): frames are concatenated.
        Matrix.Add("lz4-enc", "payloads-dict-level1", (input, _) => Payloads(input, 1, Dictionary(7)), "binary-1m", "text-3m");
        Matrix.Add("lz4-enc", "payloads-dict-level9", (input, _) => Payloads(input, 9, Dictionary(7)), "binary-1m", "text-3m");
        Matrix.Add("lz4-enc", "payloads-nodict-fast3", (input, _) => Payloads(input, -3, null), "binary-1m", "text-3m");
    }

    // A fixed dictionary built from the standard text input, so every column has the same bytes.
    private static Lz4Dictionary Dictionary(uint id)
    {
        byte[] text = Inputs.Get("text-64k");
        return new Lz4Dictionary(text.AsSpan(text.Length - 20_000, 12_000), id);
    }

    private static byte[] Pieces(ReadOnlySpan<byte> input, int level, bool linked)
    {
        var options = new Lz4CompressionOptions { Level = level, BlocksIndependent = !linked, BlockSize = Lz4BlockSize.Size64KiB, IncludeContentSize = false };
        var encoder = new Lz4Encoder(options);
        var result = new MemoryStream();
        byte[] buffer = new byte[4099];
        int pos = 0;
        int piece = 1;
        bool flushed = false;
        while (true)
        {
            int take = Math.Min(piece, input.Length - pos);
            bool last = pos + take == input.Length;
            OperationStatus status = encoder.Compress(input.Slice(pos, take), buffer, out int c, out int w, last);
            result.Write(buffer, 0, w);
            pos += c;
            if (status == OperationStatus.Done && pos == input.Length)
            {
                break;
            }

            if (status == OperationStatus.Done)
            {
                piece = (piece * 3) + 1 > 70_000 ? 1 : (piece * 3) + 1;
                if (!flushed && pos > input.Length / 2)
                {
                    flushed = true;
                    while (encoder.Flush(buffer, out w) == OperationStatus.DestinationTooSmall)
                    {
                        result.Write(buffer, 0, w);
                    }

                    result.Write(buffer, 0, w);
                }
            }
        }

        return result.ToArray();
    }

    private static byte[] Payloads(ReadOnlySpan<byte> input, int level, Lz4Dictionary? dictionary)
    {
        var encoder = new Lz4Encoder(new Lz4CompressionOptions { Level = level, Dictionary = dictionary, ContentChecksum = false });
        var result = new MemoryStream();
        byte[] buffer = new byte[2048];
        int size = 37;
        int pos = 0;
        while (pos < input.Length && result.Length < (4 << 20))
        {
            int take = Math.Min(size, input.Length - pos);
            if (!encoder.TryCompressFrame(input.Slice(pos, take), buffer, out int w))
            {
                throw new InvalidOperationException("frame did not fit");
            }

            result.Write(buffer, 0, w);
            pos += take;
            size = ((size * 7) + 13) % 900 + 1;
        }

        return result.ToArray();
    }
}
