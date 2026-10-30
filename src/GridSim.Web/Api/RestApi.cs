using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Web.Model;
using GridSim.Web.Projection;

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
        api.MapGet("/cases", (CaseCatalog catalog) =>
            Results.Ok(catalog.Entries.Select(e => new
            {
                e.Id,
                e.Label,
                kind = e.Kind.ToString(),
                replay = e.IsReplay,
            })));

        // A case's one-time topology (map nodes/edges/fleet).
        api.MapGet("/topology", (string? @case, CaseCatalog catalog, ReplayTopologyCache replayTopologies) =>
        {
            // D-DOC-170: a missing or blank case is a malformed request (400), not an unknown case (404).
            if (MissingCase(@case) is { } bad) return bad;
            CaseEntry? e = catalog.Find(@case!);
            if (e is null) return Results.NotFound(new { error = $"Unknown case '{@case}'." });

            TopologyDto topo;
            try
            {
                // D-DOC-225: a replay's topology comes from the per-run cache (built once, rebuilt only when the run's
                // files change) instead of reloading every snapshot of the run on every call.
                topo = e.Kind == CaseKind.Replay
                    ? replayTopologies.Get(e, catalog.LoadReplay)
                    : FrameProjector.BuildTopology(e.Id, e.Label, catalog.LoadModel(e.Id), replay: false, frameCount: 0);
            }
            catch (LicenceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
            return Results.Ok(topo);
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
        api.MapPost("/solve", (SolveRequest? req, CaseCatalog catalog) =>
        {
            // D-DOC-170: a body without a usable "case" is a malformed request (400). Before, it fell through to the
            // catalog lookup and answered 404 "Unknown case ''.", which reads as if a case named '' had been looked for.
            if (MissingCase(req?.Case) is { } bad) return bad;
            CaseEntry? e = catalog.Find(req!.Case!);
            if (e is null) return Results.NotFound(new { error = $"Unknown case '{req.Case}'." });
            if (e.Kind == CaseKind.Replay)
                return Results.BadRequest(new { error = $"Case '{req.Case}' is a replay run - use /ws/sim." });

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
        });

        return api;
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
        ["403"] = new { description = "The licence does not cover this model." },
        ["404"] = new { description = "Unknown case." },
        ["422"] = new { description = "The solve did NOT converge: converged=false, status (MaxIterations or NonFinite), error, iterations, maxMismatch, and the unconverged topology + state for inspection only." },
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
            ["/ws/sim"] = new { get = new { summary = "WebSocket stream. Client sends {action:'start',case,source:'live|replay|bridge' (optional; the case id decides the feed: a run-* id is a replay, a bridge:<case>@<source> id or live-bridge is the bridge, any other case is live; when given, source must match that feed or the start is refused with an error frame),fps,solver:'auto|dense|sparse|sweep' (live cases only),assetGraph:true|false (default false: the state frames' assetGraph is null),waveField:true|false (default false since N154: the state frames' experimental.waveField is null)} then play|pause|step|seek|speed|trip (seek, speed and trip carry a numeric value); server sends a 'topology' message then a 'state' message per tick, answers a malformed frame, an unknown property (named; N227), an unknown action, a control before 'start' or a missing value with an {type:'error',message} frame, and answers a client close frame with its own.", responses = new Dictionary<string, object> { ["101"] = new { description = "Switching Protocols (WebSocket)." } } } },
        },
    };
}
