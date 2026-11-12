using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>How a solve attempt ended (<see cref="WebLimits.RunSolveAsync{T}"/>).</summary>
public enum SolveRunStatus
{
    /// <summary>The work finished inside the timeout; the value is set.</summary>
    Completed = 0,
    /// <summary>Every solve slot was in use, so the work was NOT started (answer 503).</summary>
    Busy = 1,
    /// <summary>The work did not finish inside the timeout (answer 504). It is still running and still holds its slot.</summary>
    TimedOut = 2,
}

/// <summary>The outcome of <see cref="WebLimits.RunSolveAsync{T}"/>: a status and, when completed, the work's value.</summary>
public sealed record SolveRun<T>(SolveRunStatus Status, T? Value);

/// <summary>
/// GR-218 and GR-221 (November 2026): the web host's resource limits, read and checked ONCE at start-up so a malformed
/// value stops the host naming the setting (the same rule as <c>GridSim:MaxSessions</c> and <c>GridSim:AnonymousRole</c>).
/// <list type="bullet">
/// <item><c>GridSim:MaxConcurrentSolves</c> (default <see cref="DefaultMaxConcurrentSolves"/>): the most
/// <c>POST /api/solve</c> and <c>GET /api/topology</c> requests being worked at once on this instance. A request that
/// finds every slot taken is refused at once with 503 and a Retry-After; it never queues.</item>
/// <item><c>GridSim:SolveTimeoutSeconds</c> (default <see cref="DefaultSolveTimeoutSeconds"/>): how long a request waits for
/// its solve before the caller gets 504 with a clear error. A solve cannot be interrupted part-way (the numerical core is
/// not cancellable and is deliberately not touched), so a timed-out solve keeps running to its end and KEEPS ITS SLOT until
/// then: the cap bounds real CPU use, not only the number of waiting callers.</item>
/// <item><c>GridSim:WsControlRatePerSecond</c> (default <see cref="DefaultWsControlRatePerSecond"/>) and
/// <c>GridSim:WsControlBurst</c> (default <see cref="DefaultWsControlBurst"/>): the per-socket token bucket on the
/// <c>step</c> and <c>start</c> frames of <c>/ws/sim</c>, the two that run a solve. A frame over the bucket is answered with
/// an error frame and not acted on; the connection stays open. Other frames are not counted.</item>
/// </list>
/// The limits are per instance and per socket, not per client: a proxy in front of a public instance is still advised.
/// </summary>
public sealed class WebLimits : IDisposable
{
    /// <summary>Configuration key of the concurrent-solve cap (environment variable <c>GridSim__MaxConcurrentSolves</c>).</summary>
    public const string MaxConcurrentSolvesSetting = "GridSim:MaxConcurrentSolves";
    /// <summary>Configuration key of the solve timeout in seconds (<c>GridSim__SolveTimeoutSeconds</c>).</summary>
    public const string SolveTimeoutSecondsSetting = "GridSim:SolveTimeoutSeconds";
    /// <summary>Configuration key of the per-socket sustained rate (<c>GridSim__WsControlRatePerSecond</c>).</summary>
    public const string WsControlRateSetting = "GridSim:WsControlRatePerSecond";
    /// <summary>Configuration key of the per-socket burst (<c>GridSim__WsControlBurst</c>).</summary>
    public const string WsControlBurstSetting = "GridSim:WsControlBurst";

    /// <summary>Default concurrent solves: 8. A one-shot case solve takes milliseconds to seconds, so eight at once is far above
    /// the documented single-user and small-team use and still stops a flood from taking every core.</summary>
    public const int DefaultMaxConcurrentSolves = 8;
    /// <summary>Default solve timeout: 120 s, well above the slowest bundled case.</summary>
    public const int DefaultSolveTimeoutSeconds = 120;
    /// <summary>Default sustained control rate per socket: 5 step or start frames a second.</summary>
    public const int DefaultWsControlRatePerSecond = 5;
    /// <summary>Default burst per socket: 20 frames at once (a new socket starts full).</summary>
    public const int DefaultWsControlBurst = 20;

    /// <summary>The largest concurrent-solve cap accepted. A bound far above any sane use catches a stray extra digit.</summary>
    public const int MaxConcurrentSolvesLimit = 256;
    /// <summary>The largest solve timeout accepted: one hour.</summary>
    public const int SolveTimeoutSecondsLimit = 3600;
    /// <summary>The largest rate and the largest burst accepted.</summary>
    public const int WsControlLimit = 10000;

    private readonly SemaphoreSlim _slots;

    private WebLimits(int maxConcurrentSolves, TimeSpan solveTimeout, int wsRate, int wsBurst, TimeProvider clock)
    {
        MaxConcurrentSolves = maxConcurrentSolves;
        SolveTimeout = solveTimeout;
        WsControlRatePerSecond = wsRate;
        WsControlBurst = wsBurst;
        Clock = clock;
        _slots = new SemaphoreSlim(maxConcurrentSolves, maxConcurrentSolves);
    }

    /// <summary>The most solves worked at once.</summary>
    public int MaxConcurrentSolves { get; }
    /// <summary>How long a request waits for its solve.</summary>
    public TimeSpan SolveTimeout { get; }
    /// <summary>Sustained step and start frames per second, per socket.</summary>
    public int WsControlRatePerSecond { get; }
    /// <summary>Step and start frames a socket may send at once.</summary>
    public int WsControlBurst { get; }
    /// <summary>The clock the timeout and the token buckets read (the system clock unless a test supplies one).</summary>
    public TimeProvider Clock { get; }
    /// <summary>Slots free right now (for tests and diagnostics).</summary>
    public int FreeSolveSlots => _slots.CurrentCount;

    /// <summary>One line for the start-up log.</summary>
    public string Description => string.Create(CultureInfo.InvariantCulture,
        $"at most {MaxConcurrentSolves} concurrent solves ({MaxConcurrentSolvesSetting}), {SolveTimeout.TotalSeconds:0} s solve timeout ({SolveTimeoutSecondsSetting}), "
        + $"/ws/sim step and start frames {WsControlRatePerSecond}/s with a burst of {WsControlBurst} ({WsControlRateSetting}, {WsControlBurstSetting})");

    /// <summary>Read the four settings; unset or blank takes the default, anything else must be a whole number in range or
    /// start-up stops with a message naming the setting.</summary>
    public static WebLimits FromConfiguration(IConfiguration configuration, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new WebLimits(
            ReadInt(configuration[MaxConcurrentSolvesSetting], MaxConcurrentSolvesSetting, DefaultMaxConcurrentSolves, MaxConcurrentSolvesLimit, "the most solves worked at once"),
            TimeSpan.FromSeconds(ReadInt(configuration[SolveTimeoutSecondsSetting], SolveTimeoutSecondsSetting, DefaultSolveTimeoutSeconds, SolveTimeoutSecondsLimit, "seconds to wait for a solve")),
            ReadInt(configuration[WsControlRateSetting], WsControlRateSetting, DefaultWsControlRatePerSecond, WsControlLimit, "step and start frames per second per socket"),
            ReadInt(configuration[WsControlBurstSetting], WsControlBurstSetting, DefaultWsControlBurst, WsControlLimit, "step and start frames per socket at once"),
            clock ?? TimeProvider.System);
    }

    /// <summary>Build limits directly (tests). The same range checks apply.</summary>
    public static WebLimits Create(int maxConcurrentSolves, TimeSpan solveTimeout, int wsRatePerSecond, int wsBurst, TimeProvider? clock = null)
    {
        if (maxConcurrentSolves < 1 || maxConcurrentSolves > MaxConcurrentSolvesLimit)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentSolves), maxConcurrentSolves, "Out of range.");
        if (solveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(solveTimeout), solveTimeout, "The solve timeout must be above zero.");
        if (wsRatePerSecond < 1 || wsRatePerSecond > WsControlLimit)
            throw new ArgumentOutOfRangeException(nameof(wsRatePerSecond), wsRatePerSecond, "Out of range.");
        if (wsBurst < 1 || wsBurst > WsControlLimit)
            throw new ArgumentOutOfRangeException(nameof(wsBurst), wsBurst, "Out of range.");
        return new WebLimits(maxConcurrentSolves, solveTimeout, wsRatePerSecond, wsBurst, clock ?? TimeProvider.System);
    }

    /// <summary>Parse one whole-number setting: blank is the default; otherwise 1..<paramref name="max"/> or fail loud.</summary>
    internal static int ReadInt(string? raw, string setting, int fallback, int max, string meaning)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= max) return n;
        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
            $"GridSim.Web: configuration setting {setting} must be a whole number from 1 to {max} ({meaning}); it is '{raw}'."));
    }

    /// <summary>A fresh per-socket bucket for the step and start frames (GR-221).</summary>
    public TokenBucket NewControlBucket() => new(WsControlRatePerSecond, WsControlBurst, Clock);

    /// <summary>Give a slot back. A solve that outlives host shutdown may finish after <see cref="Dispose"/>; that is not an error.</summary>
    private void ReleaseSlot()
    {
        try { _slots.Release(); }
        catch (ObjectDisposedException) { /* the host is gone; there is nobody left to count slots */ }
    }

    /// <summary>Take a slot if one is free, without waiting.</summary>
    private bool TryTakeSlot() => _slots.Wait(0);

    /// <summary>
    /// GR-218: run <paramref name="work"/> on a pool thread inside a solve slot and wait for it up to the timeout.
    /// A full house returns <see cref="SolveRunStatus.Busy"/> without starting the work. The slot is released when the work
    /// itself ends (not when the caller gives up), so a timed-out solve still counts against the cap until it finishes.
    /// A fault in the work is rethrown to the caller if it is still waiting, and is observed (never left unobserved) if not.
    /// </summary>
    public async Task<SolveRun<T>> RunSolveAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryTakeSlot()) return new SolveRun<T>(SolveRunStatus.Busy, default);

        Task<T> task;
        try
        {
            task = Task.Run(() =>
            {
                try { return work(); }
                finally { ReleaseSlot(); }
            }, CancellationToken.None);
        }
        catch
        {
            ReleaseSlot();   // the work never started, so nothing else will release this slot
            throw;
        }
        // Observe a fault even when the caller has stopped waiting; a caller still waiting gets it from the await below.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        try
        {
            T value = await task.WaitAsync(SolveTimeout, Clock, cancellationToken);
            return new SolveRun<T>(SolveRunStatus.Completed, value);
        }
        catch (TimeoutException)
        {
            return new SolveRun<T>(SolveRunStatus.TimedOut, default);
        }
    }

    /// <summary>
    /// GR-870: <see cref="RunSolveAsync{T}"/> for work that is itself asynchronous (it awaits, for example a lock). The slot
    /// is held until the returned task has finished, not merely until it was created, and every other rule is the same: a
    /// full cap is Busy and the work is not started, a timeout answers TimedOut while the work keeps its slot to its end,
    /// and a fault is observed even when the caller has stopped waiting. A separate name, not an overload, so a lambda
    /// is never ambiguous between the two.
    /// </summary>
    public async Task<SolveRun<T>> RunSolveTaskAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryTakeSlot()) return new SolveRun<T>(SolveRunStatus.Busy, default);

        Task<T> task;
        try
        {
            task = Task.Run(async () =>
            {
                try { return await work().ConfigureAwait(false); }
                finally { ReleaseSlot(); }
            }, CancellationToken.None);
        }
        catch
        {
            ReleaseSlot();   // the work never started, so nothing else will release this slot
            throw;
        }
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        try
        {
            T value = await task.WaitAsync(SolveTimeout, Clock, cancellationToken).ConfigureAwait(false);
            return new SolveRun<T>(SolveRunStatus.Completed, value);
        }
        catch (TimeoutException)
        {
            return new SolveRun<T>(SolveRunStatus.TimedOut, default);
        }
    }

    /// <summary>Releases the slot semaphore.</summary>
    public void Dispose() => _slots.Dispose();
}
