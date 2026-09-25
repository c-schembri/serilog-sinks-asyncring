using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace AsyncRingBenchmarks;

public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        // Each invocation logs a whole batch from several threads, and the queue is drained between
        // iterations, so every iteration is a single invocation. BenchmarkDotNet decides how many warm-up
        // iterations are needed (enough for the JIT to finish optimizing Serilog's code).
        var job = Job.Default
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithIterationCount(15);

        // Every benchmark runs on both LTS runtimes; Ratio compares the sinks within each one.
        AddJob(job.WithRuntime(CoreRuntime.Core80));
        AddJob(job.WithRuntime(CoreRuntime.Core10_0));

        AddColumn(new DeliveredColumn());
    }
}

/// <summary>The share of logged events that reached the wrapped sink rather than being dropped.</summary>
public sealed class DeliveredColumn : IColumn
{
    public string Id => nameof(DeliveredColumn);
    public string ColumnName => "Delivered";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Share of logged events that reached the wrapped sink; the rest were dropped because the buffer was full";

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var threads = (int)benchmarkCase.Parameters[nameof(LoggingBenchmark.Threads)]!;
        var runtime = benchmarkCase.Job.Environment.Runtime?.MsBuildMoniker ?? DeliveryStats.CurrentRuntime;
        var percentage = DeliveryStats.LoadPercentage(
            runtime, benchmarkCase.Descriptor.Type.Name, benchmarkCase.Descriptor.WorkloadMethod.Name, threads);
        return percentage is { } value ? $"{value:0.#}%" : "?";
    }

    public override string ToString() => ColumnName;
}
