# Serilog.Sinks.AsyncRing

An asynchronous wrapper for other [Serilog](https://serilog.net) sinks, and a drop-in replacement for
[Serilog.Sinks.Async](https://github.com/serilog/serilog-sinks-async). It has exactly the same public API,
namespaces and defaults, but hands events to the background thread through a lock-free ring buffer. That makes
it much faster when many threads log at once.

## Performance

Nanoseconds per logging call (lower is better). Threads log as fast as they can into a sink that does nothing,
so this measures the logging pipeline plus the hand-off to the background thread. Measured on a Ryzen 9 9900X
(12 cores, 24 threads) with .NET 8, best of 5 runs. The 16-thread figures are the range over 4 runs.

| Logging threads | No queue (baseline) | Serilog.Sinks.Async | AsyncRing |
|---|---|---|---|
| 1 | 115 | 262 | 182 |
| 4 | 158 | 1,845 | 274 |
| 16 | 600–790 | 10,800–11,500 | 1,750–2,100 |

With the default 10,000-event buffer, Serilog.Sinks.Async's background thread couldn't keep up and dropped most
events. AsyncRing delivered all of them:

| Logging threads | Serilog.Sinks.Async | AsyncRing |
|---|---|---|
| 4 | 859 ns per call, 42% of events delivered | 285 ns per call, 100% delivered |
| 16 | 2,043 ns per call, 6% of events delivered | 1,244 ns per call, 100% delivered |

These are extremes. A real sink (a file, the console) is much slower than the do-nothing sink used here, so
under sustained overload any async wrapper eventually fills its buffer and then drops or blocks. The numbers show
how little each logging call costs, and how much better the background thread keeps up when many threads log at
once.

## Getting started

Replace the `Serilog.Sinks.Async` package with this one. No code changes are needed: the configuration
method, the namespaces (`Serilog`, `Serilog.Sinks.Async`) and the interfaces are the same.

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

The tests include Serilog.Sinks.Async's own test suite, run unchanged against this implementation. They also
check that the public API is identical to Serilog.Sinks.Async 2.1.0's, and stress the ring buffer: many threads,
tiny buffers, constant wrap-around, and disposal while logging.

To compare performance with Serilog.Sinks.Async:

```sh
dotnet run -c Release --project test/Serilog.Sinks.AsyncRing.Benchmarks
```

## License

Apache 2.0. The public API, its documentation and the ported tests come from
[Serilog.Sinks.Async](https://github.com/serilog/serilog-sinks-async), Copyright © Serilog Contributors, also
licensed under Apache 2.0.
