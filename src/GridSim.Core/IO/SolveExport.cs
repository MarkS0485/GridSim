using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.IO;

/// <summary>Provenance for a solve export (filled by the caller - commit, timing, host).</summary>
/// <param name="ElapsedMs">Wall-clock milliseconds the computation that produced the document took (D-DOC-140: every
/// CLI export now measures it; five used to write a hard-coded 0). What is timed is stated per export in technical
/// ch. 10; it is a measurement of this run on this machine, never a computed result.</param>
public sealed record SolveExportMeta(
    string Case, string Backend, string GitCommit, string TimestampUtc, double ElapsedMs, string Machine);

/// <summary>
/// D-DOC-071 (October 2026, docs-session finding): what a solve-shaped document holds when it is an ESTIMATE projected
/// into the solve-export shape (<c>estimate --export-solve</c>, <c>estimated-solve-export.json</c>), not a power-flow
/// solve. The projection (<see cref="Estimation.EstimationResult.ToPowerFlowResult"/>) has no power-flow mismatch: it
/// carries the estimator's last state update in the mismatch slot. With this passed, <see cref="SolveExport"/> writes that
/// number under its own name, <c>maxStateUpdate</c>, writes no <c>maxMismatch</c>, and names the estimator's status.
/// </summary>
/// <param name="Status">The estimator's status (<see cref="Estimation.EstimationStatus"/> name, e.g. "Converged").</param>
/// <param name="MaxStateUpdate">The largest state update of the last Gauss-Newton iteration (pu / rad), the estimator's
/// convergence measure.</param>
/// <param name="Method">The estimation method (e.g. "Wls"), for the <c>solver</c> field.</param>
public sealed record SolveExportEstimate(string Status, double MaxStateUpdate, string Method);

/// <summary>
/// Serialises <b>everything a solve computed</b> to a single JSON document: the run metadata, the
/// full network inputs, the solver's internal trace (admittance matrix, per-unit bus spec,
/// per-iteration mismatch/correction/Jacobian, Q-limit switching) and the converged result with all
/// line flows. Driven by <c>--enableExport</c>. Deterministic given a deterministic solve.
///
/// <para>"The full network inputs" means every field of the model's buses, generators and branches, plus the OLTCs,
/// boundaries, base and nominal frequency (D-DOC-067, October 2026: the export used to write only the in-service
/// generators and to drop the bus angles, layout coordinates and voltage limits, the generators' fault, converter, cost
/// and dynamics-class fields, and the branches' length, equipment, short-term and emergency ratings and dynamics class,
/// against this comment's claim). Buses carry their ZIP load model and their own voltage limits; generators the WHOLE
/// fleet (<see cref="GridModel.AllGenerators"/>) with <c>inService</c> on each, so an out-of-service unit re-imports
/// out of service; branches their service state, zero-sequence and winding data. Every one of these is read back by
/// <see cref="SolveImport"/> (through <see cref="BridgeProtocol.BuildModel"/>), so an exported state re-imports as
/// the same network: the same buses, generators and branches, field for field (R031; pinned by
/// Doc8SolveExportRoundTripTests). What is NOT carried: the per-asset dynamics model and the protection bundle (the
/// asset-classes.json / protections.json content; a generator or branch keeps the NAME of its dynamics class). The
/// schema stays <c>gridsim-solve-export/1</c>: the fields are additive and optional, and a reader of an older document
/// gets the model defaults (in service, constant power, no limits, no OLTCs).</para>
///
/// <para>Units of <c>solve.busSpec</c> (D-DOC-068): <c>Pspec</c> and <c>Qspec</c> are per unit on the system base,
/// <c>Qd</c>, <c>Qmin</c> and <c>Qmax</c> are MVAr, <c>Vg</c> and <c>VmInit</c> per unit, <c>VaInitDeg</c> degrees, on
/// every engine (the backward/forward sweep used to write <c>Qd</c> in per unit while the Newton engines wrote MVAr).</para>
/// </summary>
public static class SolveExport
{
    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated SolveExportJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly SolveExportJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Real solves emit non-finite values: PEGASE cases carry Inf gen limits, and a non-converged BFS
        // slice returns MaxMismatch = +inf. Without this the export throws mid-run and kills a transient/
        // gda-replay run; SolveImport already reads these back with the same setting.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });

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
        // Phase 0.1: named types (below) in place of the anonymous objects: same members, same order, same types, so
        // the bytes are unchanged; the source-generated context can describe a named type (NativeAOT).
        var doc = new SolveDoc
        {
            schema = "gridsim-solve-export/1",
            meta = meta,
            model = new ModelRow
            {
                name = model.Name,
                baseMva = model.BaseMva,
                // R177 / Q13 (October 2026): carried so a re-import (SolveImport -> BridgeProtocol.BuildModel) keeps
                // the model's own nominal frequency instead of assuming 50 Hz.
                frequencyHz = model.FrequencyHz,
                busCount = model.BusCount,
                buses = model.Buses.Select(b => new BusRow
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
                generators = model.AllGenerators.Select(g => new GeneratorRow
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
                branches = model.Branches.Select(e => new BranchRow
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
                oltcs = model.Oltcs.Select(o => new OltcRow
                {
                    transformer = o.Transformer, controlsBus = o.ControlsBus, targetVpu = o.TargetVpu,
                    minTap = o.MinTap, maxTap = o.MaxTap, stepPu = o.StepPu, deadbandPu = o.DeadbandPu,
                }),
                boundaries = model.Boundaries.Select(bd => new BoundaryRow
                {
                    name = bd.Name, limitMw = bd.LimitMw,
                    branches = bd.Branches.Select(bb => new BoundaryBranchRow { branch = bb.Branch, direction = bb.Direction }),
                }),
            },
            solve = estimate is null ? SolveBlock(result, trace, slackQNote, tightenedTolerancePu) : EstimateBlock(result, estimate),
            result = new ResultRow
            {
                totalLossMw = result.TotalLossMw,
                totalLossMvar = result.TotalLossMvar,
                buses = result.BusIds.Select((id, i) => new ResultBusRow
                {
                    id = id,
                    vm = result.Vm[i],
                    vaDeg = result.VaDeg[i],
                    pInjMw = result.PInjMw[i],
                    qInjMvar = result.QInjMvar[i],
                }),
                lineFlows = result.LineFlows.Select(l => new LineFlowRow
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
        return JsonSerializer.Serialize(doc, Json.SolveDoc);
    }

    // The power-flow solve block: the engine, the outcome and the full trace.
    // Phase 0.1: returns the named SolveBlockRow (the "solve" member stays typed object, as it was, because it holds
    // either this or an EstimateBlockRow; both runtime types are registered in SolveExportJsonContext).
    private static SolveBlockRow SolveBlock(PowerFlowResult result, SolveTrace trace, string? slackQNote, double? tightenedTolerancePu) => new()
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
        slackQNote = slackQNote,
        // The admittance matrix as non-zero (row, col, G, B) triplets. Empty for BFS, which
        // forms no Y-bus (the branch impedances are in model.branches instead).
        ybus = trace.YBus,
        // D-DOC-068: per unit for Pspec / Qspec, MVAr for Qd / Qmin / Qmax, on every engine (see the class remarks).
        busSpec = trace.Spec,
        qLimitEvents = trace.QLimitEvents,
        // `pass` counts the reactive-limit outer passes from 0 (the first solve is pass 0) and `iteration` the Newton
        // iterations within the pass from 0 (D-DOC-199: the same 0-based count as the
        // estimate export's trace and removals, the console's "pass N" and the acopf loss passes; technical ch. 10).
        passes = trace.Passes.Select(p => new PassRow
        {
            pass = p.Pass,
            effectiveTypes = p.EffectiveTypes,
            pvpqIndices = p.PvPqIndices,   // bus indices carrying a P equation, in row order
            pqIndices = p.PqIndices,        // bus indices carrying a Q equation, in row order
            iterations = p.Iterations.Select(it => new IterationRow
            {
                iteration = it.Iteration,
                maxMismatch = it.MaxMismatch,
                mismatch = it.Mismatch,
                correction = it.Correction,
                jacobian = it.Jacobian,
            }),
        }),
        // N251: last, and omitted when null (WhenWritingNull), so an export without --export-tight is byte-identical.
        tightenedTolerancePu = tightenedTolerancePu,
    };

    // D-DOC-071: the solve block of an estimate projected into the solve shape. There was no power-flow solve, so there
    // is no power mismatch, no Y-bus trace and no Newton passes: the block says what produced the state (the estimator
    // and its status) and carries the estimator's convergence measure under its own name. `maxMismatch` used to hold
    // that state update (EstimationResult.ToPowerFlowResult puts it in the mismatch slot), which read as a power
    // mismatch in pu of the base; it is no longer written here. `converged` and `iterations` are the estimator's.
    private static EstimateBlockRow EstimateBlock(PowerFlowResult result, SolveExportEstimate estimate) => new()
    {
        solver = $"state-estimate ({estimate.Method})",
        converged = result.Converged,
        status = estimate.Status,
        iterations = result.Iterations,
        maxStateUpdate = estimate.MaxStateUpdate,
    };

    // A bus ZIP load as its six coefficients, or null (omitted from the JSON) for constant power.
    private static ZipRow? ZipBlock(ZipLoad? z) =>
        z is null ? null : new ZipRow { ap = z.Ap, bp = z.Bp, cp = z.Cp, aq = z.Aq, bq = z.Bq, cq = z.Cq };

    // D-DOC-067: a generator cost curve (MATPOWER gencost row) as written in the model, or null (omitted) when absent.
    private static CostRow? CostBlock(GeneratorCost? c) =>
        c is null ? null : new CostRow
        {
            model = c.Model.ToString(), startupCost = c.StartupCost, shutdownCost = c.ShutdownCost,
            coefficients = c.Coefficients,
        };

    /// <summary>The physics-law audit of a solved state, shaped for the export. Scored by the plain
    /// <see cref="Physics.PhysicsLaws.Audit"/> against the case-file dispatch at the one documented default
    /// tolerance, <see cref="Physics.PhysicsLaws.DefaultTolMva"/> (BaseMva * 1e-8; D-DOC-040 / D-DOC-125:
    /// it was an absolute 1e-6 MVA, which failed a converged solve on the 10,000 MVA uk base). Each law carries
    /// its residual, unit, tolerance and status; the gate flag summarises the hard-law verdict.</summary>
    internal static PhysicsRow PhysicsBlock(GridModel model, PowerFlowResult result)
        => PhysicsBlock(new ExportPhysics(Physics.PhysicsLaws.Audit(model, result), ExportPhysics.CaseDispatch));

    /// <summary>A physics-law report already computed by the caller, shaped for the export (D-DOC-040: the
    /// estimate exports pass the audit of the estimate against its own state, see
    /// <see cref="EstimateExport.PhysicsReport"/>).</summary>
    internal static PhysicsRow PhysicsBlock(ExportPhysics physics)
    {
        ArgumentNullException.ThrowIfNull(physics);
        Physics.PhysicsLawReport report = physics.Report;
        return new PhysicsRow
        {
            gatePass = report.Gate(Physics.PhysicsCheckRegistry.HardGateLaws),
            hardGateLaws = Physics.PhysicsCheckRegistry.HardGateLaws,
            // D-DOC-040 (additive field, schema stays /1): what laws 6 and 8 were scored against, so a reader (and
            // physics_audit.py) knows which injections the KCL residual compares: "case-dispatch" (a solve: the
            // case file's Pg - load), "zero-injection-constraints" (an estimate the Feasible gate audited: its hard
            // zero-injection rows) or "estimate-state" (any other estimate: nothing outside the state fixes an
            // injection, so laws 6 and 8 read NotApplicable).
            basis = physics.Basis,
            laws = report.Laws.Select(l => new LawRow
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

    // ---- Phase 0.1: the document as named types ----------------------------------------------------------------------
    // These replace the anonymous objects ToJson used to build, so the source-generated context can describe them
    // (NativeAOT). The property names ARE the JSON member names (these options apply no naming policy), so they are
    // lower camelCase on purpose, spelt exactly as the anonymous members were, declared in the same order and with the
    // same CLR types (null members omitted, WhenWritingNull; named float literals allowed). Where the anonymous member
    // was typed object (solve), it stays object and the runtime types are registered in the context.
    // JsonSourceGenEquivalenceTests holds the bytes equal to the old reflection-built document.

    /// <summary>The whole <c>gridsim-solve-export/1</c> document.</summary>
    internal sealed class SolveDoc
    {
        public required string schema { get; init; }
        public required SolveExportMeta meta { get; init; }
        public required ModelRow model { get; init; }
        public required object solve { get; init; }
        public required ResultRow result { get; init; }
        public required PhysicsRow physics { get; init; }
    }

    /// <summary>The network the solve ran on.</summary>
    internal sealed class ModelRow
    {
        public required string name { get; init; }
        public required double baseMva { get; init; }
        public required double frequencyHz { get; init; }
        public required int busCount { get; init; }
        public required IEnumerable<BusRow> buses { get; init; }
        public required IEnumerable<GeneratorRow> generators { get; init; }
        public required IEnumerable<BranchRow> branches { get; init; }
        public required IEnumerable<OltcRow> oltcs { get; init; }
        public required IEnumerable<BoundaryRow> boundaries { get; init; }
    }

    /// <summary>One bus.</summary>
    internal sealed class BusRow
    {
        public required int id { get; init; }
        public required string type { get; init; }
        public required double baseKv { get; init; }
        public required double pd { get; init; }
        public required double qd { get; init; }
        public required double gs { get; init; }
        public required double bs { get; init; }
        public required double vm { get; init; }
        public required string? name { get; init; }
        public required ZipRow? zip { get; init; }
        public required double vaDeg { get; init; }
        public required double x { get; init; }
        public required double y { get; init; }
        public required double? vminPu { get; init; }
        public required double? vmaxPu { get; init; }
    }

    /// <summary>A ZIP load's six coefficients.</summary>
    internal sealed class ZipRow
    {
        public required double ap { get; init; }
        public required double bp { get; init; }
        public required double cp { get; init; }
        public required double aq { get; init; }
        public required double bq { get; init; }
        public required double cq { get; init; }
    }

    /// <summary>One generator (the whole fleet, in-service or not).</summary>
    internal sealed class GeneratorRow
    {
        public required int bus { get; init; }
        public required double pg { get; init; }
        public required double qg { get; init; }
        public required double vg { get; init; }
        public required double qmin { get; init; }
        public required double qmax { get; init; }
        public required double pmin { get; init; }
        public required double pmax { get; init; }
        public required double mBase { get; init; }
        public required double h { get; init; }
        public required string type { get; init; }
        public required string? name { get; init; }
        public required bool inService { get; init; }
        public required string? dynamicsClass { get; init; }
        public required double subtransientXdPu { get; init; }
        public required double negativeSeqXPu { get; init; }
        public required bool? converterInterfaced { get; init; }
        public required double? converterFaultCurrentPu { get; init; }
        public required bool powerFlowOnly { get; init; }
        public required double unitTransformerXPu { get; init; }
        public required bool gridForming { get; init; }
        public required CostRow? cost { get; init; }
        public required CostRow? reactiveCost { get; init; }
    }

    /// <summary>A generator cost curve.</summary>
    internal sealed class CostRow
    {
        public required string model { get; init; }
        public required double startupCost { get; init; }
        public required double shutdownCost { get; init; }
        public required IReadOnlyList<double> coefficients { get; init; }
    }

    /// <summary>One branch.</summary>
    internal sealed class BranchRow
    {
        public required int from { get; init; }
        public required int to { get; init; }
        public required double r { get; init; }
        public required double x { get; init; }
        public required double b { get; init; }
        public required double tap { get; init; }
        public required double phaseShiftDeg { get; init; }
        public required double rateAMva { get; init; }
        public required string? name { get; init; }
        public required bool isTransformer { get; init; }
        public required bool inService { get; init; }
        public required double r0 { get; init; }
        public required double x0 { get; init; }
        public required string? fromWinding { get; init; }
        public required string? toWinding { get; init; }
        public required double fromNeutralXPu { get; init; }
        public required double toNeutralXPu { get; init; }
        public required double lengthKm { get; init; }
        public required string? equipment { get; init; }
        public required double rateBMva { get; init; }
        public required double rateCMva { get; init; }
        public required string? dynamicsClass { get; init; }
    }

    /// <summary>One on-load tap changer.</summary>
    internal sealed class OltcRow
    {
        public required string transformer { get; init; }
        public required int controlsBus { get; init; }
        public required double targetVpu { get; init; }
        public required double minTap { get; init; }
        public required double maxTap { get; init; }
        public required double stepPu { get; init; }
        public required double deadbandPu { get; init; }
    }

    /// <summary>One boundary.</summary>
    internal sealed class BoundaryRow
    {
        public required string name { get; init; }
        public required double limitMw { get; init; }
        public required IEnumerable<BoundaryBranchRow> branches { get; init; }
    }

    /// <summary>{ branch, direction }.</summary>
    internal sealed class BoundaryBranchRow
    {
        public required string branch { get; init; }
        public required int direction { get; init; }
    }

    /// <summary>The power-flow solve block.</summary>
    internal sealed class SolveBlockRow
    {
        public required string solver { get; init; }
        public required int n { get; init; }
        public required bool converged { get; init; }
        public required string status { get; init; }
        public required int iterations { get; init; }
        public required double maxMismatch { get; init; }
        public required bool jacobianOmittedForSize { get; init; }
        public required string? slackQNote { get; init; }
        public required List<YBusEntry> ybus { get; init; }
        public required BusSpecRow[] busSpec { get; init; }
        public required List<QLimitEvent> qLimitEvents { get; init; }
        public required IEnumerable<PassRow> passes { get; init; }
        public required double? tightenedTolerancePu { get; init; }
    }

    /// <summary>One reactive-limit outer pass.</summary>
    internal sealed class PassRow
    {
        public required int pass { get; init; }
        public required string[] effectiveTypes { get; init; }
        public required int[] pvpqIndices { get; init; }
        public required int[] pqIndices { get; init; }
        public required IEnumerable<IterationRow> iterations { get; init; }
    }

    /// <summary>One Newton iteration.</summary>
    internal sealed class IterationRow
    {
        public required int iteration { get; init; }
        public required double maxMismatch { get; init; }
        public required double[] mismatch { get; init; }
        public required double[] correction { get; init; }
        public required double[][]? jacobian { get; init; }
    }

    /// <summary>The solve block of an estimate projected into the solve shape.</summary>
    internal sealed class EstimateBlockRow
    {
        public required string solver { get; init; }
        public required bool converged { get; init; }
        public required string status { get; init; }
        public required int iterations { get; init; }
        public required double maxStateUpdate { get; init; }
    }

    /// <summary>The solved state.</summary>
    internal sealed class ResultRow
    {
        public required double totalLossMw { get; init; }
        public required double totalLossMvar { get; init; }
        public required IEnumerable<ResultBusRow> buses { get; init; }
        public required IEnumerable<LineFlowRow> lineFlows { get; init; }
    }

    /// <summary>One solved bus.</summary>
    internal sealed class ResultBusRow
    {
        public required int id { get; init; }
        public required double vm { get; init; }
        public required double vaDeg { get; init; }
        public required double pInjMw { get; init; }
        public required double qInjMvar { get; init; }
    }

    /// <summary>One branch flow.</summary>
    internal sealed class LineFlowRow
    {
        public required int from { get; init; }
        public required int to { get; init; }
        public required double pFromMw { get; init; }
        public required double qFromMvar { get; init; }
        public required double pToMw { get; init; }
        public required double qToMvar { get; init; }
        public required double pLossMw { get; init; }
        public required double qLossMvar { get; init; }
        public required string name { get; init; }
        public required double ratingMva { get; init; }
        public required double loadingPercent { get; init; }
    }

    /// <summary>The physics-law audit block (also written by the estimate export).</summary>
    internal sealed class PhysicsRow
    {
        public required bool gatePass { get; init; }
        public required IReadOnlyCollection<int> hardGateLaws { get; init; }
        public required string basis { get; init; }
        public required IEnumerable<LawRow> laws { get; init; }
    }

    /// <summary>One audited law.</summary>
    internal sealed class LawRow
    {
        public required int number { get; init; }
        public required string name { get; init; }
        public required string statement { get; init; }
        public required string status { get; init; }
        public required double residual { get; init; }
        public required string unit { get; init; }
        public required double tolerance { get; init; }
        public required string detail { get; init; }
    }
}

/// <summary>
/// The physics-law audit written into an export's <c>physics</c> block, with what laws 6 and 8 were scored against
/// (D-DOC-040, October 2026). <see cref="Basis"/> is one of the three constants below.
/// </summary>
public sealed record ExportPhysics(Physics.PhysicsLawReport Report, string Basis)
{
    /// <summary>A power-flow solve: KCL against the case file's dispatch (Pg minus the load drawn).</summary>
    public const string CaseDispatch = "case-dispatch";

    /// <summary>An estimate the Feasible gate audited: KCL against its hard zero-injection constraint rows.</summary>
    public const string ZeroInjectionConstraints = "zero-injection-constraints";

    /// <summary>Any other estimate: no injection is fixed outside the state, so laws 6 and 8 read NotApplicable.</summary>
    public const string EstimateState = "estimate-state";
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the solve export document (and the two runtime types of its solve member) and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SolveExport.SolveDoc))]
[JsonSerializable(typeof(SolveExport.SolveBlockRow))]
[JsonSerializable(typeof(SolveExport.EstimateBlockRow))]
internal sealed partial class SolveExportJsonContext : JsonSerializerContext;
