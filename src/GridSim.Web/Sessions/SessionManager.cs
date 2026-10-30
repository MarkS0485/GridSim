using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Scheduling;
using GridSim.Web.Model;

namespace GridSim.Web.Sessions;

/// <summary>
/// Constructs <see cref="SimSession"/>s from the <see cref="CaseCatalog"/>, centralising the
/// live-vs-replay and holdSteady decisions. Sessions are owned by their caller (a Blazor circuit or a
/// WebSocket connection) and disposed when that connection ends. The manager keeps no session objects, only a count
/// of the sessions alive (D-DOC-076: the cap), and is registered as a DI singleton.
/// </summary>
public sealed class SessionManager
{
    /// <summary>
    /// D-DOC-076: the default most sessions (Blazor dashboards plus /ws/sim connections) the process holds at once when
    /// <c>GridSim:MaxSessions</c> is not set. Every live session runs its own solver loop and, by default, its own
    /// analysis-cycle worker pool, so an unbounded count lets any visitor who opens enough tabs exhaust the host. 64 is
    /// a judgement for a small host (a few cores, a few GB): enough for a public raw replay with a modest audience,
    /// small enough that the host is not run out of memory by one visitor opening tabs. A deployment raises or lowers
    /// it with <c>GridSim__MaxSessions</c>.
    /// </summary>
    public const int DefaultMaxSessions = 64;

    /// <summary>The configuration key of the session cap (environment variable <c>GridSim__MaxSessions</c>).</summary>
    public const string MaxSessionsSetting = "GridSim:MaxSessions";

    /// <summary>The bridge sources a <c>bridge:&lt;case&gt;@&lt;source&gt;</c> id may name, in the order the
    /// refusal message lists them. Anything else is refused (D-DOC-077), never replaced by <c>presolved</c>.</summary>
    public static readonly IReadOnlyList<string> BridgeSources = new[] { "presolved", "estimated", "live", "solves", "raw" };

    private readonly CaseCatalog _catalog;
    private readonly string? _bridgeEndpoint;
    private readonly GridSim.Core.Net.ResilienceOptions _bridgeResilience;

    /// <summary>The setting that allows an unencrypted <c>ws://</c> bridge endpoint (default false).</summary>
    public const string BridgeAllowUnencryptedSetting = "GridSim:BridgeAllowUnencrypted";
    private readonly CycleOptions? _cycleOptions;
    private int _counter;

    // D-DOC-076: sessions alive now. Raised by Reserve before a session is built and lowered exactly once when it is
    // disposed (or straight away when building it fails), so the count can neither leak nor go negative.
    private int _active;

    public SessionManager(CaseCatalog catalog, IConfiguration config)
    {
        _catalog = catalog;
        _bridgeEndpoint = config["GridSim:BridgeEndpoint"];   // e.g. wss://host:8765 - optional
        _bridgeResilience = ReadBridgeResilience(_bridgeEndpoint, config[BridgeAllowUnencryptedSetting]);
        ReplayOnly = config.GetValue<bool>("GridSim:ReplayOnly");   // public replay-only instance: raw replay + detuned UI
        MaxSessions = ReadMaxSessions(config[MaxSessionsSetting]);
        ReplayCases = ReadReplayCases(config[ReplayCasesSetting]);   // N346: which raw replays a replay-only host serves

        // The prioritised parallel analysis cycle is ON by default for live sessions (set Cycle:Enabled=false
        // to turn it off). Workers default to a modest share so the background fan-out never starves the web
        // server; window and shard granularity are overridable via the Cycle: config section.
        bool cycleEnabled = config.GetValue<bool?>("Cycle:Enabled") ?? true;
        if (cycleEnabled)
        {
            var o = new CycleOptions { Workers = Math.Min(GridSim.Web.Projection.PresentationLimits.DefaultCycleWorkerCap, Math.Max(1, Environment.ProcessorCount - 1)) };
            if (config.GetValue<double?>("Cycle:WindowMs") is { } w) o = o with { WindowMs = w };
            if (config.GetValue<int?>("Cycle:Workers") is { } wk) o = o with { Workers = Math.Max(1, wk) };
            if (config.GetValue<int?>("Cycle:Shards") is { } sh) o = o with { NMinus1Shards = Math.Max(1, sh) };
            _cycleOptions = o;
        }
    }

    /// <summary>
    /// Check the bridge endpoint at start-up and build the client settings every bridge session uses. The client
    /// refuses an unencrypted <c>ws://</c> endpoint unless told otherwise (NCSC CAF B3), and before this nothing in the
    /// web host could tell it otherwise: every documented <c>ws://</c> deployment failed on every bridge session with
    /// "Refusing to connect to an unencrypted bridge endpoint". Now:
    /// <list type="bullet">
    /// <item>an endpoint that is not an absolute <c>ws://</c> or <c>wss://</c> URI stops start-up, naming the setting;</item>
    /// <item>a <c>ws://</c> endpoint stops start-up unless <see cref="BridgeAllowUnencryptedSetting"/> is <c>true</c>
    /// (for a bridge on a private network or loopback, where the transport is protected by other means);</item>
    /// <item>the setting must be <c>true</c> or <c>false</c> when present (fail loud: a typo is not "allowed").</item>
    /// </list>
    /// The resulting <see cref="GridSim.Core.Net.ResilienceOptions"/> (the default limits plus the transport choice) is
    /// applied to every bridge client. Public for the tests.
    /// </summary>
    public static GridSim.Core.Net.ResilienceOptions ReadBridgeResilience(string? endpoint, string? allowUnencrypted)
    {
        bool allow = false;
        if (!string.IsNullOrWhiteSpace(allowUnencrypted))
        {
            if (!bool.TryParse(allowUnencrypted.Trim(), out allow))
                throw new InvalidOperationException(
                    $"{BridgeAllowUnencryptedSetting} must be true or false; got '{allowUnencrypted}'.");
        }
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
                || !(string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"GridSim:BridgeEndpoint must be an absolute ws:// or wss:// URI; got '{endpoint}'.");
            if (string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) && !allow)
                throw new InvalidOperationException(
                    $"GridSim:BridgeEndpoint '{endpoint}' is unencrypted (ws://). Use a wss:// endpoint, or set " +
                    $"{BridgeAllowUnencryptedSetting}=true for a bridge on a private network or loopback.");
        }
        var options = GridSim.Core.Net.ResilienceOptions.Default with { AllowUnencryptedEndpoint = allow };
        options.Validate();
        return options;
    }

    /// <summary>
    /// Read <c>GridSim:MaxSessions</c>: unset or blank means <see cref="DefaultMaxSessions"/>; otherwise it must be a
    /// whole number of 1 or more. Anything else stops start-up with a message naming the setting (fail loud: a typo
    /// must not silently mean "no cap" or "no sessions at all"). Program.cs builds the manager at start-up so this
    /// check runs before the first request, not on it.
    /// </summary>
    internal static int ReadMaxSessions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultMaxSessions;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1) return n;
        throw new InvalidOperationException(
            $"GridSim.Web: configuration setting {MaxSessionsSetting} must be a whole number of 1 or more (the most sessions held at once); it is '{raw}'.");
    }

    /// <summary>A live GDA bridge is available when an endpoint is configured.</summary>
    public bool BridgeConfigured => !string.IsNullOrWhiteSpace(_bridgeEndpoint);

    /// <summary>Public replay-only instance (GridSim:ReplayOnly=true): the app offers ONLY the raw public-data
    /// replay - solver/physics UI is hidden and the solve API / sim WebSocket are not mapped. Drives the Home
    /// redirect, the Dashboard tab filter, the map detune and (D-DOC-016) the refusal in <see cref="Create"/> of
    /// every session that is not the raw replay.</summary>
    public bool ReplayOnly { get; }

    /// <summary>D-DOC-076: the most sessions held at once (<c>GridSim:MaxSessions</c>, default
    /// <see cref="DefaultMaxSessions"/>).</summary>
    public int MaxSessions { get; }

    /// <summary>D-DOC-076: how many sessions are alive now (created and not yet disposed).</summary>
    public int ActiveSessions => Volatile.Read(ref _active);

    /// <summary>The default raw-replay session id a ReplayOnly instance lands on (bridge source=raw on gb-full).</summary>
    public const string RawLandingCaseId = "bridge:gb-full@raw";

    /// <summary>N346 (CF3, October 2026): the configuration key naming the remote cases whose raw replay
    /// (<c>bridge:&lt;case&gt;@raw</c>) a replay-only instance serves (environment variable <c>GridSim__ReplayCases</c>).</summary>
    public const string ReplayCasesSetting = "GridSim:ReplayCases";

    /// <summary>N346: the raw replays a replay-only instance serves when <see cref="ReplayCasesSetting"/> is not set:
    /// gb-full alone, the public replay it was built for (the case <see cref="RawLandingCaseId"/> opens).</summary>
    public static readonly IReadOnlyList<string> DefaultReplayCases = new[] { "gb-full" };

    /// <summary>N346: the remote cases whose raw replay this instance serves when it is replay-only, in the order
    /// configured (<see cref="ReplayCasesSetting"/>; default <see cref="DefaultReplayCases"/>). The first is where the
    /// home page lands (<see cref="LandingCaseId"/>).</summary>
    public IReadOnlyList<string> ReplayCases { get; }

    /// <summary>N346: the session id a replay-only instance's home page opens: the raw replay of the first configured
    /// case (<see cref="RawLandingCaseId"/> with the default setting).</summary>
    public string LandingCaseId => $"bridge:{ReplayCases[0]}@raw";

    /// <summary>
    /// N346: read <c>GridSim:ReplayCases</c>, a comma-separated list of remote case names (letters, digits, '-', '_' and
    /// '.'). Unset or blank means <see cref="DefaultReplayCases"/>. An empty entry, a name with any other character
    /// (a space, an '@', a ':' or a '/', for example) or a name given twice stops start-up with a message naming the
    /// setting (fail loud: a typo must not silently serve a different replay, or none). Names compare without regard to
    /// case, as the bridge source does.
    /// </summary>
    internal static IReadOnlyList<string> ReadReplayCases(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultReplayCases;
        var cases = new List<string>();
        foreach (string part in raw.Split(','))
        {
            string name = part.Trim();
            if (name.Length == 0)
                throw new InvalidOperationException(
                    $"GridSim.Web: configuration setting {ReplayCasesSetting} has an empty entry ('{raw}'); list remote case names separated by commas, for example 'gb-full'.");
            foreach (char ch in name)
                if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
                    throw new InvalidOperationException(
                        $"GridSim.Web: configuration setting {ReplayCasesSetting} entry '{name}' is not a remote case name (letters, digits, '-', '_' and '.' only); it is '{raw}'.");
            if (cases.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"GridSim.Web: configuration setting {ReplayCasesSetting} names '{name}' twice; it is '{raw}'.");
            cases.Add(name);
        }
        return cases;
    }

    /// <summary>
    /// D-DOC-016: may <paramref name="caseId"/> be opened on a replay-only instance? Only the raw public-data replay,
    /// <c>bridge:&lt;case&gt;@raw</c>, qualifies: it runs no solver, no inference and no analysis cycle here. Everything
    /// else (a built-in or JSON case, a MATPOWER file, a presolved run directory, <c>live-bridge</c>, any other bridge
    /// source, or a malformed id) is refused. Used by <see cref="Create"/> (which every session goes through, whatever
    /// the route) and by the HTTP gate in Program.cs (which answers 404 for the page before any component runs).
    /// </summary>
    /// <remarks>N346 (CF3, October 2026): only the raw replay of a CONFIGURED case qualifies. This overload checks
    /// against <see cref="DefaultReplayCases"/> (gb-full); an instance with <see cref="ReplayCasesSetting"/> set uses
    /// <see cref="IsAllowedHere"/>. Before N346 any <c>bridge:&lt;case&gt;@raw</c> was accepted, so a replay-only host
    /// would open the raw replay of any case the bridge server happened to hold.</remarks>
    public static bool IsAllowedOnReplayOnly(string? caseId) => IsAllowedOnReplayOnly(caseId, DefaultReplayCases);

    /// <summary>N346: may <paramref name="caseId"/> be opened on a replay-only instance serving
    /// <paramref name="replayCases"/>? True only for <c>bridge:&lt;case&gt;@raw</c> whose case (blank means gb-full) is
    /// one of them, compared without regard to case.</summary>
    public static bool IsAllowedOnReplayOnly(string? caseId, IReadOnlyCollection<string> replayCases)
        => RawReplayCase(caseId) is { } remote && replayCases.Contains(remote, StringComparer.OrdinalIgnoreCase);

    /// <summary>N346: <see cref="IsAllowedOnReplayOnly(string?, IReadOnlyCollection{string})"/> for this instance's
    /// <see cref="ReplayCases"/>. Used by <see cref="Create"/> and by the HTTP gate in Program.cs.</summary>
    public bool IsAllowedHere(string? caseId) => IsAllowedOnReplayOnly(caseId, ReplayCases);

    /// <summary>The remote case of a raw-replay id (<c>bridge:&lt;case&gt;@raw</c>, blank case = gb-full), or null for
    /// anything else: another source, a non-bridge id, a malformed id or an unknown <c>@source</c> (D-DOC-077).</summary>
    internal static string? RawReplayCase(string? caseId)
    {
        if (string.IsNullOrWhiteSpace(caseId)) return null;
        try
        {
            return IsBridgeRequest(caseId, out string remote, out string source) && source == "raw" ? remote.Trim() : null;
        }
        catch (ArgumentException)
        {
            return null;   // an unknown @source (D-DOC-077) is not the raw replay either
        }
    }

    /// <summary>
    /// Create a session for a case. Replay run-dirs load a <see cref="Core.Simulation.PresolvedReplay"/>;
    /// everything else drives a live sim - held steady for the IEEE/MATPOWER benchmarks (no GB demand
    /// profile) and run on the GB day/night curve for the JSON GB cases. A caseId prefixed <c>bridge:</c>
    /// (or the special <c>live-bridge</c> entry) streams from the configured GDA bridge server instead.
    /// </summary>
    /// <param name="caseId">The catalog id, a run directory id, or a <c>bridge:</c> id.</param>
    /// <param name="solver">N063: the power-flow engine for a LIVE session (default Auto, the behaviour before the
    /// switch existed). A replay or bridge session runs no solver of its own, so asking one for a specific engine is
    /// refused rather than silently ignored.</param>
    /// <exception cref="ReplayOnlyRefusedException">D-DOC-016: a replay-only instance and anything but the raw replay.</exception>
    /// <exception cref="SessionLimitException">D-DOC-076: the process already holds <see cref="MaxSessions"/> sessions.</exception>
    /// <exception cref="ArgumentException">An unknown <c>@source</c> (D-DOC-077) or a solver for a replay / bridge feed.</exception>
    public SimSession Create(string caseId, SolverKind solver = SolverKind.Auto)
    {
        // D-DOC-016 (SECURITY): before this check, replay-only was enforced only by the Home redirect and the unmapped
        // /api and /ws/sim routes, so GET /dashboard/case9 on a replay-only host opened a LIVE solver session (with the
        // AUDIT LOG tab). Refusing here closes every route at once: the page, an in-circuit navigation that never makes
        // an HTTP request, and anything added later, because every session is built by this method.
        // N346: and only the raw replay of a case this instance was configured for (GridSim:ReplayCases, default
        // gb-full); a raw replay of any other case is refused by name, not opened.
        if (ReplayOnly && !IsAllowedHere(caseId))
            throw new ReplayOnlyRefusedException(caseId, ReplayCases, RawReplayCase(caseId));

        Reserve();
        try
        {
            SimSession session = Build(caseId, solver);
            session.AttachRelease(Release);   // lowered once, when the owner disposes the session
            return session;
        }
        catch
        {
            Release();   // nothing was handed out, so the slot goes back now
            throw;
        }
    }

    private SimSession Build(string caseId, SolverKind solver)
    {
        string sid = $"s{Interlocked.Increment(ref _counter)}";

        if (IsBridgeRequest(caseId, out string remoteCase, out string remoteSource))
        {
            if (solver != SolverKind.Auto) throw NoSolverChoice(caseId, solver);
            if (!BridgeConfigured)
                throw new InvalidOperationException("No GDA bridge endpoint is configured (GridSim:BridgeEndpoint).");
            return SimSession.CreateBridge(sid, new Uri(_bridgeEndpoint!), remoteCase, remoteSource, _bridgeResilience);
        }

        CaseEntry e = _catalog.Find(caseId)
            ?? throw new KeyNotFoundException($"Unknown case '{caseId}'.");

        if (e.Kind == CaseKind.Replay)
        {
            if (solver != SolverKind.Auto) throw NoSolverChoice(caseId, solver);
            return SimSession.CreateReplay(sid, e.Id, e.Label, _catalog.LoadReplay(caseId));
        }

        GridModel model = _catalog.LoadModel(caseId);
        bool holdSteady = e.Kind is CaseKind.BuiltIn or CaseKind.Matpower;
        return SimSession.CreateLive(sid, e.Id, e.Label, model, holdSteady, cycleOptions: _cycleOptions, solver: solver);
    }

    /// <summary>D-DOC-076: take one session slot, or refuse when the cap is reached. Increment first, then check, so
    /// two requests racing for the last slot cannot both get it; the loser gives its increment back.</summary>
    private void Reserve()
    {
        int now = Interlocked.Increment(ref _active);
        if (now > MaxSessions)
        {
            Interlocked.Decrement(ref _active);
            throw new SessionLimitException(MaxSessions);
        }
    }

    private void Release() => Interlocked.Decrement(ref _active);

    private static ArgumentException NoSolverChoice(string caseId, SolverKind solver) => new(
        $"'{caseId}' is a replay or bridge feed: it runs no solver here, so solver '{SolverChoice.NameOf(solver)}' cannot be chosen (only live cases take a solver).",
        nameof(solver));

    /// <summary>
    /// The feed a case id opens, without opening it: <c>bridge</c> for a <c>bridge:</c> id or <c>live-bridge</c>,
    /// <c>replay</c> for a presolved run directory, <c>live</c> for every other catalog case, and null for an id the
    /// catalog does not know. Used by /ws/sim to check a client's optional <c>source</c> field (D-DOC-172).
    /// Throws <see cref="ArgumentException"/> for an unknown <c>@source</c>, exactly as <see cref="Create"/> does.
    /// </summary>
    public string? FeedOf(string caseId)
    {
        if (IsBridgeRequest(caseId, out _, out _)) return "bridge";
        CaseEntry? e = _catalog.Find(caseId);
        if (e is null) return null;
        return e.Kind == CaseKind.Replay ? "replay" : "live";
    }

    /// <summary>Parse <c>bridge:&lt;case&gt;[@&lt;source&gt;]</c> / <c>live-bridge</c>. The optional <c>@source</c>
    /// suffix picks the reconstruction the server replays: <c>presolved</c> (default, when there is no <c>@</c>),
    /// <c>estimated</c> (measurement-fitted WLS lake, frames carry the chi-square quality block), <c>live</c>,
    /// <c>solves</c> or <c>raw</c>.
    /// <para>D-DOC-077: an <c>@</c> followed by anything else (a typo such as <c>@estimate</c>, or nothing) is refused
    /// with an <see cref="ArgumentException"/> naming the valid sources. Before, it was dropped silently and the session
    /// opened the presolved lake, so a user asking for estimated states was shown presolved ones without being told.</para></summary>
    private static bool IsBridgeRequest(string caseId, out string remoteCase, out string remoteSource)
    {
        remoteSource = "presolved";
        if (caseId.StartsWith("bridge:", StringComparison.OrdinalIgnoreCase))
        {
            remoteCase = caseId["bridge:".Length..];
            int at = remoteCase.IndexOf('@');
            if (at >= 0)
            {
                string src = remoteCase[(at + 1)..].Trim().ToLowerInvariant();
                if (!BridgeSources.Contains(src))
                    throw new ArgumentException(
                        $"Unknown bridge source '{remoteCase[(at + 1)..]}' in '{caseId}'. Use one of: {string.Join(", ", BridgeSources)} (or leave out the '@' suffix for presolved).",
                        nameof(caseId));
                remoteSource = src;
                remoteCase = remoteCase[..at];
            }
            if (string.IsNullOrWhiteSpace(remoteCase)) remoteCase = "gb-full";
            return true;
        }
        if (string.Equals(caseId, "live-bridge", StringComparison.OrdinalIgnoreCase))
        {
            remoteCase = "gb-full";
            return true;
        }
        remoteCase = "";
        return false;
    }
}

/// <summary>
/// D-DOC-016: a session other than the raw public-data replay was asked of a replay-only instance
/// (<c>GridSim:ReplayOnly=true</c>). The message is shown on the dashboard's load-error panel or sent as a /ws/sim
/// error frame; it names the setting so an operator knows why.
/// </summary>
public sealed class ReplayOnlyRefusedException : InvalidOperationException
{
    public ReplayOnlyRefusedException(string? caseId)
        : this(caseId, SessionManager.DefaultReplayCases, rawReplayCase: null)
    {
    }

    /// <summary>N346: the refusal naming the cases this instance serves. When <paramref name="rawReplayCase"/> is not
    /// null the id WAS a raw replay, of a case the instance is not configured for, and the message says so.</summary>
    public ReplayOnlyRefusedException(string? caseId, IReadOnlyList<string> replayCases, string? rawReplayCase)
        : base(rawReplayCase is not null
            ? $"This is a replay-only instance (GridSim:ReplayOnly=true): '{caseId}' is the raw replay of case '{rawReplayCase}', which this instance is not configured to serve ({SessionManager.ReplayCasesSetting}: {string.Join(", ", replayCases)}), so it is refused."
            : $"This is a replay-only instance (GridSim:ReplayOnly=true): only the raw public-data replay of {string.Join(", ", replayCases.Select(c => $"bridge:{c}@raw"))} ({SessionManager.ReplayCasesSetting}) can be opened here, so '{caseId}' is refused.")
    {
        CaseId = caseId;
    }

    /// <summary>The refused case id, as asked.</summary>
    public string? CaseId { get; }
}

/// <summary>
/// D-DOC-076: the process already holds <c>GridSim:MaxSessions</c> sessions. Shown on the dashboard's load-error
/// panel or sent as a /ws/sim error frame; closing another tab or connection frees a slot.
/// </summary>
public sealed class SessionLimitException : InvalidOperationException
{
    public SessionLimitException(int max)
        : base($"The server is already running its maximum of {max} sessions ({SessionManager.MaxSessionsSetting}); close another dashboard or connection, or try again shortly.")
    {
        Max = max;
    }

    /// <summary>The cap that was reached.</summary>
    public int Max { get; }
}
