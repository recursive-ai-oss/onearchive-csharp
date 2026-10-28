using System.Diagnostics;
using System.Text;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>A scratch directory that is removed afterwards.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        string parent = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        Root = Path.Combine(parent, "onearchive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(string relative) => Path.Combine(Root, relative);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Builds tar archives byte by byte, independently of the library's own writer, so tests can produce the unusual and the
/// hostile ones: bad names, odd type flags, lying sizes, GNU and pax sparse maps.
/// </summary>
internal sealed class RawTar
{
    private readonly List<byte> _out = [];

    public static byte[] Block() => new byte[512];

    public static void Octal(byte[] block, int at, int len, long value)
    {
        string s = Convert.ToString(value, 8).PadLeft(len - 1, '0');
        Encoding.ASCII.GetBytes(s, 0, s.Length, block, at);
    }

    public static byte[] Header(string name, char type, long size, string? link = null, int mode = 0b110_100_100, string? prefix = null, bool gnu = false, long mtime = 1_700_000_000, int uid = 1000, int gid = 1000, string? uname = "user", string? gname = "group", bool seal = true)
    {
        byte[] b = Block();
        Encoding.UTF8.GetBytes(name).CopyTo(b, 0);
        Octal(b, 100, 8, mode);
        Octal(b, 108, 8, uid);
        Octal(b, 116, 8, gid);
        Octal(b, 124, 12, size);
        Octal(b, 136, 12, mtime);
        b[156] = (byte)type;
        if (link is not null)
        {
            Encoding.UTF8.GetBytes(link).CopyTo(b, 157);
        }

        Encoding.ASCII.GetBytes(gnu ? "ustar  \0" : "ustar\000").CopyTo(b, 257);
        if (!gnu)
        {
            "00"u8.CopyTo(b.AsSpan(263));
        }

        if (uname is not null)
        {
            Encoding.UTF8.GetBytes(uname).CopyTo(b, 265);
        }

        if (gname is not null)
        {
            Encoding.UTF8.GetBytes(gname).CopyTo(b, 297);
        }

        if (prefix is not null)
        {
            Encoding.UTF8.GetBytes(prefix).CopyTo(b, 345);
        }

        if (seal)
        {
            Seal(b);
        }

        return b;
    }

    public static void Seal(byte[] b)
    {
        for (int i = 148; i < 156; i++)
        {
            b[i] = (byte)' ';
        }

        int sum = 0;
        foreach (byte x in b)
        {
            sum += x;
        }

        string s = Convert.ToString(sum, 8).PadLeft(6, '0');
        Encoding.ASCII.GetBytes(s).CopyTo(b, 148);
        b[154] = 0;
        b[155] = (byte)' ';
    }

    public RawTar Entry(string name, char type = '0', byte[]? data = null, string? link = null, int mode = 0b110_100_100, long? size = null, string? prefix = null, bool gnu = false)
    {
        data ??= [];
        if (type == '5' && mode == 0b110_100_100)
        {
            mode = 0b111_101_101;
        }

        _out.AddRange(Header(name, type, size ?? data.Length, link, mode, prefix, gnu));
        Data(data);
        return this;
    }

    public RawTar Data(byte[] data)
    {
        _out.AddRange(data);
        int pad = (512 - (data.Length % 512)) % 512;
        _out.AddRange(new byte[pad]);
        return this;
    }

    public RawTar Pax(params (string Key, string Value)[] records) => Extended('x', Records(records));

    public RawTar GlobalPax(params (string Key, string Value)[] records) => Extended('g', Records(records));

    public RawTar GnuLong(char type, string name) => Extended(type, [.. Encoding.UTF8.GetBytes(name), 0]);

    public RawTar Extended(char type, byte[] data)
    {
        _out.AddRange(Header("PaxHeader/x", type, data.Length));
        Data(data);
        return this;
    }

    public static byte[] Records(IEnumerable<(string Key, string Value)> records)
    {
        var ms = new MemoryStream();
        foreach ((string key, string value) in records)
        {
            string body = " " + key + "=" + value + "\n";
            int len = Encoding.UTF8.GetByteCount(body);
            int total = len + Digits(len);
            if (Digits(total) != Digits(len))
            {
                total++;
            }

            ms.Write(Encoding.UTF8.GetBytes(total.ToString(System.Globalization.CultureInfo.InvariantCulture) + body));
        }

        return ms.ToArray();
    }

    private static int Digits(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

    public RawTar Raw(byte[] bytes)
    {
        _out.AddRange(bytes);
        return this;
    }

    public byte[] End(bool trailer = true)
    {
        if (trailer)
        {
            _out.AddRange(new byte[1024]);
        }

        return [.. _out];
    }
}

internal static class Tools
{
    public static bool InteropEnabled => Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1";

    public static string Run(string file, IEnumerable<string> args, string? workingDirectory = null, bool failOnError = true)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + file);
        var error = new StringBuilder();
        p.ErrorDataReceived += (_, e) => error.AppendLine(e.Data);
        p.BeginErrorReadLine();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (failOnError && p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed ({p.ExitCode}): {error}");
        }

        return output;
    }
}
