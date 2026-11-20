using System.Text;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-01 Core: grouping sidecar, scenario expansion, the study-case expansion over the unchanged RC3 planning
/// expansion, and the group-summary mode against hand-computed area figures.
/// <para>W2 oracle (lossless three-bus case, see <see cref="R401Cases.ThreeBus"/>): bus 3 holds a 40 MW unit and a 60 MW
/// load, so area B = {3} has net injection 40 - 60 MW (scaled load: 40 - 60 s) and, with no loss anywhere, area A = {1, 2}
/// sends exactly that many MW into B whatever A's own load is: the tie flow out of A is the negative of B's net
/// injection, and A's solved net injection equals it.</para>
/// </summary>
public sealed class R401StudyCaseCoreTests : IDisposable
{
    private readonly string _root = R401Cases.NewRoot();
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static DateTimeOffset T(string text) => PlanningTime.Parse(text, "test");
    private static byte[] B(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Groups(string bundle, string groups) =>
        "{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"" + bundle + "\" }, \"groups\": [ " + groups + " ] }";

    private const string AreaA = "{ \"id\": \"areaA\", \"kind\": \"area\", \"buses\": [1, 2] }";
    private const string AreaB = "{ \"id\": \"areaB\", \"kind\": \"area\", \"buses\": [3] }";

    private static BoundGroups Bind(string dir, string groups)
    {
        PlanningMaster master = R401Cases.Master(dir);
        return BoundGroups.Bind(ModelGroupsDocument.Parse(B(Groups(master.BundleSha256, groups))), master.Model);
    }

    // ---- Groups ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Groups_resolve_in_model_order_and_a_feeder_keeps_its_listed_order()
    {
        string dir = R401Cases.ThreeBus(_root);
        BoundGroups g = Bind(dir, "{ \"id\": \"areaA\", \"kind\": \"area\", \"buses\": [2, 1], \"generators\": [ { \"index\": 0, \"bus\": 1 } ], \"branches\": [ { \"index\": 1, \"from\": 2, \"to\": 3 }, { \"index\": 0, \"from\": 1, \"to\": 2 } ] }, " +
            "{ \"id\": \"f1\", \"kind\": \"feeder\", \"buses\": [3, 2], \"head\": { \"element\": \"bus\", \"bus\": 3 } }, " +
            "{ \"id\": \"tagged\", \"kind\": \"free\", \"buses\": [2, 3] }");
        Assert.Equal([1, 2], g.Get("areaA").BusIds);
        Assert.Equal([0, 1], g.Get("areaA").BranchIndices);
        Assert.Equal([0], g.Get("areaA").GeneratorIndices);
        Assert.Equal([3, 2], g.Get("f1").BusIds);                  // feeder: listed order
        Assert.Equal([2, 3], g.Get("tagged").BusIds);              // free groups may overlap area and feeder groups
        Assert.Equal(["areaA", "f1", "tagged"], g.Groups.Select(x => x.Id));
    }

    [Theory]
    [InlineData("{ \"id\": \"a\", \"kind\": \"area\", \"buses\": [1, 2] }, { \"id\": \"b\", \"kind\": \"area\", \"buses\": [2, 3] }", "Bus 2 is in both area groups 'a' and 'b'")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"owner\", \"branches\": [ { \"index\": 0, \"from\": 1, \"to\": 2 } ] }, { \"id\": \"b\", \"kind\": \"owner\", \"branches\": [ { \"index\": 0, \"from\": 1, \"to\": 2 } ] }", "Branch 0 is in both owner groups 'a' and 'b'")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"zone\", \"generators\": [ { \"index\": 1, \"bus\": 3 } ] }, { \"id\": \"b\", \"kind\": \"zone\", \"generators\": [ { \"index\": 1, \"bus\": 3 } ] }", "Generator 1 is in both zone groups 'a' and 'b'")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"area\", \"buses\": [1, 9] }", "bus 9 is not in the case")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"area\", \"buses\": [1, 1] }", "bus 1 is listed twice")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"area\", \"generators\": [ { \"index\": 1, \"bus\": 2 } ] }", "generator 1 is at bus 3 in the case, not 2")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"area\", \"generators\": [ { \"index\": 5, \"bus\": 2 } ] }", "generator index 5 is outside the fleet")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"area\", \"branches\": [ { \"index\": 1, \"from\": 1, \"to\": 2 } ] }", "branch 1 joins 2-3 in the case, not 1-2")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"area\", \"branches\": [ { \"index\": 7, \"from\": 1, \"to\": 2 } ] }", "branch index 7 is outside the case")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"area\" }", "has no members")]
    [InlineData("{ \"id\": \"a\", \"kind\": \"free\", \"buses\": [1] }, { \"id\": \"a\", \"kind\": \"free\", \"buses\": [2] }", "Duplicate group ID 'a'")]
    [InlineData("{ \"id\": \"f\", \"kind\": \"feeder\", \"buses\": [1, 2], \"head\": { \"element\": \"bus\", \"bus\": 3 } }", "the head (bus) is not a member")]
    [InlineData("{ \"id\": \"f\", \"kind\": \"feeder\", \"buses\": [1, 2] }", "a feeder needs a head element")]
    [InlineData("{ \"id\": \"f\", \"kind\": \"area\", \"buses\": [1], \"head\": { \"element\": \"bus\", \"bus\": 1 } }", "only a feeder has a head element")]
    public void Unresolved_duplicate_and_inconsistent_membership_is_refused_naming_the_member(string groups, string expected)
    {
        string dir = R401Cases.ThreeBus(_root);
        StudyCaseException ex = Assert.Throws<StudyCaseException>(() => Bind(dir, groups));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("{ \"manifestVersion\": \"gridsim-groups/2\", \"case\": { \"sha256\": \"x\" }, \"groups\": [] }", "manifestVersion must be")]
    [InlineData("{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"x\" } }", "required property \"groups\" is missing")]
    [InlineData("{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"x\" }, \"groups\": [], \"extra\": 1 }", "unknown property \"extra\"")]
    [InlineData("{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"x\", \"sha256\": \"y\" }, \"groups\": [] }", "duplicate property \"sha256\"")]
    [InlineData("{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"x\" }, \"groups\": [ { \"id\": \"a\", \"kind\": \"region\" } ] }", "kind must be area, zone, owner, feeder or free")]
    [InlineData("{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"x\" }, \"groups\": [ { \"id\": \"a\", \"kind\": \"area\", \"buses\": [1.5] } ] }", "must be an integer")]
    [InlineData("{ \"manifestVersion\": \"gridsim-groups/1\", \"case\": { \"sha256\": \"x\" }, \"groups\": [ { \"id\": \"a b\", \"kind\": \"area\" } ] }", "must be 1-64 characters")]
    [InlineData("{ not json", "not valid JSON")]
    public void Groups_document_is_strict(string text, string expected)
    {
        StudyCaseException ex = Assert.Throws<StudyCaseException>(() => ModelGroupsDocument.Parse(B(text)));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Grouping_is_a_sidecar_and_does_not_touch_the_model()
    {
        string dir = R401Cases.ThreeBus(_root);
        PlanningMaster master = R401Cases.Master(dir);
        string before = PlanningStudyExpansionTests.Hash(master.Model);
        BoundGroups.Bind(ModelGroupsDocument.Parse(B(Groups(master.BundleSha256, AreaA + "," + AreaB))), master.Model);
        Assert.Equal(before, PlanningStudyExpansionTests.Hash(master.Model));
        Assert.DoesNotContain(typeof(GridModel).GetProperties(), p => p.Name.Contains("Group", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Scenario expansion ----------------------------------------------------------------------------------------

    [Fact]
    public void Scale_set_and_availability_expand_to_exact_operations()
    {
        string dir = R401Cases.ThreeBus(_root);
        PlanningMaster master = R401Cases.Master(dir);
        BoundGroups groups = Bind(dir, AreaA + "," + AreaB + ", { \"id\": \"ties\", \"kind\": \"free\", \"branches\": [ { \"index\": 1, \"from\": 2, \"to\": 3 }, { \"index\": 2, \"from\": 1, \"to\": 3 } ] }");
        var scenario = new OperationScenario("s", null,
        [
            new("a-load", "areaA", ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(1.25)),
            new("a-again", "areaA", ScenarioQuantity.PdMw, ScenarioAction.Scale, PlanningValue.Of(2)),
            new("b-gen", "areaB", ScenarioQuantity.Generation, ScenarioAction.Set, PlanningValue.Of(30)),
            new("open-ties", "ties", ScenarioQuantity.InService, ScenarioAction.Set, PlanningValue.Of(false)),
        ]);
        ScenarioExpansion x = ScenarioExpander.Expand(master.Model, groups, scenario, T("2027-01-01T00:00:00Z"));

        Assert.Equal(["a-load.pdMw.1", "a-load.qdMvar.1", "a-load.pdMw.2", "a-load.qdMvar.2", "a-again.pdMw.1", "a-again.pdMw.2",
            "b-gen.pgMw.1", "open-ties.inService.1", "open-ties.inService.2"], x.Operations.Select(o => o.Id));
        // Hand values: bus 2 demand 100 x 1.25 = 125, then x 2 = 250 (the second operation starts from the first's value).
        PlanningOperation pd2 = x.Operations.Single(o => o.Id == "a-load.pdMw.2");
        Assert.Equal((100.0, 125.0), (pd2.ExpectedPrior.Number!.Value, pd2.Value.Number!.Value));
        PlanningOperation again = x.Operations.Single(o => o.Id == "a-again.pdMw.2");
        Assert.Equal((125.0, 250.0), (again.ExpectedPrior.Number!.Value, again.Value.Number!.Value));
        Assert.Equal((20.0, 25.0), (x.Operations.Single(o => o.Id == "a-load.qdMvar.2").ExpectedPrior.Number!.Value, x.Operations.Single(o => o.Id == "a-load.qdMvar.2").Value.Number!.Value));
        Assert.Equal(30.0, x.Operations.Single(o => o.Id == "b-gen.pgMw.1").Value.Number!.Value);
        PlanningOperation cut = x.Operations.Single(o => o.Id == "open-ties.inService.2");
        Assert.Equal((true, false), (cut.ExpectedPrior.Flag!.Value, cut.Value.Flag!.Value));
        Assert.Equal("branch 2 (1-3)", cut.Target.Id);
        Assert.All(x.Operations, o => Assert.Equal((T("2027-01-01T00:00:00Z"), (DateTimeOffset?)null), (o.EffectiveFrom, o.EffectiveTo)));
        Assert.Equal(x.Operations.Count, x.Changes.Count);
        Assert.Equal(("pdMw", "scale 1.25", "100", "125"), (x.Changes[2].Field, x.Changes[2].Action, x.Changes[2].Before, x.Changes[2].After));
    }

    [Fact]
    public void Scenario_validation_refuses_bad_operations_before_any_expansion()
    {
        string dir = R401Cases.ThreeBus(_root);
        BoundGroups groups = Bind(dir, AreaA + "," + AreaB + ", { \"id\": \"lines\", \"kind\": \"free\", \"branches\": [ { \"index\": 0, \"from\": 1, \"to\": 2 } ] }");
        PlanningMaster master = R401Cases.Master(dir);
        DateTimeOffset t = T("2027-01-01T00:00:00Z");
        OperationScenario S(params ScenarioOperation[] ops) => new("s", null, ops);

        Assert.Contains("group 'nope' is not defined", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, groups,
            S(new ScenarioOperation("o", "nope", ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(1))), t)).Message);
        Assert.Contains("no groups document", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, null,
            S(new ScenarioOperation("o", "areaA", ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(1))), t)).Message);
        Assert.Contains("cannot be negative", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, groups,
            S(new ScenarioOperation("o", "areaA", ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(-1))), t)).Message);
        Assert.Contains("scale only", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, groups,
            S(new ScenarioOperation("o", "areaA", ScenarioQuantity.Load, ScenarioAction.Set, PlanningValue.Of(1))), t)).Message);
        Assert.Contains("availability", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, groups,
            S(new ScenarioOperation("o", "lines", ScenarioQuantity.InService, ScenarioAction.Scale, PlanningValue.Of(1))), t)).Message);
        Assert.Contains("finite number", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, groups,
            S(new ScenarioOperation("o", "areaA", ScenarioQuantity.PdMw, ScenarioAction.Set, PlanningValue.Of(true))), t)).Message);
        Assert.Contains("duplicate operation ID 'o'", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, groups,
            S(new ScenarioOperation("o", "areaA", ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(1)),
              new ScenarioOperation("o", "areaB", ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(1))), t)).Message);
        // A group that holds no element of the kind: area B has no branch.
        Assert.Contains("has no branch to switch", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, groups,
            S(new ScenarioOperation("o", "areaB", ScenarioQuantity.InService, ScenarioAction.Set, PlanningValue.Of(false))), t)).Message);
        Assert.Contains("has no generating unit", Assert.Throws<StudyCaseException>(() => ScenarioExpander.Expand(master.Model, groups,
            S(new ScenarioOperation("o", "lines", ScenarioQuantity.Generation, ScenarioAction.Scale, PlanningValue.Of(1))), t)).Message);
    }

    // ---- Study-case expansion over the unchanged RC3 expansion --------------------------------------------------------

    private static PlanningStudyDefinition Study(string id = "n1") => new TransmissionSecurityPlanningStudy(id,
        [new(0, 1, 2), new(1, 1, 2)], 0.9, 1.1, GridSim.Core.PowerFlow.PostFaultRating.RateA, 20, null);

    private static PlanningOperation OpenOne(string id, DateTimeOffset from, DateTimeOffset? to = null) => new(id,
        new(PlanningElementKind.Branch, 0, 1, 2, null, null), PlanningField.BranchInService, PlanningValue.Of(true), PlanningValue.Of(false), from, to);

    private static StudyCaseModel TwoBusCase(PlanningMaster master, VariationSet? variations = null, IReadOnlyList<OperationScenario>? scenarios = null,
        string? studyScenario = "peak")
    {
        return new StudyCaseModel(new PlanningMasterReference("two-bus", master.BundleSha256, master.Interpretation), null,
            [new("before", T("2026-06-01T00:00:00Z")), new("after", T("2027-02-01T00:00:00Z"))],
            variations ?? new VariationSet([new("outage-one", null, "2027 outage", [OpenOne("open-one", T("2027-01-01T00:00:00Z"))])], []),
            scenarios ?? [new OperationScenario("peak", null, [new("all-load", null, ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(1.0))])],
            [new StudyCaseStudy(studyScenario, Study())]);
    }

    [Fact]
    public void W1_variation_and_scenario_resolve_to_the_dated_cases_with_the_pre_effective_operation_recorded()
    {
        string dir = R401Cases.TwoBus(_root);
        PlanningMaster master = R401Cases.Master(dir);
        string masterCase = PlanningStudyExpansionTests.Hash(master.Model);
        StudyCaseExpansionResult x = StudyCaseExpansion.Expand(master, TwoBusCase(master), null, PlanningStudyExpansionTests.Hash);

        Assert.Equal(["peak.before", "peak.after"], x.Cases.Select(c => c.Case.Snapshot.Id));
        Assert.Equal(["peak.before/n1", "peak.after/n1"], x.Jobs.Select(j => j.Id));
        Assert.Equal([1, 2], x.Jobs.Select(j => j.Ordinal));

        StudyCaseCase before = x.Cases[0], after = x.Cases[1];
        PlanningAuditEntry open = before.Derivation.Single(d => d.Entry.Operation == "open-one").Entry;
        Assert.Equal(PlanningDisposition.NotYetEffective, open.Disposition);
        Assert.Equal("variation", before.Derivation.Single(d => d.Entry.Operation == "open-one").Origin);
        Assert.Equal("2027 outage", before.Derivation.Single(d => d.Entry.Operation == "open-one").ExpansionStage);
        Assert.Equal(PlanningDisposition.Applied, after.Derivation.Single(d => d.Entry.Operation == "open-one").Entry.Disposition);
        Assert.All(before.Derivation.Where(d => d.Origin == "scenario"), d => Assert.Equal(PlanningDisposition.Applied, d.Entry.Disposition));

        // "load x 1.0" is exact: the resolved demand is the master's, bit for bit, at both instants.
        Assert.Equal((80.0, 30.0), (before.Case.Model.Buses[1].Pd, before.Case.Model.Buses[1].Qd));
        Assert.Equal((80.0, 30.0), (after.Case.Model.Buses[1].Pd, after.Case.Model.Buses[1].Qd));
        Assert.True(before.Case.Model.Branches[0].InService);
        Assert.False(after.Case.Model.Branches[0].InService);
        Assert.True(before.Case.Validation.Valid && after.Case.Validation.Valid);

        // The master is untouched.
        Assert.Equal(masterCase, PlanningStudyExpansionTests.Hash(master.Model));

        // Study-case jobs run the unchanged RC3 engine: with one branch out the receiving voltage is the closed form.
        PlanningJobResult run = StudyCaseModes.RunJob(x.Jobs[1], null);
        var evidence = Assert.IsType<TransmissionSecurityPlanningEvidence>(run.Evidence);
        double v = evidence.Result!.BaseCase.Limits.Single(l => l.Kind == "Voltage" && l.Index == 2).Observed;
        Assert.InRange(Math.Abs(v - R401Cases.ReceivingVoltage(0.2, 80, 30)), 0, 1e-7);   // 0.9194904559886774
        PlanningJobResult intact = StudyCaseModes.RunJob(x.Jobs[0], null);
        double v0 = ((TransmissionSecurityPlanningEvidence)intact.Evidence!).Result!.BaseCase.Limits.Single(l => l.Kind == "Voltage" && l.Index == 2).Observed;
        Assert.InRange(Math.Abs(v0 - R401Cases.ReceivingVoltage(0.1, 80, 30)), 0, 1e-7);  // 0.965488891873777
    }

    [Fact]
    public void Expansion_is_repeatable_to_the_bit_and_a_scenario_applies_after_the_variations()
    {
        string dir = R401Cases.TwoBus(_root);
        PlanningMaster master = R401Cases.Master(dir);
        PlanningOperation grow = new("grow", new(PlanningElementKind.Load, null, null, null, 2, null), PlanningField.LoadPdMw,
            PlanningValue.Of(80.0), PlanningValue.Of(100.0), T("2027-01-01T00:00:00Z"), null);
        var scenario = new OperationScenario("peak", null, [new("load", null, ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(1.5))]);

        // Listed in the variation order: the scenario scales the variation's value (100 x 1.5 = 150), not the master's.
        StudyCaseModel ordered = TwoBusCase(master, new VariationSet([new("growth", null, null, [grow])], ["growth"]), [scenario]);
        StudyCaseExpansionResult a = StudyCaseExpansion.Expand(master, ordered, null, PlanningStudyExpansionTests.Hash);
        StudyCaseExpansionResult b = StudyCaseExpansion.Expand(master, ordered, null, PlanningStudyExpansionTests.Hash);
        Assert.Equal(120.0, a.Cases[0].Case.Model.Buses[1].Pd);   // before the variation: 80 x 1.5
        Assert.Equal(150.0, a.Cases[1].Case.Model.Buses[1].Pd);   // after: 100 x 1.5
        Assert.Equal(a.Cases.Select(c => c.Case.CaseSha256), b.Cases.Select(c => c.Case.CaseSha256));
        Assert.Equal(a.Jobs.Select(j => j.RequestFingerprint), b.Jobs.Select(j => j.RequestFingerprint));
        Assert.NotEqual(a.Cases[0].Case.DerivationSha256, a.Cases[1].Case.DerivationSha256);

        // Not listed: the scenario and the variation both change pdMw while active, and the refusal says what to do.
        StudyCaseModel unordered = TwoBusCase(master, new VariationSet([new("growth", null, null, [grow])], []), [scenario]);
        StudyCaseException ex = Assert.Throws<StudyCaseException>(() => StudyCaseExpansion.Expand(master, unordered, null, PlanningStudyExpansionTests.Hash));
        Assert.Contains("growth/grow", ex.Message);
        Assert.Contains("scenario.peak/load.pdMw.2", ex.Message);
        Assert.Contains("variationOrder", ex.Message);
    }

    [Fact]
    public void W4_two_operations_at_one_instant_on_one_target_are_refused_naming_both()
    {
        string dir = R401Cases.TwoBus(_root);
        PlanningMaster master = R401Cases.Master(dir);
        var variations = new VariationSet(
        [
            new("first", null, null, [OpenOne("open-a", T("2027-01-01T00:00:00Z"))]),
            new("second", null, null, [OpenOne("open-b", T("2027-01-01T00:00:00Z"))]),
        ], []);
        var ex = Assert.Throws<PlanningConflictException>(() => StudyCaseExpansion.Expand(master,
            TwoBusCase(master, variations, [], studyScenario: null), null, PlanningStudyExpansionTests.Hash));
        Assert.Equal(("first/open-a", "second/open-b"), (ex.Operation, ex.OtherOperation));
        Assert.Contains("both change inService of branch 0 (1-2) while active", ex.Message);

        // Listing both in the order makes the second a shadowed change: its expected prior (true) no longer holds.
        var ordered = Assert.Throws<PlanningConflictException>(() => StudyCaseExpansion.Expand(master,
            TwoBusCase(master, variations with { Order = ["first", "second"] }, [], studyScenario: null), null, PlanningStudyExpansionTests.Hash));
        Assert.Contains("shadowed", ordered.Message);
    }

    [Fact]
    public void Study_case_refusals_happen_before_any_job()
    {
        string dir = R401Cases.TwoBus(_root);
        PlanningMaster master = R401Cases.Master(dir);
        StudyCaseModel good = TwoBusCase(master);
        void Refuses(StudyCaseModel m, string text) => Assert.Contains(text,
            Assert.Throws<StudyCaseException>(() => StudyCaseExpansion.Expand(master, m, null, PlanningStudyExpansionTests.Hash)).Message);

        Refuses(good with { Studies = [new StudyCaseStudy("nope", Study())] }, "names scenario 'nope', which is not declared");
        Refuses(good with { Scenarios = [new OperationScenario("base", null, [])] }, "reserved");
        Refuses(good with { Instants = [] }, "At least one instant");
        Refuses(good with { Studies = [] }, "At least one study");
        Refuses(good with { Instants = [good.Instants[0], good.Instants[0]] }, "Duplicate instant ID");
        Refuses(good with { Variations = new VariationSet([], ["ghost"]) }, "not a declared variation");
        Refuses(good with { Scenarios = [new OperationScenario("peak", null, [], ["ghost"])] }, "names instant 'ghost'");
        Refuses(good with { Studies = [new StudyCaseStudy("peak", new GroupSummaryStudy("g", null, false))] }, "needs a groups document");
    }

    // ---- W2 grouping closed form ------------------------------------------------------------------------------------

    private static GroupSummaryRow Row(PlanningJobResult r, string group) =>
        Assert.IsType<GroupSummaryEvidence>(r.Evidence).Groups.Single(g => g.Group == group);

    [Theory]
    [InlineData(1.25, 1.0, 20.0)]   // scale area A load: B is unchanged, so A still sends 60 - 40 = 20 MW to B
    [InlineData(1.0, 1.25, 35.0)]   // scale area B load: B imports 75 - 40 = 35 MW
    [InlineData(1.25, 1.25, 35.0)]
    public void W2_area_interchange_equals_net_injection_and_tie_flow_by_hand(double scaleA, double scaleB, double handExportMw)
    {
        string dir = R401Cases.ThreeBus(_root);
        PlanningMaster master = R401Cases.Master(dir);
        BoundGroups groups = Bind(dir, AreaA + "," + AreaB);
        var study = new StudyCaseModel(new PlanningMasterReference("three-bus", master.BundleSha256, master.Interpretation), null,
            [new("t", T("2027-01-01T00:00:00Z"))], VariationSet.Empty,
            [new OperationScenario("grow", null,
            [
                new("a", "areaA", ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(scaleA)),
                new("b", "areaB", ScenarioQuantity.Load, ScenarioAction.Scale, PlanningValue.Of(scaleB)),
            ])],
            [new StudyCaseStudy("grow", new GroupSummaryStudy("sum", null, Solve: true))]);
        StudyCaseExpansionResult x = StudyCaseExpansion.Expand(master, study, groups, PlanningStudyExpansionTests.Hash);
        PlanningJobResult result = StudyCaseModes.RunJob(x.Jobs.Single(), groups);
        Assert.Equal(("SUMMARISED", null), (result.Status, result.Reason));

        GroupSummaryRow a = Row(result, "areaA"), b = Row(result, "areaB");
        const double Tol = 1e-4;   // MW; Newton stops at 1e-8 pu on 100 MVA (1e-6 MW) and there is no loss
        // Scheduled figures are data sums, exact: A's demand is bus 2 only (bus 1 has none), B's is bus 3.
        Assert.Equal((100 * scaleA, 20 * scaleA, 120.0, 120.0 - 100 * scaleA), (a.LoadMw, a.LoadMvar, a.ScheduledGenerationMw, a.ScheduledBalanceMw));
        Assert.Equal((60 * scaleB, 40.0, 40.0 - 60 * scaleB), (b.LoadMw, b.ScheduledGenerationMw, b.ScheduledBalanceMw));
        // B's net injection is 40 - 60 scaleB, so A exports the opposite; no loss anywhere.
        Assert.InRange(Math.Abs(b.SolvedNetInjectionMw!.Value - (40 - 60 * scaleB)), 0, Tol);
        Assert.InRange(Math.Abs(b.TieFlowMw!.Value - (40 - 60 * scaleB)), 0, Tol);
        Assert.InRange(Math.Abs(a.TieFlowMw!.Value - handExportMw), 0, Tol);
        Assert.InRange(Math.Abs(a.SolvedNetInjectionMw!.Value - handExportMw), 0, Tol);
        Assert.InRange(Math.Abs(a.InternalLossMw!.Value), 0, Tol);
        // The solved state closes: net injection = tie flow + internal loss + shunt loss.
        foreach (GroupSummaryRow g in new[] { a, b })
            Assert.InRange(Math.Abs(g.SolvedNetInjectionMw!.Value - (g.TieFlowMw!.Value + g.InternalLossMw!.Value + g.ShuntLossMw!.Value)), 0, Tol);
        // The two areas partition the network, so interchanges cancel.
        Assert.InRange(Math.Abs(a.TieFlowMw!.Value + b.TieFlowMw!.Value), 0, Tol);
    }

    [Fact]
    public void Group_summary_without_a_solve_gives_the_scheduled_figures_only()
    {
        string dir = R401Cases.ThreeBus(_root);
        PlanningMaster master = R401Cases.Master(dir);
        BoundGroups groups = Bind(dir, AreaA + "," + AreaB);
        var job = new GroupSummaryMode().Run("j", master.Model, new GroupSummaryStudy("s", ["areaB"], false), groups);
        var ev = Assert.IsType<GroupSummaryEvidence>(job.Evidence);
        GroupSummaryRow row = Assert.Single(ev.Groups);
        Assert.Equal(("areaB", 1, 1, 60.0, 10.0, 40.0, -20.0), (row.Group, row.Buses, row.Generators, row.LoadMw, row.LoadMvar, row.ScheduledGenerationMw, row.ScheduledBalanceMw));
        Assert.Null(row.SolvedNetInjectionMw);
        Assert.Null(row.TieFlowMw);
    }
}
