using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.Rar;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz target for the RAR 5 reader.
/// <list type="bullet">
/// <item><c>rar-read</c>: the input is an archive. It is read from a seekable stream and from a forward-only one in odd pieces; both
/// must list the same entries and give the same content, or both must throw the library's exceptions. A second pass repairs the
/// header CRC-32 values first, so mutated fields get past the checksum and reach the field parsers. Later volumes, when the archive asks
/// for them, are the input again.</item>
/// </list>
/// <c>fuzz seeds-rar DIR</c> writes seeds: the committed archives made by the RAR tool, and small ones assembled here.
/// </summary>
internal static class RarTargets
{
    private static readonly ArchiveLimits s_limits = new()
    {
        MaxTotalSize = 1 << 24,
        MaxEntryCount = 2000,
        MaxPathLength = 1024,
        MaxPathDepth = 32,
    };

    public static FuzzTarget Read { get; } = new("rar-read", static data => Run(data.ToArray()), static ex => ex is CompressionException);

    private sealed class Pieces(byte[] data) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, 1 + ((_at * 7) % 613)), data.Length - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void Run(byte[] data)
    {
        Check(data);
        byte[] repaired = (byte[])data.Clone();
        Resign(repaired);
        Check(repaired);
    }

    private static void Check(byte[] data)
    {
        string seekable = Summarize(data, forwardOnly: false);
        string forward = Summarize(data, forwardOnly: true);
        if (seekable != forward)
        {
            throw new InvalidOperationException("seekable and forward-only reads disagree:\n" + seekable + "\n---\n" + forward);
        }
    }

    // Lists every entry and hashes the content of the stored ones; the first error ends the summary with its type.
    private static string Summarize(byte[] data, bool forwardOnly)
    {
        var sb = new StringBuilder();
        try
        {
            Stream first = forwardOnly ? new Pieces(data) : new MemoryStream(data);
            var options = new RarReaderOptions
            {
                Limits = s_limits,
                Volumes = n => n < 4 ? (forwardOnly ? new Pieces(data) : new MemoryStream(data)) : null,
            };
            using var reader = new RarReader(first, options);
            byte[] buffer = new byte[8192];
            while (reader.GetNextEntry() is { } e)
            {
                sb.Append(e.Kind).Append(' ').Append(e.Name).Append(' ').Append(e.Size).Append(' ').Append(e.Method).Append(' ').Append(e.ModifiedTime?.Ticks).AppendLine();
                if (e.Kind != ArchiveEntryKind.File || !e.IsStored || e.IsEncrypted)
                {
                    continue;
                }

                using Stream s = reader.OpenEntryStream();
                long total = 0;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    total += n;
                }

                if (!e.SizeIsUnknown && total != e.Size)
                {
                    throw new InvalidOperationException($"entry '{e.Name}' declares {e.Size} bytes and gave {total}");
                }

                sb.Append("  ").AppendLine(Convert.ToHexString(sha.GetHashAndReset()));
            }
        }
        catch (CompressionException ex)
        {
            sb.Append(ex.GetType().Name);
        }

        return sb.ToString();
    }

    // Recomputes each header's CRC-32 along the block chain, as far as the chain can be followed.
    private static void Resign(byte[] a)
    {
        int p = 8;
        while (p + 5 <= a.Length)
        {
            int q = p + 4;
            int headerSize = 0;
            int sizeLength = 0;
            while (q < a.Length && sizeLength < 3)
            {
                byte b = a[q++];
                headerSize |= (b & 0x7F) << (7 * sizeLength++);
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            if (headerSize < 2 || q + headerSize > a.Length)
            {
                return;
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(p), Crc32.Compute(a.AsSpan(p + 4, sizeLength + headerSize)));
            int pos = q;
            ulong ReadVint()
            {
                ulong v = 0;
                for (int i = 0; i < 10 && pos < q + headerSize; i++)
                {
                    byte b = a[pos++];
                    v |= (ulong)(b & 0x7F) << (7 * i);
                    if ((b & 0x80) == 0)
                    {
                        break;
                    }
                }

                return v;
            }

            ReadVint();
            ulong flags = ReadVint();
            if ((flags & 1) != 0)
            {
                ReadVint();
            }

            ulong dataSize = (flags & 2) != 0 ? ReadVint() : 0;
            if (dataSize > (ulong)a.Length)
            {
                return;
            }

            p = q + headerSize + (int)dataSize;
        }
    }
}
