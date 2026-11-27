using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace GridSim.Web.Site;

/// <summary>
/// A per-client request limit on the REST API (<c>/api</c>) and the script console (<c>/console</c>), read once at
/// start-up from <c>GridSim:MaxRequestsPerClientPerMinute</c>. A client is the key <see cref="AccessControl.ClientKeyFor"/>
/// names (the signed-in subject, or the remote address of an anonymous caller). Each client holds a
/// <see cref="TokenBucket"/> that starts full at the per-minute figure and refills at that figure over a minute; a request
/// that finds it empty is answered 429 with a Retry-After, before any role check, solve slot or session is touched.
/// The default is <see cref="DefaultMaxRequestsPerMinute"/>, far above interactive use; 0 switches the limit off. A caller
/// whose address is not known (an in-process host) is not limited: it is never refused on a guess. This is an
/// application-level bound; a public instance should still limit at its proxy.
/// </summary>
public sealed class ClientRateLimiter
{
    /// <summary>Configuration key (<c>GridSim__MaxRequestsPerClientPerMinute</c>).</summary>
    public const string Setting = "GridSim:MaxRequestsPerClientPerMinute";
    /// <summary>Default: 600 requests a minute per client.</summary>
    public const int DefaultMaxRequestsPerMinute = 600;
    /// <summary>The largest figure accepted.</summary>
    public const int MaxRequestsPerMinuteLimit = 1_000_000;

    private const int PruneAbove = 4096;
    private static readonly TimeSpan IdleLife = TimeSpan.FromMinutes(2);

    private sealed class Entry(TokenBucket bucket, long stamp)
    {
        public TokenBucket Bucket { get; } = bucket;
        public long LastUse { get; set; } = stamp;
    }

    private readonly ConcurrentDictionary<string, Entry> _clients = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>Build a limiter; <paramref name="maxPerMinute"/> 0 is off.</summary>
    public ClientRateLimiter(int maxPerMinute, TimeProvider? clock = null)
    {
        if (maxPerMinute < 0 || maxPerMinute > MaxRequestsPerMinuteLimit)
            throw new ArgumentOutOfRangeException(nameof(maxPerMinute), maxPerMinute, "Out of range.");
        MaxPerMinute = maxPerMinute;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Requests a minute one client may make; 0 = no limit.</summary>
    public int MaxPerMinute { get; }

    /// <summary>True when a limit is set.</summary>
    public bool Enabled => MaxPerMinute > 0;

    /// <summary>Read <c>GridSim:MaxRequestsPerClientPerMinute</c>: blank is the default, 0 is off, otherwise a whole number in range or start-up stops naming the setting.</summary>
    public static ClientRateLimiter FromConfiguration(IConfiguration configuration, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? raw = configuration[Setting];
        if (string.IsNullOrWhiteSpace(raw)) return new ClientRateLimiter(DefaultMaxRequestsPerMinute, clock);
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0 && n <= MaxRequestsPerMinuteLimit)
            return new ClientRateLimiter(n, clock);
        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
            $"GridSim.Web: configuration setting {Setting} must be a whole number from 0 to {MaxRequestsPerMinuteLimit} (requests a minute per client; 0 = no limit); it is '{raw}'."));
    }

    /// <summary>One line for the start-up log.</summary>
    public string Description => Enabled
        ? string.Create(CultureInfo.InvariantCulture, $"{MaxPerMinute} requests a minute per client on /api and /console ({Setting})")
        : $"no per-client request limit ({Setting}=0)";

    /// <summary>True when the request is a limited surface: <c>/api</c> or <c>/console</c> and anything beneath them.</summary>
    public static bool Covers(PathString path)
        => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/console", StringComparison.OrdinalIgnoreCase);

    /// <summary>Take one request for <paramref name="clientKey"/>. True when allowed (always, when off or the key is null).</summary>
    public bool TryAcquire(string? clientKey)
    {
        if (!Enabled || clientKey is null) return true;
        long now = _clock.GetTimestamp();
        if (_clients.Count > PruneAbove) Prune(now);
        Entry e = _clients.GetOrAdd(clientKey, _ => new Entry(new TokenBucket(MaxPerMinute / 60.0, MaxPerMinute, _clock), now));
        e.LastUse = now;
        return e.Bucket.TryTake();
    }

    /// <summary>Seconds a refused client should wait: one token takes 60 / MaxPerMinute seconds, at least 1.</summary>
    public int RetryAfterSeconds => Enabled ? (int)Math.Max(1, Math.Ceiling(60.0 / MaxPerMinute)) : 0;

    private void Prune(long now)
    {
        foreach ((string key, Entry e) in _clients)
            if (_clock.GetElapsedTime(e.LastUse, now) > IdleLife) _clients.TryRemove(key, out _);
    }
}
