using RecursiveAI.OneCompress;
using RecursiveAI.OneArchive.Internal;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>A window onto the archive file: one packed stream. It seeks the shared file before each read, so several can be open at once.</summary>
internal sealed class SevenZipPackStream(Stream file, long start, long length, long fileOffsetForErrors, ExpansionMeter meter) : Stream
{
    private long _pos;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _pos;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long left = length - _pos;
        if (left <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > left)
        {
            buffer = buffer[..(int)left];
        }

        file.Position = start + _pos;
        int n = file.Read(buffer);
        if (n == 0)
        {
            throw new CompressionFormatException(SevenZipFormat.Name, start + _pos, "the rest of the packed stream; the archive ends early", true);
        }

        _pos += n;
        meter.AddCompressed(n);
        _ = fileOffsetForErrors;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Delivers exactly <c>size</c> bytes of a coder's output: ends there, and reports a coder that stops early.</summary>
internal sealed class SevenZipLimitedStream(Stream inner, long size, long errorOffset) : Stream
{
    private long _pos;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => size;

    public override long Position
    {
        get => _pos;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long left = size - _pos;
        if (left <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > left)
        {
            buffer = buffer[..(int)left];
        }

        int n = inner.Read(buffer);
        if (n == 0)
        {
            throw new CompressionFormatException(SevenZipFormat.Name, errorOffset, $"{size - _pos} more bytes of unpacked data; the coder's input ended", true);
        }

        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Builds the decoding stream of a folder from its coder graph (7zFormat.txt "Folder").</summary>
internal static class SevenZipFolderDecoder
{
    public static Stream Open(SevenZipFolder folder, Func<int, Stream> openPacked, long errorOffset, ArchiveLimits limits, SevenZipReaderOptions options, SevenZipKeyCache keys)
    {
        // Check the methods first so that an unsupported one fails before any input is touched.
        foreach (SevenZipCoder c in folder.Coders)
        {
            if (!IsSupported(c))
            {
                throw SevenZipFormat.UnsupportedMethod(c.MethodId);
            }
        }

        int n = folder.Coders.Count;
        var inFirst = new int[n];
        var outFirst = new int[n];
        int ai = 0;
        int ao = 0;
        for (int i = 0; i < n; i++)
        {
            inFirst[i] = ai;
            outFirst[i] = ao;
            ai += folder.Coders[i].NumInStreams;
            ao += folder.Coders[i].NumOutStreams;
        }

        var opened = new List<Stream>();
        try
        {
            Stream Out(int coder)
            {
                SevenZipCoder c = folder.Coders[coder];
                var inputs = new Stream[c.NumInStreams];
                for (int j = 0; j < inputs.Length; j++)
                {
                    inputs[j] = In(inFirst[coder] + j);
                }

                Stream decoded = Decode(c, inputs, limits, options, keys, folder.UnpackSizes[outFirst[coder]], errorOffset);
                opened.Add(decoded);
                return new SevenZipLimitedStream(decoded, folder.UnpackSizes[outFirst[coder]], errorOffset);
            }

            Stream In(int inIndex)
            {
                foreach (SevenZipBindPair b in folder.BindPairs)
                {
                    if (b.InIndex == inIndex)
                    {
                        for (int i = n - 1; i >= 0; i--)
                        {
                            if (b.OutIndex >= outFirst[i])
                            {
                                return Out(i);
                            }
                        }
                    }
                }

                for (int k = 0; k < folder.PackedStreams.Count; k++)
                {
                    if (folder.PackedStreams[k] == inIndex)
                    {
                        Stream s = openPacked(k);
                        opened.Add(s);
                        return s;
                    }
                }

                throw new InvalidOperationException("unbound coder input");
            }

            int mainCoder = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                if (folder.MainOutStream >= outFirst[i])
                {
                    mainCoder = i;
                    break;
                }
            }

            return Out(mainCoder);
        }
        catch
        {
            foreach (Stream s in opened)
            {
                s.Dispose();
            }

            throw;
        }
    }

    private static bool IsSupported(SevenZipCoder c) =>
        c.NumInStreams == 1 && c.NumOutStreams == 1 &&
        (c.MethodId is SevenZipFormat.MethodCopy or SevenZipFormat.MethodDeflate or SevenZipFormat.MethodDeflate64 or SevenZipFormat.MethodBZip2 or SevenZipFormat.MethodZstd
            or SevenZipFormat.MethodLzma or SevenZipFormat.MethodLzma2 or SevenZipFormat.MethodDelta or SevenZipFormat.MethodAes ||
         IsBranchFilter(c.MethodId));

    // Methods.txt: the branch filters have a short ID (04..0B) and, for the older ones, a long 03 03 xx xx ID.
    private static bool IsBranchFilter(ulong id) =>
        id is >= 0x04 and <= 0x0B or 0x03030103 or 0x03030205 or 0x03030401 or 0x03030501 or 0x03030701 or 0x03030805;

    internal static CompressionFormatException BadProps(long errorOffset, SevenZipCoder c, string expected) =>
        new(SevenZipFormat.Name, errorOffset, $"{expected} in the properties of the {SevenZipFormat.MethodName(c.MethodId)} coder");

    // Methods.txt: 00 Copy, 04 01 08 Deflate, 04 02 02 BZip2, 03 01 01 LZMA, 21 LZMA2, 03 Delta, the branch filters, and
    // F7 11 01 Zstandard. A coder's output is cut to the size the header gives, so the decoders run without an output limit
    // of their own.
    private static Stream Decode(SevenZipCoder c, Stream[] inputs, ArchiveLimits limits, SevenZipReaderOptions options, SevenZipKeyCache keys, long unpackSize, long errorOffset)
    {
        Stream input = inputs[0];
        if (IsBranchFilter(c.MethodId))
        {
            return Codec(CodecId.Filters, c).OpenSevenZipDecoder(c, input, limits, options, unpackSize, errorOffset);
        }

        switch (c.MethodId)
        {
            case SevenZipFormat.MethodCopy:
                return input;
            case SevenZipFormat.MethodAes:
                {
                    ReadOnlySpan<byte> props = c.Properties.Span;
                    if (!SevenZipAes.TryParse(props, out SevenZipAes.Parameters aes, out string? problem))
                    {
                        throw BadProps(errorOffset, c, problem!);
                    }

                    if (options.Password.IsEmpty)
                    {
                        throw new ArchivePasswordException(SevenZipFormat.Name, string.Empty, ArchivePasswordProblem.Required);
                    }

                    if (aes.Power > options.MaxKeyDerivationPower)
                    {
                        throw new CompressionLimitException(SevenZipFormat.Name, nameof(SevenZipReaderOptions.MaxKeyDerivationPower), options.MaxKeyDerivationPower, aes.Power);
                    }

                    return new SevenZipAesDecryptStream(input, keys.Get(options.Password, aes), aes.Iv, errorOffset);
                }

            case SevenZipFormat.MethodDeflate:
                return Codec(CodecId.Deflate, c).OpenSevenZipDecoder(c, input, limits, options, unpackSize, errorOffset);
            case SevenZipFormat.MethodDeflate64:
                return Codec(CodecId.Deflate64, c).OpenSevenZipDecoder(c, input, limits, options, unpackSize, errorOffset);
            case SevenZipFormat.MethodBZip2:
                return Codec(CodecId.BZip2, c).OpenSevenZipDecoder(c, input, limits, options, unpackSize, errorOffset);
            case SevenZipFormat.MethodZstd:
                return Codec(CodecId.Zstd, c).OpenSevenZipDecoder(c, input, limits, options, unpackSize, errorOffset);
            case SevenZipFormat.MethodDelta:
                return Codec(CodecId.Filters, c).OpenSevenZipDecoder(c, input, limits, options, unpackSize, errorOffset);
            case SevenZipFormat.MethodLzma:
            case SevenZipFormat.MethodLzma2:
                return Codec(CodecId.Lzma, c).OpenSevenZipDecoder(c, input, limits, options, unpackSize, errorOffset);
            default:
                throw SevenZipFormat.UnsupportedMethod(c.MethodId);
        }
    }

    private static ArchiveCodec Codec(CodecId id, SevenZipCoder c) =>
        CodecRegistry.Require(id, SevenZipFormat.Name, $"the {SevenZipFormat.MethodName(c.MethodId)} method");
}
