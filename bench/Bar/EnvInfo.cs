using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RecursiveAI.Bar;

/// <summary>Where and on what a run happened.</summary>
internal sealed record Machine(
    string Name, string Os, string Cpu, int Cores, string Sdk, string Runtime, string WorkKind, string Arch);

/// <summary>Load average (1 minute) before, during (samples while running) and after a run.</summary>
internal sealed class LoadTrace : IDisposable
{
    private readonly List<double> _samples = [];
    private readonly Timer _timer;

    public LoadTrace()
    {
        Before = EnvInfo.Load1();
        _timer = new Timer(_ =>
        {
            double v = EnvInfo.Load1();
            lock (_samples)
            {
                _samples.Add(v);
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public double Before { get; }
    public double After { get; private set; }

    public double DuringMax
    {
        get
        {
            lock (_samples)
            {
                return _samples.Count == 0 ? Before : _samples.Max();
            }
        }
    }

    public double DuringMean
    {
        get
        {
            lock (_samples)
            {
                return _samples.Count == 0 ? Before : _samples.Average();
            }
        }
    }

    public void Finish() => After = EnvInfo.Load1();

    public void Dispose() => _timer.Dispose();
}

internal static class EnvInfo
{
    public static double Load1()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string[] parts = File.ReadAllText("/proc/loadavg").Split(' ');
                return double.Parse(parts[0], CultureInfo.InvariantCulture);
            }

            // macOS: "{ 1.07 1.20 1.41 }"
            string s = Proc.Sh("sysctl -n vm.loadavg").Stdout.Trim().Trim('{', '}').Trim();
            return double.Parse(s.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or FormatException or IndexOutOfRangeException)
        {
            return double.NaN;
        }
    }

    public static Machine Describe(string workDir)
    {
        string cpu = "unknown";
        if (OperatingSystem.IsLinux())
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal))
                {
                    cpu = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    break;
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            cpu = Proc.Sh("sysctl -n machdep.cpu.brand_string").Stdout.Trim();
        }

        string sdk = "unknown";
        if (Environment.ProcessPath is { } host)
        {
            sdk = Proc.FirstLine(host, "--version");
        }

        bool ram = OperatingSystem.IsLinux() && Proc.Sh("df -P " + Proc.Q(workDir) + " | tail -1").Stdout.Contains("tmpfs", StringComparison.Ordinal);
        return new Machine(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            cpu,
            Environment.ProcessorCount,
            sdk,
            RuntimeInformation.FrameworkDescription,
            ram ? "tmpfs (RAM)" : "page cache (warm files)",
            RuntimeInformation.OSArchitecture.ToString());
    }

    /// <summary>Path of this program for spawning measurement children.</summary>
    public static (string Host, string Dll) Self() =>
        (Environment.ProcessPath ?? "dotnet", typeof(EnvInfo).Assembly.Location);

    public static string Elapsed(Stopwatch sw) => sw.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
