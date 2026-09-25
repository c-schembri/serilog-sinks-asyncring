using System.Diagnostics.Tracing;

namespace AsyncRingBenchmarks;

/// <summary>
/// Measures, inside a benchmark process, how much memory the logging pipeline keeps in use and how much it
/// allocates per event. Started before the logger is created, so everything the logger holds on to counts.
/// </summary>
/// <remarks>
/// <para>Each benchmark invocation is a measured window (plus the drain after it). Only the last
/// <see cref="BenchmarkConfig.MeasuredIterations"/> windows are reported, because BenchmarkDotNet runs its
/// measured iterations last, after the warm-up ones (which run less optimized code).</para>
/// <para><b>Memory in use</b> is the managed heap size after each garbage collection (live objects, not garbage
/// waiting to be collected), sampled every 10 ms, minus the heap size before the logger existed. It's reported as
/// an average over time and a peak.</para>
/// <para><b>Allocated bytes</b> are exact (<see cref="GC.GetTotalAllocatedBytes(bool)"/>). <b>Allocations</b>
/// (objects) are estimated: .NET doesn't count them, but it reports an allocation "tick" about every 100 KB with
/// the size of the object that crossed the threshold. Larger objects are proportionally more likely to be that
/// object, so summing <c>bytes in the window / size of the sampled object</c> is an unbiased estimate of the
/// number of objects.</para>
/// </remarks>
public sealed class ResourceMonitor : IDisposable
{
    private readonly long _baselineHeap;
    private readonly AllocationTickListener _ticks = new();
    private readonly List<Window> _windows = [];
    private readonly Thread _sampler;
    private volatile bool _sampling;
    private volatile bool _stopped;
    private long _windowStartBytes;

    private ResourceMonitor()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _baselineHeap = GC.GetGCMemoryInfo().HeapSizeBytes;

        _sampler = new Thread(Sample) { IsBackground = true, Name = "Memory sampler" };
        _sampler.Start();
    }

    public static ResourceMonitor Start() => new();

    /// <summary>Starts a measured window: allocations are counted, and memory is sampled.</summary>
    public void Begin()
    {
        lock (_windows) _windows.Add(new Window());
        _windowStartBytes = GC.GetTotalAllocatedBytes(precise: true);
        _sampling = true;
    }

    /// <summary>Ends a measured window in which <paramref name="events"/> events were logged.</summary>
    public void End(long events)
    {
        var bytes = GC.GetTotalAllocatedBytes(precise: true) - _windowStartBytes;
        _sampling = false;
        lock (_windows)
        {
            var window = _windows[^1];
            window.Bytes = bytes;
            window.Events = events;
        }
    }

    /// <summary>Keeps sampling memory, into the latest window, while <paramref name="action"/> runs (a drain).</summary>
    public void SampleMemoryWhile(Action action)
    {
        _sampling = true;
        try
        {
            action();
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>What was measured over the last <paramref name="windows"/> windows.</summary>
    public ResourceStats Stats(int windows)
    {
        lock (_windows)
        {
            var measured = _windows.Skip(Math.Max(0, _windows.Count - windows)).ToList();
            var events = measured.Sum(w => w.Events);
            var bytes = measured.Sum(w => w.Bytes);
            var samples = measured.Sum(w => w.HeapSamples);
            var bytesPerEvent = events > 0 ? (double)bytes / events : 0;

            return new ResourceStats(
                AllocatedBytesPerEvent: bytesPerEvent,
                AllocationsPerEvent: bytesPerEvent * _ticks.ObjectsPerByte,
                MemoryAverage: samples > 0 ? Math.Max(0, measured.Sum(w => w.HeapSum) / samples - _baselineHeap) : 0,
                MemoryPeak: measured.Count > 0 ? Math.Max(0, measured.Max(w => w.HeapPeak) - _baselineHeap) : 0);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _sampler.Join();
        _ticks.Dispose();
    }

    private void Sample()
    {
        while (!_stopped)
        {
            if (_sampling)
            {
                var heap = GC.GetGCMemoryInfo().HeapSizeBytes;
                lock (_windows)
                {
                    if (_windows.Count > 0)
                    {
                        var window = _windows[^1];
                        window.HeapSum += heap;
                        window.HeapSamples++;
                        window.HeapPeak = Math.Max(window.HeapPeak, heap);
                    }
                }
            }

            Thread.Sleep(10);
        }
    }

    private sealed class Window
    {
        public long Bytes;
        public long Events;
        public double HeapSum;
        public long HeapSamples;
        public long HeapPeak;
    }

    private sealed class AllocationTickListener : EventListener
    {
        private readonly object _sync = new();
        private double _estimatedObjects;
        private double _sampledBytes;

        public double ObjectsPerByte
        {
            get
            {
                lock (_sync) return _sampledBytes > 0 ? _estimatedObjects / _sampledBytes : 0;
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1); // GC events, including allocation ticks
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is null || !eventData.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal))
                return;

            var names = eventData.PayloadNames;
            var payload = eventData.Payload;
            if (names is null || payload is null) return;

            var amountIndex = names.IndexOf("AllocationAmount64");
            var sizeIndex = names.IndexOf("ObjectSize");
            if (amountIndex < 0 || sizeIndex < 0) return;

            var amount = Convert.ToDouble(payload[amountIndex]);
            var size = Convert.ToDouble(payload[sizeIndex]);
            if (amount <= 0 || size <= 0) return;

            lock (_sync)
            {
                _sampledBytes += amount;
                _estimatedObjects += amount / size;
            }
        }
    }
}

public readonly record struct ResourceStats(
    double AllocatedBytesPerEvent,
    double AllocationsPerEvent,
    double MemoryAverage,
    double MemoryPeak);
