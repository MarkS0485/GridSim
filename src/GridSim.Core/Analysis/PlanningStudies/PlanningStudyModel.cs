using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.PlanningStudies;

// RC3 P7: versioned planning snapshots and repeatable studies. The typed change and run model. No file or JSON I/O
// lives here; the CLI adapter (PlanningStudyCommand) parses the manifest, loads and fingerprints the master and owns
// the result files. Model reference, projects, snapshots and study definitions are separate objects.

/// <summary>The dedicated manifest version this model implements.</summary>
public static class PlanningManifestVersion
{
    public const string Current = "gridsim-planning/1";
    public const string Result = "gridsim-planning-result/1";
}

/// <summary>How the master's source bundle is interpreted. v1 supports exactly one interpretation: the native JSON
/// loaders (v1 case directory or v2 case manifest) with loader balancing suppressed, so no merit-order dispatch runs
/// as a side effect of loading or between operations.</summary>
public sealed record PlanningInterpretation(string Loader, bool Dispatch)
{
    public const string NativeJson = "native-json";
}

/// <summary>The master reference: a source bundle (path relative to the manifest, exact bundle digest) plus its
/// interpretation options. The master identity is derived from both.</summary>
public sealed record PlanningMasterReference(string Path, string BundleSha256, PlanningInterpretation Interpretation);

/// <summary>One consumed input file: its bundle-relative path and lowercase SHA-256 of its exact bytes.</summary>
public sealed record PlanningInputFile(string Path, string Sha256);

/// <summary>The loaded, immutable master: the model, the exact consumed-file manifest in load order, and the
/// interpretation it was loaded under. <see cref="MasterSha256"/> binds bundle and interpretation together.</summary>
public sealed record PlanningMaster(GridModel Model, string BundleSha256, IReadOnlyList<PlanningInputFile> Inputs,
    PlanningInterpretation Interpretation)
{
    public string MasterSha256 => PlanningFingerprints.Master(BundleSha256, Interpretation);
}

public enum PlanningElementKind { Branch, Generator, Load }

/// <summary>The fields a v1 operation may change. Units: MVA for ratings, MW and MVAr for setpoints. Ratings are per
/// resolved branch (one circuit). A load is the aggregate constant-power demand of a bus.</summary>
public enum PlanningField
{
    BranchInService, BranchRateAMva, BranchRateBMva, BranchRateCMva,
    GeneratorPgMw, GeneratorQgMvar, LoadPdMw, LoadQdMvar
}

/// <summary>
/// A stable target identity bound to the exact master. Branch: zero-based index into the resolved branch list with
/// its expected From and To bus IDs (parallel circuits stay distinct). Generator: zero-based index into the whole
/// fleet (out-of-service units included, model order) with its expected bus. Load: the bus ID whose aggregate
/// demand is changed. An optional name is an identity check when given.
/// </summary>
public sealed record PlanningTarget(PlanningElementKind Kind, int? Index, int? From, int? To, int? Bus, string? Name)
{
    public string Id => Kind switch
    {
        PlanningElementKind.Branch => $"branch {Index} ({From}-{To})",
        PlanningElementKind.Generator => $"generator {Index} (bus {Bus})",
        _ => $"load (bus {Bus})"
    };
}

/// <summary>A replacement or expected value: a finite number, or a flag for availability.</summary>
public readonly record struct PlanningValue(double? Number, bool? Flag)
{
    public static PlanningValue Of(double value) => new(value, null);
    public static PlanningValue Of(bool value) => new(null, value);
    public bool IsFlag => Flag.HasValue;
    /// <summary>Exact comparison: a number matches only the identical double (no tolerance).</summary>
    public bool SameAs(PlanningValue other) => IsFlag
        ? other.IsFlag && Flag == other.Flag
        : !other.IsFlag && Number!.Value.Equals(other.Number!.Value);
    public override string ToString() => IsFlag
        ? (Flag!.Value ? "true" : "false")
        : Number!.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// One dated, ordered operation. Active at instant t when <c>EffectiveFrom &lt;= t &lt; EffectiveTo</c>
/// (from inclusive, to exclusive; an absent EffectiveTo is open-ended). Both instants are normalised to UTC.
/// </summary>
public sealed record PlanningOperation(string Id, PlanningTarget Target, PlanningField Field,
    PlanningValue ExpectedPrior, PlanningValue Value, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo)
{
    public bool ActiveAt(DateTimeOffset instant) => EffectiveFrom <= instant && (EffectiveTo is null || instant < EffectiveTo.Value);
}

/// <summary>A project: an ordered list of operations. Operation order inside a project is authoring order.</summary>
public sealed record PlanningProject(string Id, IReadOnlyList<PlanningOperation> Operations);

/// <summary>A snapshot: one named instant (UTC) at which the active operations are applied to a copy of the master.</summary>
public sealed record PlanningSnapshot(string Id, DateTimeOffset Instant);

/// <summary>A study definition selected by mode. New modes (transfer, corrective N-1-1) are new subclasses with their
/// own mode name and parameter object; existing manifests are unaffected.</summary>
public abstract record PlanningStudyDefinition(string Id)
{
    public abstract string Mode { get; }
    /// <summary>Canonical, culture-invariant text of every parameter, for the request fingerprint.</summary>
    public abstract string Canonical();
}

/// <summary>The P1 preventive transmission-security study as a planning mode. Outages are bound to the master and
/// re-checked against each resolved case.</summary>
public sealed record TransmissionSecurityPlanningStudy(string Id, IReadOnlyList<BranchOutage> Outages,
    double MinPu, double MaxPu, PostFaultRating PostFaultRating, int MaxRounds, IReadOnlyList<double>? Costs)
    : PlanningStudyDefinition(Id)
{
    public const string ModeName = "transmission-security";
    public override string Mode => ModeName;
    public override string Canonical()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join("\n",
            "mode\t" + ModeName,
            "outages\t" + string.Join(";", Outages.Select(o => $"{o.Index}:{o.From}-{o.To}")),
            "voltage\t" + MinPu.ToString("R", inv) + "\t" + MaxPu.ToString("R", inv),
            "postFaultRating\t" + PostFaultRating,
            "maxRounds\t" + MaxRounds.ToString(inv),
            "costs\t" + (Costs is null ? "incumbent" : string.Join(";", Costs.Select(c => c.ToString("R", inv)))));
    }
}

/// <summary>Culture-invariant canonical text helpers for study fingerprints.</summary>
internal static class PlanningCanonical
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    public static string N(double v) => v.ToString("R", Inv);
    public static string N(double? v) => v is double d ? N(d) : "none";
    public static string I(int v) => v.ToString(Inv);
    public static string Outage(BranchOutage o) => $"{I(o.Index)}:{I(o.From)}-{I(o.To)}";
}

/// <summary>The P4 AC-verified directional transfer study as a planning mode. Parameters are those of the
/// <c>transfer-capability</c> study document (without schemaVersion and case). Participants and outages are bound to
/// the master; an outage of a branch out of service in a snapshot keeps a NOT APPLICABLE row. Unlike the preventive
/// study, transfer starts from the scheduled unit P, so an authored pgMw changes its reference point.</summary>
public sealed record TransferPlanningStudy(string Id, TransferStudyRequest Request) : PlanningStudyDefinition(Id)
{
    public const string ModeName = "transfer";
    public override string Mode => ModeName;
    public override string Canonical()
    {
        TransferStudyRequest r = Request;
        static string P(IEnumerable<TransferParticipant> ps) =>
            string.Join(";", ps.Select(p => $"{PlanningCanonical.I(p.Index)}@{PlanningCanonical.I(p.Bus)}x{PlanningCanonical.N(p.Weight)}"));
        return string.Join("\n",
            "mode\t" + ModeName,
            "sources\t" + P(r.Sources),
            "sinks\t" + P(r.Sinks),
            "outages\t" + string.Join(";", r.Outages.Select(PlanningCanonical.Outage)),
            "voltage\t" + PlanningCanonical.N(r.MinPu) + "\t" + PlanningCanonical.N(r.MaxPu),
            "postFaultRating\t" + r.PostFaultRating,
            "search\t" + PlanningCanonical.N(r.Search.MaxMw) + "\t" + PlanningCanonical.N(r.Search.StepMw) + "\t" +
                PlanningCanonical.N(r.Search.ToleranceMw) + "\t" + PlanningCanonical.I(r.Search.MaxTrials) + "\t" + PlanningCanonical.I(r.Search.Probes),
            "interfaces\t" + (r.Interfaces is null ? "none" : string.Join(";", r.Interfaces.Select(i => i.Name + "=" +
                string.Join(",", i.Branches.Select(b => $"{PlanningCanonical.I(b.Index)}:{PlanningCanonical.I(b.From)}-{PlanningCanonical.I(b.To)}*{PlanningCanonical.I(b.Sign)}")) +
                "<=" + PlanningCanonical.N(i.LimitMw)))));
    }
}

/// <summary>The P5 corrective N-1-1 sequenced study as a planning mode. Parameters are those of the
/// <c>corrective-sequence</c> study document (without schemaVersion and case). A sequence whose first or second
/// branch is out of service in a snapshot keeps a NOT APPLICABLE row and is not run.</summary>
public sealed record CorrectiveN11PlanningStudy(string Id, CorrectiveSequenceRequest Request) : PlanningStudyDefinition(Id)
{
    public const string ModeName = "corrective-n-1-1";
    public override string Mode => ModeName;
    public override string Canonical()
    {
        CorrectiveSequenceRequest r = Request;
        return string.Join("\n",
            "mode\t" + ModeName,
            "voltage\t" + PlanningCanonical.N(r.MinPu) + "\t" + PlanningCanonical.N(r.MaxPu),
            "ratings\t" + r.FirstStageRating + "\t" + r.IntermediateRating + "\t" + r.FinalRating,
            "policy\t" + PlanningCanonical.N(r.Policy.AvailableMinutes) + "\t" + PlanningCanonical.N(r.Policy.BalanceToleranceMw) + "\t" +
                string.Join(";", r.Policy.Eligible.Select(e => $"{PlanningCanonical.I(e.Generator)}@{PlanningCanonical.I(e.Bus)}:{e.Direction}:" +
                    $"{PlanningCanonical.N(e.MinFinalMw)}:{PlanningCanonical.N(e.MaxFinalMw)}:{PlanningCanonical.N(e.MaxDeltaMw)}:" +
                    $"{PlanningCanonical.N(e.RampMwPerMin)}:{PlanningCanonical.N(e.WindowMinutes)}")),
            "sequences\t" + string.Join(";", r.Sequences.Select(q => PlanningCanonical.Outage(q.First) + ">" +
                PlanningCanonical.Outage(q.Second) + ":" + q.Proposal + ":" + (q.Actions is null ? "none" :
                string.Join(",", q.Actions.Select(a => $"{PlanningCanonical.I(a.Generator)}@{PlanningCanonical.I(a.Bus)}{PlanningCanonical.N(a.DeltaMw)}"))))));
    }
}

/// <summary>The typed manifest: the master reference, projects, the explicit project order, snapshots and studies.
/// Jobs are every snapshot crossed with every study, snapshot-major, in declared order.</summary>
public sealed record PlanningManifest(PlanningMasterReference Master, IReadOnlyList<PlanningProject> Projects,
    IReadOnlyList<string> ProjectOrder, IReadOnlyList<PlanningSnapshot> Snapshots,
    IReadOnlyList<PlanningStudyDefinition> Studies);

/// <summary>What happened to one operation at one snapshot.</summary>
public enum PlanningDisposition { Applied, NotYetEffective, Expired }

/// <summary>
/// The audit entry for one operation at one snapshot. <see cref="Original"/> is the master's value,
/// <see cref="Prior"/> the value immediately before this operation (after earlier ones), <see cref="Effective"/> the
/// value it leaves. Inactive operations carry the master value in all three and a null sequence.
/// </summary>
public sealed record PlanningAuditEntry(int? Sequence, string Project, string Operation, string Target, string Field,
    PlanningDisposition Disposition, string ExpectedPrior, string Replacement, string Original, string Prior,
    string Effective, string EffectiveFromUtc, string? EffectiveToUtc);

/// <summary>Topology and operating-limit checks run on a resolved case before it is handed to a study engine.</summary>
public sealed record PlanningCaseValidation(bool Valid, int? Islands, int InServiceBranches, IReadOnlyList<string> Findings);

/// <summary>One resolved case: the snapshot, its model copy, the exact content and derivation identities, and the
/// audit of every operation.</summary>
public sealed record PlanningResolvedCase(PlanningSnapshot Snapshot, GridModel Model, string CaseSha256,
    string DerivationSha256, IReadOnlyList<PlanningAuditEntry> Audit, PlanningCaseValidation Validation)
{
    public int AppliedOperations => Audit.Count(a => a.Disposition == PlanningDisposition.Applied);
}

/// <summary>One job: a resolved case crossed with a study, with its deterministic identity and fingerprints.</summary>
public sealed record PlanningJob(int Ordinal, string Id, PlanningResolvedCase Case, PlanningStudyDefinition Study,
    string RequestFingerprint, string InputFingerprint)
{
    /// <summary>The job's own output folder name: ordinal plus IDs, so two jobs can never share a path.</summary>
    public string Folder => $"{Ordinal:D3}-{Case.Snapshot.Id}--{Study.Id}";
}

/// <summary>The deterministic expansion of a manifest: the master identities and one resolved case per snapshot,
/// in declared order, plus the ordered job list.</summary>
public sealed record PlanningExpansionResult(string MasterSha256, string MasterCaseSha256,
    IReadOnlyList<PlanningResolvedCase> Cases, IReadOnlyList<PlanningJob> Jobs);

/// <summary>Terminal state of one job. Status uses the study's own nomenclature (SECURE / NOT SECURE for the
/// preventive and corrective studies, VERIFIED / QUALIFIED / INCOMPLETE / UNVERIFIED for transfer) or one of
/// <c>INVALID CASE</c> (pre-study validation refused it) and <c>FAILED</c> (the engine raised an error).</summary>
public sealed record PlanningJobResult(string JobId, string Status, string Outcome, string? Reason, object? Evidence);

/// <summary>An invalid manifest, refused before any job runs (an input error at the CLI).</summary>
public class PlanningManifestException(string message) : Exception(message);

/// <summary>A refusal caused by two operations: an unordered same-field conflict, or a shadowed change whose
/// expected prior no longer holds. Both operation identities are carried and named in the message.</summary>
public sealed class PlanningConflictException(string message, string snapshot, string operation, string other)
    : PlanningManifestException(message)
{
    public string Snapshot { get; } = snapshot;
    public string Operation { get; } = operation;
    public string OtherOperation { get; } = other;
}
