using System.Buffers;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Brotli;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.Fuzz;

/// <summary>
/// The fuzz target for the brotli decoder. It decodes the input through the one-shot call, through the span decoder fed and drained
/// in odd pieces, and through the read-mode stream, and treats any disagreement, or any exception outside the library's own
/// hierarchy, as a finding. The first byte of the input chooses options (trailing data allowed, a small window limit).
/// </summary>
internal static class BrotliTargets
{
    // Small limits keep each execution cheap and still exercise the limit path.
    private static BrotliDecompressionOptions Options(byte choice) => new()
    {
        MaxOutputSize = 1 << 22,
        MaxWindowSize = (choice & 2) != 0 ? 1 << 16 : BrotliDecompressionOptions.DefaultMaxWindowSize,
        AllowTrailingData = (choice & 1) != 0,
    };

    public static FuzzTarget Decode { get; } = new("brotli-decode", static data => Run(data), static e => e is CompressionException);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        BrotliDecompressionOptions options = Options(input[0]);
        byte[] data = input[1..].ToArray();
        (byte[]? whole, string? wholeError) = Attempt(() => BR.Decompress(data, options));
        (byte[]? pieces, string? piecesError) = Attempt(() => Pieces(data, options, fastPath: true));
        if ((wholeError is null) != (piecesError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, pieces {piecesError ?? "ok"}");
        }

        if (whole is not null && !whole.AsSpan().SequenceEqual(pieces))
        {
            throw new InvalidOperationException("paths produced different output");
        }

        // The decoder's fast command loop and its general path must agree on everything, down to the error message and its offset.
        (byte[]? general, string? generalError) = AttemptWithMessage(() => Pieces(data, options, fastPath: false));
        (byte[]? fast, string? fastError) = AttemptWithMessage(() => Pieces(data, options, fastPath: true));
        if (generalError != fastError || (general is null) != (fast is null) || (general is not null && !general.AsSpan().SequenceEqual(fast)))
        {
            throw new InvalidOperationException($"fast and general paths disagree: general {generalError ?? "ok"}, fast {fastError ?? "ok"}");
        }

        if (!options.AllowTrailingData)
        {
            (byte[]? streamed, string? streamError) = Attempt(() => ThroughStream(data, options));
            if ((wholeError is null) != (streamError is null))
            {
                throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, stream {streamError ?? "ok"}");
            }

            if (whole is not null && !whole.AsSpan().SequenceEqual(streamed))
            {
                throw new InvalidOperationException("stream produced different output");
            }
        }
    }

    private static (byte[]?, string?) Attempt(Func<byte[]> decode)
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

    private static (byte[]?, string?) AttemptWithMessage(Func<byte[]> decode)
    {
        try
        {
            return (decode(), null);
        }
        catch (CompressionException e)
        {
            return (null, e.GetType().Name + ": " + e.Message);
        }
    }

    // The span decoder with input and output cut at sizes taken from the data itself.
    private static byte[] Pieces(byte[] data, BrotliDecompressionOptions options, bool fastPath)
    {
        var decoder = new BrotliDecoder(options) { FastPathDisabled = !fastPath };
        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data.Length > 0 ? data[0] * 37 % 3000 : 0)];
        int pos = 0;
        int step = 1 + (data.Length > 0 ? data[^1] * 11 % 700 : 0);
        while (true)
        {
            OperationStatus status = decoder.Decompress(data.AsSpan(pos, Math.Min(step, data.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done)
            {
                if (pos < data.Length && !options.AllowTrailingData)
                {
                    throw new CompressionFormatException("brotli", pos, "the end of the input after the last meta-block");
                }

                break;
            }

            if (pos >= data.Length && status != OperationStatus.DestinationTooSmall)
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }

    private static byte[] ThroughStream(byte[] data, BrotliDecompressionOptions options)
    {
        using var stream = new BrotliStream(new MemoryStream(data), options);
        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data.Length > 0 ? data[0] * 53 % 5000 : 0)];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }
}
