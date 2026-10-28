using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The LZMA range decoder: a big-endian number read one byte at a time, from which binary decisions are taken either with an
/// adaptive 11-bit probability or with a fixed probability of one half. Reading past the end of the input supplies zero bytes and
/// still advances <see cref="Position"/>, so a caller detects a short input by comparing <see cref="Position"/> with the input
/// length after a group of decisions.
/// </summary>
/// <remarks>LZMA specification, "Range Decoder" and "The Bit Decoding with Probability Model". The value is a mutable struct.</remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public struct LzmaRangeDecoder
{
    /// <summary>The value an adaptive probability starts at: one half of 2^11.</summary>
    public const ushort InitialProbability = 1 << 10;

    /// <summary>The number of bytes that start a range-coded stream.</summary>
    public const int InitBytes = 5;

    private const int TopValue = 1 << 24;
    private const int ProbabilityBits = 11;
    private const int MoveBits = 5;

    private uint _range;
    private uint _code;
    private int _position;

    /// <summary>Gets the current range; it is at least 2^24 between decisions.</summary>
    public readonly uint Range => _range;

    /// <summary>Gets the current code value, which is below <see cref="Range"/> in an undamaged stream and 0 once the last decision of a stream was taken.</summary>
    public readonly uint Code => _code;

    /// <summary>Gets or sets the index in the input of the next byte to read. It may pass the input length (see the type remarks).</summary>
    public int Position
    {
        readonly get => _position;
        set => _position = value;
    }

    /// <summary>Restores a state held in locals by a loop that decodes without this struct.</summary>
    internal void Load(uint range, uint code, int position)
    {
        _range = range;
        _code = code;
        _position = position;
    }

    /// <summary>Gets a value indicating whether the code is 0, which an undamaged stream has after its last decision.</summary>
    public readonly bool IsFinishedOk => _code == 0;

    /// <summary>Starts decoding: reads <see cref="InitBytes"/> bytes at <paramref name="position"/>.</summary>
    /// <param name="input">The input.</param>
    /// <param name="position">Where the stream starts in <paramref name="input"/>.</param>
    /// <returns>False when the first byte is not 0, which the format requires (the state is still set up).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than <see cref="InitBytes"/> bytes are available at <paramref name="position"/>.</exception>
    public bool Init(ReadOnlySpan<byte> input, int position = 0)
    {
        if ((uint)position > (uint)input.Length || input.Length - position < InitBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "At least 5 bytes are needed to start a range-coded stream.");
        }

        _range = 0xFFFFFFFF;
        _code = ((uint)input[position + 1] << 24) | ((uint)input[position + 2] << 16) | ((uint)input[position + 3] << 8) | input[position + 4];
        _position = position + InitBytes;
        return input[position] == 0;
    }

    /// <summary>Decodes one decision with an adaptive probability and updates the probability.</summary>
    /// <param name="input">The input.</param>
    /// <param name="probability">The 11-bit probability that the decision is 0.</param>
    /// <returns>0 or 1.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeBit(ReadOnlySpan<byte> input, ref ushort probability)
    {
        uint p = probability;
        uint bound = (_range >> ProbabilityBits) * p;
        int bit;
        if (_code < bound)
        {
            _range = bound;
            probability = (ushort)(p + (((1 << ProbabilityBits) - p) >> MoveBits));
            bit = 0;
        }
        else
        {
            _range -= bound;
            _code -= bound;
            probability = (ushort)(p - (p >> MoveBits));
            bit = 1;
        }

        Normalize(input);
        return bit;
    }

    /// <summary>Decodes <paramref name="count"/> decisions of probability one half, most significant first.</summary>
    /// <param name="input">The input.</param>
    /// <param name="count">How many bits, 1 to 32.</param>
    /// <returns>The bits as a number.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeDirectBits(ReadOnlySpan<byte> input, int count)
    {
        uint result = 0;
        for (; count > 0; count--)
        {
            _range >>= 1;
            _code -= _range;
            uint t = 0 - (_code >> 31);
            _code += _range & t;
            Normalize(input);
            result = (result << 1) + t + 1;
        }

        return result;
    }

    /// <summary>Decodes a symbol of <paramref name="bits"/> bits with a bit tree, most significant bit first.</summary>
    /// <param name="input">The input.</param>
    /// <param name="probabilities">The tree's <c>2 &lt;&lt; bits</c> probabilities, starting at index <paramref name="offset"/>; index 0 of the tree is unused.</param>
    /// <param name="offset">Where the tree starts.</param>
    /// <param name="bits">The symbol size in bits.</param>
    /// <returns>The symbol.</returns>
    public int DecodeBitTree(ReadOnlySpan<byte> input, Span<ushort> probabilities, int offset, int bits)
    {
        int m = 1;
        for (int i = 0; i < bits; i++)
        {
            m = (m << 1) + DecodeBit(input, ref probabilities[offset + m]);
        }

        return m - (1 << bits);
    }

    /// <summary>Decodes a symbol of <paramref name="bits"/> bits with a bit tree, least significant bit first.</summary>
    /// <param name="input">The input.</param>
    /// <param name="probabilities">The tree's probabilities, as for <see cref="DecodeBitTree"/>.</param>
    /// <param name="offset">Where the tree starts.</param>
    /// <param name="bits">The symbol size in bits.</param>
    /// <returns>The symbol.</returns>
    public int DecodeReverseBitTree(ReadOnlySpan<byte> input, Span<ushort> probabilities, int offset, int bits)
    {
        int m = 1;
        int symbol = 0;
        for (int i = 0; i < bits; i++)
        {
            int bit = DecodeBit(input, ref probabilities[offset + m]);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }

        return symbol;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize(ReadOnlySpan<byte> input)
    {
        if (_range < TopValue)
        {
            _range <<= 8;
            int p = _position;
            _code = (_code << 8) | ((uint)p < (uint)input.Length ? input[p] : 0u);
            _position = p + 1;
        }
    }
}
