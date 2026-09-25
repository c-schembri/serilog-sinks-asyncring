// Copyright © Serilog.Sinks.AsyncRing Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Serilog.Sinks.Async;

/// <summary>
/// Feeds a wrapped sink from a dedicated worker thread, through a pre-allocated ring buffer.
/// </summary>
/// <remarks>
/// <para>Producers claim a slot with a single <see cref="Interlocked.Increment(ref long)"/>, which, unlike a
/// compare-and-swap loop or a lock, never has to retry when other threads are logging at the same time.
/// The event is then published by writing the slot's sequence number. The single worker reads the slots
/// in order, so events reach the wrapped sink in the order their slots were claimed.</para>
/// <para>The capacity check happens before a slot is claimed, so producers racing past it together can
/// overshoot the capacity slightly; the ring has spare slots to absorb that.</para>
/// </remarks>
sealed class BackgroundWorkerSink : ILogEventSink, IAsyncLogEventSinkInspector, IDisposable, ISetLoggingFailureListener
#if FEATURE_ASYNCDISPOSABLE
    , IAsyncDisposable
#endif
{
    struct Slot
    {
        public LogEvent? Item;

        // Equal to the position when the slot is free for that position's producer, and to
        // position + 1 once the event has been published for the worker.
        public long Sequence;
    }

    // Closing the sink sets this bit on _tail. Claims made before it are exactly the events the worker
    // still has to drain; claims made after it come back with the bit set and are rejected.
    private const long ClosedBit = 1L << 62;

    // Spare slots beyond the capacity, absorbing producers that overshoot it together.
    private const int DefaultSlack = 1024;

    // Largest ring allocated (2M slots, 32 MB on 64-bit); larger buffer sizes are capped to fit.
    private const int MaxRingSize = 1 << 21;

    // The worker publishes its progress every few events rather than after each one, so producers
    // (which read it on every call) aren't constantly invalidating each other's cached copy.
    private const int MaxHeadPublishInterval = 256;

    // How many times a waiting thread re-checks before it starts yielding the processor.
    private const int SpinIterations = 64;

    private const string DisposedMessage = "the sink has been disposed";

    private readonly ILogEventSink _wrappedSink;
    private readonly int _capacity;
    private readonly bool _blockWhenFull;
    private readonly IAsyncLogEventSinkMonitor? _monitor;
    private readonly Slot[] _slots;
    private readonly long _mask;
    private readonly int _headPublishInterval;
    private readonly string _droppedMessage;
    private readonly Thread _worker;
    private readonly ManualResetEventSlim _workAvailable = new(false);
    private readonly TaskCompletionSource<bool> _workerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _spaceAvailable = new();

    private PaddedLong _tail;         // next position to claim; incremented by producers
    private PaddedLong _head;         // next position the worker will take, as last published by the worker
    private PaddedInt _workerWaiting; // 1 while the worker is going to sleep or asleep
    private long _finalTail = -1;     // the value of _tail when the sink was closed
    private int _blockedProducers;
    private long _droppedMessages;
    private int _disposed;

    // By contract, set only during initialization, so updates are not synchronized.
    private ILoggingFailureListener _failureListener = SelfLog.FailureListener;

    public BackgroundWorkerSink(ILogEventSink wrappedSink, int bufferCapacity, bool blockWhenFull, IAsyncLogEventSinkMonitor? monitor)
        : this(wrappedSink, bufferCapacity, blockWhenFull, monitor, DefaultSlack)
    {
    }

    // Tests pass a smaller slack to force producers to lap the worker.
    internal BackgroundWorkerSink(ILogEventSink wrappedSink, int bufferCapacity, bool blockWhenFull, IAsyncLogEventSinkMonitor? monitor, int slack)
    {
        if (bufferCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(bufferCapacity));
        if (slack < 0) throw new ArgumentOutOfRangeException(nameof(slack));
        _wrappedSink = wrappedSink ?? throw new ArgumentNullException(nameof(wrappedSink));
        _blockWhenFull = blockWhenFull;

        var ringSize = RingSizeFor(bufferCapacity, slack);
        _capacity = (int)Math.Max(1, Math.Min(bufferCapacity, (long)ringSize - slack));
        _slots = new Slot[ringSize];
        for (var i = 0; i < ringSize; i++) _slots[i].Sequence = i;
        _mask = ringSize - 1;
        _headPublishInterval = Math.Max(1, Math.Min(MaxHeadPublishInterval, _capacity / 16));
        _droppedMessage = $"unable to enqueue, capacity {_capacity}";

        _worker = new Thread(Pump)
        {
            IsBackground = true,
            Name = "Serilog.Sinks.Async worker",
            // The worker is the only thread that frees buffer space. When more threads are busy than there
            // are cores, a raised priority keeps it draining instead of getting an equal share of the CPU
            // (4 logical cores, 16 logging threads: 2x the throughput, and 97% rather than 69% of events
            // delivered under overload). It sleeps when there's nothing to write. .NET only applies thread
            // priorities on Windows.
            Priority = ThreadPriority.AboveNormal,
        };
        _worker.Start();

        _monitor = monitor;
        monitor?.StartMonitoring(this);
    }

    static int RingSizeFor(int capacity, int slack)
    {
        var wanted = Math.Min((long)capacity + slack, MaxRingSize);
        var size = 1;
        while (size < wanted) size <<= 1;
        return size;
    }

    public void Emit(LogEvent logEvent)
    {
        var tail = Volatile.Read(ref _tail.Value);
        if ((tail & ClosedBit) != 0)
        {
            ReportFailure(LoggingFailureKind.Final, DisposedMessage, logEvent, null);
            return;
        }

        if (tail - Volatile.Read(ref _head.Value) >= _capacity && !WaitForSpace(logEvent))
            return;

        var position = Interlocked.Increment(ref _tail.Value) - 1;
        if ((position & ClosedBit) != 0)
        {
            ReportFailure(LoggingFailureKind.Final, DisposedMessage, logEvent, null);
            return;
        }

        ref var slot = ref _slots[position & _mask];
        if (Volatile.Read(ref slot.Sequence) != position && !WaitForSlot(ref slot, position))
        {
            ReportFailure(LoggingFailureKind.Final, "the worker thread has stopped", logEvent, null);
            return;
        }

        slot.Item = logEvent;
        Volatile.Write(ref slot.Sequence, position + 1);

        // The Increment above is a full fence, so the worker either sees this claim when it re-checks
        // _tail after announcing it's going to sleep, or this read sees the announcement.
        if (Volatile.Read(ref _workerWaiting.Value) == 1 && Interlocked.Exchange(ref _workerWaiting.Value, 0) == 1)
            _workAvailable.Set();
    }

    // Called when the buffer looks full. Returns true if the caller should go on to claim a slot.
    bool WaitForSpace(LogEvent logEvent)
    {
        // Blocking on the worker thread itself (a wrapped sink logging through this pipeline) would
        // deadlock, so events from there are dropped instead.
        if (!_blockWhenFull || Thread.CurrentThread == _worker)
        {
            Interlocked.Increment(ref _droppedMessages);
            ReportFailure(LoggingFailureKind.Permanent, _droppedMessage, logEvent, null);
            return false;
        }

        // Pairs with PublishHead: either this thread sees the worker's progress, or the worker sees
        // this thread waiting and wakes it.
        Interlocked.Increment(ref _blockedProducers);
        try
        {
            lock (_spaceAvailable)
            {
                while (true)
                {
                    var tail = Volatile.Read(ref _tail.Value);
                    if ((tail & ClosedBit) != 0) break;
                    if (tail - Volatile.Read(ref _head.Value) < _capacity) return true;
                    Monitor.Wait(_spaceAvailable);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _blockedProducers);
        }

        ReportFailure(LoggingFailureKind.Final, DisposedMessage, logEvent, null);
        return false;
    }

    // The claimed slot still holds an event from the previous lap, which only happens when more producers
    // than the slack overshoot the capacity at once. Waits for the worker to take it; returns false if the
    // worker has stopped after a fatal error. (A wrapped sink logging from the worker thread could in theory
    // wait on itself here, but only if it's among more than `slack` producers overshooting at that instant.)
    bool WaitForSlot(ref Slot slot, long position)
    {
        var iteration = 0;
        while (Volatile.Read(ref slot.Sequence) != position)
        {
            if (_workerExited.Task.IsCompleted) return false;
            Backoff(ref iteration);
        }

        return true;
    }

    // Re-checks straight away for a while, then yields the processor, but never sleeps. What's being
    // waited for (a producer between claiming a slot and publishing it) is normally nanoseconds away, so
    // pausing costs throughput; and Thread.Sleep(1), which SpinWait eventually falls back to, can stall
    // for a whole timer tick (15.6 ms by default on Windows) and let the buffer back up.
    static void Backoff(ref int iteration)
    {
        if (iteration >= SpinIterations)
        {
            if (iteration % 5 == 4)
                Thread.Sleep(0);
            else
                Thread.Yield();
        }

        iteration++;
    }

    void Pump()
    {
        try
        {
            var iteration = 0;
            while (true)
            {
                if (Drain()) iteration = 0;

                // Only this thread writes _head, and Drain leaves it at the worker's position.
                var head = _head.Value;
                var tail = Volatile.Read(ref _tail.Value);
                if ((tail & ClosedBit) != 0)
                {
                    // Everything claimed before the sink was closed has been written.
                    if (head == Volatile.Read(ref _finalTail)) return;
                }
                else if (tail == head)
                {
                    iteration = 0;
                    WakeAllBlockedProducers();
                    WaitForWork(head);
                    continue;
                }

                // A producer has claimed the next slot but hasn't published it yet, or the sink is
                // part-way through closing.
                Backoff(ref iteration);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(LoggingFailureKind.Final, "fatal error in worker thread", null, ex);
            Close();
        }
        finally
        {
            _workerExited.TrySetResult(true);
        }
    }

    // Takes every published event in order and writes it to the wrapped sink. Returns true if there were any.
    bool Drain()
    {
        // Locals rather than fields in the loop: this is the one thread every event passes through.
        var slots = _slots;
        var mask = _mask;
        var publishInterval = _headPublishInterval;
        var head = _head.Value;
        var start = head;
        var sincePublish = 0;
        while (true)
        {
            ref var slot = ref slots[head & mask];
            if (Volatile.Read(ref slot.Sequence) != head + 1) break;

            var logEvent = slot.Item!;
            slot.Item = null;
            Volatile.Write(ref slot.Sequence, head + mask + 1); // free the slot for the next lap
            head++;

            // Publish before the (possibly slow) wrapped sink runs, so the event being written no
            // longer counts against the capacity.
            if (++sincePublish >= publishInterval)
            {
                PublishHead(head, sincePublish);
                sincePublish = 0;
            }

            EmitToWrappedSink(logEvent);
        }

        if (sincePublish > 0) PublishHead(head, sincePublish);
        return head != start;
    }

    // Makes the worker's progress visible to producers, and wakes one blocked producer per freed slot
    // (waking them all for every slot would have them all fight over it).
    void PublishHead(long head, int freed)
    {
        if (!_blockWhenFull)
        {
            Volatile.Write(ref _head.Value, head);
            return;
        }

        // A full fence, so the read below can't be satisfied before the new head is visible
        // (pairs with the Increment of _blockedProducers in WaitForSpace).
        Interlocked.Exchange(ref _head.Value, head);
        var blocked = Volatile.Read(ref _blockedProducers);
        if (blocked > 0)
        {
            lock (_spaceAvailable)
            {
                for (var i = Math.Min(freed, blocked); i > 0; i--) Monitor.Pulse(_spaceAvailable);
            }
        }
    }

    // Safety net before the worker goes idle: with the buffer empty, no producer should still be waiting.
    void WakeAllBlockedProducers()
    {
        if (_blockWhenFull && Volatile.Read(ref _blockedProducers) > 0)
        {
            lock (_spaceAvailable) Monitor.PulseAll(_spaceAvailable);
        }
    }

    void WaitForWork(long head)
    {
        Interlocked.Exchange(ref _workerWaiting.Value, 1);

        // Re-check after announcing: a producer that claimed a slot before seeing the announcement is
        // visible here, and one that claims after it will see the announcement and wake this thread.
        if (Volatile.Read(ref _tail.Value) == head)
            _workAvailable.Wait();

        Interlocked.Exchange(ref _workerWaiting.Value, 0);
        _workAvailable.Reset();
    }

    void EmitToWrappedSink(LogEvent logEvent)
    {
        try
        {
            _wrappedSink.Emit(logEvent);
        }
        catch (Exception ex)
        {
            ReportFailure(LoggingFailureKind.Permanent, "failed to emit event to wrapped sink", logEvent, ex);
        }
    }

    void ReportFailure(LoggingFailureKind kind, string message, LogEvent? logEvent, Exception? exception)
    {
        try
        {
            _failureListener.OnLoggingFailed(this, kind, message, logEvent is null ? null : new[] { logEvent }, exception);
        }
        catch (Exception listenerException)
        {
            // A throwing listener must never stop the worker or escape into application code.
            try
            {
                SelfLog.WriteLine("Failure listener {0} threw while reporting a failure: {1}", _failureListener, listenerException);
            }
            catch
            {
                // The self-log output failed as well; there is nowhere left to report this.
            }
        }
    }

    // Stops accepting events, and wakes the worker and any blocked producers so they notice.
    void Close()
    {
        long tail;
        do
        {
            tail = Volatile.Read(ref _tail.Value);
            if ((tail & ClosedBit) != 0) return;
        }
        while (Interlocked.CompareExchange(ref _tail.Value, tail | ClosedBit, tail) != tail);

        Volatile.Write(ref _finalTail, tail);
        _workAvailable.Set();
        lock (_spaceAvailable) Monitor.PulseAll(_spaceAvailable);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Close();

        // Allow queued events to be flushed (unless this is the worker itself, which would deadlock).
        if (Thread.CurrentThread != _worker) _worker.Join();

        (_wrappedSink as IDisposable)?.Dispose();

        _monitor?.StopMonitoring(this);
    }

#if FEATURE_ASYNCDISPOSABLE
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Close();

        if (Thread.CurrentThread != _worker) await _workerExited.Task.ConfigureAwait(false);

        if (_wrappedSink is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else
            (_wrappedSink as IDisposable)?.Dispose();

        _monitor?.StopMonitoring(this);
    }
#endif

    public void SetFailureListener(ILoggingFailureListener failureListener)
    {
        _failureListener = failureListener ?? throw new ArgumentNullException(nameof(failureListener));

        // Serilog's Wrap() forwards the listener to the wrapped sink only when the wrapper is missing
        // one of the optional sink interfaces. This sink implements them all, so it forwards it itself.
        (_wrappedSink as ISetLoggingFailureListener)?.SetFailureListener(failureListener);
    }

    int IAsyncLogEventSinkInspector.BufferSize => _capacity;

    int IAsyncLogEventSinkInspector.Count
    {
        get
        {
            var tail = Volatile.Read(ref _tail.Value);
            if ((tail & ClosedBit) != 0) tail = Volatile.Read(ref _finalTail);
            var count = tail - Volatile.Read(ref _head.Value);
            return count <= 0 ? 0 : (int)Math.Min(count, int.MaxValue);
        }
    }

    long IAsyncLogEventSinkInspector.DroppedMessagesCount => Interlocked.Read(ref _droppedMessages);
}
