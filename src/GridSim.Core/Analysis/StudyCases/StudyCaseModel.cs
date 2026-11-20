using System.Globalization;
using GridSim.Core.Analysis.PlanningStudies;

namespace GridSim.Core.Analysis.StudyCases;

// RC4 R4-01: the typed study case. No file or JSON I/O lives here (the CLI adapter parses and writes); the master,
// the groups document, the instants, the variations, the scenarios and the studies are separate objects.

/// <summary>A named instant (UTC) at which a case is resolved.</summary>
public sealed record StudyCaseInstant(string Id, DateTimeOffset Instant);

/// <summary>A reference to the groups document: its path relative to the study case and the SHA-256 of its exact bytes.</summary>
public sealed record StudyCaseGroupsReference(string Path, string Sha256);

/// <summary>A study to run: its definition and the scenario it runs under (null = the reserved <c>base</c> scenario).</summary>
public sealed record StudyCaseStudy(string? Scenario, PlanningStudyDefinition Definition)
{
    public string Id => Definition.Id;
    public string ScenarioId => Scenario ?? StudyCaseVersions.BaseScenario;
}

/// <summary>The typed <c>gridsim-studycase/1</c> document. Jobs are scenario-major (the reserved <c>base</c> first when
/// used, then scenarios in declared order), then instant in declared order, then study in declared order.</summary>
public sealed record StudyCaseModel(PlanningMasterReference Master, StudyCaseGroupsReference? Groups,
    IReadOnlyList<StudyCaseInstant> Instants, VariationSet Variations, IReadOnlyList<OperationScenario> Scenarios,
    IReadOnlyList<StudyCaseStudy> Studies);

/// <summary>
/// A study mode of this work order: a per-group summary of a resolved case. Scheduled figures need no solve (load,
/// scheduled in-service generation and their balance, per group); with <see cref="Solve"/> the AC power flow is run and
/// the solved net injection, the tie flow out of the group and the internal loss are added, so that
/// <c>netInjection = tieFlow + internalLoss + shuntLoss</c> can be checked against the solved state.
/// </summary>
public sealed record GroupSummaryStudy(string Id, IReadOnlyList<string>? Groups, bool Solve, string? Membership = null)
    : PlanningStudyDefinition(Id)
{
    public const string ModeName = "group-summary";
    public override string Mode => ModeName;
    public override string Canonical() => string.Join("\n",
        "mode\t" + ModeName,
        "groups\t" + (Groups is null ? "all" : string.Join(";", Groups)),
        "solve\t" + (Solve ? "true" : "false"),
        "membership\t" + PlanningFingerprints.Sha256(Membership ?? "none"));
}

/// <summary>One derivation row: a RC3 audit entry plus where it came from.</summary>
public sealed record StudyCaseAuditEntry(string Origin, string? ExpansionStage, PlanningAuditEntry Entry);

/// <summary>One resolved (scenario, instant) case with its derivation.</summary>
public sealed record StudyCaseCase(string Scenario, string InstantId, PlanningResolvedCase Case,
    IReadOnlyList<ScenarioElementChange> ScenarioChanges, IReadOnlyList<StudyCaseAuditEntry> Derivation);

public sealed record StudyCaseExpansionResult(string MasterSha256, string MasterCaseSha256,
    IReadOnlyList<StudyCaseCase> Cases, IReadOnlyList<PlanningJob> Jobs);

/// <summary>Culture-invariant text helpers.</summary>
internal static class StudyCaseText
{
    public static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    public static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
