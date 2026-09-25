using System;
using System.Threading.Tasks;
using Serilog.Events;
using Serilog.Sinks.Async.Tests.Support;

namespace Serilog.Sinks.Async.Tests;

// Ported from Serilog.Sinks.Async's test suite.
public class BackgroundWorkerSinkTests
{
    [Test]
    public async Task EventsArePassedToInnerSink()
    {
        var collector = new MemorySink();

        using (var log = new LoggerConfiguration()
                   .WriteTo.Async(w => w.Sink(collector))
                   .CreateLogger())
        {
            log.Information("Hello, async world!");
            log.Information("Hello again!");
        }

        await Assert.That(collector.Events.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DisposeCompletesWithoutWorkPerformed()
    {
        var collector = new MemorySink();

        using (new LoggerConfiguration()
                   .WriteTo.Async(w => w.Sink(collector))
                   .CreateLogger())
        {
        }

        await Assert.That(collector.Events).IsEmpty();
    }

    [Test]
    public async Task CtorAndDisposeInformMonitor()
    {
        var collector = new MemorySink();
        var monitor = new DummyMonitor();

        using (new LoggerConfiguration()
                   .WriteTo.Async(w => w.Sink(collector), monitor: monitor)
                   .CreateLogger())
        {
            await Assert.That(monitor.Inspector).IsNotNull();
        }

        await Assert.That(monitor.Inspector).IsNull();
    }

    [Test]
    public async Task SupportsLoggingFailureListener()
    {
        var failureListener = new CollectingFailureListener();
        var sink = new BackgroundWorkerSink(new NotImplementedSink(), 1, false, null);
        sink.SetFailureListener(failureListener);
        var evt = new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null, MessageTemplate.Empty, []);
        sink.Emit(evt);
        sink.Dispose();
        var collected = await Assert.That(failureListener.Events).HasSingleItem();
        await Assert.That(collected).IsSameReferenceAs(evt);
        var exception = await Assert.That(failureListener.Exceptions).HasSingleItem();
        await Assert.That(exception).IsTypeOf<NotImplementedException>();
    }
}
