using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Lzx;

/// <summary>
/// The x86 call translation of LZX ([MS-PATCH] 2.2.2): the compressor turns the relative operand of each <c>E8</c> (call) byte into
/// an absolute one so that repeated calls to the same target look alike, and the decompressor turns it back.
/// </summary>
public static class LzxCallTranslation
{
    /// <summary>The size of a frame, the unit translation works on.</summary>
    public const int FrameSize = 32768;

    /// <summary>Calls are translated only in the first 2<sup>30</sup> bytes of the stream (32768 frames).</summary>
    public const long TranslationLimit = 0x40000000;

    /// <summary>Reverses the translation in place for one frame.</summary>
    /// <param name="frame">The decoded bytes of the frame.</param>
    /// <param name="frameOffset">The number of stream bytes before this frame.</param>
    /// <param name="fileSize">The translation size from the stream header.</param>
    public static void Undo(Span<byte> frame, long frameOffset, uint fileSize)
    {
        // [MS-PATCH] 2.2.2: nothing is translated past 1 GiB, and the last ten bytes of a frame never start a translated call.
        if (frameOffset >= TranslationLimit || frame.Length <= 10)
        {
            return;
        }

        int end = frame.Length - 10;
        int i = 0;
        while (i < end)
        {
            int e8 = frame.Slice(i, end - i).IndexOf((byte)0xE8);
            if (e8 < 0)
            {
                return;
            }

            i += e8;
            long current = frameOffset + i;
            int value = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(i + 1, 4));
            if (value >= -current && value < (long)fileSize)
            {
                long displacement = value >= 0 ? value - current : value + (long)fileSize;
                BinaryPrimitives.WriteInt32LittleEndian(frame.Slice(i + 1, 4), unchecked((int)displacement));
            }

            i += 5;
        }
    }
}
