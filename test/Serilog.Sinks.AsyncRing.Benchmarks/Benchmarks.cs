using BenchmarkDotNet.Attributes;

namespace AsyncRingBenchmarks;

/// <summary>
/// What a logging call costs the thread that makes it, while <see cref="LoggingBenchmark.Threads"/> threads
/// log at once. The buffer is big enough that nothing is dropped, and it's drained between iterations.
/// Mean is per call, as seen by each logging thread.
/// </summary>
[Config(typeof(BenchmarkConfig))]
[ThreadingDiagnoser]
public class LogCallBenchmarks : LoggingBenchmark
{
    private const int EventsPerThread = 100_000;

    protected override int BufferSize => 2_000_000; // more than 16 threads x 100,000 events

    [Benchmark(Baseline = true, OperationsPerInvoke = EventsPerThread)]
    public void SerilogSinksAsync() => LogOnAllThreads(EventsPerThread);

    [Benchmark(OperationsPerInvoke = EventsPerThread)]
    public void AsyncRing() => LogOnAllThreads(EventsPerThread);

    [Benchmark(OperationsPerInvoke = EventsPerThread)]
    public void NoQueue() => LogOnAllThreads(EventsPerThread);

    [IterationCleanup]
    public void Drain() => WaitUntilDrained();
}

/// <summary>
/// How long it takes to log a batch of events from <see cref="LoggingBenchmark.Threads"/> threads and have
/// every one of them reach the wrapped sink. Mean is per event, so 1 / Mean is the sustained throughput.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class ThroughputBenchmarks : LoggingBenchmark
{
    private const int TotalEvents = 1_600_000;

    protected override int BufferSize => 2_000_000;

    [Benchmark(Baseline = true, OperationsPerInvoke = TotalEvents)]
    public void SerilogSinksAsync() => LogAndDrain();

    [Benchmark(OperationsPerInvoke = TotalEvents)]
    public void AsyncRing() => LogAndDrain();

    [Benchmark(OperationsPerInvoke = TotalEvents)]
    public void NoQueue() => LogAndDrain();

    private void LogAndDrain()
    {
        LogOnAllThreads(TotalEvents / Threads);
        WaitUntilDrained();
    }
}

/// <summary>
/// Threads logging flat out into the default 10,000-event buffer, which drops events when it's full.
/// Mean is per call, as seen by each logging thread; Delivered is the share of events that reached the
/// sink.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class OverloadBenchmarks : LoggingBenchmark
{
    private const int EventsPerThread = 100_000;

    protected override int BufferSize => 10_000;

    [Benchmark(Baseline = true, OperationsPerInvoke = EventsPerThread)]
    public void SerilogSinksAsync() => LogOnAllThreads(EventsPerThread);

    [Benchmark(OperationsPerInvoke = EventsPerThread)]
    public void AsyncRing() => LogOnAllThreads(EventsPerThread);

    [Benchmark(OperationsPerInvoke = EventsPerThread)]
    public void NoQueue() => LogOnAllThreads(EventsPerThread);

    [IterationCleanup]
    public void Drain() => WaitUntilDrained();
}
