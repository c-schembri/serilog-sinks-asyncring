using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Sinks.Async.Tests.Support;

namespace Serilog.Sinks.Async.Tests;

// Many threads logging into small buffers, so the ring wraps around constantly and the full,
// lapped and closing paths all get exercised. Every event must be delivered exactly once, in its
// thread's order, or accounted for as dropped or rejected. They load every core, so each one runs on its
// own rather than alongside the timing-sensitive tests.
[NotInParallel]
public class RingBufferStressTests
{
    private const int Threads = 16;
    private const int EventsPerThread = 20_000;
    private const long Total = (long)Threads * EventsPerThread;

    [Test]
    [Arguments(64, 1024)] // normal slack
    [Arguments(8, 0)]     // no slack: producers routinely lap the worker and must wait for their slot
    public async Task WhenDroppingEveryEventIsDeliveredInOrderOrCountedAsDropped(int capacity, int slack)
    {
        var inner = new OrderCheckingSink(Threads, spinPerEvent: 20);
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(inner, capacity, blockWhenFull: false, null, slack);
        sink.SetFailureListener(listener);

        Produce(sink);
        sink.Dispose();

        var dropped = ((IAsyncLogEventSinkInspector)sink).DroppedMessagesCount;
        await Assert.That(dropped).IsGreaterThan(0).Because("the test should overload the buffer");
        await Assert.That(inner.Delivered + dropped).IsEqualTo(Total);
        await Assert.That(listener.EventCount).IsEqualTo(dropped);
        await Assert.That(listener.Failures).All(f => f.Kind == LoggingFailureKind.Permanent);
        await Assert.That(inner.OrderViolations).IsEqualTo(0);
    }

    [Test]
    [Arguments(16, 1024)]
    [Arguments(8, 0)]
    public async Task WhenBlockingEveryEventIsDeliveredInOrder(int capacity, int slack)
    {
        var inner = new OrderCheckingSink(Threads);
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(inner, capacity, blockWhenFull: true, null, slack);
        sink.SetFailureListener(listener);

        Produce(sink);
        sink.Dispose();

        await Assert.That(inner.Delivered).IsEqualTo(Total);
        await Assert.That(((IAsyncLogEventSinkInspector)sink).DroppedMessagesCount).IsEqualTo(0);
        await Assert.That(listener.Failures).IsEmpty();
        await Assert.That(inner.OrderViolations).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposingWhileLoggingLosesNothingSilently(bool blockWhenFull)
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

        await Assert.That(inner.Delivered + listener.EventCount).IsEqualTo(attempts.Sum());
        await Assert.That(listener.Failures).Contains(f => f.Kind == LoggingFailureKind.Final);
        await Assert.That(inner.OrderViolations).IsEqualTo(0);
    }

    private static void Produce(ILogEventSink sink)
    {
        var producers = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            for (long n = 0; n < EventsPerThread; n++) sink.Emit(Some.Numbered(t, n));
        })).ToArray();

        foreach (var producer in producers) producer.Start();
        foreach (var producer in producers) producer.Join();
    }
}
