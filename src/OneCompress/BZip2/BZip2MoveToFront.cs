using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// A move-to-front list over at most 256 byte values. bzip2 uses it twice: for the block's symbols (the list starts as the
/// bytes that occur in the block, in increasing order) and for the table selectors (the list starts as 0, 1, 2, ...).
/// </summary>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BZip2MoveToFront
{
    private static readonly Vector128<byte> ShiftUp = Vector128.Create((byte)0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14);
    private static readonly Vector128<byte> Lanes = Vector128.Create((byte)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

    private readonly byte[] _list = new byte[256];
    private int _count;

    /// <summary>Gets the number of values in the list.</summary>
    public int Count => _count;

    /// <summary>Starts the list as the values 0, 1, ... <paramref name="count"/> - 1.</summary>
    /// <param name="count">The number of values, 0 to 256.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 0 to 256.</exception>
    public void Reset(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 256);
        for (int i = 0; i < count; i++)
        {
            _list[i] = (byte)i;
        }

        _count = count;
    }

    /// <summary>Starts the list as the given values, front first.</summary>
    /// <param name="values">At most 256 values.</param>
    /// <exception cref="ArgumentException">There are more than 256 values.</exception>
    public void Reset(ReadOnlySpan<byte> values)
    {
        if (values.Length > 256)
        {
            throw new ArgumentException("A move-to-front list holds at most 256 values.", nameof(values));
        }

        values.CopyTo(_list);
        _count = values.Length;
    }

    /// <summary>Gets the value at the front of the list without moving anything.</summary>
    public byte Front => _list[0];

    /// <summary>Takes the value at <paramref name="index"/>, moves it to the front and returns it.</summary>
    /// <param name="index">The position, 0 to <see cref="Count"/> - 1.</param>
    /// <returns>The value that was at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the list.</exception>
    public byte Decode(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        byte[] list = _list;
        byte v = list[index];
        if (index < 16)
        {
            for (int i = index; i > 0; i--)
            {
                list[i] = list[i - 1];
            }
        }
        else
        {
            list.AsSpan(0, index).CopyTo(list.AsSpan(1));
        }

        list[0] = v;
        return v;
    }

    /// <summary>Finds <paramref name="value"/>, moves it to the front and returns the position it had.</summary>
    /// <param name="value">A value in the list.</param>
    /// <returns>The position the value had before the move.</returns>
    /// <exception cref="ArgumentException">The value is not in the list.</exception>
    public int Encode(byte value)
    {
        // Values near the front are the common case after the block sort, so the first sixteen positions are searched and shifted in
        // one pass; the rest of the list is searched with a vectorised scan.
        byte[] list = _list;
        if (_count == 0)
        {
            throw new ArgumentException("The value is not in the list.", nameof(value));
        }

        byte previous = list[0];
        if (previous == value)
        {
            return 0;
        }

        int near = Math.Min(_count, 16);
        for (int i = 1; i < near; i++)
        {
            byte current = list[i];
            list[i] = previous;
            if (current == value)
            {
                list[0] = value;
                return i;
            }

            previous = current;
        }

        if (_count > 16)
        {
            ReadOnlySpan<byte> far = list.AsSpan(16, _count - 16);
            int j = far.IndexOf(value);
            if (j >= 0)
            {
                list.AsSpan(16, j).CopyTo(list.AsSpan(17));
                list[16] = previous;
                list[0] = value;
                return 16 + j;
            }
        }

        // Not found: put the list back as it was before throwing.
        for (int i = 0; i < near - 1; i++)
        {
            list[i] = list[i + 1];
        }

        list[near - 1] = previous;

        throw new ArgumentException("The value is not in the list.", nameof(value));
    }

    /// <summary>
    /// The block-coding loop: move-to-front over <paramref name="input"/> from the current list, with runs of position 0 written as RUNA/RUNB
    /// digits (a run of <c>n</c> is <c>n</c> in bijective base 2, least significant digit first) and any other position <c>p</c> as symbol
    /// <c>p</c> + 1. Gives the same symbols as calling <see cref="Encode"/> per byte.
    /// </summary>
    /// <remarks>
    /// The first sixteen list positions live in a vector register for the whole loop. A byte found there is moved to the front with a
    /// shuffle and a select; a byte further back is found with a 16-byte-at-a-time scan, and the stretch in front of it is moved up by one
    /// with overlapping 16-byte copies. All of it computes what a one-position-at-a-time search and shift would, whatever the vector width.
    /// </remarks>
    /// <param name="input">Bytes that all occur in the list.</param>
    /// <param name="symbols">At least <c>input.Length</c> + 1 entries; the symbols are written from the start.</param>
    /// <returns>The number of symbols written (the end-of-block symbol is not included).</returns>
    internal int EncodeSymbols(ReadOnlySpan<byte> input, Span<ushort> symbols)
    {
        byte[] list = _list;
        int size = _count;
        int written = 0;
        int run = 0;
        Vector128<byte> head = Vector128.Create(list.AsSpan(0, 16));
        byte front = list[0];
        foreach (byte value in input)
        {
            if (value == front)
            {
                run++;
                continue;
            }

            while (run > 0)
            {
                if ((run & 1) != 0)
                {
                    symbols[written++] = 0;
                    run = (run - 1) >> 1;
                }
                else
                {
                    symbols[written++] = 1;
                    run = (run - 2) >> 1;
                }
            }

            Vector128<byte> needle = Vector128.Create(value);
            uint hits = Vector128.Equals(head, needle).ExtractMostSignificantBits();
            int position;
            if (hits != 0)
            {
                position = BitOperations.TrailingZeroCount(hits);
                Vector128<byte> moved = Vector128.LessThanOrEqual(Lanes, Vector128.Create((byte)position));
                head = Vector128.ConditionalSelect(moved, Vector128.Shuffle(head, ShiftUp), head).WithElement(0, value);
            }
            else
            {
                head.CopyTo(list.AsSpan(0, 16));
                position = 16;
                while (true)
                {
                    if (position >= size)
                    {
                        throw new ArgumentException("The value is not in the list.", nameof(input));
                    }

                    hits = Vector128.Equals(Vector128.Create(list.AsSpan(position, 16)), needle).ExtractMostSignificantBits();
                    if (hits != 0)
                    {
                        position += BitOperations.TrailingZeroCount(hits);
                        break;
                    }

                    position += 16;
                }

                // Positions 16 .. position move up by one (every store puts old[p - 1] at p, so overlapping stores agree); the byte at
                // position 15 lands on 16 and the register takes over positions 0 .. 15.
                for (int end = position; end > 16; end -= 16)
                {
                    Vector128.Create(list.AsSpan(end - 16, 16)).CopyTo(list.AsSpan(end - 15, 16));
                }

                list[16] = head.GetElement(15);
                head = Vector128.Shuffle(head, ShiftUp).WithElement(0, value);
            }

            front = value;
            symbols[written++] = (ushort)(position + 1);
        }

        while (run > 0)
        {
            if ((run & 1) != 0)
            {
                symbols[written++] = 0;
                run = (run - 1) >> 1;
            }
            else
            {
                symbols[written++] = 1;
                run = (run - 2) >> 1;
            }
        }

        head.CopyTo(list.AsSpan(0, 16));
        return written;
    }
}
