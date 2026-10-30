namespace GridSim.Core.Net;

/// <summary>
/// How a resident service rides out an interruption of the bridge stream (NCSC CAF B5, "resilient networks and
/// systems"): the bounded reconnect schedule, and the resource limits a <see cref="GridBridgeClient"/> applies.
/// A record so a deployment can tune it; the defaults are conservative.
///
/// <para><b>Determinism is the recovery ally.</b> A reconnect does not reconstruct anything new: because a replay
/// is bit-reproducible, a service that drops and reconnects re-runs the same window and gets the identical state,
/// so there is no divergent recovered state to reconcile - and a divergence would itself be an incident signal.
/// The backoff here only bounds how hard the client retries; it never changes what the stream is allowed to carry
/// (still permitted-age or replay data, behind the forward-inference guard).</para>
/// </summary>
/// <param name="InitialBackoff">The delay before the first reconnect attempt.</param>
/// <param name="MaxBackoff">The ceiling the backoff grows to.</param>
/// <param name="BackoffMultiplier">The factor the delay is multiplied by after each failed attempt (&gt;= 1).</param>
/// <param name="MaxReconnectAttempts">The most reconnect attempts before giving up (0 means never give up).</param>
/// <param name="MaxMessageBytes">The largest single bridge message to accept (B4 resource limit).</param>
/// <param name="MaxInFlightQueries">The most outstanding queries at once (B4 resource limit).</param>
/// <param name="AllowUnencryptedEndpoint">When false, an unencrypted <c>ws://</c> endpoint is refused (B3).</param>
public sealed record ResilienceOptions(
    TimeSpan InitialBackoff,
    TimeSpan MaxBackoff,
    double BackoffMultiplier,
    int MaxReconnectAttempts,
    long MaxMessageBytes,
    int MaxInFlightQueries,
    bool AllowUnencryptedEndpoint)
{
    /// <summary>Conservative defaults: 1 s initial backoff doubling to a 30 s ceiling, never giving up, an 8 MiB
    /// message cap, 256 in-flight queries, and encrypted transport required.</summary>
    public static ResilienceOptions Default { get; } = new(
        InitialBackoff: TimeSpan.FromSeconds(1),
        MaxBackoff: TimeSpan.FromSeconds(30),
        BackoffMultiplier: 2.0,
        MaxReconnectAttempts: 0,
        MaxMessageBytes: 8L * 1024 * 1024,
        MaxInFlightQueries: 256,
        AllowUnencryptedEndpoint: false);

    /// <summary>Validate the schedule (fail loud on a nonsensical configuration).</summary>
    public void Validate()
    {
        if (InitialBackoff <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialBackoff), InitialBackoff, "must be positive.");
        if (MaxBackoff < InitialBackoff)
            throw new ArgumentOutOfRangeException(nameof(MaxBackoff), MaxBackoff, "must be at least the initial backoff.");
        if (!(BackoffMultiplier >= 1.0) || !double.IsFinite(BackoffMultiplier))
            throw new ArgumentOutOfRangeException(nameof(BackoffMultiplier), BackoffMultiplier, "must be a finite number >= 1.");
        if (MaxReconnectAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts), MaxReconnectAttempts, "must be >= 0 (0 means unlimited).");
        if (MaxMessageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes), MaxMessageBytes, "must be positive.");
        if (MaxInFlightQueries <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxInFlightQueries), MaxInFlightQueries, "must be positive.");
    }

    /// <summary>
    /// The backoff delay before reconnect attempt number <paramref name="attempt"/> (1-based): the initial backoff
    /// times the multiplier to the (attempt-1) power, capped at <see cref="MaxBackoff"/>. Deterministic (no jitter),
    /// so a test can assert the exact schedule; a deployment that wants jitter adds it at the call site.
    /// </summary>
    public TimeSpan BackoffDelay(int attempt)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "the attempt number is 1-based.");
        double seconds = InitialBackoff.TotalSeconds * GridSim.Core.Numerics.DetMath.Pow(BackoffMultiplier, attempt - 1);   // D1: no platform libm in Core
        double capped = Math.Min(seconds, MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }

    /// <summary>Apply the resource-limit and transport settings to a <see cref="GridBridgeClient"/>.</summary>
    public void ApplyTo(GridBridgeClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.MaxMessageBytes = MaxMessageBytes;
        client.MaxInFlightQueries = MaxInFlightQueries;
        client.AllowUnencryptedEndpoint = AllowUnencryptedEndpoint;
    }
}
