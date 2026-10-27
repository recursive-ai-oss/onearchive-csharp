namespace RecursiveAI.OneArchive.Tests;

public class InfoTests
{
    [Fact]
    public void NameMentionsCodecPackage() => Assert.Contains("OneCompress", OneArchiveInfo.Name, StringComparison.Ordinal);
}
