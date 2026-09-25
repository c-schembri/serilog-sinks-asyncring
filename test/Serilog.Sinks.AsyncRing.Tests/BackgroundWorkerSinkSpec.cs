using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.Async.Tests.Support;

namespace Serilog.Sinks.Async.Tests;

// Ported from Serilog.Sinks.Async's test suite. Several of these tests check timings, so each one runs
// on its own rather than alongside other tests.
[NotInParallel]
public class BackgroundWorkerSinkSpec
{
    private readonly Logger _logger;
    private readonly MemorySink _innerSink;

    public BackgroundWorkerSinkSpec()
    {
        _innerSink = new MemorySink();
        _logger = new LoggerConfiguration().WriteTo.Sink(_innerSink).CreateLogger();
    }

    [Test]
    public async Task WhenCtorWithNullSink_ThenThrows()
    {
        await Assert.That(() => new BackgroundWorkerSink(null!, 10000, false, null)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WhenEmitSingle_ThenRelaysToInnerSink()
    {
        using var sink = CreateSinkWithDefaultOptions();
        var logEvent = CreateEvent();

        sink.Emit(logEvent);

        await Task.Delay(TimeSpan.FromSeconds(3));

        await Assert.That(_innerSink.Events).HasSingleItem();
    }

    [Test]
    public async Task WhenInnerEmitThrows_ThenContinuesRelaysToInnerSink()
    {
        using var sink = CreateSinkWithDefaultOptions();
        _innerSink.ThrowAfterCollecting = true;

        var events = new List<LogEvent>
        {
            CreateEvent(),
            CreateEvent(),
            CreateEvent()
        };
        events.ForEach(e => sink.Emit(e));

        await Task.Delay(TimeSpan.FromSeconds(3));

        await Assert.That(_innerSink.Events.Count).IsEqualTo(3);
    }

    [Test]
    public async Task WhenEmitMultipleTimes_ThenRelaysToInnerSink()
    {
        using var sink = CreateSinkWithDefaultOptions();
        var events = new List<LogEvent>
        {
            CreateEvent(),
            CreateEvent(),
            CreateEvent()
        };
        events.ForEach(e => { sink.Emit(e); });

        await Task.Delay(TimeSpan.FromSeconds(3));

        await Assert.That(_innerSink.Events.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GivenDefaultConfig_WhenRequestsExceedCapacity_DoesNotBlock()
    {
        var batchTiming = Stopwatch.StartNew();
        using (var sink = new BackgroundWorkerSink(_logger, 1, false, null))
        {
            // Cause a delay when emitting to the inner sink, allowing us to easily fill the queue to capacity
            // while the first event is being propagated
            var acceptInterval = TimeSpan.FromMilliseconds(500);
            _innerSink.DelayEmit = acceptInterval;
            var tenSecondsWorth = 10_000 / acceptInterval.TotalMilliseconds + 1;
            for (int i = 0; i < tenSecondsWorth; i++)
            {
                var emissionTiming = Stopwatch.StartNew();
                sink.Emit(CreateEvent());
                emissionTiming.Stop();

                // Should not block the caller when the queue is full
                await Assert.That(emissionTiming.ElapsedMilliseconds).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(200);
            }

            // Allow at least one to propagate
            await Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That(((IAsyncLogEventSinkInspector)sink).DroppedMessagesCount).IsNotEqualTo(0);
        }

        // Sanity check the overall timing
        batchTiming.Stop();
        // Need to add a significant fudge factor as AppVeyor build can result in `await` taking quite some time
        await Assert.That(batchTiming.ElapsedMilliseconds).IsGreaterThanOrEqualTo(950).And.IsLessThanOrEqualTo(2050);
    }

    [Test]
    public async Task GivenDefaultConfig_WhenRequestsExceedCapacity_ThenDropsEventsAndRecovers()
    {
        using var sink = new BackgroundWorkerSink(_logger, 1, false, null);
        var acceptInterval = TimeSpan.FromMilliseconds(200);
        _innerSink.DelayEmit = acceptInterval;

        for (int i = 0; i < 2; i++)
        {
            sink.Emit(CreateEvent());
            sink.Emit(CreateEvent());
            await Task.Delay(acceptInterval);
            sink.Emit(CreateEvent());
        }

        // Wait for the buffer and propagation to complete
        await Task.Delay(TimeSpan.FromSeconds(1));
        // Now verify things are back to normal; emit an event...
        var finalEvent = CreateEvent();
        sink.Emit(finalEvent);
        // ... give adequate time for it to be guaranteed to have percolated through
        await Task.Delay(TimeSpan.FromSeconds(1));

        // At least one of the preceding events should not have made it through
        var propagatedExcludingFinal =
            from e in _innerSink.Events
            where !Object.ReferenceEquals(finalEvent, e)
            select e;
        await Assert.That(propagatedExcludingFinal.Count()).IsGreaterThanOrEqualTo(2 * 3 / 2 - 1);
        // Final event should have made it through
        await Assert.That(_innerSink.Events).Contains(x => ReferenceEquals(finalEvent, x));
        await Assert.That(((IAsyncLogEventSinkInspector)sink).DroppedMessagesCount).IsNotEqualTo(0);
    }

    [Test]
    public async Task GivenConfiguredToBlock_WhenQueueFilled_ThenBlocks()
    {
        using var sink = new BackgroundWorkerSink(_logger, 1, true, null);
        // Cause a delay when emitting to the inner sink, allowing us to fill the queue to capacity
        // after the first event is popped
        _innerSink.DelayEmit = TimeSpan.FromMilliseconds(300);

        var events = new List<LogEvent>
        {
            CreateEvent(),
            CreateEvent(),
            CreateEvent()
        };

        // As in Serilog.Sinks.Async's suite, i is never incremented, so the check below never runs.
        int i = 0;
        foreach (var e in events)
        {
            var sw = Stopwatch.StartNew();
            sink.Emit(e);
            sw.Stop();

            // Emit should return immediately the first time, since the queue is not yet full. On
            // subsequent calls, the queue should be full, so we should be blocked
            if (i > 0)
            {
                await Assert.That(sw.ElapsedMilliseconds).IsGreaterThan(200).Because("it should block the caller when the queue is full");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(2));

        // No events should be dropped
        await Assert.That(_innerSink.Events.Count).IsEqualTo(3);
        await Assert.That(((IAsyncLogEventSinkInspector)sink).DroppedMessagesCount).IsEqualTo(0);
    }

    [Test]
    public async Task MonitorParameterAffordsSinkInspectorSuitableForHealthChecking()
    {
        var collector = new MemorySink { DelayEmit = TimeSpan.FromSeconds(2) };
        // 2 spaces in queue; 1 would make the second log entry eligible for dropping if consumer does not activate instantaneously
        var bufferSize = 2;
        var monitor = new DummyMonitor();
        using (var logger = new LoggerConfiguration()
                   .WriteTo.Async(w => w.Sink(collector), bufferSize: 2, monitor: monitor)
                   .CreateLogger())
        {
            // Construction of BackgroundWorkerSink triggers StartMonitoring
            var inspector = monitor.Inspector;
            await Assert.That(inspector.BufferSize).IsEqualTo(bufferSize);
            await Assert.That(inspector.Count).IsEqualTo(0);
            await Assert.That(inspector.DroppedMessagesCount).IsEqualTo(0);
            logger.Information("Something to freeze the processing for 2s");
            // Can be taken from queue either instantanously or be awaiting consumer to take
            await Assert.That(inspector.Count).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(1);
            await Assert.That(inspector.DroppedMessagesCount).IsEqualTo(0);
            logger.Information("Something that will sit in the queue");
            await Assert.That(inspector.Count).IsGreaterThanOrEqualTo(1).And.IsLessThanOrEqualTo(2);
            logger.Information(
                "Something that will probably also sit in the queue (but could get dropped if first message has still not been picked up)");
            await Assert.That(inspector.Count).IsGreaterThanOrEqualTo(1).And.IsLessThanOrEqualTo(2);
            logger.Information("Something that will get dropped unless we get preempted for 2s during our execution");
            const string droppedMessage = "Something that will definitely get dropped";
            logger.Information(droppedMessage);
            await Assert.That(inspector.Count).IsGreaterThanOrEqualTo(1).And.IsLessThanOrEqualTo(2);
            // Unless we are put to sleep for a Rip Van Winkle period, either:
            // a) the BackgroundWorker will be emitting the item [and incurring the 2s delay we established], leaving a single item in the buffer
            // or b) neither will have been picked out of the buffer yet.
            await Assert.That(inspector.Count).IsGreaterThanOrEqualTo(1).And.IsLessThanOrEqualTo(2);
            await Assert.That(inspector.BufferSize).IsEqualTo(bufferSize);
            await Assert.That(collector.Events).DoesNotContain(x => x.MessageTemplate.Text == droppedMessage);
            // Because messages wait 2 seconds, the only real way to get one into the buffer is with a debugger breakpoint or a sleep
            await Assert.That(collector.Events.Count).IsGreaterThanOrEqualTo(0).And.IsLessThanOrEqualTo(3);
        }

        // Dispose should trigger a StopMonitoring call
        await Assert.That(monitor.Inspector).IsNull();
    }

    private BackgroundWorkerSink CreateSinkWithDefaultOptions()
    {
        return new BackgroundWorkerSink(_logger, 10000, false, null);
    }

    private static LogEvent CreateEvent()
    {
        return new LogEvent(DateTimeOffset.MaxValue, LogEventLevel.Error, null,
            new MessageTemplate("amessage", Enumerable.Empty<MessageTemplateToken>()),
            Enumerable.Empty<LogEventProperty>());
    }
}
