#pragma warning disable CS1591 // Test helper shared with the hash-matrix host; not part of the library API.

using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Tests.Lzx;

namespace RecursiveAI.OneArchive.Tests.Cab;

/// <summary>Writes cabinets for tests: any mix of stored, MSZIP and LZX folders, reserve areas and spanning flags, built from [MS-CAB].</summary>
public sealed class CabTestBuilder
{
    public enum Method
    {
        None,
        Mszip,
        MszipNoHistory,
        Lzx,
    }

    public sealed class FileSpec
    {
        public required string Name { get; init; }

        public required byte[] Data { get; init; }

        public ushort Attributes { get; init; } = 0x20;

        public ushort Date { get; init; } = DosDate(2024, 3, 9);

        public ushort Time { get; init; } = DosTime(13, 45, 20);

        public ushort? RawFolderIndex { get; init; }

        public bool Utf8Name { get; init; }

        public byte[]? RawName { get; init; }
    }

    public sealed class FolderSpec
    {
        public Method Method { get; init; } = Method.Mszip;

        public int WindowBits { get; init; } = 16;

        public int BlockSize { get; init; } = 32768;

        public List<FileSpec> Files { get; } = [];

        public byte[]? Reserve { get; init; }

        /// <summary>Replaces the folder's typeCompress value.</summary>
        public ushort? RawType { get; init; }

        public LzxTestEncoder.Settings? Lzx { get; init; }
    }

    public List<FolderSpec> Folders { get; } = [];

    public ushort SetId { get; set; } = 0x1234;

    public ushort CabinetIndex { get; set; }

    public string? PreviousCabinet { get; set; }

    public string? PreviousDisk { get; set; }

    public string? NextCabinet { get; set; }

    public string? NextDisk { get; set; }

    public byte[]? HeaderReserve { get; set; }

    public int FolderReserveSize { get; set; }

    public int DataReserveSize { get; set; }

    public bool ReserveFlag { get; set; }

    public bool OmitChecksums { get; set; }

    /// <summary>1 (the default): the data is summed first, then cbData and cbUncomp only. 0: the header area also covers the data reserve, which reference tools reject.</summary>
    public int ChecksumVariant { get; set; } = 1;

    public byte[]? DataReserveFill { get; set; }

    public static ushort DosDate(int year, int month, int day) => (ushort)(((year - 1980) << 9) | (month << 5) | day);

    public static ushort DosTime(int hour, int minute, int second) => (ushort)((hour << 11) | (minute << 5) | (second / 2));

    /// <summary>The cabinet checksum of [MS-CAB] 3.1, continuing from <paramref name="seed"/>.</summary>
    public static uint Checksum(ReadOnlySpan<byte> data, uint seed = 0)
    {
        uint c = seed;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            c ^= BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
        }

        uint rest = 0;
        int left = data.Length - i;
        if (left == 3)
        {
            rest = (uint)(data[i] << 16 | data[i + 1] << 8 | data[i + 2]);
        }
        else if (left == 2)
        {
            rest = (uint)(data[i] << 8 | data[i + 1]);
        }
        else if (left == 1)
        {
            rest = data[i];
        }

        return c ^ rest;
    }

    public byte[] Build()
    {
        var blocks = new List<List<(byte[] Compressed, int Uncompressed)>>();
        foreach (FolderSpec f in Folders)
        {
            blocks.Add(EncodeFolder(f));
        }

        var header = new List<byte>();
        bool reserve = ReserveFlag || HeaderReserve is not null || FolderReserveSize > 0 || DataReserveSize > 0;
        ushort flags = 0;
        if (PreviousCabinet is not null)
        {
            flags |= 1;
        }

        if (NextCabinet is not null)
        {
            flags |= 2;
        }

        if (reserve)
        {
            flags |= 4;
        }

        int fileCount = Folders.Sum(f => f.Files.Count);
        var optional = new List<byte>();
        if (reserve)
        {
            optional.AddRange(BitConverter.GetBytes((ushort)(HeaderReserve?.Length ?? 0)));
            optional.Add((byte)FolderReserveSize);
            optional.Add((byte)DataReserveSize);
            if (HeaderReserve is not null)
            {
                optional.AddRange(HeaderReserve);
            }
        }

        AddZ(optional, PreviousCabinet, flags & 1);
        AddZ(optional, PreviousDisk, flags & 1);
        AddZ(optional, NextCabinet, flags & 2);
        AddZ(optional, NextDisk, flags & 2);

        int headerLength = 36 + optional.Count;
        int foldersLength = Folders.Count * (8 + FolderReserveSize);
        var fileTable = new List<byte>();
        for (int fi = 0; fi < Folders.Count; fi++)
        {
            int offset = 0;
            foreach (FileSpec file in Folders[fi].Files)
            {
                fileTable.AddRange(BitConverter.GetBytes((uint)file.Data.Length));
                fileTable.AddRange(BitConverter.GetBytes((uint)offset));
                fileTable.AddRange(BitConverter.GetBytes(file.RawFolderIndex ?? (ushort)fi));
                fileTable.AddRange(BitConverter.GetBytes(file.Date));
                fileTable.AddRange(BitConverter.GetBytes(file.Time));
                ushort attrs = (ushort)(file.Attributes | (file.Utf8Name ? 0x80 : 0));
                fileTable.AddRange(BitConverter.GetBytes(attrs));
                fileTable.AddRange(file.RawName ?? Encoding.UTF8.GetBytes(file.Name));
                fileTable.Add(0);
                offset += file.Data.Length;
            }
        }

        int filesOffset = headerLength + foldersLength;
        int dataStart = filesOffset + fileTable.Count;
        var data = new List<byte>();
        var folderTable = new List<byte>();
        for (int fi = 0; fi < Folders.Count; fi++)
        {
            folderTable.AddRange(BitConverter.GetBytes((uint)(dataStart + data.Count)));
            folderTable.AddRange(BitConverter.GetBytes((ushort)blocks[fi].Count));
            ushort type = Folders[fi].Method switch
            {
                Method.None => 0,
                Method.Mszip or Method.MszipNoHistory => 1,
                _ => (ushort)(3 | (Folders[fi].WindowBits << 8)),
            };
            folderTable.AddRange(BitConverter.GetBytes(Folders[fi].RawType ?? type));
            for (int i = 0; i < FolderReserveSize; i++)
            {
                folderTable.Add(Folders[fi].Reserve is { } r && i < r.Length ? r[i] : (byte)0);
            }

            foreach ((byte[] comp, int unc) in blocks[fi])
            {
                var body = new List<byte>();
                body.AddRange(BitConverter.GetBytes((ushort)comp.Length));
                body.AddRange(BitConverter.GetBytes((ushort)unc));
                for (int i = 0; i < DataReserveSize; i++)
                {
                    body.Add(DataReserveFill is { } d && i < d.Length ? d[i] : (byte)0);
                }

                // [MS-CAB] 3.1: the data is summed first, then the header area from cbData on.
                byte[] bodyBytes = body.ToArray();
                uint csum = OmitChecksums ? 0
                    : ChecksumVariant == 1 ? Checksum(bodyBytes.AsSpan(0, 4), Checksum(comp))
                    : Checksum(bodyBytes, Checksum(comp));
                body.AddRange(comp);
                data.AddRange(BitConverter.GetBytes(csum));
                data.AddRange(body);
            }
        }

        int total = dataStart + data.Count;
        header.AddRange("MSCF"u8.ToArray());
        header.AddRange(new byte[4]);
        header.AddRange(BitConverter.GetBytes((uint)total));
        header.AddRange(new byte[4]);
        header.AddRange(BitConverter.GetBytes((uint)filesOffset));
        header.AddRange(new byte[4]);
        header.Add(3);
        header.Add(1);
        header.AddRange(BitConverter.GetBytes((ushort)Folders.Count));
        header.AddRange(BitConverter.GetBytes((ushort)fileCount));
        header.AddRange(BitConverter.GetBytes(flags));
        header.AddRange(BitConverter.GetBytes(SetId));
        header.AddRange(BitConverter.GetBytes(CabinetIndex));
        header.AddRange(optional);
        header.AddRange(folderTable);
        header.AddRange(fileTable);
        header.AddRange(data);
        return [.. header];
    }

    private static void AddZ(List<byte> list, string? text, int present)
    {
        if (present != 0)
        {
            list.AddRange(Encoding.ASCII.GetBytes(text ?? string.Empty));
            list.Add(0);
        }
    }

    private static List<(byte[] Compressed, int Uncompressed)> EncodeFolder(FolderSpec f)
    {
        byte[] all = [.. f.Files.SelectMany(x => x.Data)];
        var result = new List<(byte[], int)>();
        switch (f.Method)
        {
            case Method.None:
                for (int p = 0; p < all.Length; p += f.BlockSize)
                {
                    int n = Math.Min(f.BlockSize, all.Length - p);
                    result.Add((all.AsSpan(p, n).ToArray(), n));
                }

                break;
            case Method.Mszip:
            case Method.MszipNoHistory:
                for (int p = 0; p < all.Length; p += f.BlockSize)
                {
                    int n = Math.Min(f.BlockSize, all.Length - p);
                    ReadOnlyMemory<byte> history = f.Method == Method.Mszip ? all.AsMemory(Math.Max(0, p - 32768), Math.Min(p, 32768)) : default;
                    byte[] raw = Deflate.Compress(all.AsSpan(p, n), new DeflateOptions { Dictionary = history });
                    result.Add(([(byte)'C', (byte)'K', .. raw], n));
                }

                break;
            default:
                LzxTestEncoder.Settings settings = f.Lzx ?? new LzxTestEncoder.Settings { WindowBits = f.WindowBits };
                List<byte[]> frames = LzxTestEncoder.Encode(all, settings);
                for (int i = 0; i < frames.Count; i++)
                {
                    result.Add((frames[i], Math.Min(32768, all.Length - (i * 32768))));
                }

                break;
        }

        return result;
    }
}
