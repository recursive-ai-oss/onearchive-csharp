using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RecursiveAI.Bar;

/// <summary>Outcome of one process run.</summary>
internal sealed record ProcResult(int ExitCode, double Seconds, string Stdout, string Stderr, long PeakRssBytes);

internal static class Proc
{
    public static readonly bool IsMac = OperatingSystem.IsMacOS();

    /// <summary>Single-quotes a string for sh.</summary>
    public static string Q(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Runs a shell command line; with <paramref name="measureMemory"/> it is wrapped in the platform's time utility.</summary>
    public static ProcResult Sh(string commandLine, bool measureMemory = false)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (measureMemory)
        {
            psi.FileName = "/usr/bin/time";
            psi.ArgumentList.Add(IsMac ? "-l" : "-v");
            psi.ArgumentList.Add("/bin/sh");
        }
        else
        {
            psi.FileName = "/bin/sh";
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(commandLine);

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + psi.FileName);
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        sw.Stop();
        string stderr = err.GetAwaiter().GetResult();
        return new ProcResult(p.ExitCode, sw.Elapsed.TotalSeconds, output, stderr, measureMemory ? ParsePeakRss(stderr) : 0);
    }

    /// <summary>Peak resident set size from /usr/bin/time -v (kilobytes) or -l (bytes) output.</summary>
    public static long ParsePeakRss(string timeOutput)
    {
        var m = Regex.Match(timeOutput, @"Maximum resident set size \(kbytes\):\s*(\d+)");
        if (m.Success)
        {
            return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1024;
        }

        m = Regex.Match(timeOutput, @"(\d+)\s+maximum resident set size");
        return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    public static string FirstLine(string tool, string args)
    {
        try
        {
            var r = Sh(Q(tool) + " " + args + " 2>&1");
            using var reader = new StringReader(r.Stdout + r.Stderr);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Trim().Length > 0)
                {
                    return line.Trim();
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        return "unknown";
    }
}
