namespace RecursiveAI.OneCompress.Deflate;

public sealed partial class DeflateDecoder
{
    // The fixed Huffman codes of RFC 1951 section 3.2.6, built once and shared (they are never modified).
    private static class FixedTables
    {
        public static readonly DeflateHuffmanTable Literal = Build(DeflateAlphabet.LiteralLength);
        public static readonly DeflateHuffmanTable Distance = Build(DeflateAlphabet.Distance);

        private static DeflateHuffmanTable Build(DeflateAlphabet alphabet)
        {
            var table = new DeflateHuffmanTable(alphabet);
            Span<byte> lengths = stackalloc byte[DeflateTables.LiteralLengthSymbolCount];
            if (alphabet == DeflateAlphabet.LiteralLength)
            {
                DeflateTables.GetFixedLiteralLengthCodeLengths(lengths);
                table.TryBuild(lengths, out _);
            }
            else
            {
                DeflateTables.GetFixedDistanceCodeLengths(lengths);
                table.TryBuild(lengths[..DeflateTables.DistanceSymbolCount], out _);
            }

            return table;
        }
    }

    private void BuildDynamicTables(in DeflateBitReader br)
    {
        byte[] lengths = _lengths!;
        if (lengths[DeflateTables.EndOfBlockSymbol] == 0)
        {
            throw Bad(br, "a code for the end-of-block symbol (256) in the literal/length code");
        }

        _dynamicLiteralTable ??= new DeflateHuffmanTable(DeflateAlphabet.LiteralLength);
        _dynamicDistanceTable ??= new DeflateHuffmanTable(DeflateAlphabet.Distance);
        if (!_dynamicLiteralTable.TryBuild(lengths.AsSpan(0, _hlit), out string? problem))
        {
            throw Bad(br, "the literal/length code to be " + problem);
        }

        if (!_dynamicDistanceTable.TryBuild(lengths.AsSpan(_hlit, _hdist), out problem))
        {
            throw Bad(br, "the distance code to be " + problem);
        }

        _literalTable = _dynamicLiteralTable;
        _distanceTable = _dynamicDistanceTable;
    }

    // Reads a zero-terminated gzip header field (FNAME, FCOMMENT) from the source in bulk. The header bytes before it were
    // read through the bit buffer a whole byte at a time, so the buffer is empty here.
    private bool ReadZeroTerminated(ref DeflateBitReader br, ref byte[]? buffer, ref int length)
    {
        ReadOnlySpan<byte> rest = br.Source[br.Position..];
        int terminator = rest.IndexOf((byte)0);
        int n = terminator < 0 ? rest.Length : terminator;
        if ((long)length + n > _maxField)
        {
            throw new CompressionLimitException(_name, nameof(DeflateDecompressionOptions.MaxHeaderFieldSize), _maxField, (long)length + n);
        }

        if (buffer is null || buffer.Length < length + n)
        {
            byte[] bigger = new byte[Math.Max(length + n, Math.Max(16, (buffer?.Length ?? 0) * 2))];
            buffer?.AsSpan(0, length).CopyTo(bigger);
            buffer = bigger;
        }

        rest[..n].CopyTo(buffer.AsSpan(length));
        length += n;
        int taken = terminator < 0 ? n : n + 1;
        if ((_gzipFlags & 2) != 0)
        {
            _headerCrc = Checksums.Crc32.Update(_headerCrc, rest[..taken]);
        }

        br.Set(br.Position + taken, br.BitBuffer, br.BitCount);
        return terminator >= 0;
    }

    // Copies n bytes of a match of the given distance to dst[dp..]. History before this call's output comes from the window.
    private void CopyMatch(Span<byte> dst, int dp, int distance, int n)
    {
        if (n == 0)
        {
            return;
        }

        if (distance <= dp)
        {
            int s = dp - distance;
            if (distance >= n)
            {
                dst.Slice(s, n).CopyTo(dst[dp..]);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    dst[dp + i] = dst[s + i];
                }
            }

            return;
        }

        // RFC 1951 section 3.2.5: the match may begin before this call's output, in the window, and run on into it.
        int fromWindow = distance - dp;
        int m = Math.Min(n, fromWindow);
        _window.AsSpan(_windowLen - fromWindow, m).CopyTo(dst[dp..]);
        for (int i = m; i < n; i++)
        {
            dst[dp + i] = dst[i - fromWindow];
        }
    }
}
