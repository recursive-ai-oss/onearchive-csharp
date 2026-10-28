using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// A move-to-front list over at most 256 byte values. bzip2 uses it twice: for the block's symbols (the list starts as the
/// bytes that occur in the block, in increasing order) and for the table selectors (the list starts as 0, 1, 2, ...).
/// </summary>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BZip2MoveToFront
{
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
        int index = _list.AsSpan(0, _count).IndexOf(value);
        if (index < 0)
        {
            throw new ArgumentException("The value is not in the list.", nameof(value));
        }

        Decode(index);
        return index;
    }
}
