using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.StudyCases;

/// <summary>A study mode of the study-case registry. It also sees the bound groups of the run.</summary>
public interface IStudyCaseMode
{
    string Mode { get; }
    /// <summary>The mode's own passing status.</summary>
    string PassStatus { get; }
    PlanningJobResult Run(string jobId, GridModel model, PlanningStudyDefinition study, BoundGroups? groups);
}

/// <summary>
/// The study-case mode registry. It is its own registry: <see cref="PlanningStudyModes"/> and the
/// <c>gridsim-planning/1</c> schema stay exactly as published. It wraps the three RC3 engines (each an unaltered
/// <see cref="IPlanningStudyMode"/>) and adds <c>group-summary</c>; the RC4 modes register here as they arrive.
/// </summary>
public static class StudyCaseModes
{
    public static IReadOnlyDictionary<string, IStudyCaseMode> Available { get; } = Build();

    private static Dictionary<string, IStudyCaseMode> Build()
    {
        var d = new Dictionary<string, IStudyCaseMode>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, IPlanningStudyMode> m in PlanningStudyModes.Available)
            d[m.Key] = new Rc3Mode(m.Value);
        d[GroupSummaryStudy.ModeName] = new GroupSummaryMode();
        return d;
    }

    public static bool Passed(string mode, string? status) =>
        status is not null && Available.TryGetValue(mode, out IStudyCaseMode? m) && m.PassStatus == status;

    /// <summary>Run one job: INVALID CASE never reaches an engine; an engine refusal is FAILED with its message.</summary>
    public static PlanningJobResult RunJob(PlanningJob job, BoundGroups? groups)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!job.Case.Validation.Valid)
            return new PlanningJobResult(job.Id, PlanningStudyModes.InvalidCase, "Invalid", string.Join(" ", job.Case.Validation.Findings), null);
        if (!Available.TryGetValue(job.Study.Mode, out IStudyCaseMode? mode))
            return new PlanningJobResult(job.Id, PlanningStudyModes.Failed, "Unavailable", $"Mode '{job.Study.Mode}' is not available in this build.", null);
        try
        {
            return mode.Run(job.Id, job.Case.Model, job.Study, groups);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            return new PlanningJobResult(job.Id, PlanningStudyModes.Failed, "EngineError", ex.Message, null);
        }
    }

    private sealed class Rc3Mode(IPlanningStudyMode inner) : IStudyCaseMode
    {
        public string Mode => inner.Mode;
        public string PassStatus => inner.PassStatus;
        public PlanningJobResult Run(string jobId, GridModel model, PlanningStudyDefinition study, BoundGroups? groups) =>
            inner.Run(jobId, model, study);
    }
}

/// <summary>One group's figures in a resolved case.</summary>
public sealed record GroupSummaryRow(string Group, string Kind, int Buses, int Generators, int Branches,
    double LoadMw, double LoadMvar, double ScheduledGenerationMw, double ScheduledBalanceMw,
    double? SolvedNetInjectionMw, double? TieFlowMw, double? InternalLossMw, double? ShuntLossMw);

public sealed record GroupSummaryEvidence(bool SolveRequested, string? SolveStatus, IReadOnlyList<GroupSummaryRow> Groups);

/// <summary>
/// The <c>group-summary</c> mode. Per group, in the order requested (or file order): load is the sum of the member
/// buses' demand in bus order; scheduled generation is the sum of the set points of the in-service units at the member
/// buses (or the group's own unit members) in fleet order; the scheduled balance is their difference. With a solve:
/// the solved net injection is the sum of the member buses' net injections, the tie flow is the active power leaving
/// the group through branches with exactly one end in it, the internal loss is the loss of branches with both ends in
/// it, and the shunt loss is the conductance loss at the member buses. By construction of the solved state these
/// close: <c>netInjection = tieFlow + internalLoss + shuntLoss</c>; on a lossless network the area interchange equals
/// the sum of the net injections.
/// </summary>
public sealed class GroupSummaryMode : IStudyCaseMode
{
    public string Mode => GroupSummaryStudy.ModeName;
    public string PassStatus => "SUMMARISED";

    public PlanningJobResult Run(string jobId, GridModel model, PlanningStudyDefinition study, BoundGroups? groups)
    {
        var gs = (GroupSummaryStudy)study;
        if (groups is null) throw new InvalidOperationException("group-summary needs a groups document.");
        PowerFlowResult? solved = null;
        string? solveStatus = null;
        if (gs.Solve)
        {
            solved = SolverSelector.CreateNewton(model).Solve(model, ContingencyAnalysis.StudyOptions);
            solveStatus = solved.Status.ToString();
            if (solved.Status != PowerFlowStatus.Converged) solved = null;
        }
        IEnumerable<BoundGroup> selected = gs.Groups is null ? groups.Groups : gs.Groups.Select(groups.Get);
        GroupSummaryRow[] rows = selected.Select(g => Row(model, g, solved)).ToArray();
        var evidence = new GroupSummaryEvidence(gs.Solve, solveStatus, rows);
        if (gs.Solve && solved is null)
            return new PlanningJobResult(jobId, "INCOMPLETE", "Incomplete",
                $"The power flow did not converge ({solveStatus}); only the scheduled figures are given.", evidence);
        return new PlanningJobResult(jobId, PassStatus, "Summarised", null, evidence);
    }

    private static GroupSummaryRow Row(GridModel model, BoundGroup g, PowerFlowResult? solved)
    {
        var members = new HashSet<int>(g.BusIds);
        double loadMw = 0, loadMvar = 0, gen = 0;
        foreach (Bus b in model.Buses)
            if (members.Contains(b.Id)) { loadMw += b.Pd; loadMvar += b.Qd; }
        IReadOnlyList<int> units = BoundGroups.GeneratorsOf(g, model);
        foreach (int u in units)
        {
            Generator unit = model.AllGenerators[u];
            if (unit.InService) gen += unit.Pg;
        }
        double? net = null, tie = null, internalLoss = null, shuntLoss = null;
        if (solved is not null)
        {
            double n = 0, shunt = 0;
            for (int i = 0; i < solved.BusIds.Count; i++)
                if (members.Contains(solved.BusIds[i])) n += solved.PInjMw[i];
            foreach (Bus b in model.Buses)
                if (members.Contains(b.Id))
                {
                    int k = IndexOf(solved.BusIds, b.Id);
                    shunt += b.Gs * solved.Vm[k] * solved.Vm[k];
                }
            double t = 0, loss = 0;
            for (int i = 0; i < model.Branches.Count && i < solved.LineFlows.Count; i++)
            {
                Branch br = model.Branches[i];
                if (!br.InService) continue;
                LineFlow f = solved.LineFlows[i];
                bool from = members.Contains(br.From), to = members.Contains(br.To);
                if (from && to) loss += f.PLossMw;
                else if (from) t += f.PFromMw;
                else if (to) t += f.PToMw;
            }
            net = n; tie = t; internalLoss = loss; shuntLoss = shunt;
        }
        return new GroupSummaryRow(g.Id, ModelGroup.KindName(g.Group.Kind), g.BusIds.Count, units.Count, g.BranchIndices.Count,
            loadMw, loadMvar, gen, gen - loadMw, net, tie, internalLoss, shuntLoss);
    }

    private static int IndexOf(IReadOnlyList<int> ids, int id)
    {
        for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        return -1;
    }
}
