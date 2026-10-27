using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace RecursiveAI.Bench.Micro;

// Runs in-process so that the benchmark measures whichever configuration (Release or Checked) the project was built in.
//   dotnet run -c Release --project bench/Micro -- --filter '*Checksum*'
internal static class Program
{
    private static int Main(string[] args)
    {
        IConfig config = DefaultConfig.Instance
            .AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance))
            .AddColumn(new GigabytesPerSecondColumn());
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        return 0;
    }
}

/// <summary>Throughput in GB/s (10^9 bytes) from the mean time and the benchmark's Size parameter.</summary>
internal sealed class GigabytesPerSecondColumn : IColumn
{
    public string Id => nameof(GigabytesPerSecondColumn);
    public string ColumnName => "GB/s";
    public string Legend => "Size / mean time, in 10^9 bytes per second";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase)
    {
        var report = summary[benchmarkCase];
        object? size = benchmarkCase.Parameters["Size"];
        if (report?.ResultStatistics is null || size is null)
        {
            return "n/a";
        }

        return (Convert.ToDouble(size, System.Globalization.CultureInfo.InvariantCulture) / report.ResultStatistics.Mean).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }

    public string GetValue(Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase, SummaryStyle style) => GetValue(summary, benchmarkCase);
}
