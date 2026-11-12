using System.Threading.Channels;

namespace GridSim.Web.Sessions;

/// <summary>
/// GR-224: runs the projection of bridge frames on a worker of its own, so a slow projection cannot hold up the bridge
/// client's receive loop (which used to build every envelope itself, on its own thread).
/// <para>
/// A bounded FIFO, one consumer. Every item queued is projected, in the order it was queued, and nothing is ever
/// dropped or replaced: the projection of a frame also advances the session's history queues, the imbalance-price
/// samples, the SCADA alarm log, the mechanical ramp state and the frequency-risk trace, so skipping a frame would lose a
/// history point, an alarm transition or a ramp step, and a newer frame must never be published before an older one.
/// </para>
/// <para>
/// Fail loud on overflow. When the queue is full (projection has fallen <see cref="Capacity"/> frames behind) the item is
/// NOT queued, the worker stops, and <c>onFault</c> is called once with a clear error so the session can say so and stop
/// the stream. The same happens once if the projection throws. After a fault, further items are ignored (the session is
/// marked failed; they would only extend a run of frames that can no longer be shown in order).
/// </para>
/// <para>
/// <see cref="DisposeAsync"/> stops the worker: queued items that have not started are abandoned, an item being
/// projected is allowed to finish, and the call returns once the worker has ended.
/// </para>
/// </summary>
/// <typeparam name="T">What is queued (everything the projection needs, captured at receive time).</typeparam>
internal sealed class BridgeProjectionWorker<T> : IAsyncDisposable
{
    private readonly Channel<T> _queue;
    private readonly Action<T> _project;
    private readonly Action<Exception> _onFault;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _faulted;     // 0 = running, 1 = failed (overflow or a projection exception)
    private int _disposed;

    /// <summary>How many frames may wait behind the one being projected.</summary>
    public int Capacity { get; }

    /// <summary>True once the worker has overflowed or its projection threw; no further item is accepted.</summary>
    public bool Faulted => Volatile.Read(ref _faulted) != 0;

    /// <param name="project">Projects one item. Called on the worker, one item at a time, in queue order.</param>
    /// <param name="onFault">Called once, with the reason, on overflow (on the caller of <see cref="Enqueue"/>) or when
    /// <paramref name="project"/> throws (on the worker). Must not throw.</param>
    /// <param name="capacity">The most items waiting; 1 or more.</param>
    public BridgeProjectionWorker(Action<T> project, Action<Exception> onFault, int capacity)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(onFault);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _project = project;
        _onFault = onFault;
        Capacity = capacity;
        _queue = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,                     // a reconnect's new receive loop may overlap the old one briefly
            FullMode = BoundedChannelFullMode.Wait,   // TryWrite then reports "full" instead of dropping anything
        });
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Queue one item; never blocks and never waits for a projection. Returns true when it was queued. False means it
    /// was not: the worker had already faulted or been disposed (nothing is reported), or the queue was full, in which
    /// case the worker faults now and <c>onFault</c> is called with the overflow.
    /// </summary>
    public bool Enqueue(T item)
    {
        if (Volatile.Read(ref _disposed) != 0 || Faulted) return false;
        if (_queue.Writer.TryWrite(item)) return true;
        Fault(new InvalidOperationException(
            $"Bridge frames are arriving faster than they can be projected: {Capacity} frames are already waiting, so this session was stopped rather than drop any. "
            + "Lower the playback speed or use a smaller case."));
        return false;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (T item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // A queued item is abandoned once the worker is disposed: ReadAllAsync hands over an item that is
                // already buffered without looking at the token, so the token is checked here, between items.
                if (Faulted || ct.IsCancellationRequested) break;
                try { _project(item); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Fault(ex);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    // Marks the worker failed (once), closes the queue so nothing more is accepted, and reports the reason once.
    private void Fault(Exception reason)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
        _queue.Writer.TryComplete();
        _onFault(reason);
    }

    /// <summary>Stop the worker and wait for it to end. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        _cts.Dispose();
    }
}
