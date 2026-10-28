using System.Buffers.Binary;
using System.Security.Cryptography;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for 7z.
/// <list type="bullet">
/// <item><c>sevenzip-read</c>: the 7z reader over the input with the two header CRCs repaired first (a mutated header would
/// otherwise always stop at its checksum), reading every entry in order and then again by random access, last to first; each
/// must end normally or in one of the library's exceptions, every entry read must have the length it declares, and the
/// sequential and random-access reads of one entry must agree.</item>
/// <item><c>sevenzip-read-encrypted</c>: the same with a password and the key-derivation cost capped low, so that AES properties,
/// encrypted headers and wrong-password paths are parsed and mutated at speed.</item>
/// </list>
/// </summary>
internal static class SevenZipTargets
{
    private static readonly ArchiveLimits s_limits = new()
    {
        MaxTotalSize = 1 << 24,
        MaxEntryCount = 2000,
        MaxPathLength = 1024,
        MaxPathDepth = 32,
        MaxExpansionRatio = 200,
        MaxWindowSize = 1 << 22,
    };

    public static FuzzTarget Read { get; } = new("sevenzip-read", static data => CheckRead(data.ToArray(), null), static ex => ex is CompressionException);

    public static FuzzTarget ReadEncrypted { get; } = new("sevenzip-read-encrypted", static data => CheckRead(data.ToArray(), Password), static ex => ex is CompressionException);

    // The seeds are made with this password (see the seeds-encrypted command).
    public const string Password = "fuzz-password";

    // 7zFormat.txt "SignatureHeader": bytes 12..31 are the start header, covered by the CRC at 8; the next header's CRC is at 28.
    private static void RepairCrcs(byte[] data)
    {
        if (data.Length < 32)
        {
            return;
        }

        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(12));
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(20));
        if (offset <= (ulong)data.Length && size <= (ulong)data.Length && 32 + offset + size <= (ulong)data.Length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), Crc32.Compute(data.AsSpan((int)(32 + offset), (int)size)));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), Crc32.Compute(data.AsSpan(12, 20)));
    }

    private static string HashOf(Stream s, long expected, string name)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[8192];
        long total = 0;
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            total += n;
        }

        if (expected >= 0 && total != expected)
        {
            throw new InvalidOperationException($"entry '{name}' read {total} bytes but declares {expected}");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void CheckRead(byte[] data, string? password)
    {
        RepairCrcs(data);
        try
        {
            using var reader = new SevenZipReader(new MemoryStream(data), new SevenZipReaderOptions { Limits = s_limits, MaxHeaderSize = 1 << 20, Password = password, MaxKeyDerivationPower = 8 });
            var seen = new List<(SevenZipEntry Entry, string Hash)>();
            while (reader.GetNextEntry() is { } e)
            {
                if (seen.Count >= 40)
                {
                    break;
                }

                using Stream s = reader.OpenEntryStream();
                seen.Add((e, HashOf(s, e.Kind == ArchiveEntryKind.File ? e.Size : -1, e.Name)));
            }

            for (int i = seen.Count - 1; i >= 0; i--)
            {
                using Stream s = reader.OpenEntryStream(seen[i].Entry);
                if (HashOf(s, -1, seen[i].Entry.Name) != seen[i].Hash)
                {
                    throw new InvalidOperationException("random access and sequential reads of an entry disagree");
                }
            }
        }
        catch (CompressionException)
        {
        }
    }
}
