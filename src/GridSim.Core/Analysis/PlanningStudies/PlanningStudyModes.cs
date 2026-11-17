using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis.PlanningStudies;

/// <summary>
/// A previously verified study engine exposed as a planning mode. The selector is the mode name; each mode owns one
/// typed definition (<see cref="PlanningStudyDefinition"/> subclass) and one manifest parameter object, so adding a
/// mode adds a name and a type without changing any existing manifest field.
/// </summary>
public interface IPlanningStudyMode
{
    string Mode { get; }
    /// <summary>The engine's own passing status (the one its verb maps to exit 0).</summary>
    string PassStatus { get; }
    /// <summary>Run the study on a resolved, validated case. Must not mutate <paramref name="model"/>.</summary>
    PlanningJobResult Run(string jobId, GridModel model, PlanningStudyDefinition study);
}

/// <summary>The modes this build offers, and any names reserved for later increments.</summary>
public static class PlanningStudyModes
{
    public const string InvalidCase = "INVALID CASE";
    public const string Failed = "FAILED";

    public static IReadOnlyDictionary<string, IPlanningStudyMode> Available { get; } =
        new Dictionary<string, IPlanningStudyMode>(StringComparer.Ordinal)
        {
            [TransmissionSecurityPlanningStudy.ModeName] = new TransmissionSecurityPlanningMode(),
            [TransferPlanningStudy.ModeName] = new TransferPlanningMode(),
            [CorrectiveN11PlanningStudy.ModeName] = new CorrectiveN11PlanningMode(),
        };

    /// <summary>Mode names reserved for studies not in this build: refused with a clear diagnostic.</summary>
    public static IReadOnlyList<string> Reserved { get; } = [];

    /// <summary>True when the job's status is its engine's pass. INVALID CASE and FAILED never pass.</summary>
    public static bool Passed(string mode, string? status) =>
        status is not null && Available.TryGetValue(mode, out IPlanningStudyMode? m) && m.PassStatus == status;

    /// <summary>True for a terminal engine verdict (anything but INVALID CASE or FAILED): only these are reusable.</summary>
    public static bool IsVerdict(string? status) => status is not null and not InvalidCase and not Failed;

    /// <summary>
    /// Run one job. Order: a case that failed topology or limit validation is never handed to the engine (INVALID
    /// CASE); an engine refusal or arithmetic failure is FAILED with its message; otherwise the engine's own verdict.
    /// </summary>
    public static PlanningJobResult RunJob(PlanningJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.Case.Validation.Valid)
            return new PlanningJobResult(job.Id, InvalidCase, "Invalid", string.Join(" ", job.Case.Validation.Findings), null);
        if (!Available.TryGetValue(job.Study.Mode, out IPlanningStudyMode? mode))
            return new PlanningJobResult(job.Id, Failed, "Unavailable", $"Mode '{job.Study.Mode}' is not available in this build.", null);
        try
        {
            return mode.Run(job.Id, job.Case.Model, job.Study);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            return new PlanningJobResult(job.Id, Failed, "EngineError", ex.Message, null);
        }
    }

    /// <summary>Applicability of one requested branch outage in a resolved case: a branch already out of service (a
    /// planned outage) cannot be lost again and keeps an explicit NOT APPLICABLE row.</summary>
    internal static PlanningOutageRow Applicability(GridModel model, BranchOutage o)
    {
        Branch b = model.Branches[o.Index];
        if (b.From != o.From || b.To != o.To)
            throw new ArgumentException($"Outage {o.Index} no longer joins {o.From}-{o.To} in the resolved case.");
        return b.InService
            ? new(o.Index, o.From, o.To, "REQUESTED", null)
            : new(o.Index, o.From, o.To, "NOT APPLICABLE", "Branch is out of service in this snapshot.");
    }
}

/// <summary>One requested outage as it applies to one resolved case.</summary>
public sealed record PlanningOutageRow(int Index, int From, int To, string Applicability, string? Reason);

/// <summary>The evidence of a transmission-security planning job: the requested outages with their applicability in
/// this snapshot, the request actually given to the P1 runner, and its unaltered result.</summary>
public sealed record TransmissionSecurityPlanningEvidence(IReadOnlyList<PlanningOutageRow> RequestedOutages,
    TransmissionSecurityRequest? EffectiveRequest, TransmissionSecurityResult? Result,
    string DispatchPolicy = "The study replaces unit P with its DC candidate dispatch; an authored pgMw is part of the " +
        "resolved case and its identity but not of this study's candidate.");

/// <summary>
/// Adapter over <see cref="TransmissionSecurityRunner"/>. Applicable outages are passed in requested order and the
/// runner's result is reported unaltered. With no applicable outage the job is NOT SECURE / Incomplete.
/// </summary>
public sealed class TransmissionSecurityPlanningMode : IPlanningStudyMode
{
    public string Mode => TransmissionSecurityPlanningStudy.ModeName;
    public string PassStatus => "SECURE";

    public PlanningJobResult Run(string jobId, GridModel model, PlanningStudyDefinition study)
    {
        var ts = (TransmissionSecurityPlanningStudy)study;
        PlanningOutageRow[] rows = ts.Outages.Select(o => PlanningStudyModes.Applicability(model, o)).ToArray();
        BranchOutage[] applicable = ts.Outages.Where((_, i) => rows[i].Applicability == "REQUESTED").ToArray();
        if (applicable.Length == 0)
            return new PlanningJobResult(jobId, "NOT SECURE", "Incomplete",
                "No requested outage is applicable in this snapshot.", new TransmissionSecurityPlanningEvidence(rows, null, null));
        var request = new TransmissionSecurityRequest(applicable, ts.MinPu, ts.MaxPu, ts.PostFaultRating,
            ts.MaxRounds, ts.Costs?.ToArray());
        TransmissionSecurityResult result = TransmissionSecurityRunner.Run(model, request);
        return new PlanningJobResult(jobId, result.Status, result.Outcome, null,
            new TransmissionSecurityPlanningEvidence(rows, request, result));
    }
}

/// <summary>The evidence of a transfer planning job.</summary>
public sealed record TransferPlanningEvidence(IReadOnlyList<PlanningOutageRow> RequestedOutages,
    TransferStudyRequest? EffectiveRequest, TransferStudyResult? Result);

/// <summary>Adapter over <see cref="TransferStudyRunner"/> (P4). Outage applicability as for the preventive mode;
/// with no applicable outage the job is INCOMPLETE, since no secure-transfer evidence exists.</summary>
public sealed class TransferPlanningMode : IPlanningStudyMode
{
    public string Mode => TransferPlanningStudy.ModeName;
    public string PassStatus => "VERIFIED";

    public PlanningJobResult Run(string jobId, GridModel model, PlanningStudyDefinition study)
    {
        TransferStudyRequest r = ((TransferPlanningStudy)study).Request;
        PlanningOutageRow[] rows = r.Outages.Select(o => PlanningStudyModes.Applicability(model, o)).ToArray();
        BranchOutage[] applicable = r.Outages.Where((_, i) => rows[i].Applicability == "REQUESTED").ToArray();
        if (applicable.Length == 0)
            return new PlanningJobResult(jobId, "INCOMPLETE", "Incomplete",
                "No requested outage is applicable in this snapshot.", new TransferPlanningEvidence(rows, null, null));
        TransferStudyRequest request = r with { Outages = applicable };
        TransferStudyResult result = TransferStudyRunner.Run(model, request);
        return new PlanningJobResult(jobId, result.Status, result.Outcome, result.Reason,
            new TransferPlanningEvidence(rows, request, result));
    }
}

/// <summary>One requested N-1-1 sequence as it applies to one resolved case.</summary>
public sealed record PlanningSequenceRow(int Sequence, BranchOutage First, BranchOutage Second, string Applicability, string? Reason);

/// <summary>The evidence of a corrective N-1-1 planning job.</summary>
public sealed record CorrectiveN11PlanningEvidence(IReadOnlyList<PlanningSequenceRow> RequestedSequences,
    CorrectiveSequenceRequest? EffectiveRequest, CorrectiveStudyResult? Result);

/// <summary>Adapter over <see cref="CorrectiveSequenceRunner"/> (P5). A sequence whose first or second branch is out
/// of service in the snapshot keeps a NOT APPLICABLE row; the rest run in requested order. With none applicable the
/// job is NOT SECURE / Incomplete.</summary>
public sealed class CorrectiveN11PlanningMode : IPlanningStudyMode
{
    public string Mode => CorrectiveN11PlanningStudy.ModeName;
    public string PassStatus => "SECURE";

    public PlanningJobResult Run(string jobId, GridModel model, PlanningStudyDefinition study)
    {
        CorrectiveSequenceRequest r = ((CorrectiveN11PlanningStudy)study).Request;
        var rows = new List<PlanningSequenceRow>();
        var applicable = new List<CorrectiveSequence>();
        for (int i = 0; i < r.Sequences.Count; i++)
        {
            CorrectiveSequence q = r.Sequences[i];
            PlanningOutageRow first = PlanningStudyModes.Applicability(model, q.First);
            PlanningOutageRow second = PlanningStudyModes.Applicability(model, q.Second);
            if (first.Applicability == "REQUESTED" && second.Applicability == "REQUESTED")
            {
                rows.Add(new(i + 1, q.First, q.Second, "REQUESTED", null));
                applicable.Add(q);
            }
            else
                rows.Add(new(i + 1, q.First, q.Second, "NOT APPLICABLE",
                    $"Branch {(first.Applicability == "REQUESTED" ? q.Second.Index : q.First.Index)} is out of service in this snapshot."));
        }
        if (applicable.Count == 0)
            return new PlanningJobResult(jobId, "NOT SECURE", "Incomplete",
                "No requested sequence is applicable in this snapshot.", new CorrectiveN11PlanningEvidence(rows, null, null));
        CorrectiveSequenceRequest request = r with { Sequences = applicable };
        CorrectiveStudyResult result = CorrectiveSequenceRunner.Run(model, request);
        return new PlanningJobResult(jobId, result.Status, result.Outcome, result.Reason,
            new CorrectiveN11PlanningEvidence(rows, request, result));
    }
}
