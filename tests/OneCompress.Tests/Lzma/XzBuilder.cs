using System.Security.Cryptography;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Builds small xz streams by hand (stored LZMA2 chunks), so tests can damage one field at a time.</summary>
internal sealed class XzBuilder
{
    public int Check = 1;
    public byte[] Data = "hello, xz"u8.ToArray();
    public byte StreamFlags1Override = 0xFF;
    public ulong? DeclaredCompressed;
    public ulong? DeclaredUncompressed;
    public byte[] FilterFlags = [0x21, 0x01, 0x16];
    public int FilterCount = 1;
    public int PadByte;
    public byte[]? CheckOverride;
    public long? RecordCountOverride;
    public ulong? RecordUnpaddedOverride;
    public ulong? RecordUncompressedOverride;
    public int IndexPadByte;
    public bool BreakIndexCrc;
    public bool BreakFooterCrc;
    public int BackwardSizeDelta;
    public byte FooterFlagsOverride = 0xFF;
    public bool BreakHeaderCrc;
    public byte BlockFlagsExtra;
    public byte[] StreamPadding = [];
    public int Streams = 1;
    public byte[] FooterMagic = "YZ"u8.ToArray();

    public static byte[] Varint(ulong v)
    {
        var o = new List<byte>();
        while (v >= 0x80)
        {
            o.Add((byte)(v | 0x80));
            v >>= 7;
        }

        o.Add((byte)v);
        return [.. o];
    }

    public static byte[] StoredPayload(byte[] data)
    {
        var o = new List<byte> { 1, (byte)((data.Length - 1) >> 8), (byte)(data.Length - 1) };
        o.AddRange(data);
        o.Add(0);
        return [.. o];
    }

    private static int CheckSize(int id) => id switch { 0 => 0, <= 3 => 4, <= 6 => 8, <= 9 => 16, <= 12 => 32, _ => 64 };

    private byte[] ComputeCheck() => CheckOverride ?? Check switch
    {
        0 => [],
        1 => BitConverter.GetBytes(Crc32.Compute(Data)),
        4 => BitConverter.GetBytes(Crc64.Compute(Data)),
        10 => SHA256.HashData(Data),
        _ => new byte[CheckSize(Check)],
    };

    public byte[] Build()
    {
        var all = new List<byte>();
        for (int s = 0; s < Streams; s++)
        {
            if (s > 0)
            {
                all.AddRange(StreamPadding);
            }

            all.AddRange(BuildOne());
        }

        return [.. all];
    }

    private byte[] BuildOne()
    {
        var o = new List<byte>();
        byte[] flags = [0, (byte)Check];
        o.AddRange(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 });
        o.AddRange(flags);
        o.AddRange(BitConverter.GetBytes(Crc32.Compute(flags)));

        // Block header.
        var body = new List<byte> { 0, (byte)((FilterCount - 1) | BlockFlagsExtra | (DeclaredCompressed.HasValue ? 0x40 : 0) | (DeclaredUncompressed.HasValue ? 0x80 : 0)) };
        if (DeclaredCompressed.HasValue)
        {
            body.AddRange(Varint(DeclaredCompressed.Value));
        }

        if (DeclaredUncompressed.HasValue)
        {
            body.AddRange(Varint(DeclaredUncompressed.Value));
        }

        body.AddRange(FilterFlags);
        int size = ((body.Count + 4 + 3) / 4) * 4;
        while (body.Count < size - 4)
        {
            body.Add(0);
        }

        body[0] = (byte)(size / 4 - 1);
        byte[] header = [.. body];
        uint crc = Crc32.Compute(header);
        if (BreakHeaderCrc)
        {
            crc ^= 1;
        }

        o.AddRange(header);
        o.AddRange(BitConverter.GetBytes(crc));

        byte[] payload = StoredPayload(Data);
        o.AddRange(payload);
        for (int i = 0; i < ((4 - (payload.Length & 3)) & 3); i++)
        {
            o.Add((byte)PadByte);
        }

        o.AddRange(ComputeCheck());
        long unpadded = size + payload.Length + CheckSize(Check);

        // Index.
        var index = new List<byte> { 0 };
        index.AddRange(Varint((ulong)(RecordCountOverride ?? 1)));
        index.AddRange(Varint(RecordUnpaddedOverride ?? (ulong)unpadded));
        index.AddRange(Varint(RecordUncompressedOverride ?? (ulong)Data.Length));
        while ((index.Count & 3) != 0)
        {
            index.Add((byte)IndexPadByte);
        }

        uint icrc = Crc32.Compute([.. index]);
        if (BreakIndexCrc)
        {
            icrc ^= 1;
        }

        index.AddRange(BitConverter.GetBytes(icrc));
        o.AddRange(index);

        // Footer.
        var f = new List<byte>();
        f.AddRange(BitConverter.GetBytes((uint)(index.Count / 4 - 1 + BackwardSizeDelta)));
        f.Add(0);
        f.Add(FooterFlagsOverride == 0xFF ? (byte)Check : FooterFlagsOverride);
        uint fcrc = Crc32.Compute([.. f]);
        if (BreakFooterCrc)
        {
            fcrc ^= 1;
        }

        o.AddRange(BitConverter.GetBytes(fcrc));
        o.AddRange(f);
        o.AddRange(FooterMagic);
        return [.. o];
    }
}
