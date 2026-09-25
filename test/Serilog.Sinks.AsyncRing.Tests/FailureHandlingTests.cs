using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Sinks.Async.Tests.Support;
using Xunit;

namespace Serilog.Sinks.Async.Tests;

// Regression tests for failure-path bugs found in Serilog.Sinks.Async 2.1.0.
public class FailureHandlingTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ThrowingFailureListenerDoesNotStopTheWorker()
    {
        var inner = new FlakySink(failFirst: 1);
        using (var sink = new BackgroundWorkerSink(inner, 100, false, null))
        {
            sink.SetFailureListener(new ThrowingFailureListener());
            for (var i = 0; i < 20; i++) sink.Emit(Some.Event());
        }

        Assert.Equal(19, inner.Delivered);
    }

    [Fact]
    public void FallbackChainWithThrowingFallbackKeepsDeliveringToPrimary()
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

        Assert.Equal(19, primary.Delivered);
    }

    [Fact]
    public void WrappedSinkIsDisposedExactlyOnce()
    {
        var inner = new DisposeCountingSink();
        var log = new LoggerConfiguration().WriteTo.Async(a => a.Sink(inner)).CreateLogger();
        log.Information("Hello");

        log.Dispose();

        Assert.Equal(1, inner.Disposes);
        Assert.Equal(0, inner.AsyncDisposes);
    }

#if NET6_0_OR_GREATER
    [Fact]
    public async Task WrappedSinkIsDisposedExactlyOnceAsynchronously()
    {
        var inner = new DisposeCountingSink();
        var log = new LoggerConfiguration().WriteTo.Async(a => a.Sink(inner)).CreateLogger();
        log.Information("Hello");

        await log.DisposeAsync();

        Assert.Equal(0, inner.Disposes);
        Assert.Equal(1, inner.AsyncDisposes);
    }
#endif

    [Fact]
    public void DisposingTwiceDisposesTheWrappedSinkOnce()
    {
        var inner = new DisposeCountingSink();
        var monitor = new DummyMonitor();
        var sink = new BackgroundWorkerSink(inner, 10, false, monitor);

        sink.Dispose();
        sink.Dispose();

        Assert.Equal(1, inner.Disposes);
        Assert.Null(monitor.Inspector);
    }

    [Fact]
    public void FailureListenerIsForwardedToTheWrappedSink()
    {
        var inner = new FailureListenerRecordingSink();
        var listener = new RecordingFailureListener();

        using (new LoggerConfiguration()
                   .WriteTo.Fallible(wt => wt.Async(a => a.Sink(inner)), listener)
                   .CreateLogger())
        {
            Assert.Same(listener, inner.Listener);
        }
    }

    [Fact]
    public void EmitAfterDisposeIsReportedAsFinal()
    {
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(new MemorySink(), 10, false, null);
        sink.SetFailureListener(listener);
        sink.Dispose();

        sink.Emit(Some.Event());

        var failure = Assert.Single(listener.Failures);
        Assert.Equal(LoggingFailureKind.Final, failure.Kind);
        Assert.Equal(1, failure.Events);
    }

    [Fact]
    public void DisposeReleasesProducersBlockedOnAFullBuffer()
    {
        using var gate = new ManualResetEventSlim(false);
        var inner = new GatedSink(gate);
        var listener = new RecordingFailureListener();
        var sink = new BackgroundWorkerSink(inner, 1, blockWhenFull: true, null);
        sink.SetFailureListener(listener);

        sink.Emit(Some.Event());                     // taken by the worker, which then waits at the gate
        Assert.True(inner.WaitUntilEntered(Timeout));
        sink.Emit(Some.Event());                     // fills the one-event buffer
        var blocked = Task.Run(() => sink.Emit(Some.Event()));
        Assert.False(blocked.Wait(TimeSpan.FromMilliseconds(200)), "The producer should be blocked.");

        var disposing = Task.Run(sink.Dispose);
        Assert.True(blocked.Wait(Timeout), "Dispose should release the blocked producer.");
        Assert.Contains(listener.Failures, f => f.Kind == LoggingFailureKind.Final);

        gate.Set();
        Assert.True(disposing.Wait(Timeout));
        Assert.Equal(2, inner.Count);
    }

    [Fact]
    public void WrappedSinkLoggingBackIntoAFullBlockingBufferDoesNotDeadlock()
    {
        BackgroundWorkerSink sink = null;
        var reentrant = new ReentrantSink(() => sink);
        sink = new BackgroundWorkerSink(reentrant, 1, blockWhenFull: true, null);

        var logging = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) sink.Emit(Some.Event());
            sink.Dispose();
        });

        Assert.True(logging.Wait(Timeout), "Logging from the worker thread deadlocked.");
    }

    // Logs an extra event through the async sink for each event it receives (but not for those extras).
    class ReentrantSink(Func<BackgroundWorkerSink> sink) : ILogEventSink
    {
        public void Emit(Events.LogEvent logEvent)
        {
            if (logEvent.Properties.ContainsKey("Echo")) return;
            sink().Emit(new Events.LogEvent(logEvent.Timestamp, logEvent.Level, null, logEvent.MessageTemplate,
                [new Events.LogEventProperty("Echo", new Events.ScalarValue(true))]));
        }
    }
}

[CollectionDefinition(nameof(SelfLogTests), DisableParallelization = true)]
public class SelfLogCollection;

// Changes the global SelfLog, so it doesn't run in parallel with other tests.
[Collection(nameof(SelfLogTests))]
public class SelfLogTests
{
    [Fact]
    public void ThrowingSelfLogDoesNotHangBlockingProducers()
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
            Assert.True(logging.Wait(TimeSpan.FromSeconds(10)), "Logging threads hung.");

            log.Dispose();
            Assert.Equal(19, inner.Delivered);
        }
        finally
        {
            SelfLog.Disable();
        }
    }
}
