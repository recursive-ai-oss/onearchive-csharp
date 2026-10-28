using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Filters;

/// <summary>
/// The delta filter: each byte is stored as the difference to the byte <see cref="Distance"/> positions earlier, which helps data in which a
/// byte correlates with the one at a fixed stride (audio samples, bitmap rows, fixed-size records). The transform is done in place and keeps
/// the size.
/// </summary>
/// <remarks>The xz file format specification, section 5.3.3: the distance is 1 to 256 and the history starts as zeros. Any split of the data gives the same bytes as one call.</remarks>
[Experimental("ONECOMPRESS006", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeltaFilter
{
    private readonly byte[] _history = new byte[256];
    private readonly bool _encode;
    private byte _position;

    /// <summary>Creates a filter.</summary>
    /// <param name="distance">The stride in bytes, 1 to 256.</param>
    /// <param name="encode">True to store differences (compression side), false to add them back.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="distance"/> is outside 1 to 256.</exception>
    public DeltaFilter(int distance, bool encode = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, 256);
        Distance = distance;
        _encode = encode;
    }

    /// <summary>Gets the stride in bytes.</summary>
    public int Distance { get; }

    /// <summary>Starts over with an all-zero history.</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _position = 0;
    }

    /// <summary>Converts <paramref name="data"/> in place; all of it is final.</summary>
    /// <param name="data">The bytes.</param>
    public void Convert(Span<byte> data)
    {
        byte[] history = _history;
        byte pos = _position;
        int distance = Distance;
        if (_encode)
        {
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                data[i] = (byte)(b - history[(byte)(distance + pos)]);
                history[pos] = b;
                pos--;
            }
        }
        else
        {
            for (int i = 0; i < data.Length; i++)
            {
                byte b = (byte)(data[i] + history[(byte)(distance + pos)]);
                data[i] = b;
                history[pos] = b;
                pos--;
            }
        }

        _position = pos;
    }
}
