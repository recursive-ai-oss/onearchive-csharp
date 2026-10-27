namespace RecursiveAI.OneCompress;

/// <summary>
/// The base of every error a OneCompress codec raises for its input or its configuration limits.
/// Malformed input raises <see cref="CompressionFormatException"/>, an exceeded limit raises
/// <see cref="CompressionLimitException"/>, and a missing or mismatched dictionary raises
/// <see cref="CompressionDictionaryException"/>. Argument errors, <see cref="OperationCanceledException"/>
/// and I/O errors from the caller's own streams are the only other exceptions a public method throws.
/// </summary>
public class CompressionException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public CompressionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public CompressionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with a default message.</summary>
    public CompressionException()
        : base("The compressed data could not be processed.")
    {
    }
}

/// <summary>
/// The input is not valid data of the named format: it is truncated, corrupt, or uses a value the
/// format forbids. The properties say which format, where, and what was expected there.
/// </summary>
public class CompressionFormatException : CompressionException
{
    /// <summary>Creates a format error.</summary>
    /// <param name="format">The format's name, for example <c>"zstd"</c>.</param>
    /// <param name="offset">The byte offset in the compressed input where the problem was found.</param>
    /// <param name="expected">What the format requires at that position.</param>
    /// <param name="isTruncated">True when the input ended before the format allowed it to.</param>
    public CompressionFormatException(string format, long offset, string expected, bool isTruncated = false)
        : base($"{format}: invalid data at byte offset {offset}: expected {expected}")
    {
        Format = format;
        Offset = offset;
        Expected = expected;
        IsTruncated = isTruncated;
    }

    /// <summary>Creates a format error with a default message.</summary>
    public CompressionFormatException()
        : this("unknown", 0, "valid data")
    {
    }

    /// <summary>Creates a format error with a message.</summary>
    /// <param name="message">The message.</param>
    public CompressionFormatException(string message)
        : base(message)
    {
        Format = "unknown";
        Expected = "valid data";
    }

    /// <summary>Creates a format error with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public CompressionFormatException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Format = "unknown";
        Expected = "valid data";
    }

    /// <summary>Gets the name of the format that was being read.</summary>
    public string Format { get; }

    /// <summary>Gets the byte offset in the compressed input where the problem was found.</summary>
    public long Offset { get; }

    /// <summary>Gets a description of what the format requires at <see cref="Offset"/>.</summary>
    public string Expected { get; }

    /// <summary>Gets a value indicating whether the input simply ended too early (as opposed to holding a wrong value).</summary>
    public bool IsTruncated { get; }
}

/// <summary>
/// A configured limit (window size, output size, entry size, and so on) was exceeded. The decoder raises
/// this before it allocates the memory the input asks for.
/// </summary>
public class CompressionLimitException : CompressionException
{
    /// <summary>Creates a limit error.</summary>
    /// <param name="format">The format's name.</param>
    /// <param name="limitName">The name of the option that holds the limit.</param>
    /// <param name="limit">The configured limit.</param>
    /// <param name="requested">The value the input asked for (or the least value known to exceed the limit).</param>
    public CompressionLimitException(string format, string limitName, long limit, long requested)
        : base($"{format}: {limitName} limit exceeded: the input needs {requested} but the limit is {limit}")
    {
        Format = format;
        LimitName = limitName;
        Limit = limit;
        Requested = requested;
    }

    /// <summary>Creates a limit error with a default message.</summary>
    public CompressionLimitException()
        : this("unknown", "limit", 0, 0)
    {
    }

    /// <summary>Creates a limit error with a message.</summary>
    /// <param name="message">The message.</param>
    public CompressionLimitException(string message)
        : base(message)
    {
        Format = "unknown";
        LimitName = "limit";
    }

    /// <summary>Creates a limit error with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public CompressionLimitException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Format = "unknown";
        LimitName = "limit";
    }

    /// <summary>Gets the name of the format that was being read.</summary>
    public string Format { get; }

    /// <summary>Gets the name of the option that holds the exceeded limit.</summary>
    public string LimitName { get; }

    /// <summary>Gets the configured limit.</summary>
    public long Limit { get; }

    /// <summary>Gets the value the input asked for.</summary>
    public long Requested { get; }
}

/// <summary>
/// A frame names a dictionary that was not supplied, or a dictionary was supplied that cannot be used.
/// </summary>
public class CompressionDictionaryException : CompressionException
{
    /// <summary>Creates a dictionary error.</summary>
    /// <param name="format">The format's name.</param>
    /// <param name="dictionaryId">The dictionary ID the frame asked for, or zero if there is none.</param>
    /// <param name="message">What went wrong.</param>
    public CompressionDictionaryException(string format, uint dictionaryId, string message)
        : base($"{format}: {message}")
    {
        Format = format;
        DictionaryId = dictionaryId;
    }

    /// <summary>Creates a dictionary error with a default message.</summary>
    public CompressionDictionaryException()
        : this("unknown", 0, "dictionary error")
    {
    }

    /// <summary>Creates a dictionary error with a message.</summary>
    /// <param name="message">The message.</param>
    public CompressionDictionaryException(string message)
        : base(message)
    {
        Format = "unknown";
    }

    /// <summary>Creates a dictionary error with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public CompressionDictionaryException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Format = "unknown";
    }

    /// <summary>Gets the name of the format that was being read.</summary>
    public string Format { get; }

    /// <summary>Gets the dictionary ID the frame asked for, or zero if it named none.</summary>
    public uint DictionaryId { get; }
}
