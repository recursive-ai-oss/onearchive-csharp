using System.IO.Compression;
using K4os.Compression.LZ4;
using ZstdSharp;

namespace RecursiveAI.Bar;

/// <summary>Role of an implementation in a bar row.</summary>
internal enum Role
{
    /// <summary>Our own implementation (or, until a codec lands, a stand-in): gated against the reference.</summary>
    Ours,
    /// <summary>A comparison library: reported, never gated.</summary>
    Comparison,
}

/// <summary>
/// The plug-in interface codec units implement: one instance per (codec, implementation).
/// Everything is memory to memory over caller-provided buffers.
/// </summary>
internal interface IBarCodec
{
    /// <summary>Codec id shared with the reference table: deflate, zlib, gzip, brotli, zstd, lz4, ...</summary>
    string Codec { get; }

    /// <summary>Implementation label printed in rows, e.g. "ours" or "bcl-standin".</summary>
    string Impl { get; }

    Role Role { get; }

    /// <summary>Levels this implementation supports, in the reference tool's numbering.</summary>
    IReadOnlyList<int> Levels { get; }

    /// <summary>True when the level uses more than one thread; the reference is then run with the same thread count.</summary>
    bool IsMultithreaded(int level);

    /// <summary>Upper bound of the compressed size of <paramref name="inputLength"/> bytes.</summary>
    int MaxCompressedLength(int inputLength);

    /// <summary>Compresses into <paramref name="dst"/> and returns the compressed length.</summary>
    int Compress(ReadOnlyMemory<byte> src, Memory<byte> dst, int level, int threads);

    /// <summary>Decompresses into <paramref name="dst"/> (exactly the original size) and returns the length written.</summary>
    int Decompress(ReadOnlyMemory<byte> src, Memory<byte> dst);
}

/// <summary>Registry of implementations. Codec units add their entries here.</summary>
internal static class Registry
{
    public static IReadOnlyList<IBarCodec> All { get; } =
    [
        // Stand-ins that prove the plumbing until our own codecs exist. They are gated like "ours".
        new BclDeflate("deflate", "bcl-standin", Role.Ours, (s, l) => new DeflateStream(s, Options(l), leaveOpen: true), s => new DeflateStream(s, CompressionMode.Decompress)),
        new BclDeflate("zlib", "bcl-standin", Role.Ours, (s, l) => new ZLibStream(s, Options(l), leaveOpen: true), s => new ZLibStream(s, CompressionMode.Decompress)),
        new BclDeflate("gzip", "bcl-standin", Role.Ours, (s, l) => new GZipStream(s, Options(l), leaveOpen: true), s => new GZipStream(s, CompressionMode.Decompress)),
        new BclBrotli(),
        // Comparison libraries.
        new ZstdSharpCodec(),
        new K4osLz4Codec(),
    ];

    public static IBarCodec? Find(string codec, string impl) =>
        All.FirstOrDefault(c => c.Codec == codec && c.Impl == impl);

    private static ZLibCompressionOptions Options(int level) => new() { CompressionLevel = level };
}

internal sealed class BclDeflate(string codec, string impl, Role role,
    Func<Stream, int, Stream> compress, Func<Stream, Stream> decompress) : IBarCodec
{
    public string Codec => codec;
    public string Impl => impl;
    public Role Role => role;
    public IReadOnlyList<int> Levels { get; } = [1, 6, 9];
    public bool IsMultithreaded(int level) => false;
    public int MaxCompressedLength(int inputLength) => inputLength + (inputLength >> 3) + 1024;

    public int Compress(ReadOnlyMemory<byte> src, Memory<byte> dst, int level, int threads)
    {
        var sink = new MemorySink(dst);
        using (var z = compress(sink, level))
        {
            z.Write(src.Span);
        }

        return sink.Written;
    }

    public int Decompress(ReadOnlyMemory<byte> src, Memory<byte> dst)
    {
        using var input = new ReadOnlyMemoryStream(src);
        using var z = decompress(input);
        return StreamUtil.ReadFully(z, dst.Span);
    }
}

internal sealed class BclBrotli : IBarCodec
{
    public string Codec => "brotli";
    public string Impl => "bcl-standin";
    public Role Role => Role.Ours;
    public IReadOnlyList<int> Levels { get; } = [1, 5, 9, 11];
    public bool IsMultithreaded(int level) => false;
    public int MaxCompressedLength(int inputLength) => BrotliEncoder.GetMaxCompressedLength(inputLength);

    public int Compress(ReadOnlyMemory<byte> src, Memory<byte> dst, int level, int threads)
    {
        if (!BrotliEncoder.TryCompress(src.Span, dst.Span, out int written, level, 22))
        {
            throw new InvalidOperationException("brotli: output buffer too small");
        }

        return written;
    }

    public int Decompress(ReadOnlyMemory<byte> src, Memory<byte> dst)
    {
        if (!BrotliDecoder.TryDecompress(src.Span, dst.Span, out int written))
        {
            throw new InvalidOperationException("brotli: decompression failed");
        }

        return written;
    }
}

internal sealed class ZstdSharpCodec : IBarCodec
{
    public string Codec => "zstd";
    public string Impl => "zstdsharp";
    public Role Role => Role.Comparison;
    public IReadOnlyList<int> Levels { get; } = [1, 3, 9, 19];
    public bool IsMultithreaded(int level) => false;
    public int MaxCompressedLength(int inputLength) => inputLength + (inputLength >> 7) + 1024;

    public int Compress(ReadOnlyMemory<byte> src, Memory<byte> dst, int level, int threads)
    {
        using var c = new Compressor(level);
        return c.Wrap(src.Span, dst.Span);
    }

    public int Decompress(ReadOnlyMemory<byte> src, Memory<byte> dst)
    {
        using var d = new Decompressor();
        return d.Unwrap(src.Span, dst.Span);
    }
}

internal sealed class K4osLz4Codec : IBarCodec
{
    public string Codec => "lz4";
    public string Impl => "k4os";
    public Role Role => Role.Comparison;
    public IReadOnlyList<int> Levels { get; } = [1, 3, 9];
    public bool IsMultithreaded(int level) => false;
    public int MaxCompressedLength(int inputLength) => LZ4Codec.MaximumOutputSize(inputLength);

    // The lz4 CLI treats levels below 3 as the fast mode.
    public int Compress(ReadOnlyMemory<byte> src, Memory<byte> dst, int level, int threads) =>
        LZ4Codec.Encode(src.Span, dst.Span, level < 3 ? LZ4Level.L00_FAST : (LZ4Level)level);

    public int Decompress(ReadOnlyMemory<byte> src, Memory<byte> dst) => LZ4Codec.Decode(src.Span, dst.Span);
}

