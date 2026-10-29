using System.Buffers.Binary;
using System.Text;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Builds small ISO 9660 images byte by byte, independently of the library's writer, so tests can produce the unusual and the
/// hostile ones: loops, continuation areas that point at themselves, extents past the end, lying lengths.
/// Layout: 16 zero sectors, the primary descriptor, the terminator, the root directory from sector 18, then the extra sectors.
/// </summary>
internal static class RawIso
{
    public const int S = 2048;

    public static void Both32(byte[] b, int at, long v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)v);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at + 4), (uint)v);
    }

    public static void Both16(byte[] b, int at, int v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)v);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at + 2), (ushort)v);
    }

    public static byte[] Rec(byte[] ident, long extent, long length, int flags, byte[]? sys = null)
    {
        int sysStart = 33 + ident.Length + ((ident.Length & 1) == 0 ? 1 : 0);
        int len = sysStart + (sys?.Length ?? 0);
        len += len & 1;
        byte[] r = new byte[len];
        r[0] = (byte)len;
        Both32(r, 2, extent);
        Both32(r, 10, length);
        r[18] = 123;
        r[19] = 11;
        r[20] = 14;
        Both16(r, 28, 1);
        r[25] = (byte)flags;
        r[32] = (byte)ident.Length;
        ident.CopyTo(r, 33);
        sys?.CopyTo(r, sysStart);
        return r;
    }

    public static byte[] Rec(string ident, long extent, long length, int flags, byte[]? sys = null) => Rec(Encoding.Latin1.GetBytes(ident), extent, length, flags, sys);

    public static byte[] SuspSp() => [(byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0];

    /// <summary>A System Use entry: signature, length, version 1 and the body.</summary>
    public static byte[] Su(string sig, params byte[] body)
    {
        byte[] e = new byte[4 + body.Length];
        e[0] = (byte)sig[0];
        e[1] = (byte)sig[1];
        e[2] = (byte)e.Length;
        e[3] = 1;
        body.CopyTo(e, 4);
        return e;
    }

    public static byte[] Px(uint mode, uint links = 1, uint uid = 0, uint gid = 0, uint serial = 0)
    {
        byte[] body = new byte[40];
        Both32(body, 0, mode);
        Both32(body, 8, links);
        Both32(body, 16, uid);
        Both32(body, 24, gid);
        Both32(body, 32, serial);
        return Su("PX", body);
    }

    public static byte[] Nm(string name, int flags = 0) => Su("NM", [(byte)flags, .. Encoding.UTF8.GetBytes(name)]);

    public static byte[] Ce(long block, long offset, long length)
    {
        byte[] body = new byte[24];
        Both32(body, 0, block);
        Both32(body, 8, offset);
        Both32(body, 16, length);
        return Su("CE", body);
    }

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];

    /// <summary>Lays records out in sectors (none crosses a boundary) and pads to whole sectors.</summary>
    public static byte[] Dir(params byte[][] records)
    {
        var bytes = new List<byte>();
        foreach (byte[] r in records)
        {
            int used = bytes.Count % S;
            if (used + r.Length > S)
            {
                bytes.AddRange(new byte[S - used]);
            }

            bytes.AddRange(r);
        }

        bytes.AddRange(new byte[(S - (bytes.Count % S)) % S]);
        if (bytes.Count == 0)
        {
            bytes.AddRange(new byte[S]);
        }

        return [.. bytes];
    }

    /// <summary>A directory whose first two records are "." and ".." for the directory at <paramref name="extent"/>.</summary>
    public static byte[] SubDir(long extent, long parent, params byte[][] children)
    {
        // The length is patched below: one sector unless the children need more.
        byte[][] all = [Rec([0], extent, S, 2), Rec([1], parent, S, 2), .. children];
        return Dir(all);
    }

    /// <summary>The image: descriptors, a root directory made of <paramref name="rootChildren"/> and the extra sectors from sector 19 on.</summary>
    public static byte[] Image(byte[][] rootChildren, bool suspInRoot = true, params byte[][] extra)
    {
        byte[] dot = Rec([0], 18, S, 2, suspInRoot ? Concat(SuspSp(), Px(0x41ED, 2)) : null);
        byte[] root = Dir([dot, Rec([1], 18, S, 2), .. rootChildren]);
        int sectors = 18 + (root.Length / S) + extra.Sum(static e => (e.Length + S - 1) / S);
        byte[] image = new byte[sectors * S];
        int pvd = 16 * S;
        image[pvd] = 1;
        "CD001"u8.CopyTo(image.AsSpan(pvd + 1));
        image[pvd + 6] = 1;
        Encoding.ASCII.GetBytes("RAWTEST").CopyTo(image, pvd + 40);
        Both32(image, pvd + 80, sectors);
        Both16(image, pvd + 120, 1);
        Both16(image, pvd + 124, 1);
        Both16(image, pvd + 128, S);
        byte[] rootRecord = Rec([0], 18, root.Length, 2);
        rootRecord.CopyTo(image, pvd + 156);
        image[pvd + 156] = 34;
        int term = 17 * S;
        image[term] = 255;
        "CD001"u8.CopyTo(image.AsSpan(term + 1));
        image[term + 6] = 1;
        root.CopyTo(image, 18 * S);
        int at = 18 * S + root.Length;
        foreach (byte[] e in extra)
        {
            e.CopyTo(image, at);
            at += (e.Length + S - 1) / S * S;
        }

        return image;
    }

    public static byte[] Sector(byte[] content)
    {
        byte[] s = new byte[S];
        content.CopyTo(s, 0);
        return s;
    }
}

/// <summary>A read-only stream that has real bytes at its start and zeros after them, up to a declared length (a huge image without the disk space).</summary>
internal sealed class SparseTailStream(byte[] head, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = (int)Math.Min(buffer.Length, Math.Max(0, length - _position));
        for (int i = 0; i < n; i++)
        {
            long p = _position + i;
            buffer[i] = p < head.Length ? head[p] : (byte)0;
        }

        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
