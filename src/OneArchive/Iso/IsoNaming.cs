using System.Text;

namespace RecursiveAI.OneArchive.Iso;

/// <summary>How the writer turns a file name into the identifiers of each hierarchy, and how it orders them.</summary>
internal static class IsoNaming
{
    /// <summary>Clause 7.4.1 and Annex A: a d-character is an upper-case letter, a digit or the underscore.</summary>
    private static char DChar(char c)
    {
        if (c is >= 'a' and <= 'z')
        {
            c = (char)(c - 32);
        }

        return c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' ? c : '_';
    }

    private static string DChars(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(DChar(c));
        }

        return sb.ToString();
    }

    /// <summary>Splits a name at its last dot into base and extension (a leading dot does not start an extension).</summary>
    private static (string Base, string Ext) Split(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? (name, string.Empty) : (name[..dot], name[(dot + 1)..]);
    }

    private static string Join(string name, string ext, bool isDirectory) =>
        isDirectory ? name : name + "." + ext + ";1";

    /// <summary>
    /// The primary-hierarchy identifier for a name at the given interchange level (clause 10): d-characters only; level 1 keeps
    /// 8+3 characters, levels 2 and 3 keep 30 characters for name and extension together (31 for a directory). A file identifier
    /// is NAME.EXT;1 with the dot always present (clause 7.5).
    /// </summary>
    public static string IsoName(string name, bool isDirectory, int level, int counter)
    {
        if (isDirectory)
        {
            string d = DChars(name);
            int max = level == 1 ? 8 : 31;
            return Counter(d, max, counter);
        }

        (string b, string e) = Split(name);
        b = DChars(b);
        e = DChars(e);
        if (level == 1)
        {
            e = e.Length > 3 ? e[..3] : e;
            b = Counter(b, 8, counter);
        }
        else
        {
            if (e.Length > 29)
            {
                e = e[..29];
            }

            b = Counter(b, 30 - e.Length, counter);
        }

        if (b.Length == 0)
        {
            b = "_";
        }

        return Join(b, e, false);
    }

    private static string Counter(string text, int max, int counter)
    {
        if (counter == 0)
        {
            return text.Length > max ? text[..max] : text;
        }

        string digits = counter.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        int keep = Math.Max(0, max - digits.Length);
        return (text.Length > keep ? text[..keep] : text) + digits;
    }

    /// <summary>
    /// The Joliet identifier: the name with the characters Joliet forbids (controls and * / : ; ? \) replaced by an underscore and cut
    /// to 64 UCS-2 characters, keeping a short extension. The counter is inserted before the extension to keep names apart.
    /// </summary>
    public static string JolietName(string name, int counter)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(c < ' ' || c is '*' or '/' or ':' or ';' or '?' or '\\' ? '_' : c);
        }

        string clean = sb.ToString();
        (string b, string e) = Split(clean);
        string ext = e.Length > 0 ? "." + e : string.Empty;
        if (ext.Length > 32)
        {
            b = clean;
            ext = string.Empty;
        }

        string digits = counter == 0 ? string.Empty : "_" + counter.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        int room = 64 - ext.Length - digits.Length;
        if (b.Length > room)
        {
            b = b[..room];
        }

        return b + digits + ext;
    }

    /// <summary>
    /// Clause 9.3: records are ordered by file name padded with spaces, then by extension padded with spaces, then by descending
    /// version. A directory identifier has no extension or version.
    /// </summary>
    public static int CompareIso(string a, string b)
    {
        (string na, string ea, int va) = Parts(a);
        (string nb, string eb, int vb) = Parts(b);
        int c = Padded(na, nb);
        if (c != 0)
        {
            return c;
        }

        c = Padded(ea, eb);
        return c != 0 ? c : vb.CompareTo(va);
    }

    private static (string Name, string Ext, int Version) Parts(string id)
    {
        int semi = id.LastIndexOf(';');
        int version = 0;
        if (semi >= 0)
        {
            _ = int.TryParse(id.AsSpan(semi + 1), out version);
            id = id[..semi];
        }

        int dot = id.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (id, string.Empty, version) : (id[..dot], id[(dot + 1)..], version);
    }

    private static int Padded(string a, string b)
    {
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            char x = i < a.Length ? a[i] : ' ';
            char y = i < b.Length ? b[i] : ' ';
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        return 0;
    }
}
