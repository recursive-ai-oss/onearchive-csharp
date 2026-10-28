using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Every public OneArchive type carries the preview marker.</summary>
public class PreviewMarkerTests
{
    [Fact]
    public void EveryPublicTypeIsExperimental()
    {
        int seen = 0;
        foreach (var type in typeof(Archive).Assembly.GetTypes())
        {
            // Nested types are covered by their declaring type.
            if (!type.IsPublic)
            {
                continue;
            }

            seen++;
            var attribute = type.GetCustomAttribute<ExperimentalAttribute>();
            Assert.True(attribute is not null, type.FullName + " is not marked [Experimental]");
            Assert.Equal("ONEARCHIVE001", attribute!.DiagnosticId);
            Assert.False(string.IsNullOrEmpty(attribute.UrlFormat));
        }

        Assert.True(seen > 20);
    }
}
