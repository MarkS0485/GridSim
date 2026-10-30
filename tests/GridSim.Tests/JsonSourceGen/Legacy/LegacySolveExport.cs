using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.IO;

using Physics = GridSim.Core.Physics;

namespace GridSim.Tests.JsonSourceGen.Legacy;

// Phase 0.1 equivalence oracle: a verbatim, test-only copy of GridSim.Core.IO.SolveExport as it stood before source-generated JSON
// (GridSim 2.0.48), still serialising its anonymous objects through runtime reflection. Only the class name (LegacySolveExport)
// and the namespace changed (a using alias stands in for the names Core resolves through its own namespace), and its call to SolveExport.PhysicsBlock now goes to the legacy copy.
// JsonSourceGenEquivalenceTests requires the production exporter to write exactly the bytes this copy writes. Never
// edit it to follow the production code: it is the old behaviour, frozen.

internal static class LegacySolveExport
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Real solves emit non-finite values: PEGASE cases carry Inf gen limits, and a non-converged BFS
        // slice returns MaxMismatch = +inf. Without this the export throws mid-run and kills a transient/
        // gda-replay run; SolveImport already reads these back with the same setting.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <param name="physics">D-DOC-040: the physics-law audit to write in the <c>physics</c> block. Null (every
    /// solve) audits <paramref name="result"/> against the case-file dispatch; the estimated-solve export passes
    /// <see cref="EstimateExport.PhysicsReport"/>, because an estimate is not a solve of the case-file dispatch.</param>
    /// <param name="estimate">D-DOC-071: set when <paramref name="result"/> is an estimate projected into the solve
    /// shape (see <see cref="SolveExportEstimate"/>); null (every solve) writes the power-flow solve block.</param>
    /// <param name="tightenedTolerancePu">N251 (October 2026): the stopping tolerance (pu) of the solve whose state is
    /// exported, when the caller re-solved the case below the everyday 1e-8 pu to remove the convergence residual
    /// (<c>--enableExport --export-tight</c>, the machine-epsilon cross-validation); written as
    /// <c>solve.tightenedTolerancePu</c>. Null (the default, every existing caller) writes nothing, so the document is
    /// byte-identical to before.</param>
    public static void WriteJson(
        string path, GridModel model, PowerFlowResult result, SolveTrace trace,
        SolveExportMeta meta, string? slackQNote = null, ExportPhysics? physics = null, SolveExportEstimate? estimate = null,
        double? tightenedTolerancePu = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(model, result, trace, meta, slackQNote, physics, estimate, tightenedTolerancePu));
    }

    public static string ToJson(
        GridModel model, PowerFlowResult result, SolveTrace trace,
        SolveExportMeta meta, string? slackQNote = null, ExportPhysics? physics = null, SolveExportEstimate? estimate = null,
        double? tightenedTolerancePu = null)
    {
        // N251: a tightened tolerance belongs to a power-flow solve; an estimate projected into the solve shape has none.
        if (tightenedTolerancePu is double tt && (estimate is not null || !(tt > 0) || !double.IsFinite(tt)))
            throw new ArgumentException($"a tightened solve tolerance ({tt}) needs a power-flow solve (no estimate) and a positive finite value.", nameof(tightenedTolerancePu));
        var doc = new
        {
            schema = "gridsim-solve-export/1",
            meta,
            model = new
            {
                name = model.Name,
                baseMva = model.BaseMva,
                // R177 / Q13 (October 2026): carried so a re-import (SolveImport -> BridgeProtocol.BuildModel) keeps
                // the model's own nominal frequency instead of assuming 50 Hz.
                frequencyHz = model.FrequencyHz,
                busCount = model.BusCount,
                buses = model.Buses.Select(b => new
                {
                    id = b.Id, type = b.Type.ToString(), baseKv = b.BaseKv,
                    pd = b.Pd, qd = b.Qd, gs = b.Gs, bs = b.Bs, vm = b.Vm, name = b.Name,   // carry asset identity for replay
                    zip = ZipBlock(b.Zip),   // R031: null (omitted) = constant power
                    // D-DOC-067: the initial / fixed angle (the slack's reference angle), the layout coordinates and the
                    // bus's own voltage limits (F17; null = the global band, omitted).
                    vaDeg = b.VaDeg, x = b.X, y = b.Y,
                    vminPu = b.VminPu, vmaxPu = b.VmaxPu,
                }),
                // D-DOC-067: the WHOLE fleet in the order given, each unit with its service state. Model.Generators is the
                // in-service list only (F16), so an out-of-service unit used to vanish from the export and could not come
                // back on re-import. Readers that rebuild the network skip "inService": false (the solve did).
                generators = model.AllGenerators.Select(g => new
                {
                    bus = g.Bus, pg = g.Pg, qg = g.Qg, vg = g.Vg,
                    qmin = g.Qmin, qmax = g.Qmax, pmin = g.Pmin, pmax = g.Pmax, mBase = g.MBase, h = g.H,
                    type = g.Type, name = g.Name,
                    inService = g.InService,
                    // D-DOC-067: the fault, converter and dynamics data (each the model's own value; nulls are omitted).
                    dynamicsClass = g.DynamicsClass,
                    subtransientXdPu = g.SubtransientXdPu, negativeSeqXPu = g.NegativeSeqXPu,
                    converterInterfaced = g.ConverterInterfaced, converterFaultCurrentPu = g.ConverterFaultCurrentPu,
                    powerFlowOnly = g.PowerFlowOnly, unitTransformerXPu = g.UnitTransformerXPu, gridForming = g.GridForming,
                    cost = CostBlock(g.Cost), reactiveCost = CostBlock(g.ReactiveCost),
                }),
                branches = model.Branches.Select(e => new
                {
                    from = e.From, to = e.To, r = e.R, x = e.X, b = e.B,
                    tap = e.TapRatio, phaseShiftDeg = e.PhaseShiftDeg,
                    rateAMva = e.RateAMva, name = e.Name, isTransformer = e.IsTransformer,
                    // R031: the branch's service state. Without it an exported post-outage state re-imported with
                    // its opened branches back in service, while its own result showed zero flow on them.
                    inService = e.InService,
                    // R031: the short-circuit data, so a re-imported model runs the same fault studies.
                    r0 = e.R0, x0 = e.X0,
                    fromWinding = e.FromWinding?.ToString(), toWinding = e.ToWinding?.ToString(),
                    fromNeutralXPu = e.FromNeutralXPu, toNeutralXPu = e.ToNeutralXPu,
                    // D-DOC-067: length, conductor type, the short-term and emergency ratings (F20) and the dynamics class.
                    lengthKm = e.LengthKm, equipment = e.Equipment,
                    rateBMva = e.RateBMva, rateCMva = e.RateCMva,
                    dynamicsClass = e.DynamicsClass,
                }),
                oltcs = model.Oltcs.Select(o => new
                {
                    transformer = o.Transformer, controlsBus = o.ControlsBus, targetVpu = o.TargetVpu,
                    minTap = o.MinTap, maxTap = o.MaxTap, stepPu = o.StepPu, deadbandPu = o.DeadbandPu,
                }),
                boundaries = model.Boundaries.Select(bd => new
                {
                    name = bd.Name, limitMw = bd.LimitMw,
                    branches = bd.Branches.Select(bb => new { branch = bb.Branch, direction = bb.Direction }),
                }),
            },
            solve = estimate is null ? SolveBlock(result, trace, slackQNote, tightenedTolerancePu) : EstimateBlock(result, estimate),
            result = new
            {
                totalLossMw = result.TotalLossMw,
                totalLossMvar = result.TotalLossMvar,
                buses = result.BusIds.Select((id, i) => new
                {
                    id,
                    vm = result.Vm[i],
                    vaDeg = result.VaDeg[i],
                    pInjMw = result.PInjMw[i],
                    qInjMvar = result.QInjMvar[i],
                }),
                lineFlows = result.LineFlows.Select(l => new
                {
                    from = l.From, to = l.To,
                    pFromMw = l.PFromMw, qFromMvar = l.QFromMvar,
                    pToMw = l.PToMw, qToMvar = l.QToMvar,
                    pLossMw = l.PLossMw, qLossMvar = l.QLossMvar,
                    name = l.Name, ratingMva = l.RatingMva, loadingPercent = l.LoadingPercent,
                }),
            },
            // Additive physics-law audit (schema stays /1: consumers ignore unknown fields). An
            // independent instrument (tools/crossval/physics_audit.py) recomputes each law from the
            // model + result and checks these values, so the audit itself is cross-validated.
            physics = physics is null ? PhysicsBlock(model, result) : PhysicsBlock(physics),
        };
        return JsonSerializer.Serialize(doc, Opts);
    }

    // The power-flow solve block: the engine, the outcome and the full trace.
    private static object SolveBlock(PowerFlowResult result, SolveTrace trace, string? slackQNote, double? tightenedTolerancePu) => new
    {
        solver = trace.SolverName,
        n = trace.N,
        converged = result.Converged,
        // D-DOC-180 (October 2026, docs-session finding): the solve's own status (PowerFlowStatus: Converged,
        // MaxIterations or NonFinite) beside the converged flag, so a reader can tell an iteration cap from a NaN
        // blow-up without re-deriving it from the trace. Additive; the schema stays /1.
        status = result.Status.ToString(),
        iterations = result.Iterations,
        maxMismatch = result.MaxMismatch,
        jacobianOmittedForSize = trace.JacobianOmittedForSize,
        slackQNote,
        // The admittance matrix as non-zero (row, col, G, B) triplets. Empty for BFS, which
        // forms no Y-bus (the branch impedances are in model.branches instead).
        ybus = trace.YBus,
        // D-DOC-068: per unit for Pspec / Qspec, MVAr for Qd / Qmin / Qmax, on every engine (see the class remarks).
        busSpec = trace.Spec,
        qLimitEvents = trace.QLimitEvents,
        // `pass` counts the reactive-limit outer passes from 0 (the first solve is pass 0) and `iteration` the Newton
        // iterations within the pass from 0 (D-DOC-199: the same 0-based count as the
        // estimate export's trace and removals, the console's "pass N" and the acopf loss passes; technical ch. 10).
        passes = trace.Passes.Select(p => new
        {
            pass = p.Pass,
            effectiveTypes = p.EffectiveTypes,
            pvpqIndices = p.PvPqIndices,   // bus indices carrying a P equation, in row order
            pqIndices = p.PqIndices,        // bus indices carrying a Q equation, in row order
            iterations = p.Iterations.Select(it => new
            {
                iteration = it.Iteration,
                maxMismatch = it.MaxMismatch,
                mismatch = it.Mismatch,
                correction = it.Correction,
                jacobian = it.Jacobian,
            }),
        }),
        // N251: last, and omitted when null (WhenWritingNull), so an export without --export-tight is byte-identical.
        tightenedTolerancePu,
    };

    // D-DOC-071: the solve block of an estimate projected into the solve shape. There was no power-flow solve, so there
    // is no power mismatch, no Y-bus trace and no Newton passes: the block says what produced the state (the estimator
    // and its status) and carries the estimator's convergence measure under its own name. `maxMismatch` used to hold
    // that state update (EstimationResult.ToPowerFlowResult puts it in the mismatch slot), which read as a power
    // mismatch in pu of the base; it is no longer written here. `converged` and `iterations` are the estimator's.
    private static object EstimateBlock(PowerFlowResult result, SolveExportEstimate estimate) => new
    {
        solver = $"state-estimate ({estimate.Method})",
        converged = result.Converged,
        status = estimate.Status,
        iterations = result.Iterations,
        maxStateUpdate = estimate.MaxStateUpdate,
    };

    // A bus ZIP load as its six coefficients, or null (omitted from the JSON) for constant power.
    private static object? ZipBlock(ZipLoad? z) =>
        z is null ? null : new { ap = z.Ap, bp = z.Bp, cp = z.Cp, aq = z.Aq, bq = z.Bq, cq = z.Cq };

    // D-DOC-067: a generator cost curve (MATPOWER gencost row) as written in the model, or null (omitted) when absent.
    private static object? CostBlock(GeneratorCost? c) =>
        c is null ? null : new
        {
            model = c.Model.ToString(), startupCost = c.StartupCost, shutdownCost = c.ShutdownCost,
            coefficients = c.Coefficients,
        };

    /// <summary>The physics-law audit of a solved state, shaped for the export. Scored by the plain
    /// <see cref="Physics.PhysicsLaws.Audit"/> against the case-file dispatch at the one documented default
    /// tolerance, <see cref="Physics.PhysicsLaws.DefaultTolMva"/> (BaseMva * 1e-8; D-DOC-040 / D-DOC-125:
    /// it was an absolute 1e-6 MVA, which failed a converged solve on the 10,000 MVA uk base). Each law carries
    /// its residual, unit, tolerance and status; the gate flag summarises the hard-law verdict.</summary>
    internal static object PhysicsBlock(GridModel model, PowerFlowResult result)
        => PhysicsBlock(new ExportPhysics(Physics.PhysicsLaws.Audit(model, result), ExportPhysics.CaseDispatch));

    /// <summary>A physics-law report already computed by the caller, shaped for the export (D-DOC-040: the
    /// estimate exports pass the audit of the estimate against its own state, see
    /// <see cref="EstimateExport.PhysicsReport"/>).</summary>
    internal static object PhysicsBlock(ExportPhysics physics)
    {
        ArgumentNullException.ThrowIfNull(physics);
        Physics.PhysicsLawReport report = physics.Report;
        return new
        {
            gatePass = report.Gate(Physics.PhysicsCheckRegistry.HardGateLaws),
            hardGateLaws = Physics.PhysicsCheckRegistry.HardGateLaws,
            // D-DOC-040 (additive field, schema stays /1): what laws 6 and 8 were scored against, so a reader (and
            // physics_audit.py) knows which injections the KCL residual compares: "case-dispatch" (a solve: the
            // case file's Pg - load), "zero-injection-constraints" (an estimate the Feasible gate audited: its hard
            // zero-injection rows) or "estimate-state" (any other estimate: nothing outside the state fixes an
            // injection, so laws 6 and 8 read NotApplicable).
            basis = physics.Basis,
            laws = report.Laws.Select(l => new
            {
                number = l.Number,
                name = l.Name,
                statement = l.Statement,
                status = l.Status.ToString(),
                residual = l.Residual,
                unit = l.Unit,
                tolerance = l.Tolerance,
                detail = l.Detail,
            }),
        };
    }
}
