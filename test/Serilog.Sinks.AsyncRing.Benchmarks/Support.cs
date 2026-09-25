extern alias upstream;

using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using RingInspector = Serilog.Sinks.Async.IAsyncLogEventSinkInspector;
using RingMonitor = Serilog.Sinks.Async.IAsyncLogEventSinkMonitor;
using UpstreamInspector = upstream::Serilog.Sinks.Async.IAsyncLogEventSinkInspector;
using UpstreamMonitorInterface = upstream::Serilog.Sinks.Async.IAsyncLogEventSinkMonitor;

namespace AsyncRingBenchmarks;

/// <summary>A sink that only counts the events it receives.</summary>
public sealed class CountingSink : ILogEventSink
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public void Emit(LogEvent logEvent) => Interlocked.Increment(ref _count);
}

/// <summary>Reads how many events Serilog.Sinks.Async has dropped.</summary>
public sealed class UpstreamMonitor : UpstreamMonitorInterface
{
    private UpstreamInspector? _inspector;

    public long Dropped => _inspector?.DroppedMessagesCount ?? 0;

    public void StartMonitoring(UpstreamInspector inspector) => _inspector = inspector;

    public void StopMonitoring(UpstreamInspector inspector) { }
}

/// <summary>Reads how many events AsyncRing has dropped.</summary>
public sealed class AsyncRingMonitor : RingMonitor
{
    private RingInspector? _inspector;

    public long Dropped => _inspector?.DroppedMessagesCount ?? 0;

    public void StartMonitoring(RingInspector inspector) => _inspector = inspector;

    public void StopMonitoring(RingInspector inspector) { }
}

/// <summary>
/// A fixed set of threads that run the same work together on request, so creating threads isn't
/// part of what's measured.
/// </summary>
public sealed class ThreadRunner : IDisposable
{
    private readonly Thread[] _threads;
    private readonly Barrier _start;
    private readonly Barrier _finish;
    private Action _work = () => { };
    private volatile bool _stopping;

    public ThreadRunner(int count)
    {
        _start = new Barrier(count + 1);
        _finish = new Barrier(count + 1);
        _threads = Enumerable.Range(0, count)
            .Select(i => new Thread(Loop) { IsBackground = true, Name = $"Logging thread {i}" })
            .ToArray();
        foreach (var thread in _threads) thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on every thread at once, and returns when they've all finished.</summary>
    public void Run(Action work)
    {
        _work = work;
        _start.SignalAndWait();
        _finish.SignalAndWait();
    }

    public void Dispose()
    {
        _stopping = true;
        _start.SignalAndWait();
        foreach (var thread in _threads) thread.Join();
        _start.Dispose();
        _finish.Dispose();
    }

    private void Loop()
    {
        while (true)
        {
            _start.SignalAndWait();
            if (_stopping) return;
            _work();
            _finish.SignalAndWait();
        }
    }
}

/// <summary>
/// What a benchmark process measured: events logged and delivered, and the resources used. BenchmarkDotNet
/// runs each benchmark in its own process, so these are written to a file there and read back by the summary
/// columns in <see cref="BenchmarkConfig"/>.
/// </summary>
public sealed record BenchmarkStats(long Logged, long Delivered, int EventsPerOperation, ResourceStats Resources)
{
    public const string DirectoryVariable = "ASYNCRING_BENCHMARK_STATS";

    /// <summary>The runtime this process is running on, in the same form as a target framework (<c>net8.0</c>).</summary>
    public static string CurrentRuntime => $"net{Environment.Version.Major}.{Environment.Version.Minor}";

    public double DeliveredPercentage => Logged > 0 ? 100.0 * Delivered / Logged : 0;

    public static void Save(string runtime, string benchmark, string sink, int threads, BenchmarkStats stats)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrEmpty(directory) || stats.Logged == 0) return;

        var r = stats.Resources;
        var values = new double[] { stats.Logged, stats.Delivered, stats.EventsPerOperation, r.AllocatedBytesPerEvent, r.AllocationsPerEvent, r.MemoryAverage, r.MemoryPeak };
        File.WriteAllText(Path.Combine(directory, FileName(runtime, benchmark, sink, threads)),
            string.Join(" ", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
    }

    public static BenchmarkStats? Load(string runtime, string benchmark, string sink, int threads)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrEmpty(directory)) return null;

        var path = Path.Combine(directory, FileName(runtime, benchmark, sink, threads));
        if (!File.Exists(path)) return null;

        var v = File.ReadAllText(path).Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return new BenchmarkStats((long)v[0], (long)v[1], (int)v[2], new ResourceStats(v[3], v[4], v[5], v[6]));
    }

    private static string FileName(string runtime, string benchmark, string sink, int threads) =>
        $"{runtime}.{benchmark}.{sink}.{threads}.txt";
}
