# Serilog.Sinks.AsyncRing

[![CI](https://github.com/c-schembri/serilog-sinks-asyncring/actions/workflows/ci.yml/badge.svg)](https://github.com/c-schembri/serilog-sinks-asyncring/actions/workflows/ci.yml)
[![Benchmarks](https://github.com/c-schembri/serilog-sinks-asyncring/actions/workflows/benchmarks.yml/badge.svg)](https://github.com/c-schembri/serilog-sinks-asyncring/actions/workflows/benchmarks.yml)
[![NuGet](https://img.shields.io/nuget/v/Serilog.Sinks.AsyncRing.svg)](https://www.nuget.org/packages/Serilog.Sinks.AsyncRing)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)

An asynchronous wrapper for other [Serilog](https://serilog.net) sinks, and a drop-in replacement for
[Serilog.Sinks.Async](https://github.com/serilog/serilog-sinks-async). It has exactly the same public API,
namespaces and defaults, but hands events to the background thread through a lock-free ring buffer. That makes
it much faster when many threads log at once.

## Performance

Measured with the [BenchmarkDotNet](https://benchmarkdotnet.org) suite in `test/Serilog.Sinks.AsyncRing.Benchmarks`,
on a Ryzen 9 9900X (12 cores, 24 threads), Windows 11, .NET 10. Threads log as fast as they can into a sink that
only counts events, so these measure the logging pipeline and the hand-off to the background thread.

**Cost of a logging call** (ns per call, as seen by each logging thread; nothing dropped):

| Logging threads | Serilog.Sinks.Async | AsyncRing | No queue |
|---|---|---|---|
| 1 | 233 | 246 | 189 |
| 4 | 1,783 | 257 | 169 |
| 16 | 11,489 | 762 | 530 |

With one thread the results are noisy (AsyncRing's median was 207 ns), and the two sinks are about even. At 16
threads, 14% of Serilog.Sinks.Async's logging calls ran into a lock another thread was holding; for AsyncRing it
was 0.03%.

**Throughput** (events per second reaching the wrapped sink):

| Logging threads | Serilog.Sinks.Async | AsyncRing | No queue |
|---|---|---|---|
| 1 | 3.9 million | 6.0 million | 10.2 million |
| 4 | 2.2 million | 16.5 million | 32.1 million |
| 16 | 1.3 million | 20.3 million | 31.5 million |

**Overload with the default 10,000-event buffer** (ns per call, and the share of events that weren't dropped):

| Logging threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|
| 1 | 225 ns, 100% delivered | 180 ns, 100% delivered |
| 4 | 865 ns, 50% delivered | 282 ns, 99.9% delivered |
| 16 | 2,056 ns, 7.4% delivered | 732 ns, 99.9% delivered |

On .NET 8 the numbers are within about 10% of these, and the comparison between the two sinks is the same.

These are extremes. A real sink (a file, the console) is much slower than a sink that only counts, so under
sustained overload any async wrapper eventually fills its buffer and then drops or blocks. The numbers show how
little each logging call costs, and how much better the background thread keeps up when many threads log at
once.

### Latest results from CI

CI runs the same benchmarks on Linux, Windows and macOS whenever the library changes, and updates this section.
GitHub's runners have only 3–4 cores (so 16 logging threads compete for them) and are noisier than a dedicated
machine, so compare the two sinks within each table rather than with the figures above.

<!-- ci-benchmarks:start -->
Last updated 2026-09-25 04:48 UTC from commit `c606388` ([workflow run](https://github.com/c-schembri/serilog-sinks-asyncring/actions/runs/36095247066)).

<details>
<summary><b>Linux</b>: AsyncRing is 2.3× faster per logging call with 16 threads</summary>

Linux Ubuntu 24.04.5 LTS (Noble Numbat) · AMD EPYC 9V74 2.60GHz, 4 logical and 2 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 377 ns | 291 ns | 136 ns | 1.3× faster |
| .NET 10 | 4 | 1,680 ns | 601 ns | 297 ns | 2.8× faster |
| .NET 10 | 16 | 9,512 ns | 4,172 ns | 1,217 ns | 2.3× faster |
| .NET 8 | 1 | 344 ns | 332 ns | 164 ns | 1.0× faster |
| .NET 8 | 4 | 1,604 ns | 664 ns | 351 ns | 2.4× faster |
| .NET 8 | 16 | 9,768 ns | 3,909 ns | 1,474 ns | 2.5× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 2.7M/s | 3.5M/s | 7.2M/s | 1.3× faster |
| .NET 10 | 4 | 2.2M/s | 6.5M/s | 14.3M/s | 3.0× faster |
| .NET 10 | 16 | 1.6M/s | 4.1M/s | 13.3M/s | 2.5× faster |
| .NET 8 | 1 | 2.4M/s | 3.1M/s | 6.3M/s | 1.3× faster |
| .NET 8 | 4 | 2.6M/s | 6.4M/s | 11.5M/s | 2.5× faster |
| .NET 8 | 16 | 1.6M/s | 4.1M/s | 10.7M/s | 2.6× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 362 ns, 100% delivered | 264 ns, 100% delivered |
| .NET 10 | 4 | 1,228 ns, 76% delivered | 553 ns, 79% delivered |
| .NET 10 | 16 | 3,204 ns, 23.9% delivered | 2,246 ns, 34% delivered |
| .NET 8 | 1 | 326 ns, 100% delivered | 324 ns, 100% delivered |
| .NET 8 | 4 | 1,151 ns, 88.1% delivered | 583 ns, 83.3% delivered |
| .NET 8 | 16 | 3,733 ns, 24.4% delivered | 2,529 ns, 37.6% delivered |

</details>

<details>
<summary><b>Windows</b>: AsyncRing is 2.0× faster per logging call with 16 threads</summary>

Windows 11 (10.0.26100.33438/24H2/2024Update/HudsonValley) (Hyper-V) · AMD EPYC 7763 2.44GHz, 4 logical and 2 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 426 ns | 316 ns | 174 ns | 1.3× faster |
| .NET 10 | 4 | 2,239 ns | 1,074 ns | 314 ns | 2.1× faster |
| .NET 10 | 16 | 11,600 ns | 5,881 ns | 1,374 ns | 2.0× faster |
| .NET 8 | 1 | 426 ns | 336 ns | 192 ns | 1.3× faster |
| .NET 8 | 4 | 2,149 ns | 1,042 ns | 407 ns | 2.1× faster |
| .NET 8 | 16 | 12,811 ns | 6,593 ns | 1,708 ns | 1.9× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 2.5M/s | 3.4M/s | 6.3M/s | 1.4× faster |
| .NET 10 | 4 | 1.5M/s | 4.3M/s | 12.4M/s | 2.9× faster |
| .NET 10 | 16 | 1.4M/s | 2.8M/s | 12.4M/s | 1.9× faster |
| .NET 8 | 1 | 2.2M/s | 2.9M/s | 5.2M/s | 1.3× faster |
| .NET 8 | 4 | 1.7M/s | 3.7M/s | 9.9M/s | 2.2× faster |
| .NET 8 | 16 | 1.2M/s | 2.6M/s | 9.7M/s | 2.2× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 430 ns, 100% delivered | 282 ns, 100% delivered |
| .NET 10 | 4 | 1,443 ns, 67.3% delivered | 755 ns, 69.4% delivered |
| .NET 10 | 16 | 3,810 ns, 25.8% delivered | 3,210 ns, 47.9% delivered |
| .NET 8 | 1 | 448 ns, 100% delivered | 366 ns, 100% delivered |
| .NET 8 | 4 | 1,587 ns, 79.1% delivered | 843 ns, 72.4% delivered |
| .NET 8 | 16 | 4,742 ns, 29% delivered | 3,827 ns, 54.6% delivered |

</details>

<details>
<summary><b>macOS</b>: AsyncRing is 2.1× faster per logging call with 16 threads</summary>

macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0] · Apple M1 (Virtual), 3 logical and 3 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 681 ns | 416 ns | 459 ns | 1.6× faster |
| .NET 10 | 4 | 3,024 ns | 1,120 ns | 433 ns | 2.7× faster |
| .NET 10 | 16 | 16,309 ns | 7,665 ns | 2,127 ns | 2.1× faster |
| .NET 8 | 1 | 661 ns | 573 ns | 558 ns | 1.2× faster |
| .NET 8 | 4 | 2,783 ns | 2,076 ns | 740 ns | 1.3× faster |
| .NET 8 | 16 | 16,577 ns | 9,236 ns | 2,852 ns | 1.8× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 1.8M/s | 3.1M/s | 5.7M/s | 1.7× faster |
| .NET 10 | 4 | 1.7M/s | 5.1M/s | 11.6M/s | 2.9× faster |
| .NET 10 | 16 | 1.5M/s | 3.2M/s | 12.5M/s | 2.1× faster |
| .NET 8 | 1 | 1.9M/s | 3.3M/s | 6.2M/s | 1.8× faster |
| .NET 8 | 4 | 1.2M/s | 5.3M/s | 11.0M/s | 4.3× faster |
| .NET 8 | 16 | 1.2M/s | 2.9M/s | 12.0M/s | 2.4× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 658 ns, 99% delivered | 388 ns, 100% delivered |
| .NET 10 | 4 | 2,821 ns, 67.4% delivered | 1,687 ns, 85.2% delivered |
| .NET 10 | 16 | 3,598 ns, 13.9% delivered | 3,542 ns, 35.3% delivered |
| .NET 8 | 1 | 742 ns, 100% delivered | 387 ns, 99.6% delivered |
| .NET 8 | 4 | 2,512 ns, 62.9% delivered | 1,837 ns, 78% delivered |
| .NET 8 | 16 | 3,110 ns, 14.6% delivered | 3,589 ns, 40.4% delivered |

</details>

<!-- ci-benchmarks:end -->

## Getting started

Replace the `Serilog.Sinks.Async` package with this one. No code changes are needed: the configuration
method, the namespaces (`Serilog`, `Serilog.Sinks.Async`) and the interfaces are the same.

```sh
dotnet remove package Serilog.Sinks.Async
dotnet add package Serilog.Sinks.AsyncRing
```

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Async(a => a.File("logs/myapp.log"))
    // Other logger configuration
    .CreateLogger();

Log.Information("This will be written to disk on the worker thread");

// At application shutdown (results in monitors getting StopMonitoring calls)
Log.CloseAndFlush();
```

The wrapped sink (`File` in this case) is invoked on a background thread, so the application's threads don't wait
for it. Because the buffer may contain events that haven't been written yet, call `Log.CloseAndFlush()` (or
`CloseAndFlushAsync()`, or dispose the logger) when the application exits.

As with Serilog.Sinks.Async, sinks that already batch asynchronously (Seq, Elasticsearch, anything built on
Serilog 4's batching support) don't benefit from this wrapper.

### Options

The options are the same as Serilog.Sinks.Async's:

```csharp
.WriteTo.Async(
    a => a.File("logs/myapp.log"),
    bufferSize: 10000,     // events held in memory for the worker (default 10,000)
    blockWhenFull: false,  // when the buffer is full: drop new events (default) or block the caller
    monitor: null)         // IAsyncLogEventSinkMonitor for health checks (see below)
```

When the buffer is full, new events are dropped by default and reported to the failure listener, which is
`SelfLog` unless a fallback is configured. Set `blockWhenFull: true` to make the logging call wait instead.

### Health monitoring

Pass an `IAsyncLogEventSinkMonitor` to be handed an `IAsyncLogEventSinkInspector`, which reports the buffer size,
how many events are waiting, and how many have been dropped:

```csharp
class MonitorConfiguration : IAsyncLogEventSinkMonitor
{
    public void StartMonitoring(IAsyncLogEventSinkInspector inspector) =>
        HealthMonitor.AddPeriodicCheck(() =>
        {
            var usage = (double)inspector.Count / inspector.BufferSize;
            if (usage > 0.5) SelfLog.WriteLine("Log buffer exceeded {0:p0} usage (limit: {1})", usage, inspector.BufferSize);
        });

    public void StopMonitoring(IAsyncLogEventSinkInspector inspector)
    { /* reverse of StartMonitoring */ }
}

.WriteTo.Async(a => a.File("logs/myapp.log"), monitor: new MonitorConfiguration())
```

### JSON configuration

With [Serilog.Settings.Configuration](https://github.com/serilog/serilog-settings-configuration), the sink is
configured exactly as before. If your configuration lists assemblies under `Using`, replace
`Serilog.Sinks.Async` with `Serilog.Sinks.AsyncRing`.

```json
{
  "Serilog": {
    "Using": [ "Serilog.Sinks.AsyncRing" ],
    "WriteTo": [{
      "Name": "Async",
      "Args": {
        "configure": [{ "Name": "Console" }]
      }
    }]
  }
}
```

## How it works

Each `WriteTo.Async(...)` gets a ring buffer and one dedicated background thread that writes events to the wrapped
sink, in order.

- **Logging threads claim a slot with a single `Interlocked.Increment`.** A lock or a compare-and-swap loop (what
  `BlockingCollection`, `ConcurrentQueue` and `System.Threading.Channels` are built on) has to wait or retry when
  another thread gets there first; an increment never does. This is what keeps it fast when many threads log at
  once.
- **The event is published by writing the slot's sequence number,** which the worker checks before reading it.
- **The worker sleeps when there's nothing to do.** Logging threads only pay for waking it when it's actually
  asleep.
- **Heavily used counters sit on their own CPU cache lines,** and the worker reports its progress every few
  events rather than after each one, so threads don't keep invalidating each other's caches.

## Differences from Serilog.Sinks.Async

The API is identical, and so are the defaults and the order events are written in. The differences are in how
the buffer behaves and in failure handling.

**Buffer behaviour**

- **The buffer is allocated up front.** The ring holds the next power of two at or above `bufferSize + 1024`
  slots, at 16 bytes each on 64-bit: 256 KB for the default 10,000. `BlockingCollection` grows as needed instead.
- **`bufferSize` is capped at about 2 million events** (a 32 MB ring). Larger values are reduced to fit, and
  `IAsyncLogEventSinkInspector.BufferSize` reports the value actually used.
- **The capacity limit is approximate.** Threads that pass the "is there room?" check at the same moment can
  overshoot it by a few events (at most one per thread).
- **`IAsyncLogEventSinkInspector.Count` can include up to 255 events the worker has already taken,** because the
  worker reports its progress in batches. This only applies to large buffers; for small ones it reports after
  every event.

**Failure handling (these fix bugs in Serilog.Sinks.Async 2.1.0)**

- **A failure listener that throws no longer stops the background thread.** That includes a `SelfLog` output
  that throws, or a `FallbackChain` fallback sink that throws. In Serilog.Sinks.Async this silently stopped all
  logging to that sink, and with `blockWhenFull: true` made every logging thread hang.
- **The wrapped sink is disposed exactly once.** Serilog.Sinks.Async disposed it twice on .NET 6+.
- **Async disposal is supported** (`CloseAndFlushAsync`, `DisposeAsync`) without blocking a thread while the
  buffer drains.
- **Threads blocked on a full buffer (`blockWhenFull: true`) are released when the logger is disposed.** Their
  events are reported to the failure listener.
- **If the wrapped sink logs back into the same pipeline and the buffer is full,** the event is dropped instead of
  deadlocking the background thread.
- **Every event is accounted for.** It's either written or reported to the failure listener, including events
  logged while the logger is being disposed.

**Unchanged**

- Disposing the logger waits for the buffer to drain, with no timeout.
- Events hold references to the objects they were created from (for example an `Exception`), and are rendered
  later on the background thread.

## Building and testing

```sh
dotnet build
dotnet test
```

The tests use [TUnit](https://tunit.dev) and include Serilog.Sinks.Async's own test suite, ported to TUnit and
run against this implementation. They also check that the public API is identical to Serilog.Sinks.Async 2.1.0's,
and stress the ring buffer: many threads, tiny buffers, constant wrap-around, and disposal while logging. CI runs
them on Linux, Windows and macOS, on .NET 6, 8 and 10 (and .NET Framework 4.8 on Windows), and each run's package
is attached to it as an artifact.

The benchmarks use [BenchmarkDotNet](https://benchmarkdotnet.org) and compare this package with Serilog.Sinks.Async
2.1.0 and with logging straight to the sink (no queue):

```sh
dotnet run -c Release -f net10.0 --project test/Serilog.Sinks.AsyncRing.Benchmarks -- --filter '*'          # everything
dotnet run -c Release -f net10.0 --project test/Serilog.Sinks.AsyncRing.Benchmarks -- --filter '*LogCall*'  # one class
```

Every benchmark runs on both .NET 8 and .NET 10 (`-f` only picks the runtime that hosts BenchmarkDotNet itself).

- **`LogCallBenchmarks`:** what a logging call costs the thread making it, while 1, 4 or 16 threads log at once.
  The buffer is big enough that nothing is dropped.
- **`ThroughputBenchmarks`:** how long it takes for a batch of events to be logged and to reach the wrapped sink.
- **`OverloadBenchmarks`:** threads logging flat out into the default 10,000-event buffer. The `Delivered` column
  shows the share of events that weren't dropped.

Each benchmark case runs in its own process, and the buffer is drained between iterations. Results are written
to `BenchmarkDotNet.Artifacts/results` in the directory you run it from.

## License

Apache 2.0. The public API, its documentation and the ported tests come from
[Serilog.Sinks.Async](https://github.com/serilog/serilog-sinks-async), Copyright © Serilog Contributors, also
licensed under Apache 2.0.
