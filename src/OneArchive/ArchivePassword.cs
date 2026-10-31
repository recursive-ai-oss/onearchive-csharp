
namespace RecursiveAI.OneArchive;

/// <summary>
/// A password for an encrypted archive. A <see cref="string"/>, a <c>char[]</c> or a <see cref="ReadOnlyMemory{T}"/> of
/// <see langword="char"/> converts to it implicitly, so <c>Password = "secret"</c> works; an array can be cleared by
/// the caller once the archive is done. The text is never copied into messages, logs or <see cref="ToString"/>.
/// Zip encodes it as UTF-8 and 7z as UTF-16, as the formats' other tools do.
/// </summary>
public readonly struct ArchivePassword
{
    private readonly ReadOnlyMemory<char> _chars;

    /// <summary>Creates a password from characters.</summary>
    /// <param name="chars">The password text; empty means no password.</param>
    public ArchivePassword(ReadOnlyMemory<char> chars) => _chars = chars;

    /// <summary>Gets a value indicating whether there is no password.</summary>
    public bool IsEmpty => _chars.IsEmpty;

    /// <summary>Gets the password characters.</summary>
    public ReadOnlyMemory<char> Memory => _chars;

    /// <summary>Converts a string; <see langword="null"/> and the empty string mean no password.</summary>
    /// <param name="password">The password.</param>
    public static implicit operator ArchivePassword(string? password) => new(password.AsMemory());

    /// <summary>Converts a character array.</summary>
    /// <param name="password">The password, which the caller may clear afterwards.</param>
    public static implicit operator ArchivePassword(char[]? password) => new(password.AsMemory());

    /// <summary>Converts a block of characters.</summary>
    /// <param name="password">The password.</param>
    public static implicit operator ArchivePassword(ReadOnlyMemory<char> password) => new(password);

    /// <summary>Converts to the characters.</summary>
    /// <param name="password">The password.</param>
    public static implicit operator ReadOnlyMemory<char>(ArchivePassword password) => password._chars;

    /// <summary>Returns a fixed text; the password itself is never shown.</summary>
    /// <returns>A placeholder.</returns>
    public override string ToString() => IsEmpty ? "(no password)" : "(password)";
}
