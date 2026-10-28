using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive;

/// <summary>
/// Placeholder that gives the package a public member to call. Replaced once the first container lands.
/// </summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class OneArchiveInfo
{
    /// <summary>Gets the package's display name, followed by the codec package it builds on.</summary>
    public static string Name => "OneArchive on " + OneCompressInfo.Name;
}
