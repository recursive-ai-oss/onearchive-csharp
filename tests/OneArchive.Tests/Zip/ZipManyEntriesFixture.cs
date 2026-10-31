using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Writes a zip with a very large number of tiny stored entries straight to a file, one record at a time, so that a test
/// can make a million-entry archive (the size of a mail export) without holding it in memory. Independent of the library's writer.
/// </summary>
internal static class ZipManyEntriesFixture
{
    /// <summary>The name of entry <paramref name="i"/>.</summary>
    public static string NameOf(int i) => "Messages/" + (i / 1000).ToString("D5", CultureInfo.InvariantCulture) + "/message_" + i.ToString("D8", CultureInfo.InvariantCulture) + ".eml";

    /// <summary>The content of entry <paramref name="i"/>: a short ASCII text, empty for every tenth entry.</summary>
    public static byte[] ContentOf(int i) => i % 10 == 0 ? [] : Encoding.ASCII.GetBytes("body of message " + i.ToString(CultureInfo.InvariantCulture));

    /// <summary>Writes <paramref name="count"/> entries to <paramref name="path"/>: stored, with an extended timestamp field, zip64 end records when the count needs them.</summary>
    public static void Write(string path, int count)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        long position = 0;
        var offsets = new long[count];
        var scratch = new byte[30 + 1024 + 64];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = position;
            byte[] name = Encoding.ASCII.GetBytes(NameOf(i));
            byte[] data = ContentOf(i);
            uint crc = RecursiveAI.OneCompress.Checksums.Crc32.Compute(data);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(4), 10);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(6), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(10), 0x6000);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(12), 0x5A21);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(14), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(18), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(22), (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(26), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(28), 0);
            name.CopyTo(scratch, 30);
            data.CopyTo(scratch, 30 + name.Length);
            int n = 30 + name.Length + data.Length;
            file.Write(scratch, 0, n);
            position += n;
        }

        long dirStart = position;
        for (int i = 0; i < count; i++)
        {
            byte[] name = Encoding.ASCII.GetBytes(NameOf(i));
            byte[] data = ContentOf(i);
            uint crc = RecursiveAI.OneCompress.Checksums.Crc32.Compute(data);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(4), 0x031E);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(6), 10);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(10), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(12), 0x6000);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(14), 0x5A21);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(16), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(20), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(24), (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(28), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(30), 9);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(32), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(34), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(36), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(38), 0x81A4_0000u);
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(42), (uint)offsets[i]);
            name.CopyTo(scratch, 46);
            // APPNOTE 4.5.7 extended timestamp: modification time only.
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(46 + name.Length), 0x5455);
            BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(48 + name.Length), 5);
            scratch[50 + name.Length] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(51 + name.Length), 1_700_000_000u + (uint)i);
            int n = 46 + name.Length + 9;
            file.Write(scratch, 0, n);
            position += n;
        }

        long dirSize = position - dirStart;
        if (count >= 0xFFFF)
        {
            // APPNOTE 4.3.14 and 4.3.15: the zip64 end record and its locator.
            var z = new byte[56 + 20];
            BinaryPrimitives.WriteUInt32LittleEndian(z, 0x06064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(4), 44);
            BinaryPrimitives.WriteUInt16LittleEndian(z.AsSpan(12), 45);
            BinaryPrimitives.WriteUInt16LittleEndian(z.AsSpan(14), 45);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(24), (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(32), (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(40), (ulong)dirSize);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(48), (ulong)dirStart);
            BinaryPrimitives.WriteUInt32LittleEndian(z.AsSpan(56), 0x07064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(z.AsSpan(64), (ulong)position);
            BinaryPrimitives.WriteUInt32LittleEndian(z.AsSpan(72), 1);
            file.Write(z);
        }

        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        ushort shown = count >= 0xFFFF ? (ushort)0xFFFF : (ushort)count;
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), shown);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), shown);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), count >= 0xFFFF ? 0xFFFFFFFFu : (uint)dirSize);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), count >= 0xFFFF ? 0xFFFFFFFFu : (uint)dirStart);
        file.Write(end);
    }
}
