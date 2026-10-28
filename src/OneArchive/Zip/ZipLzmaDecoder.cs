using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneArchive.Zip;

/// <summary>
/// Decodes a zip entry stored with method 14: APPNOTE 5.8 puts a 4-byte header (LZMA SDK version major and minor, then the
/// size of the properties, 5) and the 5 LZMA property bytes in front of the LZMA data. The uncompressed size comes from the
/// zip headers; when they do not give it (a streamed entry), the data must end with an end marker.
/// </summary>
internal sealed class ZipLzmaDecoder(long uncompressedSize, long dataOffset, LzmaDecompressionOptions options)
{
    private readonly byte[] _header = new byte[9];
    private int _have;
    private LzmaDecoder? _decoder;

    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        consumed = 0;
        written = 0;
        if (_decoder is null)
        {
            int take = Math.Min(_header.Length - _have, source.Length);
            source[..take].CopyTo(_header.AsSpan(_have));
            _have += take;
            consumed = take;
            if (_have < _header.Length)
            {
                return OperationStatus.NeedMoreData;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(2)) != 5)
            {
                throw ZipFormat.Bad(dataOffset + 2, "an LZMA properties size of 5 (APPNOTE 5.8.9)");
            }

            if (!LzmaProperties.TryParsePropertiesByte(_header[4], out int lc, out int lp, out int pb))
            {
                throw ZipFormat.Bad(dataOffset + 4, "a valid LZMA properties byte (lc, lp, pb)");
            }

            uint dictionary = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(5));
            _decoder = LzmaDecoder.CreateRaw(new LzmaProperties(lc, lp, pb, Math.Max(dictionary, LzmaProperties.MinimumDictionarySize)), uncompressedSize, options);
            source = source[take..];
        }

        OperationStatus status = _decoder.Decompress(source, destination, out int c, out written);
        consumed += c;
        return status;
    }
}
