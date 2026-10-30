using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.IO;

using Physics = GridSim.Core.Physics;

namespace GridSim.Tests.JsonSourceGen.Legacy;

// Phase 0.1 equivalence oracle: a verbatim, test-only copy of GridSim.Core.IO.EstimateExport as it stood before source-generated JSON
// (GridSim 2.0.48), still serialising its anonymous objects through runtime reflection. Only the class name (LegacyEstimateExport)
// and the namespace changed (a using alias stands in for the names Core resolves through its own namespace), and its call to SolveExport.PhysicsBlock now goes to the legacy copy.
// JsonSourceGenEquivalenceTests requires the production exporter to write exactly the bytes this copy writes. Never
// edit it to follow the production code: it is the old behaviour, frozen.

internal static class LegacyEstimateExport
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Normalized residuals are NaN for critical measurements and the chi-square threshold is
        // NaN without redundancy - the export must carry them, same doctrine as SolveExport.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static void WriteJson(
        string path, GridModel model, MeasurementSet measurements, EstimationResult result, SolveExportMeta meta,
        EstimationTrace? trace = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson(model, measurements, result, meta, trace));
    }

    public static string ToJson(
        GridModel model, MeasurementSet measurements, EstimationResult result, SolveExportMeta meta,
        EstimationTrace? trace = null)
    {
        var doc = new
        {
            schema = "gridsim-estimate-export/1",
            meta,
            model = new
            {
                name = model.Name,
                baseMva = model.BaseMva,
                busCount = model.BusCount,
                buses = model.Buses.Select(b => new
                {
                    id = b.Id, type = b.Type.ToString(), baseKv = b.BaseKv,
                    pd = b.Pd, qd = b.Qd, gs = b.Gs, bs = b.Bs, vm = b.Vm, name = b.Name,
                }),
                // N256 (October 2026): the WHOLE fleet in the order given, each unit with its service state, as the solve
                // export carries it since D-DOC-067. Model.Generators is the in-service list only (F16), so an
                // out-of-service unit vanished from this document. Additive for a model with every unit in service: the
                // same units in the same order, each with "inService": true and its "pmin" (the solve export's field).
                generators = model.AllGenerators.Select(g => new
                {
                    bus = g.Bus, pg = g.Pg, qg = g.Qg, vg = g.Vg,
                    qmin = g.Qmin, qmax = g.Qmax, pmin = g.Pmin, pmax = g.Pmax, mBase = g.MBase, h = g.H, type = g.Type, name = g.Name,
                    inService = g.InService,
                }),
                branches = model.Branches.Select(e => new
                {
                    from = e.From, to = e.To, r = e.R, x = e.X, b = e.B,
                    tap = e.TapRatio, phaseShiftDeg = e.PhaseShiftDeg,
                    rateAMva = e.RateAMva, name = e.Name, isTransformer = e.IsTransformer,
                    // R031 (same defect class as the solve export): an opened branch must not re-enter service
                    // when a sidecar rebuilds the network from this document.
                    inService = e.InService,
                }),
            },
            measurements = new
            {
                measurementSchema = MeasurementSet.SchemaId,
                @case = measurements.Case,
                takenUtc = measurements.TakenUtc,
                note = measurements.Note,
                count = measurements.Measurements.Count,
                items = measurements.Measurements.Select(MeasurementDoc),
            },
            // CF6 (main's ruling on CF6-F1, Ruling R1): a LAV estimate (ChiSquareIsVerdict false) is written with the LAV
            // shape below: no chi-square verdict anywhere (chiSquarePassed and chiSquareTest null), chiSquareVerdict false
            // and the LAV objective. Every other estimate is written with exactly the shape it always had (the first
            // branch), so its document is byte-for-byte unchanged.
            estimate = result.ChiSquareIsVerdict ? (object)new
            {
                status = result.Status.ToString(),
                converged = result.Converged,
                iterations = result.Iterations,
                maxStateUpdate = result.MaxStateUpdate,
                objectiveJ = result.ObjectiveJ,
                degreesOfFreedom = result.DegreesOfFreedom,
                chiSquareThreshold = result.ChiSquareThreshold,
                // N208 (Ruling R1 follow-up, 2026-10-04; a documented wire change): true / false only when the
                // chi-square test RAN, null when it did not (no redundancy, detection off, unobservable, non-converged,
                // non-finite). Before, a test that never ran was written as true (EstimationResult.ChiSquarePassed is
                // vacuously true then), and the GDA builder stored it as a pass. Written as an explicit JSON null, not
                // left out, so the field keeps its place and a reader sees "did not run" rather than a missing field.
                chiSquarePassed = ChiSquarePassedJson(result.ChiSquareTest),
                chiSquareTest = result.ChiSquareTest,   // NotRun / Passed / Failed (R261)
                // N064 (October 2026): R175's flag, until now only printed by the CLI. True when a robust method
                // (Huber, LAV, Schweppe GM) stopped re-weighting on its cap before the weights settled, so the state
                // is not a fixed point of the robust estimator; always false for the other methods. Additive field:
                // the schema stays /1 (readers ignore unknown fields) and no existing value in the document changes.
                irlsCapReached = result.IrlsCapReached,
                buses = result.BusIds.Select((id, i) => new
                {
                    id,
                    vm = result.Vm[i],
                    vaDeg = result.VaDeg[i],
                }),
            }
            : new
            {
                status = result.Status.ToString(),
                converged = result.Converged,
                iterations = result.Iterations,
                maxStateUpdate = result.MaxStateUpdate,
                objectiveJ = result.ObjectiveJ,   // information only for LAV (the LAV state does not minimise J)
                degreesOfFreedom = result.DegreesOfFreedom,
                chiSquareThreshold = result.ChiSquareThreshold,   // kept for comparability; not a test of a LAV fit
                chiSquarePassed = JsonNull,                       // CF6-F1: no chi-square verdict for LAV
                chiSquareTest = JsonNull,                         // CF6-F1: no chi-square verdict for LAV
                irlsCapReached = result.IrlsCapReached,
                // CF6-F1: false for every LAV estimate: its objective is not chi-square distributed.
                chiSquareVerdict = false,
                // CF6-F1: sum |r_i| / sigma_i, the objective LAV minimises (left out when the estimate did not converge).
                lavObjective = result.LavObjective,
                buses = result.BusIds.Select((id, i) => new
                {
                    id,
                    vm = result.Vm[i],
                    vaDeg = result.VaDeg[i],
                }),
            },
            residuals = result.Residuals.Select(r => new
            {
                measurement = MeasurementDoc(r.Measurement),
                predicted = r.Predicted,
                residual = r.Residual,
                normalizedResidual = r.NormalizedResidual,
                critical = r.IsCritical,
            }),
            observability = new
            {
                observable = result.Observability.Observable,
                rank = result.Observability.Rank,
                stateSize = result.Observability.StateSize,
                unobservableBuses = result.Observability.UnobservableBuses,
                unobservableStates = result.Observability.UnobservableStates,
                unmeasuredIslandBuses = result.Observability.UnmeasuredIslandBuses,
            },
            removed = result.Removed.Select(r => new
            {
                measurement = MeasurementDoc(r.Measurement),
                normalizedResidual = r.NormalizedResidual,
                pass = r.Pass,
            }),
            // Additive optional block (readers of /1 ignore unknown fields): the per-iteration
            // convergence path when the run captured an EstimationTrace.
            // D-DOC-199 (October 2026, docs-session finding; decision: document, do not renumber): `pass` is the bad-data
            // pass counted from 0 (the first estimate is pass 0; each removal starts the next pass), the same 0-based
            // count as `removed[].pass` above, the console's "removed bad data (pass N)" and the solve export's
            // `passes[].pass`. `iteration` is the Gauss-Newton iteration within the pass, also from 0 (for a robust method
            // it runs on across the IRLS outer loops of the pass). Renumbering only this block to start at 1 would have
            // made it disagree with the removals it is read beside. (The --hti loop is a different loop and numbers its
            // own passes from 1 in hti-removals.csv / hti-corrections.csv.) Technical ch. 10 says so.
            trace = trace is null ? null : trace.Iterations.Select(it => new
            {
                pass = it.Pass,
                iteration = it.Iteration,
                maxStateUpdate = it.MaxStateUpdate,
                stepScale = it.StepScale,
            }),
            // Additive physics-law audit of the ESTIMATED state (schema stays /1). The estimate is
            // evaluated as a power-flow result at x^ (pure evaluation, no forward inference) and audited
            // against its own state (D-DOC-040, see PhysicsReport); physics_audit.py cross-checks these values.
            physics = LegacySolveExport.PhysicsBlock(PhysicsReport(model, result)),
        };
        return JsonSerializer.Serialize(doc, Opts);
    }

    /// <summary>
    /// The physics-law audit an estimate's exports carry (D-DOC-040, October 2026), used by
    /// <c>estimate-export.json</c> and by the <c>--export-solve</c> <c>estimated-solve-export.json</c>.
    /// <para>Before, both exports scored the estimate with the plain <see cref="Physics.PhysicsLaws.Audit"/>: KCL
    /// (laws 6 and 8) against the CASE FILE's Pg - Pd at an absolute 1e-6 MVA. An estimate is not a solve of that
    /// dispatch: its injections differ from the case file by the telemetry (megawatts), so every noisy estimate
    /// showed laws 6 and 8 Violated and gatePass false, even one the Feasible gate had just accepted (case14 with
    /// synthetic noise: 1.90 MW).</para>
    /// <para>Now the audit is the estimate's own: (1) when the Feasible acceptance gate ran
    /// (<see cref="EstimationResult.Physics"/> is set), that very report is written, so the export says exactly what
    /// the estimator decided (KCL against its hard zero-injection rows, at <c>FeasibilityConstraints.AuditTolMva</c>);
    /// (2) otherwise the state is audited with <see cref="Physics.PhysicsLaws.AuditAgainst"/> and NO specified
    /// injection, at the one default tolerance <see cref="Physics.PhysicsLaws.DefaultTolMva"/>: laws 1, 2 and 3 are
    /// identities of the projected state and are scored; laws 6 and 8 read NotApplicable, because nothing outside the
    /// state fixes an injection for a method that does not enforce one (the zero-injection rows of the plain WLS and
    /// robust methods are soft, weighted observations, so a residual on them is statistics, not a breach of
    /// Kirchhoff's law). The projection is pure evaluation, so no estimated number changes.</para>
    /// </summary>
    public static ExportPhysics PhysicsReport(GridModel model, EstimationResult result)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Physics is { } gate)
            return new ExportPhysics(gate, ExportPhysics.ZeroInjectionConstraints);
        var none = new Physics.SpecifiedInjections(
            new Dictionary<int, double>(), new Dictionary<int, double>(),
            "the estimate's own state (no injection is fixed outside it)");
        Physics.PhysicsLawReport own = Physics.PhysicsLaws.AuditAgainst(
            model, result.ToPowerFlowResult(model), none, Physics.PhysicsLaws.DefaultTolMva(model));
        return new ExportPhysics(own, ExportPhysics.EstimateState);
    }

    // N208: the three possible values of estimate.chiSquarePassed as pre-parsed JSON. A JsonElement is a struct, so the
    // serializer's WhenWritingNull rule never drops it: the null one is written as a literal null in the field's own
    // place (a C# null would have removed the field). The true / false ones write exactly the bytes the bool did.
    private static readonly JsonElement JsonTrue = JsonSerializer.SerializeToElement(true);
    private static readonly JsonElement JsonFalse = JsonSerializer.SerializeToElement(false);
    private static readonly JsonElement JsonNull = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>
    /// N208: the export's <c>chiSquarePassed</c> for a chi-square outcome: true (the test ran and passed), false (it ran
    /// and failed) or null (it did not run). Never true for a test that did not run.
    /// </summary>
    internal static JsonElement ChiSquarePassedJson(ChiSquareOutcome outcome) => outcome switch
    {
        ChiSquareOutcome.Passed => JsonTrue,
        ChiSquareOutcome.Failed => JsonFalse,
        _ => JsonNull,
    };

    private static object MeasurementDoc(Measurement m) => new
    {
        kind = m.Kind.ToString(),
        value = m.Value,
        sigma = m.Sigma,
        bus = m.IsBranch ? (int?)null : m.Bus,
        from = m.IsBranch ? m.From : (int?)null,
        to = m.IsBranch ? m.To : (int?)null,
        end = m.IsBranch ? m.End.ToString() : null,
        circuit = m.Circuit,
        source = m.Source,
    };
}
