using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneCompress;
using static RecursiveAI.OneArchive.Tests.SevenZip.SevenZipFixtures;

namespace RecursiveAI.OneArchive.Tests.SevenZip;

/// <summary>
/// Headers that used to end in an IndexOutOfRangeException and must give a typed format error. Each is the smallest header
/// that shows one cause found by fuzzing.
/// </summary>
public class SevenZipHeaderHardeningTests
{
    private static CompressionFormatException Refused(byte[] archive) =>
        Assert.Throws<CompressionFormatException>(() => new SevenZipReader(new MemoryStream(archive)).GetNextEntry());

    [Fact]
    public void SecondEmptyStreamVectorAfterEmptyFileVectorIsRefused()
    {
        // 0E says one empty file, 0F gives one bit for it, then a second 0E makes two: the 0F vector no longer fits.
        byte[] header = Header([], 2,
            Prop(0x0E, Bits(true, false)),
            Prop(0x0F, Bits(true)),
            Prop(0x0E, Bits(true, true)),
            Names("a", "b"));
        Assert.Contains("empty-stream", Refused(Build([], header)).Expected);
    }

    [Fact]
    public void EmptyFileAndAntiVectorsNeedAnEmptyStreamVectorFirst()
    {
        byte[] file = Header([], 1, Prop(0x0F, Bits(true)), Names("a"));
        Assert.Contains("empty-stream", Refused(Build([], file)).Expected);
        byte[] anti = Header([], 1, Prop(0x10, Bits(true)), Names("a"));
        Assert.Contains("empty-stream", Refused(Build([], anti)).Expected);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    public void CoderWithoutInputOrOutputStreamsIsRefused(int numIn, int numOut)
    {
        // A complex coder (flag 0x10) declaring no inputs gave a total of zero inputs, and the bond index was then read
        // against a limit of -1.
        byte[] streams = Cat(0x06, 0UL, 1UL, 0x09, 3UL, 0x00,
            0x07, 0x0B, 1UL, 0, 2UL,
            Cat(0x11, 0x00, (ulong)numIn, (ulong)numOut), Cat(0x01, 0x00),
            0UL, 0UL,
            0x0C, 3UL, 3UL, 3UL, 0x00, 0x00);
        byte[] header = Header(streams, 1, Names("a"));
        Assert.Contains("at least one input and one output", Refused(Build("abc"u8.ToArray(), header)).Expected);
    }
}
