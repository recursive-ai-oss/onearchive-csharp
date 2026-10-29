using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Reads the bit-packed fields of a brotli stream (RFC 7932 section 1.5.1): bits fill each byte from the least significant end and
/// integers take their bits in that order.
/// </summary>
/// <remarks>
/// <para>Reading past the end of the input does not fail: the missing bits read as zeros and the reader notes it
/// (<see cref="Overrun"/>). The decoder reads a whole unit of the format, checks <see cref="Overrun"/> and, when the input ran out,
/// puts the reader back to a <see cref="Checkpoint"/> and waits for more input. No decision about a unit's value may be acted on
/// before that check.</para>
/// <para>The accumulator holds up to 64 bits. <see cref="Refill"/> leaves at least 56 of them, so a caller may take up to 56 bits
/// between refills.</para>
/// </remarks>
internal ref struct BrotliBitReader
{
    private readonly ReadOnlySpan<byte> _src;

    // The next byte of _src to load into the accumulator.
    private int _pos;

    // Unconsumed bits, lowest first. Bits above _count may hold copies of the next input bytes (the fast refill loads whole
    // words); they are consistent with what a later refill ORs in, so they do no harm.
    private ulong _buf;

    // The number of bits in the accumulator, including padding bits.
    private int _count;

    // The number of zero bits appended in place of input that does not exist. They are the highest bits of the accumulator, so
    // some of them have been consumed exactly when _count has fallen below _pad.
    private int _pad;

    /// <summary>A saved reader position.</summary>
    internal readonly struct Checkpoint
    {
        internal Checkpoint(int pos, ulong buf, int count, int pad)
        {
            Pos = pos;
            Buf = buf;
            Count = count;
            Pad = pad;
        }

        internal int Pos { get; }

        internal ulong Buf { get; }

        internal int Count { get; }

        internal int Pad { get; }
    }

    /// <summary>Starts reading at the first byte of <paramref name="source"/>, skipping <paramref name="bitOffset"/> bits of it.</summary>
    internal BrotliBitReader(ReadOnlySpan<byte> source, int bitOffset)
    {
        _src = source;
        _pos = 0;
        _buf = 0;
        _count = 0;
        _pad = 0;
        if (bitOffset > 0)
        {
            Refill();
            _buf >>= bitOffset;
            _count -= bitOffset;
        }
    }

    /// <summary>The input being read.</summary>
    internal readonly ReadOnlySpan<byte> Source => _src;

    /// <summary>True when bits that are not in the input have been consumed.</summary>
    internal readonly bool Overrun => _count < _pad;

    /// <summary>The position of the next unread bit, counted in bits from the start of the input. Meaningful unless <see cref="Overrun"/>.</summary>
    internal readonly long BitPosition => ((long)_pos * 8) - (_count - _pad);

    /// <summary>The index of the byte that holds the next unread bit.</summary>
    internal readonly int BytePosition => (int)(BitPosition >> 3);

    /// <summary>The number of already consumed bits of that byte.</summary>
    internal readonly int BitOffset => (int)(BitPosition & 7);

    /// <summary>True when the reader has never padded past the end of the input, so its state can be carried in plain locals.</summary>
    internal readonly bool CanDetach => _pad == 0;

    /// <summary>The index of the next input byte to load into the accumulator.</summary>
    internal readonly int RawPosition => _pos;

    /// <summary>The accumulator.</summary>
    internal readonly ulong RawBuffer => _buf;

    /// <summary>The number of bits in the accumulator.</summary>
    internal readonly int RawCount => _count;

    /// <summary>Sets the state of a reader that is not padded, from locals that the fast command loop kept.</summary>
    internal void Attach(int pos, ulong buf, int count)
    {
        _pos = pos;
        _buf = buf;
        _count = count;
    }

    internal readonly Checkpoint Save() => new(_pos, _buf, _count, _pad);

    internal void Restore(Checkpoint c)
    {
        _pos = c.Pos;
        _buf = c.Buf;
        _count = c.Count;
        _pad = c.Pad;
    }

    /// <summary>Makes at least 56 bits available, padding with zeros past the end of the input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Refill()
    {
        if (_count >= 56)
        {
            return;
        }

        if (_pos + 8 <= _src.Length)
        {
            // Load eight bytes at once and keep the whole bytes that fit: the count becomes at least 56.
            _buf |= BinaryPrimitives.ReadUInt64LittleEndian(_src.Slice(_pos)) << _count;
            _pos += (63 - _count) >> 3;
            _count |= 56;
        }
        else
        {
            RefillSlow();
        }
    }

    private void RefillSlow()
    {
        while (_count <= 56)
        {
            if (_pos < _src.Length)
            {
                _buf |= (ulong)_src[_pos++] << _count;
            }
            else
            {
                _pad += 8;
            }

            _count += 8;
        }
    }

    /// <summary>Looks at the next <paramref name="n"/> bits (at most 32) without consuming them. The caller has refilled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly uint Peek(int n) => (uint)(_buf & ((1UL << n) - 1));

    /// <summary>Consumes <paramref name="n"/> bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Drop(int n)
    {
        _buf >>= n;
        _count -= n;
    }

    /// <summary>Takes the next <paramref name="n"/> bits (at most 32) as an integer. The caller has refilled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Take(int n)
    {
        uint v = (uint)(_buf & ((1UL << n) - 1));
        _buf >>= n;
        _count -= n;
        return v;
    }

    /// <summary>Takes the next <paramref name="n"/> bits, refilling first when fewer remain.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint TakeRefilled(int n)
    {
        if (_count < n)
        {
            Refill();
        }

        return Take(n);
    }

    /// <summary>
    /// Decodes one symbol with a table built by <see cref="BrotliPrefixTables"/>, whose first entry is at <paramref name="offset"/>.
    /// The caller has refilled; a symbol takes at most 15 bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadSymbol(uint[] table, int offset)
    {
        uint e = table[offset + (int)(_buf & BrotliPrefixTables.RootMask)];
        if ((e & BrotliPrefixTables.LinkFlag) == 0)
        {
            int len = (int)(e & 0xFF);
            _buf >>= len;
            _count -= len;
            return (int)(e >> 8);
        }

        return ReadSymbolSlow(table, offset, e);
    }

    private int ReadSymbolSlow(uint[] table, int offset, uint link)
    {
        _buf >>= BrotliPrefixTables.RootBits;
        _count -= BrotliPrefixTables.RootBits;
        int subBits = (int)(link & 0x7F);
        uint e = table[offset + (int)(link >> 8) + (int)(_buf & ((1UL << subBits) - 1))];
        int len = (int)(e & 0xFF);
        _buf >>= len;
        _count -= len;
        return (int)(e >> 8);
    }

    /// <summary>
    /// Skips the bits up to the next byte boundary and reports whether they were all zero, as the format requires
    /// (RFC 7932 sections 9.2 and 9.3).
    /// </summary>
    internal bool SkipToByteBoundary()
    {
        int skip = _count & 7;
        bool zero = (_buf & ((1UL << skip) - 1)) == 0;
        _buf >>= skip;
        _count -= skip;
        return zero;
    }

    /// <summary>
    /// Gives back the whole bytes that are in the accumulator, so that the next unread byte is <c>Source[Position]</c>. The reader
    /// must be at a byte boundary and not past the end of the input.
    /// </summary>
    internal void ReturnWholeBytes()
    {
        _pos -= (_count - _pad) >> 3;
        _buf = 0;
        _count = 0;
        _pad = 0;
    }

    /// <summary>The index of the next unread byte after <see cref="ReturnWholeBytes"/>.</summary>
    internal readonly int Position => _pos;

    /// <summary>Moves the read position forward over bytes consumed directly from the source, after <see cref="ReturnWholeBytes"/>.</summary>
    internal void Advance(int bytes) => _pos += bytes;
}
