using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;

namespace Serilog.Sinks.Async.Tests.Support;

static class Some
{
    public static LogEvent Event() =>
        new(DateTimeOffset.Now, LogEventLevel.Information, null, MessageTemplate.Empty, []);

    // An event tagged with the producing thread's index and its sequence number on that thread.
    public static LogEvent Numbered(int thread, long number) =>
        new(DateTimeOffset.Now, LogEventLevel.Information, null, MessageTemplate.Empty,
        [
            new LogEventProperty("T", new ScalarValue(thread)),
            new LogEventProperty("N", new ScalarValue(number))
        ]);
}

// Throws for the first `failFirst` events, then counts the rest.
class FlakySink(int failFirst) : ILogEventSink
{
    private int _seen;
    private int _delivered;

    public int Delivered => Volatile.Read(ref _delivered);

    public void Emit(LogEvent logEvent)
    {
        if (Interlocked.Increment(ref _seen) <= failFirst)
            throw new InvalidOperationException("Transient sink failure.");
        Interlocked.Increment(ref _delivered);
    }
}

class ThrowingFailureListener : ILoggingFailureListener
{
    public void OnLoggingFailed(object sender, LoggingFailureKind kind, string message, IReadOnlyCollection<LogEvent> events, Exception exception)
    {
        throw new InvalidOperationException("Failure listener failure.");
    }
}

// Records the kind and event count of every failure it's told about.
class RecordingFailureListener : ILoggingFailureListener
{
    private readonly object _sync = new();
    private readonly List<(LoggingFailureKind Kind, string Message, int Events)> _failures = [];

    public IReadOnlyList<(LoggingFailureKind Kind, string Message, int Events)> Failures
    {
        get
        {
            lock (_sync) return _failures.ToList();
        }
    }

    public long EventCount
    {
        get
        {
            lock (_sync) return _failures.Sum(f => (long)f.Events);
        }
    }

    public void OnLoggingFailed(object sender, LoggingFailureKind kind, string message, IReadOnlyCollection<LogEvent> events, Exception exception)
    {
        lock (_sync) _failures.Add((kind, message, events?.Count ?? 0));
    }
}

class DisposeCountingSink : ILogEventSink, IDisposable
#if NET6_0_OR_GREATER
    , IAsyncDisposable
#endif
{
    public int Disposes;
    public int AsyncDisposes = 0; // only incremented on targets with IAsyncDisposable

    public void Emit(LogEvent logEvent) { }

    public void Dispose() => Interlocked.Increment(ref Disposes);

#if NET6_0_OR_GREATER
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref AsyncDisposes);
        return default;
    }
#endif
}

class FailureListenerRecordingSink : ILogEventSink, ISetLoggingFailureListener
{
    public ILoggingFailureListener Listener;

    public void Emit(LogEvent logEvent) { }

    public void SetFailureListener(ILoggingFailureListener failureListener) => Listener = failureListener;
}

// Blocks inside Emit until the gate is opened.
class GatedSink(ManualResetEventSlim gate) : ILogEventSink
{
    private readonly ManualResetEventSlim _entered = new(false);
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

    public void Emit(LogEvent logEvent)
    {
        _entered.Set();
        gate.Wait();
        Interlocked.Increment(ref _count);
    }
}

// Checks events produced by Some.Numbered: every thread's events must arrive in the order that
// thread logged them, with no duplicates. Only the worker thread calls Emit, so no locking.
class OrderCheckingSink(int threads, int spinPerEvent = 0) : ILogEventSink
{
    private readonly long[] _last = Enumerable.Repeat(-1L, threads).ToArray();

    public long Delivered { get; private set; }
    public long OrderViolations { get; private set; }

    public void Emit(LogEvent logEvent)
    {
        var thread = (int)((ScalarValue)logEvent.Properties["T"]).Value;
        var number = (long)((ScalarValue)logEvent.Properties["N"]).Value;
        if (number <= _last[thread]) OrderViolations++;
        _last[thread] = number;
        Delivered++;

        if (spinPerEvent > 0) Thread.SpinWait(spinPerEvent);
    }
}
