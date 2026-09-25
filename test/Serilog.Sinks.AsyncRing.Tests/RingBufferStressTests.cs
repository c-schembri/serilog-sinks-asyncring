using System.Linq;
using System.Threading;
using Serilog.Core;
using Serilog.Sinks.Async.Tests.Support;
using Xunit;

namespace Serilog.Sinks.Async.Tests;

// Many threads logging into small buffers, so the ring wraps around constantly and the full,
// lapped and closing paths all get exercised. Every event must be delivered exactly once, in its
// thread's order, or accounted for as dropped or rejected.
public class RingBufferStressTests
{
    const int Threads = 16;
    const int EventsPerThread = 20_000;
    const long Total = (long)Threads * EventsPerThread;

    [Theory]
    [InlineData(64, 1024)] // normal slack
    [InlineData(8, 0)]     // no slack: producers routinely lap the worker and must wait for their slot
    public void WhenDroppingEveryEventIsDeliveredInOrderOrCountedAsDropped(int capacity, int slack)
    {
        var inner = new OrderCheckingSink(Threads, spinPerEvent: 20);
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(inner, capacity, blockWhenFull: false, null, slack);
        sink.SetFailureListener(listener);

        Produce(sink);
        sink.Dispose();

        var dropped = ((IAsyncLogEventSinkInspector)sink).DroppedMessagesCount;
        Assert.True(dropped > 0, "The test should overload the buffer.");
        Assert.Equal(Total, inner.Delivered + dropped);
        Assert.Equal(dropped, listener.EventCount);
        Assert.All(listener.Failures, f => Assert.Equal(LoggingFailureKind.Permanent, f.Kind));
        Assert.Equal(0, inner.OrderViolations);
    }

    [Theory]
    [InlineData(16, 1024)]
    [InlineData(8, 0)]
    public void WhenBlockingEveryEventIsDeliveredInOrder(int capacity, int slack)
    {
        var inner = new OrderCheckingSink(Threads);
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(inner, capacity, blockWhenFull: true, null, slack);
        sink.SetFailureListener(listener);

        Produce(sink);
        sink.Dispose();

        Assert.Equal(Total, inner.Delivered);
        Assert.Equal(0, ((IAsyncLogEventSinkInspector)sink).DroppedMessagesCount);
        Assert.Empty(listener.Failures);
        Assert.Equal(0, inner.OrderViolations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposingWhileLoggingLosesNothingSilently(bool blockWhenFull)
    {
        var inner = new OrderCheckingSink(Threads);
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(inner, 32, blockWhenFull, null);
        sink.SetFailureListener(listener);

        var attempts = new long[Threads];
        var stop = 0;
        using var started = new CountdownEvent(Threads);
        var producers = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            started.Signal();
            for (long n = 0; Volatile.Read(ref stop) == 0; n++)
            {
                attempts[t]++;
                sink.Emit(Some.Numbered(t, n));
            }
        })).ToArray();

        foreach (var producer in producers) producer.Start();
        started.Wait();
        Thread.Sleep(100);
        sink.Dispose();       // while all threads are still logging
        Thread.Sleep(50);
        Volatile.Write(ref stop, 1);
        foreach (var producer in producers) producer.Join();

        Assert.Equal(attempts.Sum(), inner.Delivered + listener.EventCount);
        Assert.Contains(listener.Failures, f => f.Kind == LoggingFailureKind.Final);
        Assert.Equal(0, inner.OrderViolations);
    }

    static void Produce(ILogEventSink sink)
    {
        var producers = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            for (long n = 0; n < EventsPerThread; n++) sink.Emit(Some.Numbered(t, n));
        })).ToArray();

        foreach (var producer in producers) producer.Start();
        foreach (var producer in producers) producer.Join();
    }
}
