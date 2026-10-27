using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A streaming xxHash64 with seed 0, used for the frame content checksum (RFC 8878 §3.1.1: the low 32 bits of
/// XXH64 over the decoded content). This is the decoder's private seam for the checksum; it is replaced by the
/// shared checksum implementation once that exists.
/// </summary>
internal struct ContentChecksum
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    private ulong _v1;
    private ulong _v2;
    private ulong _v3;
    private ulong _v4;
    private ulong _total;
    private int _pending;
    private Buffer32 _buffer;

    [System.Runtime.CompilerServices.InlineArray(32)]
    private struct Buffer32
    {
        private byte _element;
    }

    public void Reset()
    {
        _v1 = unchecked(P1 + P2);
        _v2 = P2;
        _v3 = 0;
        _v4 = unchecked(0UL - P1);
        _total = 0;
        _pending = 0;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;
        Span<byte> buffer = _buffer;
        if (_pending > 0)
        {
            int take = Math.Min(32 - _pending, data.Length);
            data[..take].CopyTo(buffer[_pending..]);
            _pending += take;
            data = data[take..];
            if (_pending < 32)
            {
                return;
            }

            Stripe(buffer);
            _pending = 0;
        }

        while (data.Length >= 32)
        {
            Stripe(data);
            data = data[32..];
        }

        data.CopyTo(buffer);
        _pending = data.Length;
    }

    /// <summary>Returns the low 32 bits of the hash of everything passed to <see cref="Update"/>.</summary>
    public readonly uint Digest()
    {
        ulong h;
        if (_total >= 32)
        {
            h = BitOperations.RotateLeft(_v1, 1) + BitOperations.RotateLeft(_v2, 7) + BitOperations.RotateLeft(_v3, 12) + BitOperations.RotateLeft(_v4, 18);
            h = Merge(h, _v1);
            h = Merge(h, _v2);
            h = Merge(h, _v3);
            h = Merge(h, _v4);
        }
        else
        {
            h = P5;
        }

        h += _total;
        ReadOnlySpan<byte> rest = ((ReadOnlySpan<byte>)_buffer)[.._pending];
        while (rest.Length >= 8)
        {
            h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(rest));
            h = (BitOperations.RotateLeft(h, 27) * P1) + P4;
            rest = rest[8..];
        }

        if (rest.Length >= 4)
        {
            h ^= BinaryPrimitives.ReadUInt32LittleEndian(rest) * P1;
            h = (BitOperations.RotateLeft(h, 23) * P2) + P3;
            rest = rest[4..];
        }

        foreach (byte b in rest)
        {
            h ^= b * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
        }

        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return (uint)h;
    }

    private static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(acc + (input * P2), 31) * P1;

    private static ulong Merge(ulong h, ulong v) => ((h ^ Round(0, v)) * P1) + P4;

    private void Stripe(ReadOnlySpan<byte> s)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(s));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(s[8..]));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(s[16..]));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(s[24..]));
    }
}
