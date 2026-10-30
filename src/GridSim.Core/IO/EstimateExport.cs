using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Estimation;
using GridSim.Core.Model;

namespace GridSim.Core.IO;

/// <summary>
/// Serialises <b>everything a state estimation computed</b> to a single JSON document: run
/// metadata, the full network inputs, the measurement set the estimate was asked to fit, the
/// estimated state, the per-measurement residual table, the observability report and any bad-data
/// removals. Schema <c>gridsim-estimate-export/1</c> - the entire input contract of the
/// <c>crossval_estimation.py</c> / <c>se_residual_check.py</c> sidecars, mirrored from
/// <see cref="SolveExport"/>'s <c>gridsim-solve-export/1</c>.
/// </summary>
public static class EstimateExport
{
    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated EstimateExportJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly EstimateExportJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Normalized residuals are NaN for critical measurements and the chi-square threshold is
        // NaN without redundancy - the export must carry them, same doctrine as SolveExport.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });

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
        // Phase 0.1: named types (below) in place of the anonymous objects: same members, same order, same types, so
        // the bytes are unchanged; the source-generated context can describe a named type (NativeAOT).
        var doc = new EstimateDoc
        {
            schema = "gridsim-estimate-export/1",
            meta = meta,
            model = new ModelRow
            {
                name = model.Name,
                baseMva = model.BaseMva,
                busCount = model.BusCount,
                buses = model.Buses.Select(b => new BusRow
                {
                    id = b.Id, type = b.Type.ToString(), baseKv = b.BaseKv,
                    pd = b.Pd, qd = b.Qd, gs = b.Gs, bs = b.Bs, vm = b.Vm, name = b.Name,
                }),
                // N256 (October 2026): the WHOLE fleet in the order given, each unit with its service state, as the solve
                // export carries it since D-DOC-067. Model.Generators is the in-service list only (F16), so an
                // out-of-service unit vanished from this document. Additive for a model with every unit in service: the
                // same units in the same order, each with "inService": true and its "pmin" (the solve export's field).
                generators = model.AllGenerators.Select(g => new GeneratorRow
                {
                    bus = g.Bus, pg = g.Pg, qg = g.Qg, vg = g.Vg,
                    qmin = g.Qmin, qmax = g.Qmax, pmin = g.Pmin, pmax = g.Pmax, mBase = g.MBase, h = g.H, type = g.Type, name = g.Name,
                    inService = g.InService,
                }),
                branches = model.Branches.Select(e => new BranchRow
                {
                    from = e.From, to = e.To, r = e.R, x = e.X, b = e.B,
                    tap = e.TapRatio, phaseShiftDeg = e.PhaseShiftDeg,
                    rateAMva = e.RateAMva, name = e.Name, isTransformer = e.IsTransformer,
                    // R031 (same defect class as the solve export): an opened branch must not re-enter service
                    // when a sidecar rebuilds the network from this document.
                    inService = e.InService,
                }),
            },
            measurements = new MeasurementsRow
            {
                measurementSchema = MeasurementSet.SchemaId,
                caseName = measurements.Case,
                takenUtc = measurements.TakenUtc,
                note = measurements.Note,
                count = measurements.Measurements.Count,
                items = measurements.Measurements.Select(MeasurementDoc),
            },
            // CF6 (main's ruling on CF6-F1, Ruling R1): a LAV estimate (ChiSquareIsVerdict false) is written with the LAV
            // shape below: no chi-square verdict anywhere (chiSquarePassed and chiSquareTest null), chiSquareVerdict false
            // and the LAV objective. Every other estimate is written with exactly the shape it always had (the first
            // branch), so its document is byte-for-byte unchanged.
            estimate = result.ChiSquareIsVerdict ? (object)new WlsEstimateRow
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
                buses = result.BusIds.Select((id, i) => new EstimateBusRow
                {
                    id = id,
                    vm = result.Vm[i],
                    vaDeg = result.VaDeg[i],
                }),
            }
            : new LavEstimateRow
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
                buses = result.BusIds.Select((id, i) => new EstimateBusRow
                {
                    id = id,
                    vm = result.Vm[i],
                    vaDeg = result.VaDeg[i],
                }),
            },
            residuals = result.Residuals.Select(r => new ResidualRow
            {
                measurement = MeasurementDoc(r.Measurement),
                predicted = r.Predicted,
                residual = r.Residual,
                normalizedResidual = r.NormalizedResidual,
                critical = r.IsCritical,
            }),
            observability = new ObservabilityRow
            {
                observable = result.Observability.Observable,
                rank = result.Observability.Rank,
                stateSize = result.Observability.StateSize,
                unobservableBuses = result.Observability.UnobservableBuses,
                unobservableStates = result.Observability.UnobservableStates,
                unmeasuredIslandBuses = result.Observability.UnmeasuredIslandBuses,
            },
            removed = result.Removed.Select(r => new RemovedRow
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
            trace = trace is null ? null : trace.Iterations.Select(it => new TraceRow
            {
                pass = it.Pass,
                iteration = it.Iteration,
                maxStateUpdate = it.MaxStateUpdate,
                stepScale = it.StepScale,
            }),
            // Additive physics-law audit of the ESTIMATED state (schema stays /1). The estimate is
            // evaluated as a power-flow result at x^ (pure evaluation, no forward inference) and audited
            // against its own state (D-DOC-040, see PhysicsReport); physics_audit.py cross-checks these values.
            physics = SolveExport.PhysicsBlock(PhysicsReport(model, result)),
        };
        return JsonSerializer.Serialize(doc, Json.EstimateDoc);
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
    // Phase 0.1: parsed from the literal JSON tokens instead of serialised by reflection; each is the same single
    // token (true, false, null), so the element written back is the same.
    private static readonly JsonElement JsonTrue = ParseToken("true");
    private static readonly JsonElement JsonFalse = ParseToken("false");
    private static readonly JsonElement JsonNull = ParseToken("null");

    // One literal JSON token as a free-standing element (Clone detaches it from the disposed document).
    private static JsonElement ParseToken(string token)
    {
        using JsonDocument d = JsonDocument.Parse(token);
        return d.RootElement.Clone();
    }

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

    // Phase 0.1: typed (it returned object); the same members in the same order, so the same bytes.
    private static MeasurementRow MeasurementDoc(Measurement m) => new()
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

    // ---- Phase 0.1: the document as named types ----------------------------------------------------------------------
    // These replace the anonymous objects ToJson used to build, so the source-generated context can describe them
    // (NativeAOT). The property names ARE the JSON member names (these options apply no naming policy), so they are
    // lower camelCase on purpose, spelt exactly as the anonymous members were, declared in the same order and with the
    // same CLR types. These options have no enum converter, so chiSquareTest is still written as its number. The
    // estimate member stays typed object, as it was (a WLS-shaped or a LAV-shaped block); both runtime types are
    // registered in EstimateExportJsonContext. JsonSourceGenEquivalenceTests holds the bytes equal to the old
    // reflection-built document.

    /// <summary>The whole <c>gridsim-estimate-export/1</c> document.</summary>
    internal sealed class EstimateDoc
    {
        public required string schema { get; init; }
        public required SolveExportMeta meta { get; init; }
        public required ModelRow model { get; init; }
        public required MeasurementsRow measurements { get; init; }
        public required object estimate { get; init; }
        public required IEnumerable<ResidualRow> residuals { get; init; }
        public required ObservabilityRow observability { get; init; }
        public required IEnumerable<RemovedRow> removed { get; init; }
        public required IEnumerable<TraceRow>? trace { get; init; }
        public required SolveExport.PhysicsRow physics { get; init; }
    }

    /// <summary>The network the estimate ran on.</summary>
    internal sealed class ModelRow
    {
        public required string name { get; init; }
        public required double baseMva { get; init; }
        public required int busCount { get; init; }
        public required IEnumerable<BusRow> buses { get; init; }
        public required IEnumerable<GeneratorRow> generators { get; init; }
        public required IEnumerable<BranchRow> branches { get; init; }
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
    }

    /// <summary>One generator (the whole fleet).</summary>
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
    }

    /// <summary>The measurement set the estimate used.</summary>
    internal sealed class MeasurementsRow
    {
        public required string measurementSchema { get; init; }
        // Named caseName in C# because the source generator does not escape the keyword in an @case property; the
        // attribute keeps the JSON name "case" exactly as before.
        [JsonPropertyName("case")]
        public required string caseName { get; init; }
        public required DateTime? takenUtc { get; init; }
        public required string? note { get; init; }
        public required int count { get; init; }
        public required IEnumerable<MeasurementRow> items { get; init; }
    }

    /// <summary>One measurement.</summary>
    internal sealed class MeasurementRow
    {
        public required string kind { get; init; }
        public required double value { get; init; }
        public required double sigma { get; init; }
        public required int? bus { get; init; }
        public required int? from { get; init; }
        public required int? to { get; init; }
        public required string? end { get; init; }
        public required string? circuit { get; init; }
        public required string source { get; init; }
    }

    /// <summary>The estimate block of a chi-square (WLS-family) estimate.</summary>
    internal sealed class WlsEstimateRow
    {
        public required string status { get; init; }
        public required bool converged { get; init; }
        public required int iterations { get; init; }
        public required double maxStateUpdate { get; init; }
        public required double objectiveJ { get; init; }
        public required int degreesOfFreedom { get; init; }
        public required double chiSquareThreshold { get; init; }
        public required JsonElement chiSquarePassed { get; init; }
        public required ChiSquareOutcome chiSquareTest { get; init; }
        public required bool irlsCapReached { get; init; }
        public required IEnumerable<EstimateBusRow> buses { get; init; }
    }

    /// <summary>The estimate block of a LAV estimate (no chi-square verdict).</summary>
    internal sealed class LavEstimateRow
    {
        public required string status { get; init; }
        public required bool converged { get; init; }
        public required int iterations { get; init; }
        public required double maxStateUpdate { get; init; }
        public required double objectiveJ { get; init; }
        public required int degreesOfFreedom { get; init; }
        public required double chiSquareThreshold { get; init; }
        public required JsonElement chiSquarePassed { get; init; }
        public required JsonElement chiSquareTest { get; init; }
        public required bool irlsCapReached { get; init; }
        public required bool chiSquareVerdict { get; init; }
        public required double? lavObjective { get; init; }
        public required IEnumerable<EstimateBusRow> buses { get; init; }
    }

    /// <summary>One estimated bus: { id, vm, vaDeg }.</summary>
    internal sealed class EstimateBusRow
    {
        public required int id { get; init; }
        public required double vm { get; init; }
        public required double vaDeg { get; init; }
    }

    /// <summary>One residual.</summary>
    internal sealed class ResidualRow
    {
        public required MeasurementRow measurement { get; init; }
        public required double predicted { get; init; }
        public required double residual { get; init; }
        public required double normalizedResidual { get; init; }
        public required bool critical { get; init; }
    }

    /// <summary>The observability report.</summary>
    internal sealed class ObservabilityRow
    {
        public required bool observable { get; init; }
        public required int rank { get; init; }
        public required int stateSize { get; init; }
        public required IReadOnlyList<int> unobservableBuses { get; init; }
        public required IReadOnlyList<string> unobservableStates { get; init; }
        public required IReadOnlyList<int> unmeasuredIslandBuses { get; init; }
    }

    /// <summary>One removed (bad-data) measurement.</summary>
    internal sealed class RemovedRow
    {
        public required MeasurementRow measurement { get; init; }
        public required double normalizedResidual { get; init; }
        public required int pass { get; init; }
    }

    /// <summary>One estimator iteration of the trace.</summary>
    internal sealed class TraceRow
    {
        public required int pass { get; init; }
        public required int iteration { get; init; }
        public required double maxStateUpdate { get; init; }
        public required double stepScale { get; init; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for the estimate export document (and the two runtime types of its estimate member) and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(EstimateExport.EstimateDoc))]
[JsonSerializable(typeof(EstimateExport.WlsEstimateRow))]
[JsonSerializable(typeof(EstimateExport.LavEstimateRow))]
internal sealed partial class EstimateExportJsonContext : JsonSerializerContext;
