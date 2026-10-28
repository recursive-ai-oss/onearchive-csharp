using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Zstd;
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
         BranchOf(c.MethodId) is not null);

    // Methods.txt: the branch filters have a short ID (04..0B) and, for the older ones, a long 03 03 xx xx ID.
    private static BranchArchitecture? BranchOf(ulong id) => id switch
    {
        0x04 or 0x03030103 => BranchArchitecture.X86,
        0x05 or 0x03030205 => BranchArchitecture.PowerPc,
        0x06 or 0x03030401 => BranchArchitecture.Ia64,
        0x07 or 0x03030501 => BranchArchitecture.Arm,
        0x08 or 0x03030701 => BranchArchitecture.ArmThumb,
        0x09 or 0x03030805 => BranchArchitecture.Sparc,
        0x0A => BranchArchitecture.Arm64,
        0x0B => BranchArchitecture.RiscV,
        _ => null,
    };

    private static CompressionFormatException BadProps(long errorOffset, SevenZipCoder c, string expected) =>
        new(SevenZipFormat.Name, errorOffset, $"{expected} in the properties of the {SevenZipFormat.MethodName(c.MethodId)} coder");

    // Methods.txt: 00 Copy, 04 01 08 Deflate, 04 02 02 BZip2, 03 01 01 LZMA, 21 LZMA2, 03 Delta, the branch filters, and
    // F7 11 01 Zstandard. A coder's output is cut to the size the header gives, so the decoders run without an output limit
    // of their own.
    private static Stream Decode(SevenZipCoder c, Stream[] inputs, ArchiveLimits limits, SevenZipReaderOptions options, SevenZipKeyCache keys, long unpackSize, long errorOffset)
    {
        Stream input = inputs[0];
        ReadOnlySpan<byte> props = c.Properties.Span;
        var lzmaOptions = new LzmaDecompressionOptions { MaxDictionarySize = options.MaxDictionarySize, MaxOutputSize = long.MaxValue };
        if (BranchOf(c.MethodId) is { } arch)
        {
            // An optional 4-byte start offset (the address the first byte is assumed to have).
            if (props.Length != 0 && props.Length != 4)
            {
                throw BadProps(errorOffset, c, "no property or a 4-byte start offset");
            }

            uint start = props.Length == 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(props) : 0;
            try
            {
                return new SevenZipBranchStream(input, new BranchFilter(arch, start));
            }
            catch (ArgumentException)
            {
                throw BadProps(errorOffset, c, "a start offset the filter accepts");
            }
        }

        switch (c.MethodId)
        {
            case SevenZipFormat.MethodCopy:
                return input;
            case SevenZipFormat.MethodAes:
                {
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
                return new DeflateCodecStream(input, DeflateFormat.Raw, new DeflateDecompressionOptions { MaxOutputSize = long.MaxValue });
            case SevenZipFormat.MethodDeflate64:
                return new RecursiveAI.OneCompress.Deflate64.Deflate64Stream(input, new RecursiveAI.OneCompress.Deflate64.Deflate64DecompressionOptions { MaxOutputSize = long.MaxValue });
            case SevenZipFormat.MethodBZip2:
                return new RecursiveAI.OneCompress.BZip2.BZip2Stream(input, new RecursiveAI.OneCompress.BZip2.BZip2DecompressionOptions { MaxOutputSize = long.MaxValue });
            case SevenZipFormat.MethodZstd:
                return new ZstdStream(input, new ZstdDecompressionOptions { MaxWindowSize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue });
            case SevenZipFormat.MethodDelta:
                if (props.Length != 1)
                {
                    throw BadProps(errorOffset, c, "one byte (the distance minus one)");
                }

                return new SevenZipDeltaStream(input, new DeltaFilter(props[0] + 1));
            case SevenZipFormat.MethodLzma:
                {
                    if (props.Length != 5 || !LzmaProperties.TryParsePropertiesByte(props[0], out int lc, out int lp, out int pb))
                    {
                        throw BadProps(errorOffset, c, "five bytes (lc/lp/pb and the dictionary size)");
                    }

                    uint dict = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(props[1..]);
                    LzmaDecoder d;
                    try
                    {
                        d = LzmaDecoder.CreateRaw(new LzmaProperties(lc, lp, pb, Math.Max(dict, LzmaProperties.MinimumDictionarySize)), unpackSize, lzmaOptions);
                    }
                    catch (ArgumentException)
                    {
                        throw BadProps(errorOffset, c, "valid lc, lp, pb and dictionary size");
                    }

                    return new SevenZipDecoderStream(input, d.Decompress, d.ThrowIfIncomplete, errorOffset);
                }

            case SevenZipFormat.MethodLzma2:
                {
                    if (props.Length != 1 || props[0] > 40)
                    {
                        throw BadProps(errorOffset, c, "one byte of at most 40 (the dictionary size)");
                    }

                    var d = new Lzma2Decoder(props[0], lzmaOptions);
                    return new SevenZipDecoderStream(input, d.Decompress, d.ThrowIfIncomplete, errorOffset);
                }

            default:
                throw SevenZipFormat.UnsupportedMethod(c.MethodId);
        }
    }
}
