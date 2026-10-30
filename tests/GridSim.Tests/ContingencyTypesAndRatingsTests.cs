using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F20 (October 2026): generator and bus outages beside branch outages, and the short-term / emergency ratings
/// (RateB / RateC) read from MATPOWER and JSON and used, when asked, for post-fault loading. Grid rule: NETS SQSS v2.10
/// (docs/reference S036, 8 April 2025): pre-fault rating before a fault (2.9.1), no unacceptable overloading (loading
/// beyond the specified time-related capability) after a secured event (5.1.8); secured events include a single
/// generating unit (5.1.2) and a section of busbar (5.1.5).
/// </summary>
public class ContingencyTypesAndRatingsTests
{
    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    private static long B(double v) => BitConverter.DoubleToInt64Bits(v);

    private static void AssertSameResult(string label, ContingencyResult a, ContingencyResult b)
    {
        Assert.Equal(a.Outage, b.Outage);
        Assert.Equal(a.Converged, b.Converged);
        Assert.True(B(a.MaxBranchLoadingPercent) == B(b.MaxBranchLoadingPercent), $"{label}: max loading {a.MaxBranchLoadingPercent:R} vs {b.MaxBranchLoadingPercent:R}");
        Assert.True(B(a.WorstBoundaryLoadingPercent) == B(b.WorstBoundaryLoadingPercent), $"{label}: boundary");
        Assert.True(B(a.MinVm) == B(b.MinVm) && B(a.MaxVm) == B(b.MaxVm), $"{label}: voltages");
        Assert.Equal(a.Violations, b.Violations);
        Assert.Equal(a.Details.Count, b.Details.Count);
        for (int i = 0; i < a.Details.Count; i++)
        {
            Assert.Equal(a.Details[i].Kind, b.Details[i].Kind);
            Assert.Equal(a.Details[i].Element, b.Details[i].Element);
            Assert.True(B(a.Details[i].Value) == B(b.Details[i].Value), $"{label}: detail {i}");
        }
    }

    // ---- Ratings read and carried ----------------------------------------------------------------------------------

    [Fact]
    public void Matpower_rate_b_and_c_are_read_and_round_trip_through_both_writers()
    {
        GridModel m = Case("lib/case24_ieee_rts.m");
        Branch first = m.Branches[0];   // 1-2: 175 / 250 / 200 in the file
        Assert.Equal(175.0, first.RateAMva);
        Assert.Equal(250.0, first.RateBMva);
        Assert.Equal(200.0, first.RateCMva);
        Assert.Equal(208.0, m.Branches[1].RateBMva);
        Assert.Equal(220.0, m.Branches[1].RateCMva);

        MatpowerWriteResult w = MatpowerWriter.Write(m);
        GridModel back = MatpowerParser.ParseWithDiagnostics(w.Text, "rt").Model;
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Assert.Equal(B(m.Branches[i].RateBMva), B(back.Branches[i].RateBMva));
            Assert.Equal(B(m.Branches[i].RateCMva), B(back.Branches[i].RateCMva));
        }

        string dir = Path.Combine(Path.GetTempPath(), "gridsim-f20-" + Guid.NewGuid().ToString("N"));
        try
        {
            JsonCaseWriter.WriteDirectory(m, dir);
            GridModel json = JsonGridLoader.LoadDirectory(dir);
            for (int i = 0; i < m.Branches.Count; i++)
            {
                Assert.Equal(B(m.Branches[i].RateBMva), B(json.Branches[i].RateBMva));
                Assert.Equal(B(m.Branches[i].RateCMva), B(json.Branches[i].RateCMva));
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Json_rating_b_and_c_are_per_circuit_like_rating_a()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-f20j-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "system.json"), """{ "name": "t", "baseMva": 100, "frequencyHz": 50 }""");
            File.WriteAllText(Path.Combine(dir, "buses.json"), """[ { "id": 1, "baseKv": 132, "type": "slack" }, { "id": 2, "baseKv": 132 } ]""");
            File.WriteAllText(Path.Combine(dir, "generation.json"), """[ { "name": "G", "bus": 1, "capacityMva": 100, "outputMw": 10, "role": "slack" } ]""");
            File.WriteAllText(Path.Combine(dir, "demand.json"), """[ { "bus": 2, "pMw": 10 } ]""");
            File.WriteAllText(Path.Combine(dir, "transmission.json"),
                """[ { "name": "L", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1, "ratingMva": 50, "ratingBMva": 60, "ratingCMva": 70, "circuits": 2 } ]""");
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            Assert.Equal(100.0, m.Branches[0].RateAMva);
            Assert.Equal(120.0, m.Branches[0].RateBMva);
            Assert.Equal(140.0, m.Branches[0].RateCMva);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- Post-fault rating ----------------------------------------------------------------------------------------

    [Fact]
    public void Rating_a_is_the_default_and_leaves_every_outage_result_bit_identical()
    {
        foreach (string rel in new[] { "lib/case24_ieee_rts.m", "lib/case30.m" })
        {
            GridModel m = Case(rel);
            IReadOnlyList<ContingencyResult> a = ContingencyAnalysis.ScreenBranches(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
            IReadOnlyList<ContingencyResult> b = ContingencyAnalysis.ScreenBranches(m, ContingencyAnalysis.CreateStudySolver(m),
                ContingencyAnalysis.StudyOptions with { PostFaultRating = PostFaultRating.RateA });
            // The element-outage entry point screens a branch outage with the very same code.
            IReadOnlyList<ContingencyResult> c = ContingencyAnalysis.ScreenElementOutages(m, ContingencyAnalysis.SingleOutages(m, true, false, false),
                ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
            Assert.Equal(m.Branches.Count, c.Count);
            for (int i = 0; i < a.Count; i++)
            {
                AssertSameResult($"{rel} #{i} explicit A", a[i], b[i]);
                AssertSameResult($"{rel} #{i} element entry", a[i], c[i]);
                Assert.Equal(OutageKind.Branch, c[i].Kind);
                Assert.Equal(0.0, c[i].LostLoadMw);
                Assert.Null(c[i].Note);
            }
        }
    }

    [Theory]
    [InlineData(PostFaultRating.RateB)]
    [InlineData(PostFaultRating.RateC)]
    public void An_emergency_rating_rescales_every_outage_loading_and_leaves_the_base_case_on_rating_a(PostFaultRating which)
    {
        GridModel m = Case("lib/case24_ieee_rts.m");
        PowerFlowOptions opt = ContingencyAnalysis.StudyOptions with { PostFaultRating = which };
        IReadOnlyList<ContingencyResult> a = ContingencyAnalysis.ScreenBranches(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        IReadOnlyList<ContingencyResult> e = ContingencyAnalysis.ScreenBranches(m, ContingencyAnalysis.CreateStudySolver(m), opt);
        int overA = 0, overE = 0;
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Converged, e[i].Converged);
            if (!a[i].Converged) continue;
            // Every rating B / C in case24 is at least its rating A, so no emergency loading exceeds the A loading.
            Assert.True(e[i].MaxBranchLoadingPercent <= a[i].MaxBranchLoadingPercent + 1e-9, $"outage {i}");
            overA += a[i].Details.Count(d => d.Kind == ContingencyViolationKind.BranchOverload);
            overE += e[i].Details.Count(d => d.Kind == ContingencyViolationKind.BranchOverload);
            // Voltages do not depend on the rating.
            Assert.Equal(B(a[i].MinVm), B(e[i].MinVm));
            Assert.Equal(B(a[i].MaxVm), B(e[i].MaxVm));
        }
        Assert.True(overE <= overA, $"{which}: {overE} overloads vs {overA} on rating A");

        // The base case is pre-fault: rating A whatever is asked.
        ContingencyResult baseA = ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        ContingencyResult baseE = ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), opt);
        AssertSameResult("base case", baseA, baseE);
    }

    [Fact]
    public void Post_fault_loading_is_the_end_mva_over_the_chosen_rating_and_falls_back_to_rating_a()
    {
        var f = new LineFlow(1, 2, 120, 50, -119, -45, 1, 5, "L", 100, 130.0);   // |S_from| = 130 MVA
        var br = new Branch(1, 2, 0.01, 0.1, RateAMva: 100, RateBMva: 130, RateCMva: 0);
        Assert.Equal(100.0, ContingencyAnalysis.PostFaultLoadingPercent(br, f, PostFaultRating.RateB), 12);
        // No rating C: the solver's own rating-A figure, unchanged to the bit.
        Assert.Equal(B(130.0), B(ContingencyAnalysis.PostFaultLoadingPercent(br, f, PostFaultRating.RateC)));
    }

    // ---- Generator outages ---------------------------------------------------------------------------------------

    private static GridModel ThreeBus(double g2 = 60) => new()
    {
        Name = "three-bus",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pv, BaseKv: 132), new Bus(3, BusType.Pq, Pd: 100, Qd: 20, BaseKv: 132) },
        Generators = new[] { new Generator(1, 40, Name: "G1", Pmax: 200), new Generator(2, g2, Name: "G2", Pmax: 200) },
        Branches = new[] { new Branch(1, 3, 0.01, 0.1, RateAMva: 150), new Branch(2, 3, 0.01, 0.1, RateAMva: 150), new Branch(1, 2, 0.01, 0.1, RateAMva: 150) },
    };

    [Fact]
    public void A_generator_outage_is_picked_up_by_the_reference_and_reports_the_lost_output()
    {
        GridModel m = ThreeBus();
        ContingencyResult r = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Generator, 1));
        Assert.True(r.Converged);
        Assert.Equal(OutageKind.Generator, r.Kind);
        Assert.Equal("gen G2 (bus 2)", r.Outage);
        Assert.Equal(60.0, r.LostGenerationMw);
        Assert.Null(r.Note);
        // The same state solved by hand: the model without G2.
        GridModel without = m.CopyWith(generators: new[] { m.Generators[0] });
        PowerFlowResult pf = SolverSelector.CreateNewton(without).Solve(without);
        Assert.Equal(B(pf.Vm.Min()), B(r.MinVm));
    }

    [Fact]
    public void Losing_the_reference_unit_moves_the_reference_to_the_largest_remaining_unit()
    {
        GridModel m = ThreeBus();
        ContingencyResult r = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Generator, 0));
        Assert.True(r.Converged);
        Assert.Equal("reference moved from bus 1 to bus 2", r.Note);
        Assert.Equal(40.0, r.LostGenerationMw);

        // A single-unit network has nothing left to take over: reported as not solved, never as a success.
        GridModel one = m.CopyWith(generators: new[] { m.Generators[0] });
        ContingencyResult lone = ContingencyAnalysis.ScreenElementOutage(one, new ElementOutage(OutageKind.Generator, 0));
        Assert.False(lone.Converged);
        Assert.Contains("no in-service unit", lone.Note, StringComparison.Ordinal);
    }

    // ---- Bus outages -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_bus_outage_removes_its_branches_units_and_load_and_keys_violations_by_original_branch()
    {
        GridModel m = Case("lib/case30.m");
        int idx = m.Buses.ToList().FindIndex(b => b.Id == 7);   // a load bus with two branches
        Bus bus7 = m.Buses[idx];
        ContingencyResult r = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Bus, idx), ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.Equal(OutageKind.Bus, r.Kind);
        Assert.Equal("bus 7", r.Outage);
        Assert.Equal(bus7.Pd, r.LostLoadMw);
        Assert.Equal(0.0, r.LostGenerationMw);
        Assert.True(r.Converged);
        // Every branch violation key is a branch of the ORIGINAL model that does not touch bus 7.
        foreach (ContingencyViolation v in r.Details.Where(d => d.Kind == ContingencyViolationKind.BranchOverload))
        {
            Branch br = m.Branches[v.Element];
            Assert.True(br.From != 7 && br.To != 7);
        }
        // And no voltage detail names the removed bus.
        Assert.DoesNotContain(r.Details, d => d.Kind is ContingencyViolationKind.Undervoltage or ContingencyViolationKind.Overvoltage && d.Element == 7);
    }

    [Fact]
    public void Outaging_the_reference_bus_moves_the_reference_and_reports_its_units_as_lost()
    {
        GridModel m = ThreeBus();
        ContingencyResult r = ContingencyAnalysis.ScreenElementOutage(m, new ElementOutage(OutageKind.Bus, 0));
        Assert.True(r.Converged);
        Assert.Equal(40.0, r.LostGenerationMw);
        Assert.Equal("reference moved from bus 1 to bus 2", r.Note);
    }

    [Fact]
    public void Single_outages_list_branches_then_generators_then_buses()
    {
        GridModel m = ThreeBus();
        IReadOnlyList<ElementOutage> all = ContingencyAnalysis.SingleOutages(m, true, true, true);
        Assert.Equal(3 + 2 + 3, all.Count);
        Assert.Equal(new ElementOutage(OutageKind.Branch, 0), all[0]);
        Assert.Equal(new ElementOutage(OutageKind.Generator, 0), all[3]);
        Assert.Equal(new ElementOutage(OutageKind.Bus, 2), all[^1]);
    }

    // ---- CLI wiring (source level) ---------------------------------------------------------------------------------

    [Fact]
    public void The_outage_type_and_post_fault_rating_flags_are_known_documented_and_refused_elsewhere()
    {
        string root = CliProcess.Root();
        string src = File.ReadAllText(Path.Combine(root, "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("\"--outage-types\",", src, StringComparison.Ordinal);
        Assert.Contains("\"--post-fault-rating\",", src, StringComparison.Ordinal);
        Assert.Contains("error: --post-fault-rating applies to nminus1, nminus2 and n-1-1 only", src, StringComparison.Ordinal);
        Assert.Contains("error: --outage-types applies to nminus1 only", src, StringComparison.Ordinal);
        // The default screen keeps its header; the study options still flow from ContingencyAnalysis.StudyOptions (D04).
        Assert.Contains("GridSim  -  N-1 branch screening: {model.Name}", src, StringComparison.Ordinal);
        Assert.Contains("ContingencyAnalysis.StudyOptions with { PerBusVoltageLimits = busVlimits, PostFaultRating = postFaultRating }", src, StringComparison.Ordinal);
        string ch19 = File.ReadAllText(Path.Combine(root, "docs", "manual", "0019 CLI reference.md"));
        Assert.Contains("`--post-fault-rating B`", ch19, StringComparison.Ordinal);
        Assert.Contains("`--outage-types`", ch19, StringComparison.Ordinal);
    }
}
