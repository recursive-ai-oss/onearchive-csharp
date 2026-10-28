using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive;

/// <summary>
/// Extraction refused an entry because it would break a safety rule: a path that leaves the target directory, a link
/// that escapes or is written through, or a device or FIFO entry. Nothing is written for the entry.
/// </summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public class ArchiveSafetyException : CompressionException
{
    /// <summary>Creates a safety error.</summary>
    /// <param name="format">The archive format's name.</param>
    /// <param name="rule">A short stable name for the rule that was broken, for example <c>"path-traversal"</c>.</param>
    /// <param name="entryName">The entry's name as stored in the archive.</param>
    /// <param name="detail">What exactly is wrong.</param>
    public ArchiveSafetyException(string format, string rule, string entryName, string detail)
        : base($"{format}: unsafe entry '{Printable(entryName)}' ({rule}): {detail}")
    {
        Format = format;
        Rule = rule;
        EntryName = entryName;
    }

    /// <summary>Creates a safety error with a default message.</summary>
    public ArchiveSafetyException()
        : this("unknown", "unsafe-entry", string.Empty, "the entry is unsafe")
    {
    }

    /// <summary>Creates a safety error with a message.</summary>
    /// <param name="message">The message.</param>
    public ArchiveSafetyException(string message)
        : base(message)
    {
        Format = "unknown";
        Rule = "unsafe-entry";
        EntryName = string.Empty;
    }

    /// <summary>Creates a safety error with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ArchiveSafetyException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Format = "unknown";
        Rule = "unsafe-entry";
        EntryName = string.Empty;
    }

    /// <summary>Gets the name of the archive format.</summary>
    public string Format { get; }

    /// <summary>Gets the stable name of the broken rule: <c>path-traversal</c>, <c>absolute-path</c>, <c>invalid-name</c>, <c>link-escape</c>, <c>link-write-through</c>, <c>hardlink-target</c>, <c>special-file</c> or <c>existing-file</c>.</summary>
    public string Rule { get; }

    /// <summary>Gets the entry's name as stored in the archive.</summary>
    public string EntryName { get; }

    private static string Printable(string name)
    {
        // Names come from hostile input: keep control characters out of logs and consoles.
        var chars = name.Length > 200 ? name[..200].ToCharArray() : name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }
}

/// <summary>
/// The archive uses a feature, method or format that is recognised but not supported: a compression layer such as gzip
/// that has not landed yet, a multi-volume tar, or an entry type that cannot be created on this platform.
/// </summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public class ArchiveUnsupportedException : CompressionException
{
    /// <summary>Creates an unsupported-feature error.</summary>
    /// <param name="format">The archive format's name.</param>
    /// <param name="feature">What is not supported.</param>
    public ArchiveUnsupportedException(string format, string feature)
        : base($"{format}: {feature} is not supported")
    {
        Format = format;
        Feature = feature;
    }

    /// <summary>Creates an unsupported-feature error that wraps the exception that revealed it.</summary>
    /// <param name="format">The archive format's name.</param>
    /// <param name="feature">What is not supported.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ArchiveUnsupportedException(string format, string feature, Exception? innerException)
        : base($"{format}: {feature} is not supported", innerException)
    {
        Format = format;
        Feature = feature;
    }

    /// <summary>Creates an unsupported-feature error with a default message.</summary>
    public ArchiveUnsupportedException()
        : this("unknown", "this feature")
    {
    }

    /// <summary>Creates an unsupported-feature error with a message.</summary>
    /// <param name="message">The message.</param>
    public ArchiveUnsupportedException(string message)
        : base(message)
    {
        Format = "unknown";
        Feature = message;
    }

    /// <summary>Creates an unsupported-feature error with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ArchiveUnsupportedException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Format = "unknown";
        Feature = message;
    }

    /// <summary>Gets the name of the archive format.</summary>
    public string Format { get; }

    /// <summary>Gets a description of what is not supported.</summary>
    public string Feature { get; }
}
