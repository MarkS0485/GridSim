namespace GridSim.Web.Site;

/// <summary>
/// GR-221: a small token bucket, used once per WebSocket so a client cannot make the server run a full solve per frame
/// as fast as it can type. It holds up to <c>burst</c> tokens and gains <c>ratePerSecond</c> tokens each second of the
/// injected clock; each accepted frame takes one token. The clock is a <see cref="TimeProvider"/>, so a test drives time
/// by hand and never waits on the wall clock.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _ratePerSecond;
    private readonly int _burst;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private double _tokens;
    private long _lastStamp;

    /// <summary>A full bucket. Both numbers must be above zero; a rate or burst of zero would refuse every frame.</summary>
    public TokenBucket(double ratePerSecond, int burst, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!double.IsFinite(ratePerSecond) || ratePerSecond <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(ratePerSecond), ratePerSecond, "The refill rate must be a finite number above zero.");
        if (burst < 1)
            throw new ArgumentOutOfRangeException(nameof(burst), burst, "The burst size must be 1 or more.");
        _ratePerSecond = ratePerSecond;
        _burst = burst;
        _clock = clock;
        _tokens = burst;                      // a new socket may burst at once
        _lastStamp = clock.GetTimestamp();
    }

    /// <summary>Take one token if there is one; false (and nothing taken) when the bucket is empty.</summary>
    public bool TryTake()
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            double elapsedSeconds = _clock.GetElapsedTime(_lastStamp, now).TotalSeconds;
            _lastStamp = now;
            // A clock that steps backwards adds nothing (never negative tokens); the bucket never exceeds its burst.
            if (elapsedSeconds > 0.0)
                _tokens = Math.Min(_burst, _tokens + (elapsedSeconds * _ratePerSecond));
            if (_tokens < 1.0) return false;
            _tokens -= 1.0;
            return true;
        }
    }
}
