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

    /// <summary>Creates an unsupported-feature error with a message of the caller's own wording, for derived types.</summary>
    /// <param name="format">The archive format's name.</param>
    /// <param name="feature">What is not supported.</param>
    /// <param name="message">The complete message.</param>
    protected ArchiveUnsupportedException(string format, string feature, string message)
        : base(message)
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

/// <summary>Why <see cref="ArchivePasswordException"/> was raised.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ArchivePasswordProblem
{
    /// <summary>The data is encrypted and no password was supplied.</summary>
    Required = 0,

    /// <summary>A password was supplied but it is not the one the data was encrypted with (or, for formats that cannot tell, the decrypted data does not check out).</summary>
    Incorrect = 1,
}

/// <summary>
/// An encrypted entry or header could not be opened because the password is missing or wrong. This is distinct from
/// corruption (<see cref="CompressionFormatException"/>), although 7z cannot always tell the two apart: it stores no
/// password check, so a wrong password shows up as data that fails its CRC. The message never contains the password.
/// </summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public class ArchivePasswordException : CompressionException
{
    /// <summary>Creates a password error.</summary>
    /// <param name="format">The archive format's name.</param>
    /// <param name="entryName">The entry's name as stored in the archive; empty when the header itself is encrypted.</param>
    /// <param name="problem">Whether the password is missing or wrong.</param>
    /// <param name="innerException">The exception that revealed a wrong password, if any.</param>
    /// <param name="note">A short remark added to the message, for example that the format cannot tell a wrong password from damage.</param>
    public ArchivePasswordException(string format, string entryName, ArchivePasswordProblem problem, Exception? innerException = null, string? note = null)
        : base(Describe(format, entryName, problem) + (note is null ? string.Empty : $" ({note})"), innerException)
    {
        Format = format;
        EntryName = entryName;
        Problem = problem;
    }

    /// <summary>Creates a password error with a default message.</summary>
    public ArchivePasswordException()
        : this("unknown", string.Empty, ArchivePasswordProblem.Required)
    {
    }

    /// <summary>Creates a password error with a message.</summary>
    /// <param name="message">The message.</param>
    public ArchivePasswordException(string message)
        : base(message)
    {
        Format = "unknown";
        EntryName = string.Empty;
    }

    /// <summary>Creates a password error with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ArchivePasswordException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Format = "unknown";
        EntryName = string.Empty;
    }

    /// <summary>Gets the name of the archive format.</summary>
    public string Format { get; }

    /// <summary>Gets the name of the entry that needs the password, as stored; empty when the archive's header is the encrypted part.</summary>
    public string EntryName { get; }

    /// <summary>Gets whether the password was missing or wrong.</summary>
    public ArchivePasswordProblem Problem { get; }

    private static string Describe(string format, string entryName, ArchivePasswordProblem problem)
    {
        // Names come from hostile input: keep control characters out of logs and consoles.
        char[] chars = (entryName.Length > 200 ? entryName[..200] : entryName).ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = '?';
            }
        }

        string what = chars.Length == 0 ? "the archive header" : $"entry '{new string(chars)}'";
        return problem == ArchivePasswordProblem.Required
            ? $"{format}: {what} is encrypted and no password was given"
            : $"{format}: the password given does not open {what}";
    }
}
