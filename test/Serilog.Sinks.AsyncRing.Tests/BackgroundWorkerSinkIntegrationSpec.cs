using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.Async.Tests.Support;

namespace Serilog.Sinks.Async.Tests;

// Ported from Serilog.Sinks.Async's test suite, with the waits made asynchronous so the tests don't tie
// up thread-pool threads while TUnit runs them in parallel.
public static class BackgroundWorkerSinkIntegrationSpec
{
    /// <summary>
    ///     If <paramref name="withDelay" />, then adds a 1sec delay before every fifth element created
    /// </summary>
    private static async Task CreateAudits(ILogger logger, int count, bool withDelay)
    {
        var delay = TimeSpan.FromMilliseconds(1000);
        var sw = new Stopwatch();
        sw.Start();
        Debug.WriteLine("{0:h:mm:ss tt} Start: Writing {1} audits", DateTime.Now, count);
        try
        {
            var delayCount = 0;
            for (var counter = 0; counter < count; counter++)
            {
                if (withDelay
                    && counter > 0
                    && counter % 5 == 0)
                {
                    delayCount++;
                    Debug.WriteLine("{0:h:mm:ss tt} Delay ({1}) after {2}th write, for {3:0.###}secs", DateTime.Now,
                        delayCount, counter,
                        delay.TotalSeconds);
                    await Task.Delay(delay);
                }

                logger.Information("{$Counter}", counter);
            }
        }
        finally
        {
            sw.Stop();
            Debug.WriteLine("{0:h:mm:ss tt}   End: Writing {1} audits, taking {2:0.###}", DateTime.Now, count,
                sw.Elapsed.TotalSeconds);
        }
    }

    private static async Task<List<LogEvent>> RetrieveEvents(MemorySink sink, int count)
    {
        Debug.WriteLine("{0:h:mm:ss tt} Retrieving {1} events", DateTime.Now, count);

        var timeout = Stopwatch.StartNew();
        while (sink.Events.Count < count && timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return sink.Events.ToList();
    }

    [InheritsTests]
    public class GivenNoBufferQueueAndNoDelays : SinkSpecBase
    {
        public GivenNoBufferQueueAndNoDelays()
            : base(false, false)
        {
        }
    }

    [InheritsTests]
    public class GivenBufferQueueAndNoDelays : SinkSpecBase
    {
        public GivenBufferQueueAndNoDelays()
            : base(true, false)
        {
        }
    }

    [InheritsTests]
    public class GivenNoBufferQueueAndDelays : SinkSpecBase
    {
        public GivenNoBufferQueueAndDelays()
            : base(false, true)
        {
        }
    }

    [InheritsTests]
    public class GivenBufferQueueAndDelays : SinkSpecBase
    {
        public GivenBufferQueueAndDelays()
            : base(true, true)
        {
        }
    }

    public abstract class SinkSpecBase : IDisposable
    {
        private readonly bool _delayCreation;
        private readonly Logger _logger;
        private readonly MemorySink _memorySink;

        protected SinkSpecBase(bool useBufferedQueue, bool delayCreation)
        {
            _delayCreation = delayCreation;

            _memorySink = new MemorySink();

            if (useBufferedQueue)
            {
                _logger = new LoggerConfiguration()
                    .WriteTo.Async(a => a.Sink(_memorySink))
                    .CreateLogger();
            }
            else
            {
                _logger = new LoggerConfiguration()
                    .WriteTo.Sink(_memorySink)
                    .CreateLogger();
            }

            Debug.WriteLine("{0:h:mm:ss tt} Started test", DateTime.Now);
        }

        public void Dispose()
        {
            _logger.Dispose();
            Debug.WriteLine("{0:h:mm:ss tt} Ended test", DateTime.Now);
        }

        [Test]
        public async Task WhenAuditSingle_ThenQueued()
        {
            await CreateAudits(_logger, 1, _delayCreation);

            var result = await RetrieveEvents(_memorySink, 1);

            await Assert.That(result).HasSingleItem();
        }

        [Test]
        public async Task WhenAuditTen_ThenQueued()
        {
            await CreateAudits(_logger, 10, _delayCreation);

            var result = await RetrieveEvents(_memorySink, 10);

            await Assert.That(result.Count).IsEqualTo(10);
        }

        [Test]
        public async Task WhenAuditHundred_ThenQueued()
        {
            await CreateAudits(_logger, 100, _delayCreation);

            var result = await RetrieveEvents(_memorySink, 100);

            await Assert.That(result.Count).IsEqualTo(100);
        }

        [Test]
        public async Task WhenAuditFiveHundred_ThenQueued()
        {
            await CreateAudits(_logger, 500, _delayCreation);

            var result = await RetrieveEvents(_memorySink, 500);

            await Assert.That(result.Count).IsEqualTo(500);
        }
    }
}
