using System.Text;
using RecursiveAI.OneArchive.Rar;

namespace RecursiveAI.OneArchive.Tests.Rar;

public class Blake2spTests
{
    // Digests of the input bytes (i % 251) for i in 0..n-1, computed by tools/rar-testdata/blake2sp-vectors.py with Python's hashlib
    // (BLAKE2s with tree parameters) as a black box.
    public static TheoryData<int, string> Vectors => new()
    {
        { 0, "dd0e891776933f43c7d032b08a917e25741f8aa9a12c12e1cac8801500f2ca4f" },
        { 1, "a6b9eecc25227ad788c99d3f236debc8da408849e9a5178978727a81457f7239" },
        { 63, "1024c940be7341449b5010522b509f65bbdc1287b455c2bb7f72b2c92fd0d189" },
        { 64, "52603b6cbfad4966cb044cb267568385cf35f21e6c45cf30aed19832cb51e9f5" },
        { 65, "fff24d3cc729d395daf978b0157306cb495797e6c8dca1731d2f6f81b849baae" },
        { 127, "a626543c271fccc3e4450b48d66bc9cbdeb25e5d077a6213cd90cbbd0fd22076" },
        { 128, "05cf3a90049116dc60efc31536aaa3d167762994892876dcb7ef3fbecd7449c0" },
        { 255, "3aafcdc0f0ec17f0d35db5dae359b9fa2045f4ed5af4e708bd3b8817e1722d21" },
        { 256, "d1b35d04c0849d6dc758990229c9539784b9e9a8592aa5db63b7cb424ac7105c" },
        { 511, "8e1e8ee1ffa0a01028fff3bff0ae9df2565a82e55a04e9541bb78b9c4778336f" },
        { 512, "8d9e357863298dd8364b7caf4234317f8a49f180d788b7abffb521925f1e1ff1" },
        { 513, "8a4bc3330497e681f15daf24fc496044a1c32bf0a837a210399e1ae4af7e92be" },
        { 1000, "611f1af6610cdaf674ec2c9178f6376ebe234ef50998a3be3f1fa698fb779274" },
        { 1024, "48467549502e2d3f422870bfb1d09bce71a065735763bf654582cf46a5112793" },
        { 4096, "dd02c617ddc87d204cbcb5795b637368467fa516710f880e9c782b00b0dca78c" },
        { 4097, "055d3f5f5440fe6c8dd0dcceb503b93caa03a533b71b1e47b4fdcb5577b8762a" },
        { 100000, "eb7050ea034453a75de798404d87f5568c4c0c6a7cccba79bd5863a40a33d10b" },
    };

    private static byte[] Pattern(int n)
    {
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (byte)(i % 251);
        }

        return b;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void MatchesReferenceDigest(int length, string hex)
    {
        Assert.Equal(hex, Convert.ToHexString(Blake2sp.Hash(Pattern(length))).ToLowerInvariant());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(511)]
    [InlineData(513)]
    [InlineData(5000)]
    public void StreamingInAnyChunkSizeGivesTheSameDigest(int chunk)
    {
        byte[] data = Pattern(100000);
        var h = new Blake2sp();
        for (int i = 0; i < data.Length; i += chunk)
        {
            h.Update(data.AsSpan(i, Math.Min(chunk, data.Length - i)));
        }

        byte[] digest = new byte[32];
        h.Final(digest);
        Assert.Equal("eb7050ea034453a75de798404d87f5568c4c0c6a7cccba79bd5863a40a33d10b", Convert.ToHexString(digest).ToLowerInvariant());
    }

    [Theory]
    [InlineData("", "69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9")]
    [InlineData("abc", "508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982")]
    public void Blake2sMatchesRfc7693(string text, string hex)
    {
        // RFC 7693 Appendix B traces BLAKE2s-256("abc").
        var h = new Blake2s();
        h.Update(Encoding.ASCII.GetBytes(text));
        byte[] digest = new byte[32];
        h.Final(digest);
        Assert.Equal(hex, Convert.ToHexString(digest).ToLowerInvariant());
    }
}
