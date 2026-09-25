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
Last updated 2026-09-25 14:25 UTC from commit `58c8bb1` ([workflow run](https://github.com/c-schembri/serilog-sinks-asyncring/actions/runs/36146183299)).

<details>
<summary><b>Linux</b>: AsyncRing is 3.5× faster per logging call with 16 threads</summary>

Linux Ubuntu 24.04.5 LTS (Noble Numbat) · Intel Xeon 6973P-C 2.60GHz, 4 logical and 2 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 615 ns | 344 ns | 126 ns | 1.8× faster |
| .NET 10 | 4 | 2,814 ns | 674 ns | 361 ns | 4.2× faster |
| .NET 10 | 16 | 12,607 ns | 3,569 ns | 1,356 ns | 3.5× faster |
| .NET 8 | 1 | 664 ns | 379 ns | 155 ns | 1.8× faster |
| .NET 8 | 4 | 2,894 ns | 752 ns | 342 ns | 3.8× faster |
| .NET 8 | 16 | 13,524 ns | 3,825 ns | 1,512 ns | 3.5× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 1.4M/s | 2.7M/s | 8.1M/s | 1.9× faster |
| .NET 10 | 4 | 1.3M/s | 5.8M/s | 12.8M/s | 4.5× faster |
| .NET 10 | 16 | 1.2M/s | 4.2M/s | 11.9M/s | 3.5× faster |
| .NET 8 | 1 | 1.4M/s | 2.6M/s | 6.1M/s | 1.9× faster |
| .NET 8 | 4 | 1.2M/s | 5.3M/s | 11.3M/s | 4.4× faster |
| .NET 8 | 16 | 1.2M/s | 3.9M/s | 9.9M/s | 3.4× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 671 ns, 100% delivered | 357 ns, 100% delivered |
| .NET 10 | 4 | 1,644 ns, 58.7% delivered | 569 ns, 72.6% delivered |
| .NET 10 | 16 | 4,201 ns, 13% delivered | 2,433 ns, 31% delivered |
| .NET 8 | 1 | 708 ns, 100% delivered | 362 ns, 100% delivered |
| .NET 8 | 4 | 1,829 ns, 55.7% delivered | 666 ns, 76.8% delivered |
| .NET 8 | 16 | 4,784 ns, 17.3% delivered | 3,006 ns, 34.2% delivered |

<details>
<summary>Memory and allocations</summary>

Memory in use is the managed heap after garbage collections, above what it was before the logger existed (average over time / peak). Allocations per second are estimated from the runtime's allocation sampling; allocated bytes are exact. Faster sinks log more events per second, so they allocate more per second: compare the bytes per event.

**Cost of a logging call**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 34.4 KB / 124.9 KB | 13.1M | 661.0 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.0 MB / 32.0 MB | 22.6M | 1.15 GB/s | 426 B |
| .NET 10 | 1 | No queue | 0 B / 39.7 KB | 63.8M | 3.16 GB/s | 426 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 34.8 MB / 93.7 MB | 8.4M | 578.2 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 32.7 MB / 39.8 MB | 34.7M | 2.36 GB/s | 426 B |
| .NET 10 | 4 | No queue | 0 B / 16.6 KB | 64.6M | 4.41 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 328.0 MB / 648.9 MB | 7.5M | 517.1 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 57.0 MB / 145.1 MB | 26.1M | 1.78 GB/s | 426 B |
| .NET 10 | 16 | No queue | 2.7 KB / 19.0 KB | 68.1M | 4.69 GB/s | 426 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 1.8 KB / 11.6 KB | 12.1M | 612.4 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 32.0 MB / 32.0 MB | 20.4M | 1.05 GB/s | 426 B |
| .NET 8 | 1 | No queue | 0 B / 33.8 KB | 51.8M | 2.56 GB/s | 426 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 27.9 MB / 71.5 MB | 8.2M | 562.2 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 33.4 MB / 41.3 MB | 31.0M | 2.11 GB/s | 426 B |
| .NET 8 | 4 | No queue | 0 B / 1.8 KB | 67.6M | 4.64 GB/s | 426 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 307.1 MB / 651.1 MB | 7.0M | 481.2 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 47.9 MB / 116.5 MB | 24.3M | 1.66 GB/s | 426 B |
| .NET 8 | 16 | No queue | 10.2 KB / 24.2 KB | 61.3M | 4.20 GB/s | 426 B |

**Throughput**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 11.9 KB / 27.0 KB | 11.5M | 583.5 MB/s | 426 B |
| .NET 10 | 1 | AsyncRing | 32.0 MB / 32.0 MB | 21.6M | 1.07 GB/s | 426 B |
| .NET 10 | 1 | No queue | 0 B / 0 B | 64.8M | 3.21 GB/s | 426 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 83.7 MB / 213.9 MB | 7.7M | 526.9 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 33.5 MB / 42.4 MB | 33.5M | 2.30 GB/s | 426 B |
| .NET 10 | 4 | No queue | 0 B / 30.2 KB | 73.9M | 5.09 GB/s | 426 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 293.6 MB / 642.5 MB | 7.1M | 486.1 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 52.1 MB / 130.3 MB | 24.1M | 1.65 GB/s | 426 B |
| .NET 10 | 16 | No queue | 6.0 KB / 38.1 KB | 68.5M | 4.71 GB/s | 426 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 1.6 KB / 10.0 KB | 11.0M | 559.2 MB/s | 426 B |
| .NET 8 | 1 | AsyncRing | 32.0 MB / 32.0 MB | 20.5M | 1.01 GB/s | 426 B |
| .NET 8 | 1 | No queue | 0 B / 3.5 KB | 49.3M | 2.44 GB/s | 426 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 85.9 MB / 213.7 MB | 7.2M | 490.4 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 33.4 MB / 41.9 MB | 30.6M | 2.10 GB/s | 426 B |
| .NET 8 | 4 | No queue | 2.8 KB / 50.9 KB | 65.3M | 4.51 GB/s | 426 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 281.5 MB / 585.6 MB | 6.9M | 476.0 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 51.4 MB / 127.6 MB | 23.0M | 1.57 GB/s | 426 B |
| .NET 8 | 16 | No queue | 10.9 KB / 24.7 KB | 57.2M | 3.92 GB/s | 426 B |

**Overload with the default 10,000-event buffer**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 24.0 KB / 27.9 KB | 11.9M | 606.0 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 248.0 KB / 269.9 KB | 22.4M | 1.11 GB/s | 426 B |
| .NET 10 | 1 | No queue | 0 B / 7.4 KB | 37.1M | 1.84 GB/s | 426 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 2.5 MB / 4.0 MB | 21.8M | 1.09 GB/s | 482 B |
| .NET 10 | 4 | AsyncRing | 911.4 KB / 3.8 MB | 53.5M | 2.86 GB/s | 438 B |
| .NET 10 | 4 | No queue | 0 B / 6.5 KB | 76.5M | 5.23 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 7.0 MB / 16.4 MB | 34.1M | 1.86 GB/s | 525 B |
| .NET 10 | 16 | AsyncRing | 3.3 MB / 12.9 MB | 65.6M | 2.75 GB/s | 449 B |
| .NET 10 | 16 | No queue | 0 B / 9.5 KB | 68.6M | 4.72 GB/s | 426 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 55 B / 4.2 KB | 11.4M | 574.5 MB/s | 426 B |
| .NET 8 | 1 | AsyncRing | 255.5 KB / 275.5 KB | 22.2M | 1.10 GB/s | 426 B |
| .NET 8 | 1 | No queue | 0 B / 9.9 KB | 54.3M | 2.68 GB/s | 426 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 2.6 MB / 4.0 MB | 19.7M | 1009.4 MB/s | 484 B |
| .NET 8 | 4 | AsyncRing | 678.5 KB / 3.8 MB | 43.9M | 2.43 GB/s | 435 B |
| .NET 8 | 4 | No queue | 0 B / 1.5 KB | 59.8M | 4.11 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 5.3 MB / 12.5 MB | 29.7M | 1.62 GB/s | 520 B |
| .NET 8 | 16 | AsyncRing | 3.4 MB / 11.1 MB | 52.3M | 2.22 GB/s | 448 B |
| .NET 8 | 16 | No queue | 8.8 KB / 26.6 KB | 62.3M | 4.27 GB/s | 426 B |

</details>

</details>

<details>
<summary><b>Windows</b>: AsyncRing is 2.0× faster per logging call with 16 threads</summary>

Windows 11 (10.0.26100.33296/24H2/2024Update/HudsonValley) (Hyper-V) · AMD EPYC 7763 2.44GHz, 4 logical and 2 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 477 ns | 340 ns | 327 ns | 1.4× faster |
| .NET 10 | 4 | 2,280 ns | 1,174 ns | 337 ns | 1.9× faster |
| .NET 10 | 16 | 11,928 ns | 6,091 ns | 1,380 ns | 2.0× faster |
| .NET 8 | 1 | 452 ns | 352 ns | 213 ns | 1.3× faster |
| .NET 8 | 4 | 2,334 ns | 1,235 ns | 430 ns | 1.9× faster |
| .NET 8 | 16 | 13,231 ns | 6,970 ns | 1,745 ns | 1.9× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 2.4M/s | 3.2M/s | 5.6M/s | 1.3× faster |
| .NET 10 | 4 | 1.6M/s | 3.1M/s | 11.6M/s | 2.0× faster |
| .NET 10 | 16 | 1.3M/s | 2.0M/s | 10.6M/s | 1.5× faster |
| .NET 8 | 1 | 2.3M/s | 2.8M/s | 4.9M/s | 1.3× faster |
| .NET 8 | 4 | 1.8M/s | 2.8M/s | 9.1M/s | 1.6× faster |
| .NET 8 | 16 | 1.1M/s | 2.2M/s | 8.7M/s | 2.0× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 455 ns, 100% delivered | 338 ns, 100% delivered |
| .NET 10 | 4 | 1,500 ns, 68.5% delivered | 900 ns, 66.4% delivered |
| .NET 10 | 16 | 4,029 ns, 27.3% delivered | 3,268 ns, 47.5% delivered |
| .NET 8 | 1 | 469 ns, 100% delivered | 354 ns, 100% delivered |
| .NET 8 | 4 | 1,517 ns, 85.1% delivered | 906 ns, 66.2% delivered |
| .NET 8 | 16 | 4,664 ns, 30.7% delivered | 3,864 ns, 49.3% delivered |

<details>
<summary>Memory and allocations</summary>

Memory in use is the managed heap after garbage collections, above what it was before the logger existed (average over time / peak). Allocations per second are estimated from the runtime's allocation sampling; allocated bytes are exact. Faster sinks log more events per second, so they allocate more per second: compare the bytes per event.

**Cost of a logging call**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 45.6 KB / 56.4 KB | 16.7M | 853.5 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.0 MB / 32.1 MB | 22.8M | 1.17 GB/s | 426 B |
| .NET 10 | 1 | No queue | 29.6 KB / 46.3 KB | 24.6M | 1.21 GB/s | 426 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 24.4 MB / 51.1 MB | 10.4M | 714.1 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 43.8 MB / 74.4 MB | 20.0M | 1.35 GB/s | 426 B |
| .NET 10 | 4 | No queue | 0 B / 0 B | 69.4M | 4.72 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 171.0 MB / 412.4 MB | 8.0M | 546.3 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 54.5 MB / 104.2 MB | 15.5M | 1.04 GB/s | 427 B |
| .NET 10 | 16 | No queue | 71.4 KB / 117.2 KB | 67.6M | 4.61 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 0 B / 0 B | 17.8M | 900.1 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 31.9 MB / 31.9 MB | 22.1M | 1.13 GB/s | 426 B |
| .NET 8 | 1 | No queue | 0 B / 0 B | 37.8M | 1.86 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 19.2 MB / 45.5 MB | 10.1M | 697.5 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 43.1 MB / 56.1 MB | 19.0M | 1.29 GB/s | 426 B |
| .NET 8 | 4 | No queue | 0 B / 0 B | 54.1M | 3.70 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 269.7 MB / 578.8 MB | 7.2M | 492.1 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 52.7 MB / 82.8 MB | 13.5M | 934.0 MB/s | 427 B |
| .NET 8 | 16 | No queue | 0 B / 0 B | 53.6M | 3.64 GB/s | 427 B |

**Throughput**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 101.5 KB / 2.3 MB | 19.2M | 971.2 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.1 MB / 32.2 MB | 25.5M | 1.27 GB/s | 427 B |
| .NET 10 | 1 | No queue | 31.1 KB / 41.3 KB | 45.2M | 2.24 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 25.9 MB / 55.4 MB | 9.2M | 636.4 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 47.3 MB / 77.8 MB | 18.5M | 1.24 GB/s | 427 B |
| .NET 10 | 4 | No queue | 0 B / 0 B | 67.8M | 4.61 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 134.3 MB / 337.0 MB | 7.8M | 535.6 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 55.4 MB / 83.3 MB | 12.0M | 827.4 MB/s | 427 B |
| .NET 10 | 16 | No queue | 0 B / 0 B | 62.2M | 4.23 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 0 B / 0 B | 18.3M | 925.4 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 31.9 MB / 31.9 MB | 22.9M | 1.13 GB/s | 427 B |
| .NET 8 | 1 | No queue | 0 B / 0 B | 39.2M | 1.94 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 19.6 MB / 48.0 MB | 10.3M | 720.6 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 47.8 MB / 73.2 MB | 16.2M | 1.10 GB/s | 427 B |
| .NET 8 | 4 | No queue | 0 B / 0 B | 53.0M | 3.63 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 128.5 MB / 417.3 MB | 6.7M | 460.5 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 53.9 MB / 83.5 MB | 13.1M | 903.1 MB/s | 427 B |
| .NET 8 | 16 | No queue | 0 B / 0 B | 50.7M | 3.46 GB/s | 427 B |

**Overload with the default 10,000-event buffer**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 32.4 KB / 38.6 KB | 17.5M | 894.2 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 280.6 KB / 286.1 KB | 23.8M | 1.18 GB/s | 427 B |
| .NET 10 | 1 | No queue | 31.0 KB / 53.3 KB | 46.4M | 2.29 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 4.6 MB / 11.1 MB | 20.7M | 1.16 GB/s | 467 B |
| .NET 10 | 4 | AsyncRing | 3.5 MB / 11.0 MB | 35.2M | 1.81 GB/s | 438 B |
| .NET 10 | 4 | No queue | 38.5 KB / 87.2 KB | 52.1M | 3.56 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 6.9 MB / 18.0 MB | 33.5M | 1.89 GB/s | 511 B |
| .NET 10 | 16 | AsyncRing | 4.8 MB / 19.0 MB | 44.4M | 2.02 GB/s | 444 B |
| .NET 10 | 16 | No queue | 71.0 KB / 124.6 KB | 62.0M | 4.21 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 0 B / 0 B | 17.1M | 867.5 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 115.7 KB / 122.0 KB | 22.8M | 1.12 GB/s | 427 B |
| .NET 8 | 1 | No queue | 0 B / 0 B | 39.2M | 1.93 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 3.8 MB / 11.8 MB | 16.8M | 1.09 GB/s | 445 B |
| .NET 8 | 4 | AsyncRing | 2.8 MB / 3.7 MB | 35.1M | 1.80 GB/s | 438 B |
| .NET 8 | 4 | No queue | 0 B / 0 B | 53.0M | 3.63 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 6.6 MB / 19.2 MB | 27.9M | 1.61 GB/s | 505 B |
| .NET 8 | 16 | AsyncRing | 3.9 MB / 14.8 MB | 37.0M | 1.71 GB/s | 442 B |
| .NET 8 | 16 | No queue | 0 B / 0 B | 52.8M | 3.60 GB/s | 427 B |

</details>

</details>

<details>
<summary><b>macOS</b>: AsyncRing is 1.8× faster per logging call with 16 threads</summary>

macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0] · Apple M1 (Virtual), 3 logical and 3 physical cores

**Cost of a logging call** (per call, on each logging thread; nothing dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 376 ns | 314 ns | 178 ns | 1.2× faster |
| .NET 10 | 4 | 2,427 ns | 1,207 ns | 475 ns | 2.0× faster |
| .NET 10 | 16 | 11,130 ns | 6,144 ns | 1,626 ns | 1.8× faster |
| .NET 8 | 1 | 424 ns | 466 ns | 268 ns | 1.1× slower |
| .NET 8 | 4 | 2,185 ns | 1,158 ns | 498 ns | 1.9× faster |
| .NET 8 | 16 | 13,362 ns | 9,450 ns | 1,788 ns | 1.4× faster |

**Throughput** (events per second reaching the sink)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing | No queue | AsyncRing is |
|---|---|---|---|---|---|
| .NET 10 | 1 | 2.4M/s | 3.1M/s | 6.3M/s | 1.3× faster |
| .NET 10 | 4 | 1.9M/s | 5.1M/s | 10.8M/s | 2.7× faster |
| .NET 10 | 16 | 1.6M/s | 2.5M/s | 10.7M/s | 1.6× faster |
| .NET 8 | 1 | 2.2M/s | 3.6M/s | 5.7M/s | 1.6× faster |
| .NET 8 | 4 | 1.9M/s | 4.5M/s | 11.7M/s | 2.4× faster |
| .NET 8 | 16 | 1.1M/s | 2.0M/s | 11.1M/s | 1.8× faster |

**Overload with the default 10,000-event buffer** (per call, and the share of events not dropped)

| Runtime | Threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| .NET 10 | 1 | 445 ns, 81.5% delivered | 236 ns, 94.4% delivered |
| .NET 10 | 4 | 1,539 ns, 63.6% delivered | 739 ns, 79.6% delivered |
| .NET 10 | 16 | 3,066 ns, 17.3% delivered | 2,621 ns, 37.1% delivered |
| .NET 8 | 1 | 514 ns, 90.9% delivered | 394 ns, 86.5% delivered |
| .NET 8 | 4 | 1,689 ns, 77.4% delivered | 1,269 ns, 69.4% delivered |
| .NET 8 | 16 | 3,674 ns, 22.2% delivered | 2,812 ns, 31.6% delivered |

<details>
<summary>Memory and allocations</summary>

Memory in use is the managed heap after garbage collections, above what it was before the logger existed (average over time / peak). Allocations per second are estimated from the runtime's allocation sampling; allocated bytes are exact. Faster sinks log more events per second, so they allocate more per second: compare the bytes per event.

**Cost of a logging call**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 414.2 KB / 515.9 KB | 21.4M | 1.06 GB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 32.5 MB / 38.0 MB | 24.7M | 1.26 GB/s | 427 B |
| .NET 10 | 1 | No queue | 31.8 KB / 89.0 KB | 45.2M | 2.23 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 35.9 MB / 70.0 MB | 10.1M | 671.3 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 45.8 MB / 65.6 MB | 20.3M | 1.32 GB/s | 427 B |
| .NET 10 | 4 | No queue | 109.6 KB / 171.9 KB | 51.0M | 3.35 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 160.0 MB / 366.6 MB | 8.8M | 585.4 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 78.7 MB / 251.2 MB | 15.6M | 1.04 GB/s | 427 B |
| .NET 10 | 16 | No queue | 113.4 KB / 254.4 KB | 58.1M | 3.91 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 5.2 MB / 7.1 MB | 18.9M | 959.7 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 42.6 MB / 58.8 MB | 16.8M | 872.6 MB/s | 427 B |
| .NET 8 | 1 | No queue | 6.0 MB / 6.0 MB | 30.2M | 1.48 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 29.5 MB / 55.4 MB | 11.1M | 746.3 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 44.3 MB / 55.2 MB | 20.6M | 1.37 GB/s | 427 B |
| .NET 8 | 4 | No queue | 5.4 MB / 5.4 MB | 46.6M | 3.19 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 63.8 MB / 121.2 MB | 7.2M | 488.2 MB/s | 428 B |
| .NET 8 | 16 | AsyncRing | 60.7 MB / 88.8 MB | 10.1M | 690.6 MB/s | 428 B |
| .NET 8 | 16 | No queue | 6.0 MB / 6.1 MB | 53.2M | 3.56 GB/s | 427 B |

**Throughput**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 4.9 MB / 25.8 MB | 19.7M | 995.8 MB/s | 427 B |
| .NET 10 | 1 | AsyncRing | 40.8 MB / 72.1 MB | 25.3M | 1.25 GB/s | 427 B |
| .NET 10 | 1 | No queue | 125.2 KB / 227.1 KB | 50.6M | 2.50 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 43.1 MB / 110.7 MB | 11.6M | 778.6 MB/s | 427 B |
| .NET 10 | 4 | AsyncRing | 45.6 MB / 96.9 MB | 30.8M | 2.05 GB/s | 427 B |
| .NET 10 | 4 | No queue | 100.8 KB / 227.3 KB | 64.6M | 4.31 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 185.7 MB / 580.3 MB | 9.6M | 647.9 MB/s | 427 B |
| .NET 10 | 16 | AsyncRing | 71.7 MB / 158.3 MB | 15.4M | 1.01 GB/s | 427 B |
| .NET 10 | 16 | No queue | 112.6 KB / 235.0 KB | 61.3M | 4.26 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 6.8 MB / 20.1 MB | 17.9M | 903.2 MB/s | 427 B |
| .NET 8 | 1 | AsyncRing | 36.9 MB / 43.3 MB | 29.0M | 1.43 GB/s | 427 B |
| .NET 8 | 1 | No queue | 4.4 MB / 6.2 MB | 46.0M | 2.27 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 35.5 MB / 107.7 MB | 11.3M | 763.1 MB/s | 427 B |
| .NET 8 | 4 | AsyncRing | 41.9 MB / 59.0 MB | 27.0M | 1.78 GB/s | 427 B |
| .NET 8 | 4 | No queue | 5.9 MB / 6.0 MB | 68.0M | 4.65 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 63.5 MB / 122.7 MB | 6.8M | 450.9 MB/s | 427 B |
| .NET 8 | 16 | AsyncRing | 61.2 MB / 89.9 MB | 12.0M | 821.6 MB/s | 428 B |
| .NET 8 | 16 | No queue | 6.0 MB / 6.0 MB | 64.2M | 4.43 GB/s | 427 B |

**Overload with the default 10,000-event buffer**

| Runtime | Threads | Sink | Memory in use (avg / peak) | Allocations/s | Allocated/s | Allocated/event |
|---|---|---|---|---|---|---|
| .NET 10 | 1 | Serilog.Sinks.Async | 3.4 MB / 9.9 MB | 19.3M | 980.8 MB/s | 458 B |
| .NET 10 | 1 | AsyncRing | 1.6 MB / 9.8 MB | 34.3M | 1.69 GB/s | 430 B |
| .NET 10 | 1 | No queue | 15.7 KB / 19.6 KB | 55.5M | 2.75 GB/s | 427 B |
| .NET 10 | 4 | Serilog.Sinks.Async | 7.5 MB / 19.2 MB | 19.9M | 1.15 GB/s | 477 B |
| .NET 10 | 4 | AsyncRing | 5.2 MB / 16.8 MB | 39.0M | 2.19 GB/s | 434 B |
| .NET 10 | 4 | No queue | 49.2 KB / 115.8 KB | 64.5M | 4.22 GB/s | 427 B |
| .NET 10 | 16 | Serilog.Sinks.Async | 6.8 MB / 17.5 MB | 43.8M | 2.52 GB/s | 519 B |
| .NET 10 | 16 | AsyncRing | 6.7 MB / 19.4 MB | 59.1M | 2.54 GB/s | 447 B |
| .NET 10 | 16 | No queue | 128.6 KB / 253.5 KB | 61.8M | 3.94 GB/s | 427 B |
| .NET 8 | 1 | Serilog.Sinks.Async | 4.7 MB / 11.1 MB | 16.2M | 820.8 MB/s | 442 B |
| .NET 8 | 1 | AsyncRing | 5.4 MB / 13.4 MB | 20.9M | 1.02 GB/s | 433 B |
| .NET 8 | 1 | No queue | 2.8 MB / 6.0 MB | 35.0M | 1.72 GB/s | 427 B |
| .NET 8 | 4 | Serilog.Sinks.Async | 9.7 MB / 18.8 MB | 16.3M | 1.01 GB/s | 460 B |
| .NET 8 | 4 | AsyncRing | 11.4 MB / 18.8 MB | 24.7M | 1.28 GB/s | 436 B |
| .NET 8 | 4 | No queue | 6.0 MB / 6.0 MB | 55.3M | 3.67 GB/s | 427 B |
| .NET 8 | 16 | Serilog.Sinks.Async | 12.6 MB / 21.0 MB | 36.9M | 2.10 GB/s | 517 B |
| .NET 8 | 16 | AsyncRing | 12.3 MB / 20.2 MB | 57.3M | 2.39 GB/s | 451 B |
| .NET 8 | 16 | No queue | 5.9 MB / 6.1 MB | 60.1M | 4.00 GB/s | 427 B |

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
