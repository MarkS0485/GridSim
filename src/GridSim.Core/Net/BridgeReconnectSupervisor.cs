using GridSim.Core.Diagnostics;

namespace GridSim.Core.Net;

/// <summary>Where a <see cref="BridgeReconnectSupervisor"/> is in its cycle.</summary>
public enum BridgeReconnectState
{
    /// <summary>Not supervising anything yet, or stopped by the host (<see cref="BridgeReconnectSupervisor.Stop"/>).</summary>
    Idle = 0,

    /// <summary>The host reported a working connection, or the supervisor re-established one. The attempt count is 0.</summary>
    Connected = 1,

    /// <summary>A connection was lost and the supervisor is working through the backoff schedule.</summary>
    Reconnecting = 2,

    /// <summary>
    /// The supervisor stopped trying: the schedule's bound was reached, the loss was a horizon rejection (never retried),
    /// or the supervisor itself failed. Reported once through <see cref="BridgeReconnectSupervisor.GaveUp"/>; it stays
    /// here until the host reports a new connection (<see cref="BridgeReconnectSupervisor.Connected"/>) or stops it.
    /// </summary>
    GaveUp = 3,

    /// <summary>Disposed: it will never make another attempt.</summary>
    Disposed = 4,
}

/// <summary>One reconnect attempt about to be made: its 1-based number, the bound (0 means no bound), the backoff delay
/// the supervisor waits first (exactly <see cref="ResilienceOptions.BackoffDelay"/> of the attempt number) and why the
/// connection is being re-established (the fault that lost it, or the failure of the previous attempt).</summary>
public sealed record BridgeReconnectSchedule(int Attempt, int MaxAttempts, TimeSpan Delay, Exception Cause);

/// <summary>The outcome of one reconnect attempt. <see cref="Failure"/> is null when the attempt re-established the
/// connection.</summary>
public sealed record BridgeReconnectAttempt(int Attempt, TimeSpan Delay, Exception? Failure)
{
    /// <summary>True when this attempt re-established the connection.</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>The supervisor's final failure report: how many attempts were made in this outage, the last failure, and
/// a plain-English reason that says why it stopped.</summary>
public sealed record BridgeReconnectGiveUp(int Attempts, Exception LastFailure, string Reason);

/// <summary>
/// Supervised reconnect for a GDA bridge connection (NCSC CAF B5, "resilient networks and systems"; NIS principle 4).
/// <see cref="ResilienceOptions"/> carries a bounded backoff schedule; this class is the part that drives it.
///
/// <para><b>What it does.</b> The host tells it when the connection is lost (<see cref="ConnectionLost"/>, from the
/// client's <see cref="GridBridgeClient.Faulted"/> event). It then works through the schedule: for attempt n (1-based)
/// it waits exactly <see cref="ResilienceOptions.BackoffDelay"/>(n) and calls the host's reconnect function, which
/// builds, wires and connects a FRESH client (a <see cref="GridBridgeClient"/> wraps one socket and cannot be reused).
/// A reconnect that throws is a failed attempt and the next one is scheduled. A reconnect that returns is a success:
/// the attempt count goes back to 0 and the state to <see cref="BridgeReconnectState.Connected"/>. When
/// <see cref="ResilienceOptions.MaxReconnectAttempts"/> attempts have failed (0 means no bound) it stops and raises
/// <see cref="GaveUp"/> exactly once. It never spins silently: every attempt is announced before it is made
/// (<see cref="ReconnectScheduled"/>) and reported after (<see cref="AttemptCompleted"/>).</para>
///
/// <para><b>The restriction invariant is untouched.</b> The supervisor carries no timestamps and no data. Each attempt
/// goes through the host's own connect path, so every reconnected stream is a new client with the same exact 169 h
/// forward-inference guard and no clock-skew allowance (R276), streaming from the host's own historical anchor. A
/// horizon rejection (<see cref="ForwardInferenceException"/>) is an invariant breach, not a dropped connection: it is
/// NEVER retried automatically. The supervisor gives up at once and reports why, so the operator decides.</para>
///
/// <para><b>Testable without sleeping.</b> The wait is an injected function (default <see cref="Task.Delay(TimeSpan,
/// CancellationToken)"/>), and the reconnect is an injected function, so a test can record every requested delay and
/// script every outcome with no socket and no real time passing.</para>
///
/// <para><b>Threading.</b> Every member is thread-safe. The events are raised on the supervisor's own background task
/// (or, for a horizon rejection, on the thread that called <see cref="ConnectionLost"/>); a UI host moves them onto its
/// UI thread itself. A handler must not await <see cref="DisposeAsync"/> of the supervisor that raised it (it would wait
/// for itself). The injected delay must honour its cancellation token, so <see cref="Stop"/> and
/// <see cref="DisposeAsync"/> end a pending wait at once.</para>
///
/// <para><b>One outage at a time.</b> A second loss reported while the supervisor is already reconnecting does not start
/// a second schedule. If it arrives while an attempt is in progress, that attempt is counted as failed (the new client
/// lost its connection before the attempt finished), so a reconnect that "succeeds" into an already-dead stream is not
/// reported as a success.</para>
/// </summary>
public sealed class BridgeReconnectSupervisor : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly ResilienceOptions _options;
    private readonly Func<int, CancellationToken, Task> _reconnect;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // All of the following are guarded by _lock.
    private BridgeReconnectState _state = BridgeReconnectState.Idle;
    private int _attempt;                              // attempts made in the current outage (0 once connected)
    private int _generation;                           // bumped by every start, stop, connect and dispose: a running
                                                       // outage whose generation is no longer current changes nothing
    private CancellationTokenSource? _outageCts;       // cancels the current outage's wait and attempt
    private Task _outage = Task.CompletedTask;         // the current (or last) outage's background task
    private bool _attemptInFlight;                     // true while the host's reconnect function is running
    private Exception? _lostDuringAttempt;             // a loss reported while that attempt was running
    private BridgeReconnectGiveUp? _lastGiveUp;
    private Exception? _supervisorFailure;

    /// <summary>
    /// A supervisor that drives <paramref name="options"/>' backoff schedule. <paramref name="reconnect"/> is called with
    /// the 1-based attempt number and a token that is cancelled when the supervisor is stopped or disposed; it must
    /// build, wire and connect a fresh client and either return (the connection is back) or throw (this attempt
    /// failed). <paramref name="delay"/> is the wait (default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The schedule is nonsensical (<see cref="ResilienceOptions.Validate"/>).</exception>
    public BridgeReconnectSupervisor(ResilienceOptions options, Func<int, CancellationToken, Task> reconnect,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reconnect);
        // Fail loud on a schedule that makes no sense (a zero backoff would spin; a negative bound has no meaning).
        options.Validate();
        _options = options;
        _reconnect = reconnect;
        _delay = delay ?? DefaultDelay;
    }

    private static Task DefaultDelay(TimeSpan wait, CancellationToken ct) => Task.Delay(wait, ct);

    /// <summary>The schedule this supervisor drives.</summary>
    public ResilienceOptions Options => _options;

    /// <summary>Where the supervisor is now.</summary>
    public BridgeReconnectState State { get { lock (_lock) return _state; } }

    /// <summary>How many reconnect attempts the current outage has made (0 while connected or idle).</summary>
    public int Attempt { get { lock (_lock) return _attempt; } }

    /// <summary>The background task of the current (or most recent) outage. It completes when the connection is back,
    /// when the supervisor gives up, or when it is stopped or disposed; it never faults. A test awaits it.</summary>
    public Task Outage { get { lock (_lock) return _outage; } }

    /// <summary>The last give-up report, or null if the supervisor has not given up since it last saw a connection.</summary>
    public BridgeReconnectGiveUp? LastGiveUp { get { lock (_lock) return _lastGiveUp; } }

    /// <summary>
    /// The exception that stopped the supervisor itself (one of its event handlers, or the injected delay, threw), or
    /// null in normal running. The supervisor gives up when that happens; if the <see cref="GaveUp"/> handler was the
    /// one that threw, the report could not be delivered, so this property is where the host finds the failure.
    /// </summary>
    public Exception? SupervisorFailure { get { lock (_lock) return _supervisorFailure; } }

    /// <summary>Raised before each attempt, with the delay the supervisor is about to wait.</summary>
    public event Action<BridgeReconnectSchedule>? ReconnectScheduled;

    /// <summary>Raised after each attempt with its outcome.</summary>
    public event Action<BridgeReconnectAttempt>? AttemptCompleted;

    /// <summary>Raised exactly once per outage when the supervisor stops trying (see <see cref="BridgeReconnectState.GaveUp"/>).</summary>
    public event Action<BridgeReconnectGiveUp>? GaveUp;

    /// <summary>
    /// The host has a working connection (its first connect succeeded, or the operator reconnected by hand). Any outage
    /// in progress is abandoned without a give-up report, the attempt count goes back to 0 and the state to
    /// <see cref="BridgeReconnectState.Connected"/>. Ignored once disposed.
    /// </summary>
    public void Connected()
    {
        CancellationTokenSource? cancel;
        lock (_lock)
        {
            if (_state == BridgeReconnectState.Disposed) return;
            cancel = EndOutageLocked();
            _state = BridgeReconnectState.Connected;
            _lastGiveUp = null;
        }
        CancelAndDispose(cancel);
    }

    /// <summary>
    /// The host is leaving the bridge (another source was chosen, or the window is closing): abandon any outage in
    /// progress without a give-up report, make no further attempt, and go back to <see cref="BridgeReconnectState.Idle"/>.
    /// The supervisor can be used again afterwards. Ignored once disposed.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cancel;
        lock (_lock)
        {
            if (_state == BridgeReconnectState.Disposed) return;
            cancel = EndOutageLocked();
            _state = BridgeReconnectState.Idle;
        }
        CancelAndDispose(cancel);
    }

    /// <summary>
    /// The connection was lost (the client raised <see cref="GridBridgeClient.Faulted"/>). Starts working through the
    /// schedule and returns true; returns false when nothing new was started: a schedule is already running (a loss
    /// during an attempt fails that attempt), the supervisor has already given up (it was reported once; the host must
    /// reconnect by hand), or it is disposed. A <see cref="ForwardInferenceException"/> is never retried: the supervisor
    /// gives up at once, raises <see cref="GaveUp"/> on the calling thread, and returns false.
    /// </summary>
    public bool ConnectionLost(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        BridgeReconnectGiveUp? refusal = null;
        lock (_lock)
        {
            switch (_state)
            {
                case BridgeReconnectState.Disposed:
                case BridgeReconnectState.GaveUp:
                    return false;
                case BridgeReconnectState.Reconnecting:
                    // One outage at a time. A loss while an attempt is running means the client that attempt is
                    // building has already lost its connection: that attempt is failed when it returns.
                    if (_attemptInFlight) _lostDuringAttempt ??= cause;
                    return false;
            }

            _generation++;
            _attempt = 0;
            _lastGiveUp = null;
            if (cause is ForwardInferenceException)
            {
                // The restriction invariant: a frame inside the 169 h horizon is a protocol breach by the server, not a
                // lost link. Reconnecting would replay the same stream into the same refusal, so it is not retried.
                refusal = new BridgeReconnectGiveUp(0, cause, HorizonReason(cause));
                _state = BridgeReconnectState.GaveUp;
                _lastGiveUp = refusal;
            }
            else
            {
                _state = BridgeReconnectState.Reconnecting;
                // The previous outage (if any) ended by itself (reconnected or gave up) and no longer uses its token
                // source, which is released here rather than left for the garbage collector.
                _outageCts?.Dispose();
                var cts = new CancellationTokenSource();
                _outageCts = cts;
                int generation = _generation;
                CancellationToken token = cts.Token;
                // The token is not passed to Task.Run (as GridBridgeClient does for its loop): a cancel that landed before
                // the thread pool started the task would skip the body, and the body is where the cancellation is handled.
                _outage = Task.Run(() => RunOutageAsync(cause, generation, token), CancellationToken.None);
                return true;
            }
        }
        GaveUp?.Invoke(refusal);
        return false;
    }

    /// <summary>Stop for good: abandon any outage in progress (no further attempt, no give-up report) and wait for its
    /// background task to end. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        Task outage;
        lock (_lock)
        {
            if (_state == BridgeReconnectState.Disposed) return;
            // Invalidate the running outage (if any) and reset the count. The token source stays in its field: with the
            // state Disposed no new outage can start, so it is cancelled, awaited and disposed below.
            _generation++;
            _attempt = 0;
            _attemptInFlight = false;
            _lostDuringAttempt = null;
            _state = BridgeReconnectState.Disposed;
            outage = _outage;
        }
        _outageCts?.Cancel();
        // RunOutageAsync never faults (it ends every path itself), so this only waits for it to notice the cancel.
        await outage.ConfigureAwait(false);
        _outageCts?.Dispose();
        _outageCts = null;
    }

    // ---- the outage loop ----

    private async Task RunOutageAsync(Exception firstCause, int generation, CancellationToken ct)
    {
        Exception last = firstCause;
        try
        {
            while (true)
            {
                int attempt;
                bool boundReached;
                int max = _options.MaxReconnectAttempts;   // 0 means no bound
                lock (_lock)
                {
                    if (generation != _generation) return;   // stopped, reconnected by hand, or disposed
                    boundReached = max > 0 && _attempt >= max;
                    // The count only moves when an attempt is really going to be made, so after a give-up Attempt
                    // reads exactly the number of attempts that failed.
                    attempt = boundReached ? _attempt : ++_attempt;
                }
                if (boundReached)
                {
                    // The bound is reached: stop and say so, once.
                    ReportGiveUp(generation, attempt, last, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"The bridge connection was not re-established after {attempt} attempt(s), the limit set by MaxReconnectAttempts; the last failure was: {last.Message}"));
                    return;
                }

                // The exact schedule: attempt n waits BackoffDelay(n), no jitter (ResilienceOptions.BackoffDelay).
                TimeSpan wait = _options.BackoffDelay(attempt);
                Raise(generation, ReconnectScheduled, new BridgeReconnectSchedule(attempt, _options.MaxReconnectAttempts, wait, last));
                await _delay(wait, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                Exception? failure = null;
                Task? pending = null;
                lock (_lock)
                {
                    if (generation != _generation) return;   // stopped during the wait: no further attempt
                    _attemptInFlight = true;
                    _lostDuringAttempt = null;
                }
                try
                {
                    pending = _reconnect(attempt, ct);
                    // WaitAsync: a host whose reconnect never completes (a UI thread that has shut down) cannot hold the
                    // supervisor; a stop or dispose ends the wait at once.
                    await pending.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (pending is not null) Observe(pending);   // its own outcome no longer matters, but is never left unobserved
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;   // this attempt failed; the next one is scheduled below
                }
                finally
                {
                    lock (_lock)
                    {
                        // Only while this outage is still current: after a stop, a NEW outage may own these fields.
                        if (generation == _generation)
                        {
                            _attemptInFlight = false;
                            failure ??= _lostDuringAttempt;   // the new connection was lost before the attempt finished
                            _lostDuringAttempt = null;
                        }
                    }
                }

                Raise(generation, AttemptCompleted, new BridgeReconnectAttempt(attempt, wait, failure));
                if (failure is null)
                {
                    lock (_lock)
                    {
                        if (generation != _generation) return;
                        _attempt = 0;   // success resets the count, so the next outage starts the schedule from the top
                        _state = BridgeReconnectState.Connected;
                    }
                    return;
                }

                last = failure;
                if (failure is ForwardInferenceException)
                {
                    ReportGiveUp(generation, attempt, failure, HorizonReason(failure));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped or disposed: no further attempt, and no give-up report (the host ended the supervision itself).
        }
        catch (Exception ex)
        {
            // The supervisor itself failed (an event handler or the injected delay threw). Fail loud: give up and say
            // why, rather than end the outage silently with the connection still down.
            int attempts;
            lock (_lock)
            {
                attempts = _attempt;
                _supervisorFailure = ex;   // kept even when the give-up below cannot be raised (it was the thrower)
            }
            try
            {
                ReportGiveUp(generation, attempts, ex,
                    $"The bridge reconnect supervisor stopped because it failed itself ({ex.GetType().Name}: {ex.Message}).");
            }
            catch (Exception handlerFailure)
            {
                // The give-up handler threw as well: keep it where the host can read it (SupervisorFailure).
                lock (_lock) _supervisorFailure = handlerFailure;
            }
        }
    }

    // ---- helpers ----

    // Move to GaveUp and raise GaveUp once, unless this outage is no longer the current one.
    private void ReportGiveUp(int generation, int attempts, Exception last, string reason)
    {
        BridgeReconnectGiveUp report;
        lock (_lock)
        {
            if (generation != _generation || _state != BridgeReconnectState.Reconnecting) return;
            report = new BridgeReconnectGiveUp(attempts, last, reason);
            _state = BridgeReconnectState.GaveUp;
            _lastGiveUp = report;
        }
        GaveUp?.Invoke(report);
    }

    // Raise an event only while this outage is still the current one (a stopped outage reports nothing more).
    private void Raise<T>(int generation, Action<T>? handler, T value)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
        }
        handler?.Invoke(value);
    }

    // End the current outage (called under _lock): invalidate it, forget its token source, and reset the count.
    // Returns the token source for the caller to cancel and dispose outside the lock.
    private CancellationTokenSource? EndOutageLocked()
    {
        _generation++;
        _attempt = 0;
        _attemptInFlight = false;
        _lostDuringAttempt = null;
        CancellationTokenSource? cts = _outageCts;
        _outageCts = null;
        return cts;
    }

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    // An attempt abandoned on a stop: observe its outcome so a late failure is never an unobserved task exception.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static string HorizonReason(Exception cause) =>
        "The bridge stream was refused by the 169 h forward-inference horizon guard. That is an invariant breach by the " +
        "server, not a lost connection, so it is not retried automatically; the operator decides. The refusal was: " + cause.Message;
}
