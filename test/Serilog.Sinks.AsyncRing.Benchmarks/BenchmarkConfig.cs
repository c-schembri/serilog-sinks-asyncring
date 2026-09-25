using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace AsyncRingBenchmarks;

public sealed class BenchmarkConfig : ManualConfig
{
    /// <summary>Measured iterations per benchmark (after the warm-up ones).</summary>
    public const int MeasuredIterations = 15;

    public BenchmarkConfig()
    {
        // Each invocation logs a whole batch from several threads, and the queue is drained between
        // iterations, so every iteration is a single invocation. BenchmarkDotNet decides how many warm-up
        // iterations are needed (enough for the JIT to finish optimizing Serilog's code).
        var job = Job.Default
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithIterationCount(MeasuredIterations);

        // Every benchmark runs on both LTS runtimes (or just those in BENCH_RUNTIMES, e.g. "net10.0");
        // Ratio compares the sinks within each one.
        var runtimes = (Environment.GetEnvironmentVariable("BENCH_RUNTIMES") ?? "net8.0,net10.0")
            .Split([','], StringSplitOptions.RemoveEmptyEntries);
        foreach (var runtime in runtimes)
        {
            AddJob(job.WithRuntime(runtime.Trim() switch
            {
                "net8.0" => CoreRuntime.Core80,
                "net10.0" => CoreRuntime.Core10_0,
                var other => throw new ArgumentException($"Unsupported runtime '{other}' in BENCH_RUNTIMES.")
            }));
        }

        AddColumn(
            new StatsColumn(0, "Delivered",
                "Share of logged events that reached the wrapped sink; the rest were dropped because the buffer was full",
                stats => $"{stats.DeliveredPercentage:0.#}%"),
            new StatsColumn(1, "Allocations/s",
                "Objects allocated per second while logging, at the rate implied by Mean (estimated from the runtime's allocation sampling)",
                (stats, eventsPerSecond) => Count(stats.Resources.AllocationsPerEvent * eventsPerSecond)),
            new StatsColumn(2, "Allocated/s",
                "Bytes allocated per second while logging, at the rate implied by Mean",
                (stats, eventsPerSecond) => Bytes(stats.Resources.AllocatedBytesPerEvent * eventsPerSecond) + "/s"),
            new StatsColumn(3, "Allocated/event",
                "Bytes allocated per logged event (by Serilog and the sink together)",
                stats => Bytes(stats.Resources.AllocatedBytesPerEvent)),
            new StatsColumn(4, "Memory avg",
                "Managed memory in use while logging, averaged over time: the heap after each garbage collection, above what it was before the logger existed",
                stats => Bytes(stats.Resources.MemoryAverage)),
            new StatsColumn(5, "Memory peak",
                "Highest managed memory in use while logging (the heap after a garbage collection, above what it was before the logger existed)",
                stats => Bytes(stats.Resources.MemoryPeak)));
    }

    private static string Count(double value) => value switch
    {
        >= 1e9 => (value / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + "G",
        >= 1e6 => (value / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1e3 => (value / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString("0", CultureInfo.InvariantCulture)
    };

    private static string Bytes(double value) => value switch
    {
        >= 1L << 30 => (value / (1L << 30)).ToString("0.00", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (value / (1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (value / (1L << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => value.ToString("0", CultureInfo.InvariantCulture) + " B"
    };
}

/// <summary>A summary column showing one of the <see cref="BenchmarkStats"/> the benchmark process recorded.</summary>
/// <remarks>Per-second values combine per-event figures with the event rate implied by BenchmarkDotNet's Mean.</remarks>
public sealed class StatsColumn(int priority, string name, string legend, Func<BenchmarkStats, double, string> format) : IColumn
{
    public StatsColumn(int priority, string name, string legend, Func<BenchmarkStats, string> format)
        : this(priority, name, legend, (stats, _) => format(stats))
    {
    }

    public string Id => $"{nameof(StatsColumn)}.{name}";
    public string ColumnName => name;
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => priority;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => legend;

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var threads = (int)benchmarkCase.Parameters[nameof(LoggingBenchmark.Threads)]!;
        var runtime = benchmarkCase.Job.Environment.Runtime?.MsBuildMoniker ?? BenchmarkStats.CurrentRuntime;
        var stats = BenchmarkStats.Load(
            runtime, benchmarkCase.Descriptor.Type.Name, benchmarkCase.Descriptor.WorkloadMethod.Name, threads);
        if (stats is null) return "?";

        // Mean is nanoseconds per operation, and an operation stands for EventsPerOperation events.
        var mean = summary[benchmarkCase]?.ResultStatistics?.Mean;
        var eventsPerSecond = mean is > 0 ? stats.EventsPerOperation * 1e9 / mean.Value : 0;
        return format(stats, eventsPerSecond);
    }

    public override string ToString() => ColumnName;
}
