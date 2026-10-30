using System.Text.Json;
using System.Text.Json.Serialization;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.IO;

/// <summary>A grid state reconstructed from a solve-export: the network and its solved result.</summary>
public sealed record SolveSnapshot(GridModel Model, PowerFlowResult Result, string Case, string TimestampUtc);

/// <summary>
/// The inverse of <see cref="SolveExport"/>: rebuilds a <see cref="GridModel"/> + <see cref="PowerFlowResult"/>
/// from a <c>gridsim-solve-export/1</c> JSON document, so a pre-solved run can be loaded and rendered by the same
/// views that drive the live solve. Deserialisation is case-insensitive (the export writes <c>meta</c> in
/// PascalCase but <c>model</c>/<c>result</c> in camelCase) and tolerant of named float literals (Infinity limits).
/// The model block is read by <see cref="BridgeProtocol.BuildModel"/>, the same reader the bridge topology uses, so
/// every bus, generator (in service or not) and branch field the export writes round-trips, with the OLTCs and
/// boundaries (R031, D-DOC-067). What the export does not carry (the asset dynamics model and the protection bundle)
/// is absent from the re-imported model.
///
/// <para>The <c>solve</c> block (D-DOC-180, D-DOC-071): <c>status</c>, when present, is the solve's
/// <see cref="PowerFlowStatus"/> and is restored (an older document without it reads Converged when it converged and
/// MaxIterations when not). The block itself and its <c>converged</c> flag are required (GR-180): a document without
/// them is refused rather than read as a converged solve. A
/// document written for an ESTIMATE (<c>estimated-solve-export.json</c>) carries <c>maxStateUpdate</c> and no
/// <c>maxMismatch</c>; the imported result then holds that state update in <see cref="PowerFlowResult.MaxMismatch"/>,
/// exactly as the in-memory projection <c>EstimationResult.ToPowerFlowResult</c> does, so the round trip is exact.</para>
/// </summary>
public static class SolveImport
{
    // Phase 0.1 (source-generated JSON, so the core can run under NativeAOT): exactly the options this file has always
    // used, now bound to the source-generated SolveImportJsonContext (below) so no runtime reflection is needed.
    // Internal, not private, so JsonSourceGenEquivalenceTests reads and writes through this very instance.
    internal static readonly SolveImportJsonContext Json = new(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // tolerate Infinity Qmin/Qmax
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    });

    public static SolveSnapshot ReadFile(string path) => Read(File.ReadAllText(path));

    public static SolveSnapshot Read(string json)
    {
        RootDto root = JsonSerializer.Deserialize(json, Json.RootDto)
            ?? throw new InvalidDataException("Empty or invalid solve-export JSON.");
        // Bug hunt 2026-10-29, finding 40: the tag is matched exactly, as every other reader in this folder matches its
        // own. A prefix match read a future "/2" document (or any "gridsim-solve-export/..." text) with /1 semantics and
        // defaults; it is refused, naming the tag this reader knows.
        if (root.Schema is null || !string.Equals(root.Schema, "gridsim-solve-export/1", StringComparison.Ordinal))
            throw new InvalidDataException($"Not a gridsim solve-export this version reads (schema={root.Schema ?? "null"}; expected gridsim-solve-export/1).");
        if (root.Model is null || root.Result is null)
            throw new InvalidDataException("Solve-export is missing its model or result.");

        // One model reader for both the bridge topology and the solve-export (R031): the branch state, ZIP,
        // pmin, zero-sequence/winding data, OLTCs and boundaries all round-trip.
        GridModel model = BridgeProtocol.BuildModel(root.Model, defaultName: "replay");
        PowerFlowResult result = BuildResult(root.Result, root.Solve);
        return new SolveSnapshot(model, result, root.Meta?.Case ?? model.Name, root.Meta?.TimestampUtc ?? "");
    }

    private static PowerFlowResult BuildResult(ResultDto r, SolveDto? solve)
    {
        var rb = r.Buses ?? new();
        var flows = (r.LineFlows ?? new()).Select(l => new LineFlow(
            l.From, l.To, l.PFromMw, l.QFromMvar, l.PToMw, l.QToMvar, l.PLossMw, l.QLossMvar,
            l.Name ?? "", l.RatingMva, l.LoadingPercent)).ToList();

        // D-DOC-180: restore the solve status when the document states it. A power-flow status name that is not a
        // PowerFlowStatus is invalid data (fail loud). An estimate document's status is the ESTIMATOR's
        // (EstimationStatus, e.g. "Converged"); the projection it came from never set a power-flow status, so it is not
        // read here and the result keeps the default, as the projection does.
        // GR-180: a document with no solve block, or a solve block with no converged flag, is refused. Before, either
        // read as a converged solve with zero iterations and mismatch, so a truncated or hand-written document could
        // present an unsolved state as solved. GridSim's SolveExport and GDA's to_solve_export always write both.
        if (solve is null)
            throw new InvalidDataException(
                "Solve export has no solve block, so whether the state converged is unknown; refusing to read it as converged.");
        if (solve.Converged is null)
            throw new InvalidDataException(
                "Solve export solve block has no 'converged' flag, so whether the state converged is unknown; refusing to read it as converged.");

        bool isEstimate = solve.MaxStateUpdate is not null && solve.MaxMismatch is null;
        // GR-180: with no status written, a non-converged document reads MaxIterations (not Converged).
        PowerFlowStatus status = solve.Converged.Value ? PowerFlowStatus.Converged : PowerFlowStatus.MaxIterations;
        if (!isEstimate && solve.Status is string st)
        {
            if (!Enum.TryParse(st, ignoreCase: false, out status) || !Enum.IsDefined(status) || int.TryParse(st, out _))
                throw new InvalidDataException(
                    $"Solve-export solve.status '{st}' is not a power-flow status (expected one of {string.Join(", ", Enum.GetNames<PowerFlowStatus>())}).");
        }

        return new PowerFlowResult
        {
            Converged = solve.Converged.Value,
            Iterations = solve.Iterations,
            // D-DOC-071: an estimate document names its convergence measure maxStateUpdate; it goes back into the slot
            // the projection put it in. A power-flow document (or an older estimate one) keeps maxMismatch, as before.
            MaxMismatch = isEstimate ? solve.MaxStateUpdate!.Value : solve.MaxMismatch ?? 0,
            Status = status,
            BusIds = rb.Select(x => x.Id).ToArray(),
            Vm = rb.Select(x => x.Vm).ToArray(),
            VaDeg = rb.Select(x => x.VaDeg).ToArray(),
            PInjMw = rb.Select(x => x.PInjMw).ToArray(),
            QInjMvar = rb.Select(x => x.QInjMvar).ToArray(),
            TotalLossMw = r.TotalLossMw,
            TotalLossMvar = r.TotalLossMvar,
            LineFlows = flows,
        };
    }

    // ---- DTOs mirroring the SolveExport document shape (case-insensitive, so PascalCase meta + camelCase body both bind) ----
    // Phase 0.1: the wire DTOs below are internal rather than private only so the source-generated JSON context, a
    // separate class, can reach them; nothing else changes.
    internal sealed class RootDto
    {
        public string? Schema { get; set; }
        public MetaDto? Meta { get; set; }
        public BridgeProtocol.ModelBlock? Model { get; set; }
        public SolveDto? Solve { get; set; }
        public ResultDto? Result { get; set; }
    }

    internal sealed class MetaDto { public string? Case { get; set; } public string? TimestampUtc { get; set; } }

    internal sealed class SolveDto
    {
        public bool? Converged { get; set; }             // GR-180: required; absent is refused, never read as true
        public int Iterations { get; set; }
        public double? MaxMismatch { get; set; }       // absent in an estimate document (D-DOC-071)
        public double? MaxStateUpdate { get; set; }    // only in an estimate document (D-DOC-071)
        public string? Status { get; set; }            // D-DOC-180; absent in older documents (read as the default)
    }

    internal sealed class ResultDto
    {
        public double TotalLossMw { get; set; }
        public double TotalLossMvar { get; set; }
        public List<ResBusDto>? Buses { get; set; }
        public List<FlowDto>? LineFlows { get; set; }
    }

    internal sealed class ResBusDto
    {
        public int Id { get; set; }
        public double Vm { get; set; } = 1.0;
        public double VaDeg { get; set; }
        public double PInjMw { get; set; }
        public double QInjMvar { get; set; }
    }

    internal sealed class FlowDto
    {
        public int From { get; set; }
        public int To { get; set; }
        public double PFromMw { get; set; }
        public double QFromMvar { get; set; }
        public double PToMw { get; set; }
        public double QToMvar { get; set; }
        public double PLossMw { get; set; }
        public double QLossMvar { get; set; }
        public string? Name { get; set; }
        public double RatingMva { get; set; }
        public double LoadingPercent { get; set; }
    }
}

/// <summary>
/// Phase 0.1: the source-generated JSON metadata for a solve-export document read back and every type it reaches, so it is read and
/// written without runtime reflection (NativeAOT). Metadata mode only: the bytes come from the same converters the
/// reflection path used, never from a separately generated fast path.
/// The runtime options (naming, key case, null and number handling, indentation) are passed in by the call site.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SolveImport.RootDto))]
internal sealed partial class SolveImportJsonContext : JsonSerializerContext;
