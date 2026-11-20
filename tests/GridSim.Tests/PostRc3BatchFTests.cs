using GridSim.Core.Dynamics.Models;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.DynamicsStudy;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;
using static GridSim.Tests.DynamicsStudyTestSupport;

namespace GridSim.Tests;

/// <summary>Post-RC3 audit batch F (solved state, islands, out-of-service): DP-02, DP-01, AN-01, DP-23, AN-02, AN-03,
/// AN-06, AN-24, PR-03.</summary>
public class PostRc3BatchFTests
{
    private static GridModel With(GridModel m, IReadOnlyList<Generator>? gens = null, IReadOnlyList<Branch>? branches = null) => new()
    {
        BaseMva = m.BaseMva, Buses = m.Buses, Generators = gens ?? m.Generators, Branches = branches ?? m.Branches,
        Oltcs = m.Oltcs, Boundaries = m.Boundaries, FrequencyHz = m.FrequencyHz, Name = m.Name,
        AssetDynamics = m.AssetDynamics, Protections = m.Protections,
    };

    // ---- DP-02 ----
    [Fact]
    public void DP02_reverse_power_reads_the_solved_output_of_a_slack_unit()
    {
        GridModel m = GridSim.Core.IO.IeeeCases.Case14();
        // The slack unit is scheduled to export, but the other units carry far more than the demand, so the solve makes it motor.
        int slackBus = m.Buses.First(b => b.Type == BusType.Slack).Id;
        var gens = m.Generators.Select(g => g.Bus == slackBus ? g with { Pg = 50.0 } : g.Bus == 2 ? g with { Pg = 330.0 } : g).ToList();
        GridModel mm = With(m, gens);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(mm);
        Assert.True(r.Converged, "case14 with the shifted dispatch must solve");
        int si = gens.FindIndex(g => g.Bus == slackBus);
        double solved = SolvedGeneration.UnitOutput(mm, r, si).PMw;
        Assert.True(solved < -new ProtectionSettings().ReversePowerMw, $"solved {solved}");
        ProtectionReport rep = ProtectionAssessment.Assess(mm, r);
        RelayVerdict v = Assert.Single(rep.Operating, x => x.Device == "32" && x.LocationId == si);
        Assert.Equal(solved, v.Quantity);
        // A non-slack unit (scheduled positive) does not operate.
        Assert.DoesNotContain(rep.Operating, x => x.Device == "32" && x.LocationId != si);
    }

    // ---- DP-01 ----
    private static string Dyr() => File.ReadAllText(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.dyr")).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void DP01_a_pss_whose_exciter_was_left_out_is_left_out_too_and_the_document_loads()
    {
        string text = Dyr().Replace("     2 'IEEET1' 1   0.0  400.0", "     2 'IEEET1' 1   0.02  400.0", StringComparison.Ordinal);
        PsseParseResult raw = PsseRawParser.ParseFile(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));
        PsseDyrResult r = PsseDyrReader.Read(text, raw, "case14", new PsseDyrOptions { AllowUnmodelled = true });
        Assert.Contains(r.Notes, n => n.Contains("IEEET1 for machine '1' at bus 2", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("PSS1A for machine '1' at bus 2 (line 10): its exciter record (IEEET1, line 9) was left out", StringComparison.Ordinal));
        MachineDynamicsSet set = DynamicsModelCoverage.BindDocument(r.Json, "case14.dyr", Schemas, raw.Model);
        Assert.NotNull(set);
        // The coverage report is produced instead of an exception.
        DynamicsModelBinding b = DynamicsModelCoverage.Build(raw, text, "case14.dyr", Schemas, DynamicsUnmodelledPolicy.LeaveOut);
        Assert.Equal("APPROXIMATED", b.Report.Units.Single(u => u.Bus == 2).Status);
    }

    // ---- AN-01 ----
    // The N124 four-bus model, twice: buses/units 1-4 (island A) and 11-14 (island B), each with its own slack.
    private static GridModel TwoIslands()
    {
        Bus[] buses =
        {
            new(1, BusType.Slack, BaseKv: 132), new(2, BusType.Pv, BaseKv: 132), new(3, BusType.Pq, Pd: 150, Qd: 30, BaseKv: 132), new(4, BusType.Pv, BaseKv: 132),
            new(11, BusType.Slack, BaseKv: 132), new(12, BusType.Pv, BaseKv: 132), new(13, BusType.Pq, Pd: 150, Qd: 30, BaseKv: 132), new(14, BusType.Pv, BaseKv: 132),
        };
        Generator[] gens =
        {
            new(1, 40, Name: "G1", Pmax: 200, MBase: 100, H: 5, Type: "ccgt"), new(2, 60, Name: "G2", Pmax: 200, MBase: 100, H: 4, Type: "ccgt"),
            new(4, 50, Name: "G3", Pmax: 400, MBase: 300, H: 2, Type: "ccgt"),
            new(11, 40, Name: "H1", Pmax: 200, MBase: 100, H: 5, Type: "ccgt"), new(12, 60, Name: "H2", Pmax: 200, MBase: 100, H: 4, Type: "ccgt"),
            new(14, 50, Name: "H3", Pmax: 400, MBase: 300, H: 2, Type: "ccgt"),
        };
        Branch[] br =
        {
            new(1, 3, 0.01, 0.1, RateAMva: 250), new(2, 3, 0.01, 0.1, RateAMva: 250), new(1, 2, 0.01, 0.1, RateAMva: 250), new(4, 3, 0.01, 0.1, RateAMva: 250),
            new(11, 13, 0.01, 0.1, RateAMva: 250), new(12, 13, 0.01, 0.1, RateAMva: 250), new(11, 12, 0.01, 0.1, RateAMva: 250), new(14, 13, 0.01, 0.1, RateAMva: 250),
        };
        return new GridModel { Name = "two-islands", BaseMva = 100, Buses = buses, Generators = gens, Branches = br };
    }

    [Fact]
    public void AN01_lost_power_is_shared_only_inside_the_island_that_lost_it()
    {
        GridModel m = TwoIslands();
        var remaining = m.Generators.Where((_, i) => i != 1).ToList();
        GridModel reduced = m.CopyWith(generators: remaining);
        (GridModel shared, string note) = ContingencyAnalysis.ShareLostPower(reduced, 60.0, OutageDispatch.GovernorDroop, new[] { 2 });
        // Island A: G1 (100) and G3 (300) share 60 MW 1 : 3, exactly as in the single-island N124 case.
        Assert.Equal(40.0 + 60.0 * 100.0 / 400.0, shared.Generators[0].Pg, 12);
        Assert.Equal(50.0 + 60.0 * 300.0 / 400.0, shared.Generators[1].Pg, 12);
        for (int i = 2; i < 5; i++) Assert.Equal(reduced.Generators[i].Pg, shared.Generators[i].Pg);
        Assert.Equal("lost 60.0 MW shared by governor droop over 2 unit(s)", note);

        ContingencyResult r = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Generator, 1), dispatch: OutageDispatch.GovernorDroop);
        Assert.Equal("lost 60.0 MW shared by governor droop over 2 unit(s)", r.Note);
    }

    // ---- AN-03 ----
    [Fact]
    public void AN03_an_overload_no_generator_can_move_is_not_reported_as_applied()
    {
        // Slack unit at bus 1 only; the line 1-2 feeds a 100 MW load at bus 2 but is rated 50 MW. Every PTDF column of a
        // generator at the reference bus is zero, so nothing can relieve it.
        var m = new GridModel
        {
            Name = "radial", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 100) },
            Generators = new[] { new Generator(1, 100, Pmax: 300) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, RateAMva: 50) },
        };
        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(m);
        Assert.Equal(RedispatchOutcome.Infeasible, plan.Outcome);
        Assert.Equal(0.0, plan.MovedMw);
        Assert.Same(m, plan.Model);
    }

    // ---- AN-06 ----
    [Fact]
    public void AN06_a_screen_row_says_when_the_tap_loop_stopped_at_its_pass_cap()
    {
        GridModel m = new()
        {
            BaseMva = 100, Name = "oltc-screen",
            Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0), new Bus(2, BusType.Pq, Pd: 80, Qd: 30), new Bus(3, BusType.Pq, Pd: 40, Qd: 15) },
            Generators = new[] { new Generator(1, 0) },
            Branches = new[]
            {
                new Branch(1, 2, 0.008, 0.08, Name: "T1", TapRatio: 1.0),
                new Branch(2, 3, 0.02, 0.10, Name: "L23a"), new Branch(2, 3, 0.02, 0.10, Name: "L23b"),
            },
            Oltcs = new[] { new Oltc("T1", ControlsBus: 2, TargetVpu: 1.02, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125, DeadbandPu: 0.01) },
        };
        var capped = new OltcLoopPowerFlow(SolverSelector.CreateNewton(m), maxOltcPasses: 1);
        ContingencyResult hit = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Branch, 2), capped);
        Assert.True(hit.Converged);
        Assert.Contains("tap changers not settled after 1 pass(es)", hit.Note);
        ContingencyResult hitBase = ContingencyAnalysis.ScreenBaseCase(m, new OltcLoopPowerFlow(SolverSelector.CreateNewton(m), maxOltcPasses: 1));
        Assert.Contains("tap changers not settled", hitBase.Note);

        var full = ContingencyAnalysis.CreateStudySolver(m);
        ContingencyResult ok = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Branch, 2), full);
        Assert.Null(ok.Note);
    }

    // ---- AN-24 ----
    private static GridModel TwoOltcModel(bool t2InService, bool withT2)
    {
        var branches = new List<Branch>
        {
            new(1, 2, 0.008, 0.08, Name: "T1", TapRatio: 1.0),
            new(2, 3, 0.02, 0.10, Name: "L23"),
        };
        var oltcs = new List<Oltc> { new("T1", ControlsBus: 2, TargetVpu: 1.02, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125, DeadbandPu: 0.01) };
        if (withT2)
        {
            branches.Add(new Branch(1, 3, 0.008, 0.08, Name: "T2", TapRatio: 1.0, InService: t2InService));
            oltcs.Add(new Oltc("T2", ControlsBus: 3, TargetVpu: 1.02, MinTap: 0.9, MaxTap: 1.1, StepPu: 0.0125, DeadbandPu: 0.01));
        }
        return new GridModel
        {
            BaseMva = 100, Name = "two-oltc",
            Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0), new Bus(2, BusType.Pq, Pd: 80, Qd: 30), new Bus(3, BusType.Pq, Pd: 40, Qd: 15) },
            Generators = new[] { new Generator(1, 0) },
            Branches = branches, Oltcs = oltcs,
        };
    }

    [Fact]
    public void AN24_an_out_of_service_transformer_gets_no_tap_recommendation()
    {
        TapSchedule withDead = TapOptimizer.Flatten(TwoOltcModel(t2InService: false, withT2: true));
        TapSchedule alone = TapOptimizer.Flatten(TwoOltcModel(t2InService: true, withT2: false));
        TapSetting only = Assert.Single(withDead.Taps);
        Assert.Equal("T1", only.Transformer);
        Assert.Equal(new[] { "T2" }, withDead.SkippedOutOfService);
        Assert.Equal(alone.Taps[0].RecommendedTap, only.RecommendedTap);
        Assert.Equal(alone.ObjectiveAfter, withDead.ObjectiveAfter);
        Assert.Empty(alone.SkippedOutOfService);
    }

    // ---- DP-23 ----
    [Fact]
    public void DP23_a_slack_bus_with_units_of_both_signs_splits_only_the_solves_change()
    {
        GridModel m = new()
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 30) },
            Generators = new[] { new Generator(1, 120, MBase: 100), new Generator(1, -100, MBase: 100) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        };
        // schedP 20 MW, solved bus total 30 MW: each unit keeps its schedule and takes half (equal MBase) of the 10 MW.
        double a = SubtransientInit.MixedSignSlackBusP(m, m.Generators[0], 30.0, 20.0)!.Value;
        double b = SubtransientInit.MixedSignSlackBusP(m, m.Generators[1], 30.0, 20.0)!.Value;
        Assert.Equal(125.0, a, 12);
        Assert.Equal(-95.0, b, 12);
        Assert.Equal(30.0, a + b, 12);

        // The same units on a PV bus, or two generating units on the slack bus, keep the proportional rule (null).
        GridModel pv = m.CopyWith(buses: new[] { new Bus(1, BusType.Pv), m.Buses[1] });
        Assert.Null(SubtransientInit.MixedSignSlackBusP(pv, pv.Generators[0], 30.0, 20.0));
        GridModel same = m.CopyWith(generators: new[] { new Generator(1, 120, MBase: 100), new Generator(1, 100, MBase: 100) });
        Assert.Null(SubtransientInit.MixedSignSlackBusP(same, same.Generators[0], 30.0, 20.0));
    }

    // ---- PR-03 ----
    private static GridModel Abc(bool abInService)
        => new()
        {
            BaseMva = 100, Name = "abc",
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq), new Bus(3, BusType.Pq, Pd: 20, Qd: 5) },
            Generators = new[] { new Generator(1, 20, MBase: 100) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, InService: abInService), new Branch(2, 3, 0.0, 0.1, TapRatio: 1.05) },
        };

    private static (GridModel Live, PowerFlowResult Result) LiveBusA()
    {
        GridModel live = new() { BaseMva = 100, Name = "a", Buses = new[] { new Bus(1, BusType.Slack) }, Generators = new[] { new Generator(1, 0, MBase: 100) }, Branches = Array.Empty<Branch>() };
        return (live, new NewtonRaphsonPowerFlow().Solve(live));
    }

    [Fact]
    public void PR03_a_dead_island_with_no_source_no_longer_makes_the_fault_study_singular()
    {
        (GridModel live, PowerFlowResult res) = LiveBusA();
        Assert.True(res.Converged);
        FaultResult withDead = FaultAnalysis.ThreePhase(Abc(abInService: false), res, 1);
        FaultResult alone = FaultAnalysis.ThreePhase(live, res, 1);
        Assert.Equal(alone.FaultCurrentPu, withDead.FaultCurrentPu);
    }

    [Fact]
    public void PR03_a_missing_bus_still_joined_to_a_live_bus_is_refused_by_name()
    {
        (_, PowerFlowResult res) = LiveBusA();
        var ex = Assert.Throws<InvalidOperationException>(() => FaultAnalysis.ThreePhase(Abc(abInService: true), res, 1));
        Assert.Contains("bus 2", ex.Message);
        Assert.Contains("1-2", ex.Message);
    }

    // ---- AN-02 ----
    private static GridModel Case9WithDeadCopy()
    {
        GridModel m = GridSim.Core.IO.IeeeCases.Case9();
        var br = m.Branches.ToList();
        br.Add(br[3] with { InService = false });
        return With(m, branches: br);
    }

    [Fact]
    public void AN02_an_out_of_service_branch_is_not_screened_as_an_outage()
    {
        GridModel m = Case9WithDeadCopy();
        int live = GridSim.Core.IO.IeeeCases.Case9().Branches.Count;
        Assert.Equal(live, ContingencyAnalysis.ScreenBranches(m).Count);
        Assert.Equal(live, ContingencyAnalysis.ScreenBranchesParallel(m, maxDegreeOfParallelism: 1).Count);
        Assert.Equal(live, ContingencyAnalysis.ScreenBranchesBatched(m).Count);
        Assert.Equal(live, LossContingency.Sweep(m, new NewtonRaphsonPowerFlow().Solve(m)).Count);
        Assert.DoesNotContain(Enumerable.Range(0, live), i => !m.Branches[i].InService);
        // An explicit outage of the dead branch is refused by name.
        Assert.Throws<ArgumentException>(() => ContingencyAnalysis.ScreenBranchesParallel(m, branchIndices: new[] { live }));
    }

    [Fact]
    public void AN02_the_pruned_pair_screen_pairs_real_branches_only_and_keeps_the_branch_indices()
    {
        GridModel withDead = Case9WithDeadCopy();
        GridModel plain = GridSim.Core.IO.IeeeCases.Case9();
        var a = ContingencyAnalysis.ScreenNMinus2(withDead, candidateCount: 4, maxDegreeOfParallelism: 1);
        var b = ContingencyAnalysis.ScreenNMinus2(plain, candidateCount: 4, maxDegreeOfParallelism: 1);
        Assert.Equal(b.Count, a.Count);
        Assert.Equal(b.Select(r => r.Outage), a.Select(r => r.Outage));
        var ex = ContingencyAnalysis.ScreenNMinus2(withDead, candidateCount: 4, exhaustive: true, maxDegreeOfParallelism: 1);
        Assert.Equal(ContingencyAnalysis.ScreenNMinus2(plain, candidateCount: 4, exhaustive: true, maxDegreeOfParallelism: 1).Count, ex.Count);
    }
}
