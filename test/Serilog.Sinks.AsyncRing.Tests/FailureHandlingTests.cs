using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Sinks.Async.Tests.Support;

namespace Serilog.Sinks.Async.Tests;

// Regression tests for failure-path bugs found in Serilog.Sinks.Async 2.1.0.
public class FailureHandlingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ThrowingFailureListenerDoesNotStopTheWorker()
    {
        var inner = new FlakySink(failFirst: 1);
        using (var sink = new BackgroundWorkerSink(inner, 100, false, null))
        {
            sink.SetFailureListener(new ThrowingFailureListener());
            for (var i = 0; i < 20; i++) sink.Emit(Some.Event());
        }

        await Assert.That(inner.Delivered).IsEqualTo(19);
    }

    [Test]
    public async Task FallbackChainWithThrowingFallbackKeepsDeliveringToPrimary()
    {
        var primary = new FlakySink(failFirst: 1);
        using (var log = new LoggerConfiguration()
                   .WriteTo.FallbackChain(
                       wt => wt.Async(a => a.Sink(primary), bufferSize: 100),
                       wt => wt.Sink(new FlakySink(failFirst: int.MaxValue)))
                   .CreateLogger())
        {
            for (var i = 0; i < 20; i++) log.Information("Event {N}", i);
        }

        await Assert.That(primary.Delivered).IsEqualTo(19);
    }

    [Test]
    public async Task WrappedSinkIsDisposedExactlyOnce()
    {
        var inner = new DisposeCountingSink();
        var log = new LoggerConfiguration().WriteTo.Async(a => a.Sink(inner)).CreateLogger();
        log.Information("Hello");

        log.Dispose();

        await Assert.That(inner.Disposes).IsEqualTo(1);
        await Assert.That(inner.AsyncDisposes).IsEqualTo(0);
    }

#if NET6_0_OR_GREATER
    [Test]
    public async Task WrappedSinkIsDisposedExactlyOnceAsynchronously()
    {
        var inner = new DisposeCountingSink();
        var log = new LoggerConfiguration().WriteTo.Async(a => a.Sink(inner)).CreateLogger();
        log.Information("Hello");

        await log.DisposeAsync();

        await Assert.That(inner.Disposes).IsEqualTo(0);
        await Assert.That(inner.AsyncDisposes).IsEqualTo(1);
    }
#endif

    [Test]
    public async Task DisposingTwiceDisposesTheWrappedSinkOnce()
    {
        var inner = new DisposeCountingSink();
        var monitor = new DummyMonitor();
        var sink = new BackgroundWorkerSink(inner, 10, false, monitor);

        sink.Dispose();
        sink.Dispose();

        await Assert.That(inner.Disposes).IsEqualTo(1);
        await Assert.That(monitor.Inspector).IsNull();
    }

    [Test]
    public async Task FailureListenerIsForwardedToTheWrappedSink()
    {
        var inner = new FailureListenerRecordingSink();
        var listener = new RecordingFailureListener();

        using (new LoggerConfiguration()
                   .WriteTo.Fallible(wt => wt.Async(a => a.Sink(inner)), listener)
                   .CreateLogger())
        {
            await Assert.That(inner.Listener).IsSameReferenceAs(listener);
        }
    }

    [Test]
    public async Task EmitAfterDisposeIsReportedAsFinal()
    {
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(new MemorySink(), 10, false, null);
        sink.SetFailureListener(listener);
        sink.Dispose();

        sink.Emit(Some.Event());

        var failure = await Assert.That(listener.Failures).HasSingleItem();
        await Assert.That(failure.Kind).IsEqualTo(LoggingFailureKind.Final);
        await Assert.That(failure.Events).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeReleasesProducersBlockedOnAFullBuffer()
    {
        using var gate = new ManualResetEventSlim(false);
        var inner = new GatedSink(gate);
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(inner, 1, blockWhenFull: true, null);
        sink.SetFailureListener(listener);

        sink.Emit(Some.Event());                     // taken by the worker, which then waits at the gate
        await Assert.That(inner.WaitUntilEntered(Timeout)).IsTrue();
        sink.Emit(Some.Event());                     // fills the one-event buffer
        var blocked = Task.Run(() => sink.Emit(Some.Event()));
        await Assert.That(await CompletesWithin(blocked, TimeSpan.FromMilliseconds(200))).IsFalse()
            .Because("the producer should be blocked");

        var disposing = Task.Run(sink.Dispose);
        await Assert.That(await CompletesWithin(blocked, Timeout)).IsTrue()
            .Because("Dispose should release the blocked producer");
        await Assert.That(listener.Failures).Contains(f => f.Kind == LoggingFailureKind.Final);

        gate.Set();
        await Assert.That(await CompletesWithin(disposing, Timeout)).IsTrue();
        await Assert.That(inner.Count).IsEqualTo(2);
    }

    [Test]
    public async Task WrappedSinkLoggingBackIntoAFullBlockingBufferDoesNotDeadlock()
    {
        BackgroundWorkerSink sink = null;
        var reentrant = new ReentrantSink(() => sink);
        sink = new BackgroundWorkerSink(reentrant, 1, blockWhenFull: true, null);

        var logging = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) sink.Emit(Some.Event());
            sink.Dispose();
        });

        await Assert.That(await CompletesWithin(logging, Timeout)).IsTrue()
            .Because("logging from the worker thread must not deadlock");
    }

    private static async Task<bool> CompletesWithin(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    // Logs an extra event through the async sink for each event it receives (but not for those extras).
    private class ReentrantSink(Func<BackgroundWorkerSink> sink) : ILogEventSink
    {
        public void Emit(Events.LogEvent logEvent)
        {
            if (logEvent.Properties.ContainsKey("Echo")) return;
            sink().Emit(new Events.LogEvent(logEvent.Timestamp, logEvent.Level, null, logEvent.MessageTemplate,
                [new Events.LogEventProperty("Echo", new Events.ScalarValue(true))]));
        }
    }
}

// Changes the global SelfLog, so it runs on its own.
[NotInParallel]
public class SelfLogTests
{
    [Test]
    public async Task ThrowingSelfLogDoesNotHangBlockingProducers()
    {
        SelfLog.Enable(_ => throw new IOException("Self-log output is unavailable."));
        try
        {
            var inner = new FlakySink(failFirst: 1);
            var log = new LoggerConfiguration()
                .WriteTo.Async(a => a.Sink(inner), bufferSize: 5, blockWhenFull: true)
                .CreateLogger();

            var logging = Task.Run(() =>
            {
                for (var i = 0; i < 20; i++) log.Information("Event {N}", i);
            });
            await Assert.That(await Task.WhenAny(logging, Task.Delay(TimeSpan.FromSeconds(10))) == logging).IsTrue()
                .Because("logging threads must not hang");

            log.Dispose();
            await Assert.That(inner.Delivered).IsEqualTo(19);
        }
        finally
        {
            SelfLog.Disable();
        }
    }
}
