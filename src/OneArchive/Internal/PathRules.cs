using System.Text;

namespace RecursiveAI.OneArchive.Internal;

/// <summary>
/// The path rules shared by every container's extraction. Archive names are treated the same on every platform:
/// both <c>/</c> and <c>\</c> separate components (a zip written on Windows says <c>a\b</c>), <c>.</c> and empty
/// components are dropped, and anything that could leave the target directory is unsafe.
/// </summary>
internal static class PathRules
{
    /// <summary>Counts the components of a name (empty and <c>.</c> components do not count).</summary>
    public static int Depth(string name)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i == name.Length || name[i] == '/' || name[i] == '\\')
            {
                int len = i - start;
                if (len > 0 && !(len == 1 && name[start] == '.'))
                {
                    depth++;
                }

                start = i + 1;
            }
        }

        return depth;
    }

    private static bool StartsWithDrive(string name) =>
        name.Length >= 2 && name[1] == ':' && ((name[0] >= 'A' && name[0] <= 'Z') || (name[0] >= 'a' && name[0] <= 'z'));

    /// <summary>
    /// Turns a stored name into the relative components to create. Returns null and sets <paramref name="rule"/> and
    /// <paramref name="detail"/> when the name is unsafe and <paramref name="sanitize"/> does not repair it.
    /// An empty result means the target directory itself.
    /// </summary>
    public static List<string>? Resolve(string name, bool sanitize, out string rule, out string detail)
    {
        rule = string.Empty;
        detail = string.Empty;
        if (name.Contains('\0', StringComparison.Ordinal))
        {
            rule = "invalid-name";
            detail = "the name contains a NUL character";
            return null;
        }

        bool absolute = name.Length > 0 && (name[0] == '/' || name[0] == '\\');
        if (absolute || StartsWithDrive(name))
        {
            if (!sanitize)
            {
                rule = "absolute-path";
                detail = "the name is absolute (a root, UNC prefix or drive letter)";
                return null;
            }

            int skip = StartsWithDrive(name) ? 2 : 0;
            name = name[skip..];
        }

        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i != name.Length && name[i] != '/' && name[i] != '\\')
            {
                continue;
            }

            string part = name[start..i];
            start = i + 1;
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (!sanitize)
                {
                    rule = "path-traversal";
                    detail = "the name contains a '..' component";
                    return null;
                }

                continue;
            }

            parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// True if a symbolic link whose own directory has <paramref name="linkDirDepth"/> components may point at
    /// <paramref name="target"/>: it is relative, and any <c>..</c> components come first and do not climb above the
    /// target directory. A <c>..</c> after a name is refused, because a name earlier in the path may itself be a
    /// link and make the lexical result wrong.
    /// </summary>
    public static bool IsSafeSymlinkTarget(string target, int linkDirDepth)
    {
        if (target.Length == 0 || target.Contains('\0', StringComparison.Ordinal) || target[0] == '/' || target[0] == '\\' || StartsWithDrive(target))
        {
            return false;
        }

        int climbs = 0;
        bool seenName = false;
        int start = 0;
        for (int i = 0; i <= target.Length; i++)
        {
            if (i != target.Length && target[i] != '/' && target[i] != '\\')
            {
                continue;
            }

            string part = target[start..i];
            start = i + 1;
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (seenName)
                {
                    return false;
                }

                climbs++;
            }
            else
            {
                seenName = true;
            }
        }

        return climbs <= linkDirDepth;
    }

    /// <summary>Decodes archive name bytes: UTF-8 when valid, otherwise Latin-1 (one char per byte).</summary>
    public static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return Encoding.Latin1.GetString(bytes);
    }
}
