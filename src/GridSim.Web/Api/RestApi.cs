using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Security;
using GridSim.Core.Simulation;
using GridSim.Web.Model;
using GridSim.Web.Projection;
using GridSim.Web.Site;

namespace GridSim.Web.Api;

/// <summary>The REST surface: enumerate cases, fetch a case's topology, and run a one-shot solve.</summary>
public static class RestApi
{
    /// <summary>OLTC outer-loop pass cap for the one-shot REST solve: 25, the value <see cref="SteadyStateSolver.Solve"/>
    /// itself defaults to (R204: was a bare literal).</summary>
    public const int RestOltcPassCap = GridSim.Core.PowerFlow.SteadyStateSolver.DefaultMaxOltcPasses;   // 25: the solver default, referenced not repeated

    public static RouteGroupBuilder MapGridSimApi(this RouteGroupBuilder api)
    {
        // The models the dashboard can load.
        // GR-715: the models found under results/ and gda-out/ are listed only to a caller whose band reaches
        // CaseCatalog.PrivateListingRole (an anonymous caller on a host that answers non-loopback names is a Viewer and so
        // is not shown them). Listing only; no refusal, so the call still answers 200 for every caller.
        api.MapGet("/cases", (CaseCatalog catalog, HttpContext http, AccessControl access) =>
            Results.Ok(catalog.EntriesFor(access.IdentityFor(http.User, http.TraceIdentifier).Role).Select(e => new
            {
                e.Id,
                e.Label,
                kind = e.Kind.ToString(),
                replay = e.IsReplay,
            })));

        // A case's one-time topology (map nodes/edges/fleet).
        // B2 (November 2026): routed through the role gate before anything else runs. Topology is a read, so its band is
        // Viewer and every caller holds it; the call states the band here so a later change is one line (AccessControl).
        // GR-218: the load and layout run inside a solve slot with a timeout (WebLimits), AFTER the role gate and the
        // request checks, so a refused or malformed call never takes a slot.
        api.MapGet("/topology", async (string? @case, CaseCatalog catalog, ReplayTopologyCache replayTopologies, HttpContext http, AccessControl access, WebLimits limits, CancellationToken ct) =>
        {
            if (Authorise(http, access, AccessControl.TopologyRole, AccessControl.TopologyOperation) is { } refused) return refused;
            // D-DOC-170: a missing or blank case is a malformed request (400), not an unknown case (404).
            if (MissingCase(@case) is { } bad) return bad;
            CaseEntry? e = catalog.Find(@case!);
            if (e is null) return Results.NotFound(new { error = $"Unknown case '{@case}'." });
            // GR-715: a model under results/ or gda-out/ is read only by a caller who is shown it in a listing
            // (CaseCatalog.PrivateListingRole); a lower band is refused with the same 403 and the same AccessDenied
            // audit entry as any other role denial. Bundled cases stay Viewer. (After the unknown-case check, so an
            // unknown id answers 404 to everyone, as before.)
            if (catalog.IsPrivate(e.Id)
                && Authorise(http, access, CaseCatalog.PrivateListingRole, AccessControl.TopologyPrivateOperation) is { } refusedPrivate)
                return refusedPrivate;

            CaseEntry topoCase = e;   // a non-null copy for the closure below
            // GR-870: a replay's topology is read through the cache's asynchronous lock, inside the same solve slot as
            // before (a cold build is the heavy part and stays capped); every other case keeps the synchronous path.
            if (topoCase.Kind == CaseKind.Replay)
                return await RunLimitedTask(limits, http, async () =>
                {
                    try { return Results.Ok(await replayTopologies.GetAsync(topoCase, catalog.LoadReplay, ct)); }
                    catch (LicenceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
                }, ct);
            return await RunLimited(limits, http, () =>
            {
                TopologyDto topo;
                try
                {
                    // D-DOC-225: a replay's topology comes from the per-run cache (built once, rebuilt only when the run's
                    // files change) instead of reloading every snapshot of the run on every call.
                    topo = topoCase.Kind == CaseKind.Replay
                        ? replayTopologies.Get(topoCase, catalog.LoadReplay)
                        : FrameProjector.BuildTopology(topoCase.Id, topoCase.Label, catalog.LoadModel(topoCase.Id), replay: false, frameCount: 0);
                }
                catch (LicenceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
                return Results.Ok(topo);
            }, ct);
        });

        // The API contract as a small hand-written OpenAPI 3.0 document (no external dependency).
        api.MapGet("/openapi.json", () => Results.Json(OpenApiDoc));

        // R100: the resident real-time estimator's output (it had no consumer). 404 when Realtime:Enabled is off.
        api.MapGet("/realtime", (IServiceProvider sp) =>
        {
            var svc = sp.GetService(typeof(GridSim.Web.Realtime.RealtimeEstimatorHostedService)) as GridSim.Web.Realtime.RealtimeEstimatorHostedService;
            if (svc is null) return Results.NotFound(new { error = "The resident real-time estimator is not enabled (Realtime:Enabled)." });
            GridSim.Core.Simulation.RealtimeEstimateFrame? f = svc.LatestFrame;
            return Results.Ok(new
            {
                // GR-707: one top-level flag a consumer can test, as /api/solve's status code is: true only when there is
                // a latest frame and its estimate converged. The answer stays 200 (the endpoint reports the estimator).
                ok = f is not null && f.Result.Converged,
                summary = svc.LastSummary,
                latest = f is null ? null : new
                {
                    f.Sequence,
                    stateTimeUtc = f.StateTimeUtc,
                    status = f.Result.Status.ToString(),
                    converged = f.Result.Converged,
                    objectiveJ = f.Result.ObjectiveJ,
                    chiSquare = f.Result.ChiSquareTest.ToString(),
                },
                skippedFiles = svc.SkippedFiles,
            });
        });

        // One-shot solve: load, solve once, return topology + the projected frame. Not valid for replays.
        api.MapPost("/solve", async (SolveRequest? req, CaseCatalog catalog, HttpContext http, AccessControl access, WebLimits limits, CancellationToken ct) =>
        {
            // B2 (November 2026): the role gate runs FIRST, before the body is checked or the catalog is asked, so a caller
            // below the Analyst band learns nothing about which cases exist and no model is loaded or solved for it. A
            // denial answers 403 with the reason and is recorded as one AccessDenied audit entry when the host has an
            // audit log (AccessControl.Require).
            if (Authorise(http, access, AccessControl.SolveRole, AccessControl.SolveOperation) is { } refused) return refused;
            // D-DOC-170: a body without a usable "case" is a malformed request (400). Before, it fell through to the
            // catalog lookup and answered 404 "Unknown case ''.", which reads as if a case named '' had been looked for.
            if (MissingCase(req?.Case) is { } bad) return bad;
            CaseEntry? e = catalog.Find(req!.Case!);
            if (e is null) return Results.NotFound(new { error = $"Unknown case '{req.Case}'." });
            if (e.Kind == CaseKind.Replay)
                return Results.BadRequest(new { error = $"Case '{req.Case}' is a replay run - use /ws/sim." });

            // GR-218: from here the call loads and solves, so it runs inside a solve slot with a timeout (WebLimits). The role
            // gate and the body checks above have already passed, so a refused or malformed call never takes a slot.
            CaseEntry solveCase = e;
            return await RunLimited(limits, http, () => SolveOnce(solveCase, catalog), ct);
        });

        return api;
    }

    /// <summary>
    /// GR-218: run a load-and-solve inside a solve slot. Every slot taken answers 503 with a Retry-After (the work is not
    /// started); a solve still running after <see cref="WebLimits.SolveTimeout"/> answers 504 with a clear error naming the
    /// setting. A solve cannot be interrupted, so a timed-out one finishes in the background and keeps its slot until it does.
    /// </summary>
    internal static async Task<IResult> RunLimited(WebLimits limits, HttpContext http, Func<IResult> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(work);
        SolveRun<IResult> run;
        try { run = await limits.RunSolveAsync(work, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away; nobody is left to answer. 499 is the common "client closed request" code.
            return Results.StatusCode(499);
        }
        return Answer(limits, http, run);
    }

    /// <summary>GR-870: <see cref="RunLimited"/> for work that awaits (the replay topology cache's asynchronous lock). Same slot,
    /// same 503, 504 and 499 answers.</summary>
    internal static async Task<IResult> RunLimitedTask(WebLimits limits, HttpContext http, Func<Task<IResult>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(work);
        SolveRun<IResult> run;
        try { run = await limits.RunSolveTaskAsync(work, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        return Answer(limits, http, run);
    }

    /// <summary>The HTTP answer for a finished <see cref="SolveRun{T}"/>: the work's own result, or 503 (busy) or 504 (timed out).</summary>
    private static IResult Answer(WebLimits limits, HttpContext http, SolveRun<IResult> run)
    {
        switch (run.Status)
        {
            case SolveRunStatus.Busy:
                http.Response.Headers.RetryAfter = "5";
                return Results.Json(new
                {
                    error = $"The server is busy: all {limits.MaxConcurrentSolves} solve slots are in use ({WebLimits.MaxConcurrentSolvesSetting}). Try again shortly.",
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            case SolveRunStatus.TimedOut:
                return Results.Json(new
                {
                    error = $"The solve did not finish within {limits.SolveTimeout.TotalSeconds:0} seconds ({WebLimits.SolveTimeoutSecondsSetting}); the result is not available. The work may still be running on the server and holds its slot until it ends.",
                }, statusCode: StatusCodes.Status504GatewayTimeout);
            default:
                return run.Value ?? throw new InvalidOperationException("A completed solve returned no response.");
        }
    }

    /// <summary>The body of POST /api/solve after the gate and the request checks: load, solve once, project. Runs inside a solve slot.</summary>
    private static IResult SolveOnce(CaseEntry e, CaseCatalog catalog)
    {
        GridModel model;
        try { model = catalog.LoadModel(e.Id); }
        catch (LicenceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
        // A dedicated solver per request: SteadyStateSolver is not thread-safe.
        var solver = new SteadyStateSolver();
        // OLTC outer-loop pass cap: the solver's own default (25); a test pins RestOltcPassCap to that default so the
        // REST snapshot and the solver cannot drift apart (R204: was a bare 25 that merely happened to equal it).
        PowerFlowResult result = solver.Solve(model, options: null, maxOltcPasses: RestOltcPassCap);
        GridModel solved = solver.RegulatedModel;

        double gen = solved.Generators.Sum(g => g.Pg);
        double demand = solved.Buses.Sum(b => b.Pd);
        var frame = new SimFrame(solved, result, Hour: 0, Tick: 0,
            // N273 (October 2026, stream B2): a steady snapshot sits at the case's own nominal (50.0 for a GB case).
            FrequencyHz: solved.FrequencyHz, GenerationMw: gen, DemandMw: demand);

        // Full parity with the stream: enrich the single frame with every tab's DTO. The stateful tabs
        // (mechanical ramp / machine time-error) get fresh services, so their history-based fields read zero.
        var fb = new FuelBreakdown(solved);
        FrameEnvelope env = FrameProjector.Project(frame) with
        {
            GenInertia = TabProjections.GenInertia(frame, fb),
            Operator = TabProjections.Operator(frame, fb),
            Market = TabProjections.Market(frame, fb),
            Rp1 = TabProjections.Rp1(frame, fb),
            Mechanical = new Sessions.MechanicalService(1).Project(frame),
            Scada = new Sessions.ScadaService().Project(frame),
            Machine = new Sessions.MachineService().Project(frame, fb),
        };

        TopologyDto topo = FrameProjector.BuildTopology(e.Id, e.Label, model, replay: false, frameCount: 0);

        // D-DOC-075 (fail loud): the answer now says whether the solve converged, in the status code AND the body.
        // Before, a non-converged solve answered 200 with a frame that looked like any other, so a consumer checking
        // the status code took an untrustworthy state as a result. Now:
        //   converged      -> 200, converged = true,  status = "Converged"
        //   not converged  -> 422 Unprocessable Content, converged = false, status = "MaxIterations" or "NonFinite",
        //                     an error text, and the same topology + state so the caller can still inspect the
        //                     failed state (it is marked, never passed off as a result).
        return SolveResponse(result, topo, env);
    }

    /// <summary>
    /// B2 (November 2026): the role gate for a REST call. Null when the caller's band (from the signed-in identity or the
    /// anonymous role, <see cref="AccessControl.IdentityFor"/>) reaches <paramref name="required"/>; otherwise the 403
    /// answer, after <see cref="AccessControl.Require"/> has recorded the denial. The body carries the refusal text
    /// (operation, required and actual band), never a secret.
    /// </summary>
    internal static IResult? Authorise(HttpContext http, AccessControl access, OperationRole required, string operation)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(access);
        IdentityContext identity = access.IdentityFor(http.User, http.TraceIdentifier);
        try
        {
            access.Require(identity, required, operation);
            return null;
        }
        catch (AccessDeniedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>
    /// D-DOC-170: the 400 answer for a request that names no case (absent, null, empty or only spaces), or null when a
    /// case is named. Whether a named case exists is the catalog's question (404), asked after this.
    /// </summary>
    internal static IResult? MissingCase(string? caseId) =>
        string.IsNullOrWhiteSpace(caseId)
            ? Results.BadRequest(new { error = "The request must name a case, for example {\"case\":\"case9\"} (POST /api/solve) or ?case=case9 (GET /api/topology)." })
            : null;

    /// <summary>
    /// D-DOC-075: the HTTP answer for a one-shot solve. 200 only for a converged solve; every other
    /// <see cref="PowerFlowStatus"/> is 422 with <c>converged: false</c>, the status name, the iteration count and the
    /// final mismatch, plus the topology and the (untrustworthy) state for inspection. Internal so a test can drive it
    /// with a non-converged result, which no bundled case produces through the endpoint.
    /// </summary>
    internal static IResult SolveResponse(PowerFlowResult result, TopologyDto topology, FrameEnvelope state)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Converged)
            return Results.Ok(new
            {
                converged = true,
                status = result.Status.ToString(),
                iterations = result.Iterations,
                maxMismatch = result.MaxMismatch,
                topology,
                state,
            });
        return Results.Json(new
        {
            error = $"The power flow did not converge ({result.Status} after {result.Iterations} iterations, largest mismatch {result.MaxMismatch.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}): the state below is not a solution and must not be used as one.",
            converged = false,
            status = result.Status.ToString(),
            iterations = result.Iterations,
            maxMismatch = result.MaxMismatch,
            topology,
            state,
        }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>The POST /api/solve body. <c>Case</c> is nullable so a body without it reaches the 400 check above
    /// instead of being treated as a lookup of the empty name.</summary>
    public sealed record SolveRequest(string? Case);

    // D-DOC-172: these response tables are declared BEFORE OpenApiDoc on purpose. Static fields initialise in text
    // order, and OpenApiDoc reads them; when Ok200 sat below it, four of the five paths were built while Ok200 was still
    // null and the published document carried "responses": null, which is not valid OpenAPI (responses is required).
    private static readonly Dictionary<string, object> Ok200 = new() { ["200"] = new { description = "OK" } };

    private static readonly Dictionary<string, object> TopologyResponses = new()
    {
        ["200"] = new { description = "OK: the topology." },
        ["400"] = new { description = "No case named (missing or blank ?case=)." },
        ["403"] = new { description = "The licence does not cover this model." },
        ["404"] = new { description = "Unknown case." },
        ["503"] = new { description = "Every solve slot is in use (GridSim:MaxConcurrentSolves); retry after the Retry-After seconds." },
        ["504"] = new { description = "The load did not finish within GridSim:SolveTimeoutSeconds." },
    };

    private static readonly Dictionary<string, object> RealtimeResponses = new()
    {
        ["200"] = new { description = "OK: the estimator's latest output." },
        ["404"] = new { description = "The resident estimator is not enabled (Realtime:Enabled)." },
    };

    private static readonly Dictionary<string, object> SolveResponses = new()
    {
        ["200"] = new { description = "The solve converged: converged=true, status, iterations, maxMismatch, topology, state." },
        ["400"] = new { description = "No case named in the body, or the case is a replay run (use /ws/sim)." },
        ["403"] = new { description = "The caller's role is below Analyst (GridSim:AnonymousRole, or a GridSim.<role> role claim), or the licence does not cover this model." },
        ["404"] = new { description = "Unknown case." },
        ["422"] = new { description = "The solve did NOT converge: converged=false, status (MaxIterations or NonFinite), error, iterations, maxMismatch, and the unconverged topology + state for inspection only." },
        ["503"] = new { description = "Every solve slot is in use (GridSim:MaxConcurrentSolves); retry after the Retry-After seconds." },
        ["504"] = new { description = "The solve did not finish within GridSim:SolveTimeoutSeconds." },
    };

    /// <summary>A minimal OpenAPI 3.0 description of the REST + WebSocket surface.</summary>
    private static readonly object OpenApiDoc = new
    {
        openapi = "3.0.3",
        // F02: the API document carries the one release number from Directory.Build.props (was a hard-coded "1.0").
        info = new { title = "GridSim.Web API", version = GridSim.Core.BuildInfo.Version, description = "REST + WebSocket API over GridSim.Core: enumerate cases, fetch topology, one-shot solve, and stream a live/replay/bridge simulation." },
        paths = new Dictionary<string, object>
        {
            ["/api/cases"] = new { get = new { summary = "List loadable models (built-ins, GB JSON, MATPOWER, replay runs).", responses = Ok200 } },
            ["/api/topology"] = new { get = new { summary = "One-time map topology (nodes/edges/fleet) for a case.", parameters = new[] { new { name = "case", @in = "query", required = true, schema = new { type = "string" } } }, responses = TopologyResponses } },
            ["/api/realtime"] = new { get = new { summary = "The resident real-time estimator's last run summary, latest estimate frame and skipped input files (404 when Realtime:Enabled is off). Always 200 when enabled; the top-level ok is true only when there is a latest frame and it converged.", responses = RealtimeResponses } },
            ["/api/solve"] = new { post = new { summary = "Solve a (non-replay) case once; returns topology + a projected frame, with converged / status. A solve that does not converge answers 422, never 200.", requestBody = new { required = true, content = new Dictionary<string, object> { ["application/json"] = new { schema = new { type = "object", properties = new { @case = new { type = "string" } } } } } }, responses = SolveResponses } },
            ["/ws/sim"] = new { get = new { summary = "WebSocket stream. Client sends {action:'start',case,source:'live|replay|bridge' (optional; the case id decides the feed: a run-* id is a replay, a bridge:<case>@<source> id or live-bridge is the bridge, any other case is live; when given, source must match that feed or the start is refused with an error frame),fps,solver:'auto|dense|sparse|sweep' (live cases only),assetGraph:true|false (default false: the state frames' assetGraph is null),waveField:true|false (default false since N154: the state frames' experimental.waveField is null)} then play|pause|step|seek|speed|trip (seek, speed and trip carry a numeric value); server sends a 'topology' message then a 'state' message per tick, answers a malformed frame, an unknown property (named; N227), an unknown action, a control before 'start' or a missing value with an {type:'error',message} frame, and answers a client close frame with its own. A socket may send at most GridSim:WsControlBurst step or start frames at once and GridSim:WsControlRatePerSecond a second after that; a frame over that is answered with an {type:'error',message} frame and not acted on.", responses = new Dictionary<string, object> { ["101"] = new { description = "Switching Protocols (WebSocket)." } } } },
        },
    };
}
