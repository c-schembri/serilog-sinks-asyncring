extern alias upstream;

using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Upstream = upstream::Serilog.LoggerConfigurationAsyncExtensions;

// Compares Serilog.Sinks.Async 2.1.0 with this package: several threads log as fast as they can into
// a sink that does nothing, so the cost measured is the logging pipeline plus the queue.
//
//   dotnet run -c Release                  1, 4 and 16 threads, buffer big enough that nothing drops
//   dotnet run -c Release -- 8 32          other thread counts
//   BENCH_BUFFER=10000 dotnet run ...      the default buffer size, so events drop under overload
//
// Each measurement runs in a fresh process: when they share one, the heap left behind by one sink
// changes how the garbage collector treats the next, which skews the comparison.

const int Total = 2_000_000;
const int Repetitions = 5;

var configs = new (string Name, Func<ILogEventSink, int, Logger> Create)[]
{
    ("no queue (baseline)", (sink, _) => new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger()),
    ("Serilog.Sinks.Async", (sink, buffer) => Upstream.Async(new LoggerConfiguration().WriteTo, a => a.Sink(sink), buffer).CreateLogger()),
    ("AsyncRing",           (sink, buffer) => new LoggerConfiguration().WriteTo.Async(a => a.Sink(sink), buffer).CreateLogger()),
};

var bufferSize = int.TryParse(Environment.GetEnvironmentVariable("BENCH_BUFFER"), out var b) ? b : Total;

if (args is ["--child", var configArg, var threadsArg])
{
    Measure(configs[int.Parse(configArg)], int.Parse(threadsArg), bufferSize);
    return;
}

var threadCounts = args.Length > 0 ? args.Select(int.Parse).ToArray() : [1, 4, 16];
Console.WriteLine($"{Environment.ProcessorCount} logical cores, {Total:N0} events, buffer {bufferSize:N0}, best of {Repetitions}");
Console.WriteLine();
Console.WriteLine("threads  sink                  ns/call  delivered M/s  delivered %");
foreach (var threads in threadCounts)
{
    for (var config = 0; config < configs.Length; config++)
    {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--child {config} {threads}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        Console.Write(child.StandardOutput.ReadToEnd());
        child.WaitForExit();
    }

    Console.WriteLine();
}

static void Measure((string Name, Func<ILogEventSink, int, Logger> Create) config, int threads, int bufferSize)
{
    double bestNs = double.MaxValue, bestRate = 0, deliveredPct = 0;
    for (var rep = 0; rep < Repetitions; rep++)
    {
        var sink = new CountingSink();
        var log = config.Create(sink, bufferSize);
        var perThread = Total / threads;
        var events = (double)perThread * threads;

        var stopwatch = Stopwatch.StartNew();
        RunOnThreads(threads, () =>
        {
            for (var i = 0; i < perThread; i++) log.Information("Order {OrderId} for {Customer}", i, "acme");
        });
        var produced = stopwatch.Elapsed;
        log.Dispose(); // waits for the queue to drain
        var drained = stopwatch.Elapsed;

        var ns = produced.TotalNanoseconds / perThread;
        if (ns < bestNs)
        {
            bestNs = ns;
            deliveredPct = 100 * sink.Count / events;
        }
        bestRate = Math.Max(bestRate, sink.Count / drained.TotalSeconds / 1e6);
    }

    Console.WriteLine($"  {threads,3}    {config.Name,-20} {bestNs,8:F0}  {bestRate,13:F2}  {deliveredPct,10:F1}");
}

static void RunOnThreads(int threads, Action body)
{
    using var start = new Barrier(threads);
    var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
    {
        start.SignalAndWait();
        body();
    })).ToArray();
    foreach (var worker in workers) worker.Start();
    foreach (var worker in workers) worker.Join();
}

sealed class CountingSink : ILogEventSink
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public void Emit(LogEvent logEvent) => Interlocked.Increment(ref _count);
}
