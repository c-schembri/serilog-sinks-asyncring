extern alias upstream;

using BenchmarkDotNet.Attributes;
using Serilog;
using Serilog.Core;
using Upstream = upstream::Serilog.LoggerConfigurationAsyncExtensions;

namespace AsyncRingBenchmarks;

/// <summary>
/// Shared set-up for the benchmarks: a logger whose sink only counts events, and a pool of
/// <see cref="Threads"/> threads that log through it at the same time.
/// </summary>
/// <remarks>
/// BenchmarkDotNet runs each benchmark case in its own process, so only the logger that case uses
/// is created. Serilog.Sinks.Async is the baseline, so the Ratio column is relative to it.
/// </remarks>
public abstract class LoggingBenchmark
{
    protected const string NoQueueTarget = "NoQueue";
    protected const string SerilogSinksAsyncTarget = "SerilogSinksAsync";
    protected const string AsyncRingTarget = "AsyncRing";

    private readonly CountingSink _sink = new();
    private ThreadRunner? _threads;
    private Logger? _logger;
    private Func<long> _dropped = () => 0;
    private string _sinkName = "";
    private long _logged;

    [Params(1, 4, 16)]
    public int Threads { get; set; }

    /// <summary>The <c>bufferSize</c> passed to <c>WriteTo.Async()</c>.</summary>
    protected abstract int BufferSize { get; }

    [GlobalSetup(Target = NoQueueTarget)]
    public void SetUpNoQueue() =>
        SetUp(NoQueueTarget, new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger(), () => 0);

    [GlobalSetup(Target = SerilogSinksAsyncTarget)]
    public void SetUpSerilogSinksAsync()
    {
        var monitor = new UpstreamMonitor();
        var logger = Upstream.Async(new LoggerConfiguration().WriteTo, a => a.Sink(_sink), BufferSize, monitor: monitor).CreateLogger();
        SetUp(SerilogSinksAsyncTarget, logger, () => monitor.Dropped);
    }

    [GlobalSetup(Target = AsyncRingTarget)]
    public void SetUpAsyncRing()
    {
        var monitor = new AsyncRingMonitor();
        var logger = new LoggerConfiguration().WriteTo.Async(a => a.Sink(_sink), BufferSize, monitor: monitor).CreateLogger();
        SetUp(AsyncRingTarget, logger, () => monitor.Dropped);
    }

    [GlobalCleanup]
    public void CleanUp()
    {
        _logger?.Dispose(); // flushes anything still queued
        _threads?.Dispose();
        DeliveryStats.Save(BenchmarkClassName(), _sinkName, Threads, _logged, _sink.Count);
    }

    // BenchmarkDotNet runs a generated subclass of the benchmark class; the stats are keyed by the real one.
    private string BenchmarkClassName()
    {
        var type = GetType();
        while (type.Assembly != typeof(LoggingBenchmark).Assembly) type = type.BaseType!;
        return type.Name;
    }

    /// <summary>Has every thread log <paramref name="eventsPerThread"/> events, and returns once they all have.</summary>
    protected void LogOnAllThreads(int eventsPerThread)
    {
        var logger = _logger!;
        _threads!.Run(() =>
        {
            for (var i = 0; i < eventsPerThread; i++)
                logger.Information("Order {OrderId} for {Customer}", i, "acme");
        });
        _logged += (long)eventsPerThread * Threads;
    }

    /// <summary>Waits until every event logged so far has either reached the sink or been dropped.</summary>
    protected void WaitUntilDrained()
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        var spinner = new SpinWait();
        while (_sink.Count + _dropped() < _logged)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Only {_sink.Count + _dropped():N0} of {_logged:N0} events were written or dropped.");
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    private void SetUp(string sinkName, Logger logger, Func<long> dropped)
    {
        _sinkName = sinkName;
        _logger = logger;
        _dropped = dropped;
        _threads = new ThreadRunner(Threads);
    }
}
