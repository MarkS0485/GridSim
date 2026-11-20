using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.StudyCases;

/// <summary>
/// Deterministic expansion of a study case into resolved cases and jobs, on top of the unchanged RC3
/// <see cref="PlanningExpansion"/>. Order of operations (a refusal stops the whole expansion before any job runs):
/// <list type="number">
/// <item>Validate the study case against the groups: unique IDs, the reserved <c>base</c> scenario name, every study
/// and instant reference, every group reference, every scenario operation (kind, action and value).</item>
/// <item>For each scenario (the reserved <c>base</c> first when a study uses it, then declared order) that has studies,
/// and each of its instants in declared order: resolve the variations alone through <see cref="PlanningExpansion.Expand"/>
/// to obtain the case the scenario scales; expand the scenario to explicit per-element operations from that case;
/// resolve variations plus the scenario project (listed last, so it applies after every variation) through
/// <see cref="PlanningExpansion.Expand"/>. Dating, conflict refusal, expected-prior checks and the topology and rating
/// validation are therefore the RC3 ones.</item>
/// <item>Form the jobs: scenario, instant, study, each in declared order, numbered from 1.</item>
/// </list>
/// </summary>
public static class StudyCaseExpansion
{
    public static StudyCaseExpansionResult Expand(PlanningMaster master, StudyCaseModel studyCase, BoundGroups? groups,
        Func<GridModel, string> caseHasher)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(studyCase);
        ArgumentNullException.ThrowIfNull(caseHasher);
        Validate(studyCase, groups);

        string masterCase = caseHasher(master.Model);
        var order = new List<string>();
        if (studyCase.Studies.Any(s => s.Scenario is null)) order.Add(StudyCaseVersions.BaseScenario);
        order.AddRange(studyCase.Scenarios.Select(s => s.Id));
        var emptyScenario = new OperationScenario(StudyCaseVersions.BaseScenario, null, []);

        var cases = new List<StudyCaseCase>();
        var jobs = new List<PlanningJob>();
        foreach (string scenarioId in order)
        {
            StudyCaseStudy[] studies = studyCase.Studies.Where(s => s.ScenarioId == scenarioId).ToArray();
            if (studies.Length == 0) continue;
            OperationScenario scenario = scenarioId == StudyCaseVersions.BaseScenario
                ? emptyScenario : studyCase.Scenarios.First(s => s.Id == scenarioId);
            IEnumerable<StudyCaseInstant> instants = scenario.Instants is null
                ? studyCase.Instants : studyCase.Instants.Where(i => scenario.Instants.Contains(i.Id));

            PlanningStudyDefinition[] rcStudies = studies.Select(s => s.Definition).Where(d => d is not GroupSummaryStudy).ToArray();
            if (rcStudies.Length == 0) rcStudies = [Placeholder(master.Model)];

            foreach (StudyCaseInstant instant in instants)
            {
                var snapshot = new PlanningSnapshot(scenarioId + "." + instant.Id, instant.Instant);
                StudyCaseCase resolved = Resolve(master, studyCase, groups, caseHasher, scenario, snapshot, instant, rcStudies);
                cases.Add(resolved);
                foreach (StudyCaseStudy s in studies)
                {
                    PlanningStudyDefinition definition = s.Definition is GroupSummaryStudy gs
                        ? gs with { Membership = MembershipOf(gs, groups) } : s.Definition;
                    string id = snapshot.Id + "/" + definition.Id;
                    jobs.Add(new PlanningJob(jobs.Count + 1, id, resolved.Case, definition,
                        PlanningFingerprints.Request(id, snapshot, definition, resolved.Case.DerivationSha256),
                        PlanningFingerprints.Input(master.MasterSha256, resolved.Case.CaseSha256)));
                }
            }
        }
        if (caseHasher(master.Model) != masterCase)
            throw new InvalidOperationException("The master model changed during expansion.");
        return new StudyCaseExpansionResult(master.MasterSha256, masterCase, cases, jobs);
    }

    private static string MembershipOf(GroupSummaryStudy study, BoundGroups? groups)
    {
        if (groups is null) return "none";
        IEnumerable<ModelGroup> selected = study.Groups is null ? groups.Set.Groups
            : study.Groups.Select(id => groups.Get(id).Group);
        return ModelGroupSet.Canonical(selected);
    }

    private static PlanningStudyDefinition Placeholder(GridModel model)
    {
        if (model.Branches.Count == 0)
            throw new StudyCaseException("The case has no branch; a study case needs a network.");
        Branch b = model.Branches[0];
        return new TransmissionSecurityPlanningStudy("expansion-placeholder", [new BranchOutage(0, b.From, b.To)], 0.9, 1.1,
            PostFaultRating.RateA, new ScopfOptions().MaxRounds, null);
    }

    private static StudyCaseCase Resolve(PlanningMaster master, StudyCaseModel sc, BoundGroups? groups,
        Func<GridModel, string> hasher, OperationScenario scenario, PlanningSnapshot snapshot, StudyCaseInstant instant,
        PlanningStudyDefinition[] rcStudies)
    {
        IReadOnlyList<PlanningProject> variationProjects = sc.Variations.ToProjects();
        try
        {
            ScenarioExpansion scenarioOps = new([], []);
            if (scenario.Operations.Count > 0)
            {
                // The case the scenario scales: the variations active at this instant, nothing else.
                var variationsOnly = new PlanningManifest(sc.Master, variationProjects, sc.Variations.Order, [snapshot], rcStudies);
                GridModel baseline = PlanningExpansion.Expand(master, variationsOnly, hasher).Cases[0].Model;
                scenarioOps = ScenarioExpander.Expand(baseline, groups, scenario, snapshot.Instant);
            }
            var projects = new List<PlanningProject>(variationProjects);
            var projectOrder = new List<string>(sc.Variations.Order);
            if (scenarioOps.Operations.Count > 0)
            {
                string project = ScenarioExpander.ProjectPrefix + scenario.Id;
                projects.Add(new PlanningProject(project, scenarioOps.Operations));
                projectOrder.Add(project);
            }
            var manifest = new PlanningManifest(sc.Master, projects, projectOrder, [snapshot], rcStudies);
            PlanningResolvedCase resolved = PlanningExpansion.Expand(master, manifest, hasher).Cases[0];
            var derivation = resolved.Audit.Select(a => new StudyCaseAuditEntry(
                a.Project.StartsWith(ScenarioExpander.ProjectPrefix, StringComparison.Ordinal) ? "scenario" : "variation",
                sc.Variations.StageOf(a.Project), a)).ToArray();
            return new StudyCaseCase(scenario.Id, instant.Id, resolved, scenarioOps.Changes, derivation);
        }
        catch (PlanningConflictException ex) when (ex.Operation.StartsWith(ScenarioExpander.ProjectPrefix, StringComparison.Ordinal)
                                                   || ex.OtherOperation.StartsWith(ScenarioExpander.ProjectPrefix, StringComparison.Ordinal))
        {
            throw new StudyCaseException(ex.Message + " A scenario always applies after the variations; list the clashing variation in variationOrder.");
        }
    }

    private static void Validate(StudyCaseModel sc, BoundGroups? groups)
    {
        if (sc.Instants.Count == 0) throw new StudyCaseException("At least one instant is required.");
        if (sc.Studies.Count == 0) throw new StudyCaseException("At least one study is required.");
        Unique(sc.Instants.Select(i => i.Id), "instant");
        Unique(sc.Studies.Select(s => s.Id), "study");
        Unique(sc.Scenarios.Select(s => s.Id), "scenario");
        sc.Variations.Validate();
        foreach (OperationScenario s in sc.Scenarios)
        {
            if (s.Id == StudyCaseVersions.BaseScenario)
                throw new StudyCaseException($"The scenario name '{StudyCaseVersions.BaseScenario}' is reserved for a study that names no scenario.");
            foreach (string i in s.Instants ?? [])
                if (sc.Instants.All(x => x.Id != i))
                    throw new StudyCaseException($"Scenario '{s.Id}' names instant '{i}', which is not declared.");
            ScenarioExpander.Validate(s, groups);
        }
        foreach (StudyCaseStudy st in sc.Studies)
        {
            if (st.Scenario is not null && sc.Scenarios.All(s => s.Id != st.Scenario))
                throw new StudyCaseException($"Study '{st.Id}' names scenario '{st.Scenario}', which is not declared.");
            if (st.Definition is GroupSummaryStudy gs)
            {
                if (groups is null) throw new StudyCaseException($"Study '{gs.Id}': group-summary needs a groups document.");
                foreach (string g in gs.Groups ?? [])
                    if (!groups.TryGet(g, out _)) throw new StudyCaseException($"Study '{gs.Id}': group '{g}' is not defined in the groups document.");
            }
        }
    }

    private static void Unique(IEnumerable<string> ids, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
            if (!seen.Add(id)) throw new StudyCaseException($"Duplicate {what} ID '{id}'.");
    }
}
