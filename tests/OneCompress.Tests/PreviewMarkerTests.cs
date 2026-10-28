using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Every public zstd, LZ4 and Deflate type carries the preview marker with its codec's diagnostic ID.</summary>
public class PreviewMarkerTests
{
    [Theory]
    [InlineData("RecursiveAI.OneCompress.Zstd", "ONECOMPRESS001")]
    [InlineData("RecursiveAI.OneCompress.Lz4", "ONECOMPRESS002")]
    [InlineData("RecursiveAI.OneCompress.Deflate", "ONECOMPRESS003")]
    [InlineData("RecursiveAI.OneCompress.Deflate64", "ONECOMPRESS003")]
    [InlineData("RecursiveAI.OneCompress.BZip2", "ONECOMPRESS004")]
    [InlineData("RecursiveAI.OneCompress.Lzma", "ONECOMPRESS005")]
    [InlineData("RecursiveAI.OneCompress.Xz", "ONECOMPRESS006")]
    [InlineData("RecursiveAI.OneCompress.Filters", "ONECOMPRESS006")]
    [InlineData("RecursiveAI.OneCompress.Lzip", "ONECOMPRESS007")]
    [InlineData("RecursiveAI.OneCompress.Brotli", "ONECOMPRESS008")]
    public void EveryPublicTypeIsExperimental(string ns, string id)
    {
        var assembly = typeof(OneCompressInfo).Assembly;
        int seen = 0;
        foreach (var type in assembly.GetTypes())
        {
            // Nested types are covered by their declaring type.
            if (!type.IsPublic || type.Namespace is null) continue;
            if (type.Namespace != ns && !type.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)) continue;
            seen++;
            var attribute = type.GetCustomAttribute<ExperimentalAttribute>();
            Assert.True(attribute is not null, type.FullName + " is not marked [Experimental]");
            Assert.Equal(id, attribute!.DiagnosticId);
            Assert.False(string.IsNullOrEmpty(attribute.UrlFormat));
        }
        Assert.True(seen > 0);
    }

    [Fact]
    public void ChecksumsAndSharedTypesAreNotMarked()
    {
        foreach (var type in typeof(OneCompressInfo).Assembly.GetTypes())
        {
            if (!type.IsPublic) continue;
            if (type.Namespace is { } ns && (ns.EndsWith(".Zstd", StringComparison.Ordinal) || ns.Contains(".Zstd.", StringComparison.Ordinal)
                || ns.EndsWith(".Lz4", StringComparison.Ordinal) || ns.Contains(".Lz4.", StringComparison.Ordinal)
                || ns.EndsWith(".Deflate", StringComparison.Ordinal) || ns.Contains(".Deflate.", StringComparison.Ordinal)
                || ns.EndsWith(".Deflate64", StringComparison.Ordinal)
                || ns.EndsWith(".BZip2", StringComparison.Ordinal) || ns.Contains(".BZip2.", StringComparison.Ordinal)
                || ns.EndsWith(".Lzma", StringComparison.Ordinal) || ns.EndsWith(".Xz", StringComparison.Ordinal) || ns.EndsWith(".Filters", StringComparison.Ordinal) || ns.EndsWith(".Lzip", StringComparison.Ordinal) || ns.EndsWith(".Brotli", StringComparison.Ordinal))) continue;
            Assert.Null(type.GetCustomAttribute<ExperimentalAttribute>());
        }
    }
}
