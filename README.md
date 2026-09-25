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
| 1 | 339 | 277 | 197 |
| 4 | 2,493 | 303 | 242 |
| 16 | 11,807 | 797 | 582 |

With one thread the results are noisy. At 16 threads, 12% of Serilog.Sinks.Async's logging calls ran into a lock
another thread was holding; for AsyncRing it was 0.03%.

**Throughput** (events per second reaching the wrapped sink):

| Logging threads | Serilog.Sinks.Async | AsyncRing | No queue |
|---|---|---|---|
| 1 | 4.3 million | 5.8 million | 9.8 million |
| 4 | 1.9 million | 14.9 million | 27.1 million |
| 16 | 1.4 million | 19.8 million | 24.2 million |

**Overload with the default 10,000-event buffer** (ns per call, and the share of events that weren't dropped):

| Logging threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|
| 1 | 309 ns, 100% delivered | 322 ns, 100% delivered |
| 4 | 1,054 ns, 59% delivered | 316 ns, 99.9% delivered |
| 16 | 2,152 ns, 7.4% delivered | 759 ns, 99.9% delivered |

**Memory and allocations** (16 logging threads):

| Benchmark | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|
| Cost of a logging call (2,000,000-event buffer) | Serilog.Sinks.Async | 301 MB / 588 MB | 8.0 million | 565 MB/s | 437 B |
| | AsyncRing | 32.1 MB / 34.5 MB | 115 million | 7.98 GB/s | 427 B |
| Throughput (2,000,000-event buffer) | Serilog.Sinks.Async | 308 MB / 603 MB | 8.2 million | 578 MB/s | 437 B |
| | AsyncRing | 32.1 MB / 34.6 MB | 114 million | 7.87 GB/s | 427 B |
| Overload (default 10,000-event buffer) | Serilog.Sinks.Async | 6.9 MB / 15.5 MB | 67.0 million | 3.68 GB/s | 531 B |
| | AsyncRing | 358 KB / 550 KB | 121 million | 8.38 GB/s | 427 B |

- **Memory in use** is the managed heap after each garbage collection (what's still referenced, not garbage
  waiting to be collected), above what it was before the logger existed. AsyncRing allocates its buffer up front
  (32 MB for the 2,000,000 events the no-drop benchmarks allow, 256 KB for the default 10,000), and it stays
  there. Serilog.Sinks.Async starts small but grows as its background thread falls behind: at 16 threads,
  hundreds of megabytes of events pile up waiting to be written.
- **Allocated per event** is what Serilog allocates to create each event (427 bytes here). AsyncRing adds
  nothing to that. Serilog.Sinks.Async adds a little as its queue grows, and more when it drops events, because
  it builds a message for each dropped one.
- **Allocations per second** are higher for AsyncRing only because it logs far more events per second.
  Allocations are estimated from the runtime's allocation sampling; allocated bytes are exact.

**On a 4-core machine** (the same benchmarks pinned to 4 logical / 2 physical cores, like GitHub's runners):

| Logging threads | Cost of a logging call | Throughput | Delivered under overload |
|---|---|---|---|
| 4 | 1,250 ns → 481 ns | 3.1 → 8.8 million/s | 80% → 81% |
| 16 | 5,844 ns → 2,991 ns | 2.7 → 5.9 million/s | 48% → 73% |

Each cell is Serilog.Sinks.Async → AsyncRing. With more logging threads than cores, the operating system often
pauses a logging thread between claiming a slot and filling it in, and the background thread has to wait for it,
so AsyncRing's lead is smaller than with cores to spare.

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
Last updated 2026-09-25 08:13 UTC from commit `ce93c02` ([workflow run](https://github.com/c-schembri/serilog-sinks-asyncring/actions/runs/36110592383)).

<details>
<summary><b>Linux</b>: AsyncRing is 2.7× faster per logging call with 16 threads</summary>

Linux Ubuntu 24.04.5 LTS (Noble Numbat) · AMD EPYC 9V74 2.60GHz, 4 logical and 2 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 439 ns | 297 ns | 159 ns | 1.5× faster |
| .NET 10 | 4 | 1,895 ns | 655 ns | 325 ns | 2.9× faster |
| .NET 10 | 16 | 11,538 ns | 4,250 ns | 1,433 ns | 2.7× faster |
| .NET 8 | 1 | 386 ns | 338 ns | 181 ns | 1.1× faster |
| .NET 8 | 4 | 1,488 ns | 725 ns | 394 ns | 2.1× faster |
| .NET 8 | 16 | 12,116 ns | 4,342 ns | 1,776 ns | 2.8× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 2.2M/s | 3.3M/s | 6.9M/s | 1.5× faster |
| .NET 10 | 4 | 2.2M/s | 6.1M/s | 12.3M/s | 2.8× faster |
| .NET 10 | 16 | 1.5M/s | 3.9M/s | 11.4M/s | 2.7× faster |
| .NET 8 | 1 | 2.5M/s | 3.0M/s | 5.8M/s | 1.2× faster |
| .NET 8 | 4 | 3.0M/s | 5.8M/s | 10.6M/s | 1.9× faster |
| .NET 8 | 16 | 1.4M/s | 3.6M/s | 9.1M/s | 2.7× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 396 ns, 100% delivered | 315 ns, 100% delivered |
| .NET 10 | 4 | 1,154 ns, 86.7% delivered | 630 ns, 80.7% delivered |
| .NET 10 | 16 | 3,680 ns, 24.5% delivered | 2,577 ns, 36.5% delivered |
| .NET 8 | 1 | 395 ns, 100% delivered | 354 ns, 100% delivered |
| .NET 8 | 4 | 1,277 ns, 81.4% delivered | 636 ns, 86.4% delivered |
| .NET 8 | 16 | 4,521 ns, 34.5% delivered | 2,973 ns, 44.8% delivered |

<details>
<summary>Memory and allocations</summary>

Memory in use is the managed heap after garbage collections, above what it was before the logger existed (average over time / peak). Allocations per second are estimated from the runtime's allocation sampling; allocated bytes are exact. Faster sinks log more events per second, so they allocate more per second: compare the bytes per event.

**Cost of a logging call**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 9.4 KB / 31.4 KB | 18.2M | 927.1 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.0 MB / 32.0 MB | 26.1M | 1.33 GB/s | 426 B |
| .NET 10 | 1 | No queue | 0 B / 0 B | 50.7M | 2.49 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 18.0 MB / 55.6 MB | 12.5M | 859.0 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 34.1 MB / 47.6 MB | 35.3M | 2.43 GB/s | 427 B |
| .NET 10 | 4 | No queue | 0 B / 4.1 KB | 72.1M | 4.90 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 360.3 MB / 641.7 MB | 8.3M | 564.3 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 47.0 MB / 96.5 MB | 22.2M | 1.50 GB/s | 427 B |
| .NET 10 | 16 | No queue | 18.3 KB / 41.8 KB | 65.8M | 4.44 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 1.3 KB / 9.5 KB | 20.8M | 1.03 GB/s | 426 B |
| .NET 8 | 1 | AsyncRing | 32.0 MB / 32.0 MB | 23.0M | 1.18 GB/s | 426 B |
| .NET 8 | 1 | No queue | 0 B / 1.6 KB | 44.7M | 2.20 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 12.3 MB / 40.8 MB | 15.7M | 1.07 GB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 34.1 MB / 48.0 MB | 31.8M | 2.19 GB/s | 427 B |
| .NET 8 | 4 | No queue | 60 B / 12.0 KB | 59.0M | 4.03 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 361.2 MB / 643.2 MB | 7.9M | 537.3 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 44.8 MB / 81.7 MB | 21.6M | 1.46 GB/s | 427 B |
| .NET 8 | 16 | No queue | 36.1 KB / 104.5 KB | 52.4M | 3.58 GB/s | 427 B |

**Throughput**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 73.3 KB / 94.0 KB | 17.8M | 901.9 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.0 MB / 32.1 MB | 26.4M | 1.31 GB/s | 427 B |
| .NET 10 | 1 | No queue | 588 B / 29.3 KB | 55.6M | 2.74 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 21.1 MB / 62.2 MB | 12.9M | 893.5 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 35.3 MB / 52.4 MB | 35.2M | 2.43 GB/s | 427 B |
| .NET 10 | 4 | No queue | 22.4 KB / 134.9 KB | 72.0M | 4.89 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 355.5 MB / 641.5 MB | 8.8M | 597.3 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 49.7 MB / 91.5 MB | 23.2M | 1.56 GB/s | 427 B |
| .NET 10 | 16 | No queue | 18.1 KB / 43.5 KB | 67.0M | 4.54 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 21.4 KB / 41.2 KB | 20.4M | 1.01 GB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 32.0 MB / 32.0 MB | 24.0M | 1.19 GB/s | 427 B |
| .NET 8 | 1 | No queue | 4.1 KB / 17.7 KB | 46.6M | 2.30 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 13.9 MB / 51.6 MB | 17.5M | 1.20 GB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 33.4 MB / 45.1 MB | 33.4M | 2.31 GB/s | 427 B |
| .NET 8 | 4 | No queue | 31.5 KB / 86.3 KB | 61.2M | 4.20 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 342.4 MB / 623.4 MB | 8.2M | 556.0 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 46.1 MB / 80.7 MB | 21.4M | 1.44 GB/s | 427 B |
| .NET 8 | 16 | No queue | 33.7 KB / 73.5 KB | 52.8M | 3.60 GB/s | 427 B |

**Overload with the default 10,000-event buffer**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 11.5 KB / 31.7 KB | 20.3M | 1.00 GB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 248.6 KB / 262.7 KB | 25.6M | 1.26 GB/s | 427 B |
| .NET 10 | 1 | No queue | 0 B / 0 B | 39.5M | 1.94 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 3.0 MB / 10.0 MB | 22.7M | 1.43 GB/s | 444 B |
| .NET 10 | 4 | AsyncRing | 1.2 MB / 4.0 MB | 44.7M | 2.56 GB/s | 434 B |
| .NET 10 | 4 | No queue | 0 B / 16.6 KB | 68.9M | 4.66 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 6.5 MB / 20.1 MB | 36.1M | 2.08 GB/s | 513 B |
| .NET 10 | 16 | AsyncRing | 5.6 MB / 15.5 MB | 60.4M | 2.59 GB/s | 447 B |
| .NET 10 | 16 | No queue | 16.7 KB / 41.6 KB | 66.3M | 4.48 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 3.1 KB / 13.7 KB | 20.4M | 1.01 GB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 251.6 KB / 255.9 KB | 22.7M | 1.12 GB/s | 427 B |
| .NET 8 | 1 | No queue | 0 B / 0 B | 41.2M | 2.04 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 3.2 MB / 7.7 MB | 20.7M | 1.32 GB/s | 453 B |
| .NET 8 | 4 | AsyncRing | 1.0 MB / 3.8 MB | 42.0M | 2.53 GB/s | 432 B |
| .NET 8 | 4 | No queue | 1.8 KB / 27.6 KB | 58.7M | 4.02 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 6.4 MB / 20.0 MB | 28.1M | 1.65 GB/s | 500 B |
| .NET 8 | 16 | AsyncRing | 5.0 MB / 14.3 MB | 49.8M | 2.23 GB/s | 445 B |
| .NET 8 | 16 | No queue | 35.0 KB / 74.0 KB | 52.8M | 3.60 GB/s | 427 B |

</details>

</details>

<details>
<summary><b>Windows</b>: AsyncRing is 2.4× faster per logging call with 16 threads</summary>

Windows 11 (10.0.26100.33296/24H2/2024Update/HudsonValley) (Hyper-V) · AMD EPYC 7763 2.44GHz, 4 logical and 2 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 480 ns | 325 ns | 235 ns | 1.5× faster |
| .NET 10 | 4 | 2,571 ns | 1,124 ns | 369 ns | 2.3× faster |
| .NET 10 | 16 | 13,152 ns | 5,513 ns | 1,451 ns | 2.4× faster |
| .NET 8 | 1 | 458 ns | 351 ns | 213 ns | 1.3× faster |
| .NET 8 | 4 | 2,365 ns | 1,189 ns | 446 ns | 2.0× faster |
| .NET 8 | 16 | 14,141 ns | 6,331 ns | 1,838 ns | 2.2× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 2.5M/s | 3.1M/s | 4.8M/s | 1.3× faster |
| .NET 10 | 4 | 1.6M/s | 3.6M/s | 11.2M/s | 2.3× faster |
| .NET 10 | 16 | 1.2M/s | 3.0M/s | 11.4M/s | 2.4× faster |
| .NET 8 | 1 | 2.0M/s | 2.6M/s | 4.3M/s | 1.3× faster |
| .NET 8 | 4 | 1.6M/s | 3.6M/s | 9.4M/s | 2.3× faster |
| .NET 8 | 16 | 1.2M/s | 2.8M/s | 9.0M/s | 2.4× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 453 ns, 100% delivered | 337 ns, 100% delivered |
| .NET 10 | 4 | 1,464 ns, 78.7% delivered | 773 ns, 68.8% delivered |
| .NET 10 | 16 | 3,988 ns, 25.5% delivered | 3,965 ns, 57.7% delivered |
| .NET 8 | 1 | 467 ns, 100% delivered | 360 ns, 100% delivered |
| .NET 8 | 4 | 1,445 ns, 81.1% delivered | 894 ns, 69.4% delivered |
| .NET 8 | 16 | 4,520 ns, 29.4% delivered | 4,183 ns, 58.4% delivered |

<details>
<summary>Memory and allocations</summary>

Memory in use is the managed heap after garbage collections, above what it was before the logger existed (average over time / peak). Allocations per second are estimated from the runtime's allocation sampling; allocated bytes are exact. Faster sinks log more events per second, so they allocate more per second: compare the bytes per event.

**Cost of a logging call**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 40.6 KB / 55.2 KB | 16.8M | 848.0 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.0 MB / 32.1 MB | 23.9M | 1.22 GB/s | 426 B |
| .NET 10 | 1 | No queue | 32.8 KB / 60.3 KB | 34.3M | 1.69 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 22.9 MB / 51.6 MB | 9.2M | 633.3 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 41.2 MB / 53.8 MB | 20.9M | 1.41 GB/s | 426 B |
| .NET 10 | 4 | No queue | 32.2 KB / 62.0 KB | 63.4M | 4.31 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 225.6 MB / 542.0 MB | 7.3M | 495.5 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 49.7 MB / 68.5 MB | 17.0M | 1.15 GB/s | 427 B |
| .NET 10 | 16 | No queue | 61.0 KB / 117.1 KB | 64.7M | 4.38 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 0 B / 0 B | 17.4M | 887.8 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 31.9 MB / 31.9 MB | 22.2M | 1.13 GB/s | 426 B |
| .NET 8 | 1 | No queue | 0 B / 0 B | 37.8M | 1.87 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 18.8 MB / 46.6 MB | 9.9M | 688.4 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 42.9 MB / 52.4 MB | 19.6M | 1.34 GB/s | 427 B |
| .NET 8 | 4 | No queue | 0 B / 0 B | 52.3M | 3.57 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 255.3 MB / 577.1 MB | 6.7M | 460.4 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 48.7 MB / 68.8 MB | 14.8M | 1.00 GB/s | 427 B |
| .NET 8 | 16 | No queue | 0 B / 0 B | 50.7M | 3.46 GB/s | 427 B |

**Throughput**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 73.2 KB / 156.8 KB | 19.7M | 999.2 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.0 MB / 32.1 MB | 25.0M | 1.24 GB/s | 427 B |
| .NET 10 | 1 | No queue | 0 B / 0 B | 38.5M | 1.90 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 24.2 MB / 53.0 MB | 9.4M | 650.0 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 45.0 MB / 61.3 MB | 21.4M | 1.45 GB/s | 427 B |
| .NET 10 | 4 | No queue | 0 B / 0 B | 65.6M | 4.45 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 191.9 MB / 447.5 MB | 7.5M | 506.8 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 46.9 MB / 68.7 MB | 17.5M | 1.19 GB/s | 427 B |
| .NET 10 | 16 | No queue | 78.1 KB / 121.5 KB | 67.0M | 4.55 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 343.2 KB / 3.0 MB | 15.8M | 801.1 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 31.9 MB / 34.2 MB | 20.6M | 1.02 GB/s | 427 B |
| .NET 8 | 1 | No queue | 0 B / 0 B | 34.3M | 1.70 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 22.4 MB / 52.6 MB | 9.4M | 652.1 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 42.7 MB / 58.9 MB | 21.4M | 1.44 GB/s | 427 B |
| .NET 8 | 4 | No queue | 0 B / 0 B | 54.4M | 3.72 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 345.0 MB / 641.8 MB | 6.9M | 476.2 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 49.6 MB / 68.0 MB | 16.1M | 1.09 GB/s | 427 B |
| .NET 8 | 16 | No queue | 0 B / 0 B | 52.6M | 3.58 GB/s | 427 B |

**Overload with the default 10,000-event buffer**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 41.3 KB / 60.7 KB | 17.8M | 898.5 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 281.0 KB / 291.6 KB | 24.0M | 1.18 GB/s | 427 B |
| .NET 10 | 1 | No queue | 18.3 KB / 47.4 KB | 37.1M | 1.83 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 4.1 MB / 10.4 MB | 18.2M | 1.15 GB/s | 452 B |
| .NET 10 | 4 | AsyncRing | 2.7 MB / 3.9 MB | 40.1M | 2.11 GB/s | 437 B |
| .NET 10 | 4 | No queue | 30.9 KB / 62.6 KB | 52.8M | 3.59 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 7.0 MB / 16.1 MB | 33.8M | 1.91 GB/s | 512 B |
| .NET 10 | 16 | AsyncRing | 3.3 MB / 3.9 MB | 33.9M | 1.65 GB/s | 440 B |
| .NET 10 | 16 | No queue | 73.9 KB / 127.7 KB | 67.0M | 4.55 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 0 B / 0 B | 17.2M | 871.5 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 112.6 KB / 122.6 KB | 22.3M | 1.10 GB/s | 427 B |
| .NET 8 | 1 | No queue | 0 B / 0 B | 39.1M | 1.92 GB/s | 426 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 4.1 MB / 10.9 MB | 17.7M | 1.15 GB/s | 447 B |
| .NET 8 | 4 | AsyncRing | 2.8 MB / 3.9 MB | 34.6M | 1.82 GB/s | 437 B |
| .NET 8 | 4 | No queue | 0 B / 0 B | 52.7M | 3.60 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 6.2 MB / 15.4 MB | 28.5M | 1.67 GB/s | 507 B |
| .NET 8 | 16 | AsyncRing | 3.2 MB / 4.0 MB | 32.0M | 1.57 GB/s | 440 B |
| .NET 8 | 16 | No queue | 0 B / 0 B | 52.3M | 3.57 GB/s | 427 B |

</details>

</details>

<details>
<summary><b>macOS</b>: AsyncRing is 1.9× faster per logging call with 16 threads</summary>

macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0] · Apple M1 (Virtual), 3 logical and 3 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 442 ns | 370 ns | 186 ns | 1.2× faster |
| .NET 10 | 4 | 2,625 ns | 1,216 ns | 481 ns | 2.2× faster |
| .NET 10 | 16 | 15,862 ns | 8,505 ns | 2,141 ns | 1.9× faster |
| .NET 8 | 1 | 468 ns | 402 ns | 196 ns | 1.2× faster |
| .NET 8 | 4 | 3,311 ns | 2,209 ns | 591 ns | 1.5× faster |
| .NET 8 | 16 | 16,967 ns | 9,866 ns | 2,017 ns | 1.7× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 1.5M/s | 3.4M/s | 6.3M/s | 2.3× faster |
| .NET 10 | 4 | 1.3M/s | 2.3M/s | 7.3M/s | 1.7× faster |
| .NET 10 | 16 | 1.0M/s | 1.7M/s | 6.0M/s | 1.8× faster |
| .NET 8 | 1 | 2.4M/s | 2.3M/s | 4.5M/s | 1.1× slower |
| .NET 8 | 4 | 1.1M/s | 1.9M/s | 6.4M/s | 1.8× faster |
| .NET 8 | 16 | 0.9M/s | 1.7M/s | 5.5M/s | 1.8× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 789 ns, 78.4% delivered | 682 ns, 76.8% delivered |
| .NET 10 | 4 | 2,876 ns, 58.7% delivered | 1,992 ns, 64.6% delivered |
| .NET 10 | 16 | 4,497 ns, 17.9% delivered | 5,416 ns, 29.8% delivered |
| .NET 8 | 1 | 779 ns, 81.1% delivered | 606 ns, 72.9% delivered |
| .NET 8 | 4 | 2,930 ns, 61.3% delivered | 1,941 ns, 67.6% delivered |
| .NET 8 | 16 | 3,680 ns, 20.5% delivered | 6,808 ns, 46.9% delivered |

<details>
<summary>Memory and allocations</summary>

Memory in use is the managed heap after garbage collections, above what it was before the logger existed (average over time / peak). Allocations per second are estimated from the runtime's allocation sampling; allocated bytes are exact. Faster sinks log more events per second, so they allocate more per second: compare the bytes per event.

**Cost of a logging call**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 294.7 KB / 2.6 MB | 18.2M | 921.1 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.9 MB / 39.5 MB | 21.1M | 1.08 GB/s | 427 B |
| .NET 10 | 1 | No queue | 7.4 KB / 56.9 KB | 43.1M | 2.13 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 24.1 MB / 54.4 MB | 9.6M | 620.4 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 41.1 MB / 57.4 MB | 20.0M | 1.31 GB/s | 427 B |
| .NET 10 | 4 | No queue | 129.8 KB / 217.0 KB | 49.6M | 3.31 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 133.4 MB / 379.9 MB | 6.4M | 410.8 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 70.2 MB / 136.4 MB | 11.7M | 766.0 MB/s | 427 B |
| .NET 10 | 16 | No queue | 125.0 KB / 273.2 KB | 46.3M | 2.97 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 5.8 MB / 10.7 MB | 17.2M | 870.1 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 36.2 MB / 40.4 MB | 19.4M | 1011.2 MB/s | 427 B |
| .NET 8 | 1 | No queue | 5.2 MB / 6.0 MB | 41.0M | 2.03 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 24.8 MB / 51.2 MB | 7.5M | 492.2 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 49.2 MB / 67.3 MB | 11.3M | 737.2 MB/s | 427 B |
| .NET 8 | 4 | No queue | 5.0 MB / 5.4 MB | 42.2M | 2.69 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 50.3 MB / 106.2 MB | 5.8M | 384.4 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 57.5 MB / 85.2 MB | 9.8M | 661.4 MB/s | 428 B |
| .NET 8 | 16 | No queue | 5.2 MB / 5.5 MB | 48.3M | 3.16 GB/s | 427 B |

**Throughput**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 7.4 MB / 37.2 MB | 11.7M | 592.9 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.1 MB / 33.5 MB | 27.3M | 1.35 GB/s | 427 B |
| .NET 10 | 1 | No queue | 87.7 KB / 212.8 KB | 50.4M | 2.49 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 38.1 MB / 82.7 MB | 8.5M | 537.2 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 43.3 MB / 68.9 MB | 14.2M | 927.9 MB/s | 427 B |
| .NET 10 | 4 | No queue | 117.4 KB / 231.3 KB | 46.3M | 2.91 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 129.0 MB / 327.7 MB | 6.4M | 401.1 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 68.4 MB / 136.1 MB | 11.3M | 708.8 MB/s | 427 B |
| .NET 10 | 16 | No queue | 103.2 KB / 238.7 KB | 41.0M | 2.37 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 4.9 MB / 8.1 MB | 19.3M | 978.0 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 38.8 MB / 57.6 MB | 18.2M | 923.6 MB/s | 427 B |
| .NET 8 | 1 | No queue | 4.5 MB / 6.1 MB | 35.9M | 1.77 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 28.8 MB / 99.7 MB | 6.6M | 431.0 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 44.6 MB / 58.9 MB | 12.6M | 790.9 MB/s | 427 B |
| .NET 8 | 4 | No queue | 5.9 MB / 6.0 MB | 40.2M | 2.56 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 107.9 MB / 249.4 MB | 5.7M | 371.0 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 73.9 MB / 146.4 MB | 10.8M | 683.0 MB/s | 427 B |
| .NET 8 | 16 | No queue | 6.0 MB / 6.0 MB | 36.1M | 2.18 GB/s | 427 B |

**Overload with the default 10,000-event buffer**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 4.4 MB / 12.2 MB | 11.0M | 556.3 MB/s | 460 B |
| .NET 10 | 1 | AsyncRing | 3.0 MB / 7.7 MB | 12.2M | 610.3 MB/s | 436 B |
| .NET 10 | 1 | No queue | 15.1 KB / 24.4 KB | 27.1M | 1.34 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 7.1 MB / 19.1 MB | 11.1M | 631.4 MB/s | 476 B |
| .NET 10 | 4 | AsyncRing | 7.7 MB / 14.9 MB | 16.6M | 842.2 MB/s | 440 B |
| .NET 10 | 4 | No queue | 40.8 KB / 93.6 KB | 35.3M | 2.20 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 7.6 MB / 18.8 MB | 30.6M | 1.72 GB/s | 518 B |
| .NET 10 | 16 | AsyncRing | 9.3 MB / 18.6 MB | 29.8M | 1.24 GB/s | 449 B |
| .NET 10 | 16 | No queue | 110.4 KB / 236.0 KB | 45.3M | 2.93 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 7.0 MB / 16.5 MB | 11.0M | 557.1 MB/s | 455 B |
| .NET 8 | 1 | AsyncRing | 7.3 MB / 12.8 MB | 13.8M | 687.6 MB/s | 437 B |
| .NET 8 | 1 | No queue | 3.7 MB / 5.5 MB | 15.7M | 794.9 MB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 9.8 MB / 18.3 MB | 10.4M | 609.7 MB/s | 468 B |
| .NET 8 | 4 | AsyncRing | 10.9 MB / 20.6 MB | 16.8M | 861.9 MB/s | 439 B |
| .NET 8 | 4 | No queue | 4.8 MB / 5.5 MB | 25.0M | 1.51 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 12.3 MB / 25.7 MB | 37.0M | 2.10 GB/s | 519 B |
| .NET 8 | 16 | AsyncRing | 13.9 MB / 24.0 MB | 21.5M | 992.2 MB/s | 443 B |
| .NET 8 | 16 | No queue | 5.3 MB / 5.5 MB | 29.4M | 1.85 GB/s | 427 B |

</details>

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
