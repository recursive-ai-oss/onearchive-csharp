using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneArchive.Rar;

/// <summary>
/// BLAKE2sp, the eight-way parallel construction over BLAKE2s that RAR 5 uses for its file hashes.
/// BLAKE2s is RFC 7693; the tree layout is the one the BLAKE2 paper defines for BLAKE2sp (fanout 8, depth 2, 32-byte inner
/// and output length): leaf <c>i</c> hashes input blocks <c>i</c>, <c>i + 8</c>, <c>i + 16</c>, ... of 64 bytes, and the root hashes
/// the eight leaf digests.
/// </summary>
/// <remarks>
/// Pure integer arithmetic, so the digest is the same on every platform. The class is internal: RAR is its only user, and
/// keeping it out of the public surface leaves room to move it next to the checksums if another format needs it.
/// </remarks>
internal sealed class Blake2sp
{
    public const int DigestLength = 32;
    private const int Leaves = 8;
    private const int BlockLength = Blake2s.BlockLength;
    private const int Stripe = Leaves * BlockLength;

    private readonly Blake2s[] _leaves = new Blake2s[Leaves];
    private readonly byte[] _buffer = new byte[Stripe];
    private int _buffered;

    public Blake2sp()
    {
        for (int i = 0; i < Leaves; i++)
        {
            _leaves[i] = new Blake2s(nodeOffset: i, nodeDepth: 0, lastNode: i == Leaves - 1);
        }
    }

    /// <summary>Hashes <paramref name="data"/> in one call.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        var h = new Blake2sp();
        h.Update(data);
        byte[] digest = new byte[DigestLength];
        h.Final(digest);
        return digest;
    }

    /// <summary>Adds input.</summary>
    public void Update(ReadOnlySpan<byte> data)
    {
        if (_buffered > 0)
        {
            int take = Math.Min(Stripe - _buffered, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
            if (_buffered < Stripe)
            {
                return;
            }

            Feed(_buffer);
            _buffered = 0;
        }

        while (data.Length >= Stripe)
        {
            Feed(data[..Stripe]);
            data = data[Stripe..];
        }

        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    /// <summary>Finishes the hash and writes the 32-byte digest. The instance must not be used afterwards.</summary>
    public void Final(Span<byte> digest)
    {
        var root = new Blake2s(nodeOffset: 0, nodeDepth: 1, lastNode: true);
        Span<byte> leafDigest = stackalloc byte[DigestLength];
        for (int i = 0; i < Leaves; i++)
        {
            int start = Math.Min(i * BlockLength, _buffered);
            int end = Math.Min(start + BlockLength, _buffered);
            _leaves[i].Update(_buffer.AsSpan(start, end - start));
            _leaves[i].Final(leafDigest);
            root.Update(leafDigest);
        }

        root.Final(digest);
    }

    private void Feed(ReadOnlySpan<byte> stripe)
    {
        for (int i = 0; i < Leaves; i++)
        {
            _leaves[i].Update(stripe.Slice(i * BlockLength, BlockLength));
        }
    }
}

/// <summary>BLAKE2s (RFC 7693) with the tree parameters BLAKE2sp needs; unkeyed, 32-byte output.</summary>
internal sealed class Blake2s
{
    public const int BlockLength = 64;

    // RFC 7693 2.6: the initialization vector is the one of SHA-256.
    private static readonly uint[] s_iv =
    [
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    ];

    // RFC 7693 2.7: the message word schedule; rounds 10 and 11 of BLAKE2b reuse rows 0 and 1, and BLAKE2s has ten rounds.
    private static readonly byte[] s_sigma =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    ];

    private readonly uint[] _h = new uint[8];
    private readonly byte[] _buffer = new byte[BlockLength];
    private readonly bool _lastNode;
    private int _buffered;
    private ulong _counter;

    /// <summary>Creates a plain sequential BLAKE2s-256 state (RFC 7693 with no key; fanout 1, depth 1), which is what the RFC's test vectors use.</summary>
    public Blake2s()
    {
        Array.Copy(s_iv, _h, 8);
        _h[0] ^= 32u | (1u << 16) | (1u << 24);
    }

    /// <summary>Creates a BLAKE2s state for a node of the BLAKE2sp tree (fanout 8, depth 2, inner length 32).</summary>
    public Blake2s(int nodeOffset, int nodeDepth, bool lastNode)
    {
        Array.Copy(s_iv, _h, 8);

        // The parameter block (RFC 7693 2.5) is XORed into the first words of the state.
        // Word 0: digest length 32, key length 0, fanout 8, depth 2. Word 1: leaf length 0.
        // Word 2: node offset (low 32 bits). Word 3: node offset high 16 bits, node depth, inner length 32.
        _h[0] ^= 32u | (8u << 16) | (2u << 24);
        _h[2] ^= (uint)nodeOffset;
        _h[3] ^= ((uint)nodeDepth << 16) | (32u << 24);
        _lastNode = lastNode;
    }

    /// <summary>Adds input. The last block is kept back until <see cref="Final"/> because it is compressed with the final flag.</summary>
    public void Update(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_buffered == BlockLength)
            {
                _counter += BlockLength;
                Compress(_buffer, false);
                _buffered = 0;
            }

            int take = Math.Min(BlockLength - _buffered, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
        }
    }

    /// <summary>Finishes the hash and writes 32 bytes.</summary>
    public void Final(Span<byte> digest)
    {
        _counter += (uint)_buffered;
        _buffer.AsSpan(_buffered).Clear();
        Compress(_buffer, true);
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(digest[(i * 4)..], _h[i]);
        }
    }

    private void Compress(ReadOnlySpan<byte> block, bool last)
    {
        Span<uint> m = stackalloc uint[16];
        Span<uint> v = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
        {
            m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);
        }

        for (int i = 0; i < 8; i++)
        {
            v[i] = _h[i];
            v[i + 8] = s_iv[i];
        }

        v[12] ^= (uint)_counter;
        v[13] ^= (uint)(_counter >> 32);
        if (last)
        {
            v[14] = ~v[14];
            if (_lastNode)
            {
                v[15] = ~v[15];
            }
        }

        for (int r = 0; r < 10; r++)
        {
            int s = r * 16;
            G(v, 0, 4, 8, 12, m[s_sigma[s]], m[s_sigma[s + 1]]);
            G(v, 1, 5, 9, 13, m[s_sigma[s + 2]], m[s_sigma[s + 3]]);
            G(v, 2, 6, 10, 14, m[s_sigma[s + 4]], m[s_sigma[s + 5]]);
            G(v, 3, 7, 11, 15, m[s_sigma[s + 6]], m[s_sigma[s + 7]]);
            G(v, 0, 5, 10, 15, m[s_sigma[s + 8]], m[s_sigma[s + 9]]);
            G(v, 1, 6, 11, 12, m[s_sigma[s + 10]], m[s_sigma[s + 11]]);
            G(v, 2, 7, 8, 13, m[s_sigma[s + 12]], m[s_sigma[s + 13]]);
            G(v, 3, 4, 9, 14, m[s_sigma[s + 14]], m[s_sigma[s + 15]]);
        }

        for (int i = 0; i < 8; i++)
        {
            _h[i] ^= v[i] ^ v[i + 8];
        }
    }

    // RFC 7693 3.1: the mixing function G with rotation constants 16, 12, 8 and 7.
    private static void G(Span<uint> v, int a, int b, int c, int d, uint x, uint y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
        v[c] += v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
    }
}
