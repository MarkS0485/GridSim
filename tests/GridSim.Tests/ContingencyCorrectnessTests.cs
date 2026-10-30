using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S1 contingency correctness (October 2026, agent "S1 contingency correctness"): D04 one solver option set for the
/// contingency family, D05 the sweep's dense and sparse engines bit-identical under the sweep's options, D03 violations
/// netted against the base case, D02 failing single outages reported apart from the pruned candidate ranking, D01 and
/// N069 the N-1-1 corrective redispatch (minimum change, line AND boundary limits, island by island).
/// Every bit-identity claim is an exact comparison of the IEEE-754 bit patterns, never a tolerance. The numeric
/// tolerances below appear only where a test checks the VALUE of an LP answer against a hand calculation.
/// </summary>
public class ContingencyCorrectnessTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    private static GridModel Ireland() =>
        JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "intl", "ireland-all-island"));

    private static void SameBits(double a, double b, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b),
            $"{what}: {a:R} vs {b:R} differ in their bits");

    /// <summary>Two contingency results are the same to the last bit: label, verdict, every number, every violation
    /// text and every per-element detail.</summary>
    private static void SameResult(ContingencyResult a, ContingencyResult b)
    {
        Assert.Equal(a.Outage, b.Outage);
        Assert.Equal(a.Converged, b.Converged);
        SameBits(a.MaxBranchLoadingPercent, b.MaxBranchLoadingPercent, a.Outage + " max loading");
        SameBits(a.WorstBoundaryLoadingPercent, b.WorstBoundaryLoadingPercent, a.Outage + " worst boundary");
        SameBits(a.MinVm, b.MinVm, a.Outage + " MinVm");
        SameBits(a.MaxVm, b.MaxVm, a.Outage + " MaxVm");
        Assert.Equal(a.Violations, b.Violations);
        Assert.Equal(a.Details.Count, b.Details.Count);
        for (int i = 0; i < a.Details.Count; i++)
        {
            Assert.Equal(a.Details[i].Kind, b.Details[i].Kind);
            Assert.Equal(a.Details[i].Element, b.Details[i].Element);
            Assert.Equal(a.Details[i].Name, b.Details[i].Name);
            SameBits(a.Details[i].Value, b.Details[i].Value, a.Outage + " detail value");
        }
        Assert.Equal(a.PreExistingViolations, b.PreExistingViolations);
        Assert.Equal(a.NettedAgainstBase, b.NettedAgainstBase);
    }

    private static void SameResults(IReadOnlyList<ContingencyResult> a, IReadOnlyList<ContingencyResult> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++) SameResult(a[i], b[i]);
    }

    // =============================================================================================================
    // D04: one solver option set
    // =============================================================================================================

    [Fact]
    public void Study_options_are_the_one_documented_set()
    {
        // 100 iterations (the default case solve's cap), reactive limits on (NETS SQSS v2.10 s.6.2), the default
        // tolerance and Q-pass cap, one-way PV->PQ switching, start from the stored voltages.
        PowerFlowOptions o = ContingencyAnalysis.StudyOptions;
        Assert.Equal(100, ContingencyAnalysis.StudyMaxIterations);
        Assert.Equal(100, o.MaxIterations);
        Assert.True(o.EnforceQLimits);
        Assert.Equal(1e-8, o.Tolerance);
        Assert.Equal(12, o.MaxQLimitPasses);
        Assert.False(o.RevertQLimits);
        Assert.False(o.FlatStart);
        Assert.Null(o.Trace);
        // One shared immutable instance, so every verb reads the same object.
        Assert.Same(o, ContingencyAnalysis.StudyOptions);
    }

    [Fact]
    public void The_oltc_loop_is_bit_identical_to_the_raw_engine_on_a_model_without_oltcs()
    {
        // case118 has no OLTC records: the loop is skipped and every outage is the raw engine's answer, bit for bit.
        GridModel m = IeeeCases.Case118();
        Assert.Empty(m.Oltcs);
        IReadOnlyList<ContingencyResult> looped = ContingencyAnalysis.ScreenBranches(
            m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        IReadOnlyList<ContingencyResult> raw = ContingencyAnalysis.ScreenBranches(
            m, SolverSelector.CreateNewton(m), ContingencyAnalysis.StudyOptions);
        SameResults(looped, raw);
    }

    [Fact]
    public void The_oltc_loop_wrapper_is_exactly_the_steady_state_solver()
    {
        // GB reduced has OLTC records, so here the loop acts. The wrapper must be the SteadyStateSolver's answer to the
        // last bit (it adds orchestration only), and it must differ from the raw engine (the taps moved).
        GridModel m = Uk();
        Assert.NotEmpty(m.Oltcs);
        var reduced = m.CopyWith(branches: m.Branches.Where((_, j) => j != 2).ToList());   // L3-4 out
        PowerFlowResult viaWrapper = new OltcLoopPowerFlow(SolverSelector.CreateNewton(reduced)).Solve(reduced, ContingencyAnalysis.StudyOptions);
        PowerFlowResult viaLoop = new SteadyStateSolver(SolverSelector.CreateNewton(reduced)).Solve(reduced, ContingencyAnalysis.StudyOptions);
        PowerFlowResult raw = SolverSelector.CreateNewton(reduced).Solve(reduced, ContingencyAnalysis.StudyOptions);
        Assert.True(viaWrapper.Converged);
        Assert.Equal(viaLoop.Vm.Length, viaWrapper.Vm.Length);
        for (int i = 0; i < viaLoop.Vm.Length; i++)
        {
            SameBits(viaLoop.Vm[i], viaWrapper.Vm[i], $"Vm[{i}]");
            SameBits(viaLoop.VaDeg[i], viaWrapper.VaDeg[i], $"Va[{i}]");
        }
        // Before D04 the screen of L3-4 reported Vmin 1.027 pu (raw engine, no taps); with the loop it is 0.998 pu.
        Assert.NotEqual(raw.Vm.Min(), viaWrapper.Vm.Min());
        Assert.Equal("1.027", raw.Vm.Min().ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("0.998", viaWrapper.Vm.Min().ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_oltc_loop_wrapper_fails_loud_on_bad_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new OltcLoopPowerFlow(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OltcLoopPowerFlow(new NewtonRaphsonPowerFlow(), -1));
    }

    [Fact]
    public void The_sequential_and_parallel_screens_classify_every_outage_alike_under_the_study_set()
    {
        // nminus1 runs the sequential screen, nminus1-sweep the parallel one; with the one option set and the OLTC
        // loop both, they now agree field for field (before D04 the sweep switched Q-limits off: case118 gave
        // 167 secure / 10 violating in the sweep against 164 / 13 in nminus1).
        foreach (GridModel m in new[] { IeeeCases.Case14(), IeeeCases.Case118(), Uk() })
        {
            IReadOnlyList<ContingencyResult> seq = ContingencyAnalysis.ScreenBranches(
                m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
            IReadOnlyList<ContingencyResult> par = ContingencyAnalysis.ScreenBranchesParallel(
                m, ContingencyAnalysis.StudySolverFactory(m), ContingencyAnalysis.StudyOptions, maxDegreeOfParallelism: 4);
            SameResults(seq, par);
        }
    }

    [Fact]
    public void Case118_n1_counts_under_the_study_set()
    {
        // Pinned before -> after (D04): case118 nminus1 was 164 secure / 13 violating / 9 islanded with 30 iterations
        // and stays so with 100 (no outage needed 31 to 100 steps); the sweep's own count was 167 / 10 / 9 with
        // Q-limits off and is now the same 164 / 13 / 9.
        GridModel m = IeeeCases.Case118();
        IReadOnlyList<ContingencyResult> r = ContingencyAnalysis.ScreenBranches(
            m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.Equal(164, r.Count(c => c.Secure));
        Assert.Equal(13, r.Count(c => c.Converged && !c.Secure));
        Assert.Equal(9, r.Count(c => !c.Converged));
    }

    // =============================================================================================================
    // D05: the sweep's engine choice is speed only - dense and sparse bit-identical under the sweep's options
    // =============================================================================================================

    public static IEnumerable<object[]> SweepCases() => new[]
    {
        new object[] { "case57", 1 },
        new object[] { "case118", 1 },
        new object[] { "case300", 3 },   // every third outage: 137 of 411, enough to cover every engine path
    };

    private static GridModel Named(string name) => name switch
    {
        "case57" => IeeeCases.Case57(),
        "case118" => IeeeCases.Case118(),
        "case300" => IeeeCases.Case300(),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(SweepCases))]
    public void Sweep_dense_and_sparse_engines_are_bit_identical_under_the_study_set_and_the_old_sweep_set(string name, int stride)
    {
        // The 1,000-bus sweep threshold was not a measured crossover (sparse is faster at 57, 118 and 300 buses); the
        // sweep now routes through the shared selector (1.95.105). This pins what makes that a speed-only change: the
        // two engines return the same bits for every outage, under the contingency study set (100 iterations, Q-limits
        // on, OLTC loop) AND under the sweep's old set (100 iterations, Q-limits off), which no test pinned before.
        GridModel m = Named(name);
        int[] idx = Enumerable.Range(0, m.Branches.Count).Where(i => i % stride == 0).ToArray();
        var oldSweep = new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = false };
        foreach (PowerFlowOptions opt in new[] { ContingencyAnalysis.StudyOptions, oldSweep })
        {
            IReadOnlyList<ContingencyResult> dense = ContingencyAnalysis.ScreenBranchesParallel(
                m, () => ContingencyAnalysis.WithOltcLoop(new NewtonRaphsonPowerFlow()), opt, 4, idx);
            IReadOnlyList<ContingencyResult> sparse = ContingencyAnalysis.ScreenBranchesParallel(
                m, () => ContingencyAnalysis.WithOltcLoop(new SparseNewtonRaphsonPowerFlow()), opt, 4, idx);
            SameResults(dense, sparse);
        }
    }

    // =============================================================================================================
    // D03: violations netted against the base case
    // =============================================================================================================

    private static (ContingencyResult Base, IReadOnlyList<ContingencyResult> Raw) StudyN1(GridModel m)
    {
        ContingencyResult b = ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        IReadOnlyList<ContingencyResult> raw = ContingencyAnalysis.ScreenBranches(
            m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        return (b, raw);
    }

    [Fact]
    public void The_base_case_is_the_unoutaged_network_with_the_same_solver()
    {
        GridModel m = IeeeCases.Case14();
        ContingencyResult b = ContingencyAnalysis.ScreenBaseCase(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.Equal(ContingencyAnalysis.BaseCaseLabel, b.Outage);
        Assert.True(b.Converged);
        // case14 holds bus 8 at its 1.09 pu set-point and bus 6 at 1.07, and bus 7 settles at 1.0615: three buses above
        // the 1.06 band, summarised as the one overvoltage line at the highest voltage.
        Assert.Equal(new[] { "overvoltage 1.090 p.u." }, b.Violations);
        Assert.Equal(new[] { 6, 7, 8 }, b.Details.Select(d => d.Element));
        Assert.All(b.Details, d => Assert.Equal(ContingencyViolationKind.Overvoltage, d.Kind));
        Assert.Equal(1.07, b.Details[0].Value);
        Assert.Equal(1.09, b.Details[2].Value);
    }

    [Fact]
    public void Case14_base_overvoltage_is_reported_once_not_on_every_outage()
    {
        // Before D03: every converged case14 outage showed the base case's overvoltage, so 0 of 20 were secure
        // (19 violating, 1 not solved). After: 16 secure, 3 with violations of their own, 1 not solved.
        (ContingencyResult b, IReadOnlyList<ContingencyResult> raw) = StudyN1(IeeeCases.Case14());
        Assert.Equal(0, raw.Count(c => c.Secure));
        Assert.Equal(19, raw.Count(c => c.Converged && !c.Secure));
        Assert.Equal(1, raw.Count(c => !c.Converged));

        IReadOnlyList<ContingencyResult> net = ContingencyAnalysis.NetAgainstBase(raw, b);
        Assert.Equal(16, net.Count(c => c.Secure));
        Assert.Equal(3, net.Count(c => c.Converged && !c.Secure));
        Assert.Equal(1, net.Count(c => !c.Converged));
        Assert.All(net, c => Assert.True(c.NettedAgainstBase));
        // Each converged outage carries the base overvoltage as pre-existing, never as its own violation.
        foreach (ContingencyResult c in net.Where(c => c.Converged))
        {
            Assert.Equal(new[] { "overvoltage 1.090 p.u." }, c.PreExistingViolations);
            Assert.DoesNotContain("overvoltage 1.090 p.u.", c.Violations);
        }
    }

    [Fact]
    public void Ireland_netted_counts()
    {
        // Ireland's base case holds a bus at 1.126 pu. Before D03 (and D04): 2 secure / 76 violating / 4 islanded.
        // After: 70 secure / 8 violating / 4 islanded.
        (ContingencyResult b, IReadOnlyList<ContingencyResult> raw) = StudyN1(Ireland());
        Assert.True(b.Converged);
        Assert.Equal(new[] { "overvoltage 1.126 p.u." }, b.Violations);
        IReadOnlyList<ContingencyResult> net = ContingencyAnalysis.NetAgainstBase(raw, b);
        Assert.Equal(70, net.Count(c => c.Secure));
        Assert.Equal(8, net.Count(c => c.Converged && !c.Secure));
        Assert.Equal(4, net.Count(c => !c.Converged));
    }

    [Fact]
    public void Netting_against_a_clean_base_returns_the_raw_violations_exactly()
    {
        // case300 has a rich mix (no ratings, so voltage violations of both signs; outages that do not solve). Netting
        // against a converged base with NO violations must give back the raw text, line for line: the detail list
        // rebuilds the summary exactly.
        GridModel m = IeeeCases.Case300();
        IReadOnlyList<ContingencyResult> raw = ContingencyAnalysis.ScreenBranchesParallel(
            m, ContingencyAnalysis.StudySolverFactory(m), ContingencyAnalysis.StudyOptions, 4);
        var cleanBase = new ContingencyResult("clean", true, 0, 0, 1, 1, Array.Empty<string>());
        IReadOnlyList<ContingencyResult> net = ContingencyAnalysis.NetAgainstBase(raw, cleanBase);
        for (int i = 0; i < raw.Count; i++)
        {
            Assert.Equal(raw[i].Violations, net[i].Violations);
            Assert.Empty(net[i].PreExistingViolations);
            Assert.Equal(raw[i].Secure, net[i].Secure);
        }
        Assert.Contains(raw, c => c.Converged && c.Violations.Count > 0);   // the comparison covered real violations
    }

    [Fact]
    public void Netting_keys_by_element_so_a_new_bus_outside_the_band_still_violates()
    {
        // Bus 8 is already high in the base case; the outage leaves it high AND pushes bus 9 high, and overloads
        // branch index 3 while branch index 5 was already overloaded. Only bus 9 and branch 3 count against the outage.
        var baseCase = new ContingencyResult("base case", true, 120, 0, 1.0, 1.09,
            new[] { "branch b5 120%", "overvoltage 1.090 p.u." })
        {
            Details = new[]
            {
                new ContingencyViolation(ContingencyViolationKind.BranchOverload, 5, 120, "b5"),
                new ContingencyViolation(ContingencyViolationKind.Overvoltage, 8, 1.09, ""),
            },
        };
        var outage = new ContingencyResult("x", true, 130, 0, 1.0, 1.095,
            new[] { "branch b3 110%", "branch b5 130%", "overvoltage 1.095 p.u." })
        {
            Details = new[]
            {
                new ContingencyViolation(ContingencyViolationKind.BranchOverload, 3, 110, "b3"),
                new ContingencyViolation(ContingencyViolationKind.BranchOverload, 5, 130, "b5"),
                new ContingencyViolation(ContingencyViolationKind.Overvoltage, 8, 1.095, ""),
                new ContingencyViolation(ContingencyViolationKind.Overvoltage, 9, 1.07, ""),
            },
        };
        ContingencyResult net = ContingencyAnalysis.NetAgainstBase(outage, baseCase);
        Assert.Equal(new[] { "branch b3 110%", "overvoltage 1.070 p.u." }, net.Violations);
        Assert.Equal(new[] { "branch b5 130%", "overvoltage 1.095 p.u." }, net.PreExistingViolations);
        Assert.False(net.Secure);
        // The numeric fields are never changed by netting.
        Assert.Equal(130.0, net.MaxBranchLoadingPercent);
        Assert.Equal(1.095, net.MaxVm);
    }

    [Fact]
    public void Netting_never_hides_an_islanding_and_refuses_an_unsolved_base()
    {
        var baseCase = new ContingencyResult("base case", true, 0, 0, 1, 1, Array.Empty<string>());
        var islanded = new ContingencyResult("y", false, 0, 0, 0, 0, new[] { "islanded / no solution" });
        ContingencyResult net = ContingencyAnalysis.NetAgainstBase(islanded, baseCase);
        Assert.False(net.Secure);
        Assert.Equal(new[] { "islanded / no solution" }, net.Violations);

        var unsolved = new ContingencyResult("base case", false, 0, 0, 0, 0, new[] { "islanded / no solution" });
        Assert.Throws<InvalidOperationException>(() => ContingencyAnalysis.NetAgainstBase(islanded, unsolved));
    }

    [Fact]
    public void Branch_violations_are_keyed_by_the_original_branch_index_in_a_double_outage()
    {
        // An N-2 outage removes two branches, so the reduced model's branch j is not the original j. The detail must
        // carry the ORIGINAL index (the key the base case uses). GB reduced, exhaustive N-2: the worst converged pair
        // overloads L6-8 Midlands-London, original branch index 8.
        GridModel m = Uk();
        ContingencyResult pair = ContingencyAnalysis.ScreenOutage(m, new[] { 9, 11 });   // L7-8 + L8-9
        ContingencyViolation v = Assert.Single(pair.Details, d => d.Kind == ContingencyViolationKind.BranchOverload);
        Assert.Equal(8, v.Element);
        Assert.Equal("L6-8 Midlands-London", m.Branches[8].Name);
        Assert.Equal("L6-8 Midlands-London", v.Name);
    }

    // =============================================================================================================
    // D02: failing single outages reported apart from the pruned candidate ranking
    // =============================================================================================================

    [Fact]
    public void Pruned_N2_pairs_only_outages_that_solve_and_reports_the_rest_separately()
    {
        // Before D02: with 5 candidates the 12 islanding GB outages (+Infinity score) filled every slot, and all 10
        // pairs islanded. After: the 10 pairs are drawn from the 5 most-stressed outages that solve, and the 12
        // islanding outages come back separately, in branch order.
        GridModel m = Uk();
        NkScreenReport r = ContingencyAnalysis.ScreenNMinusKReport(m, 2, candidateCount: 5);
        Assert.Equal(10, r.Results.Count);
        Assert.All(r.Results, c => Assert.True(c.Converged, c.Outage));
        Assert.Equal(12, r.ExcludedSingleOutages.Count);
        Assert.All(r.ExcludedSingleOutages, c => Assert.False(c.Converged));
        Assert.Equal(new[] { "L1-2 N-S Scotland", "L2-3 B6 boundary" }, r.ExcludedSingleOutages.Take(2).Select(c => c.Outage));
        Assert.All(r.ExcludedSingleOutages.Skip(2), c => Assert.Contains("GSP", c.Outage));

        // The pairs are exactly the pairs of the 5 highest-scoring solvable single outages (stable order, ties in
        // branch order), each solved exactly as ScreenOutage solves it.
        IReadOnlyList<ContingencyResult> single = ContingencyAnalysis.ScreenBranches(m);
        int[] top = Enumerable.Range(0, m.Branches.Count).Where(i => single[i].Converged)
            .OrderByDescending(i => ContingencyAnalysis.StressScore(single[i])).Take(5).ToArray();
        var expected = new List<ContingencyResult>();
        for (int x = 0; x < top.Length; x++)
            for (int y = x + 1; y < top.Length; y++)
                expected.Add(ContingencyAnalysis.ScreenOutage(m, new[] { top[x], top[y] }));
        SameResults(expected.OrderByDescending(ContingencyAnalysis.StressScore).ToList(), r.Results);

        // The list-returning API gives the same pairs.
        SameResults(r.Results, ContingencyAnalysis.ScreenNMinus2(m, candidateCount: 5));
    }

    [Fact]
    public void The_candidate_count_is_capped_by_the_outages_that_solve()
    {
        // GB reduced: 12 of 24 single outages solve, so 20 candidates give C(12,2) = 66 pairs (it was C(20,2) = 190,
        // 163 of them islanding).
        NkScreenReport r = ContingencyAnalysis.ScreenNMinusKReport(Uk(), 2, candidateCount: 20);
        Assert.Equal(66, r.Results.Count);
        Assert.Equal(12, r.ExcludedSingleOutages.Count);
    }

    [Fact]
    public void An_exhaustive_screen_excludes_nothing()
    {
        GridModel m = Uk();
        NkScreenReport r = ContingencyAnalysis.ScreenNMinusKReport(m, 2, exhaustive: true);
        Assert.Equal(276, r.Results.Count);
        Assert.Empty(r.ExcludedSingleOutages);
        NkScreenReport s = ContingencyAnalysis.ScreenNMinus1Minus1Report(m, exhaustive: true);
        Assert.Equal(552, s.Results.Count);
        Assert.Empty(s.ExcludedSingleOutages);
    }

    [Fact]
    public void Pruned_N1_1_also_reports_its_failing_single_outages_separately()
    {
        NkScreenReport r = ContingencyAnalysis.ScreenNMinus1Minus1Report(Uk(), candidateCount: 5);
        Assert.Equal(20, r.Results.Count);   // 5 x 4 ordered pairs of solvable outages
        Assert.Equal(12, r.ExcludedSingleOutages.Count);
        Assert.DoesNotContain(r.Results, c => c.Outage.Contains("GSP"));
    }

    // =============================================================================================================
    // D01 and N069: the corrective redispatch
    // =============================================================================================================

    [Fact]
    public void Redispatch_leaves_a_secure_post_outage_dispatch_unchanged_on_gb_reduced()
    {
        // Before D01 the redispatch re-optimised the whole fleet after every meshed first outage: 31,810 to 32,380 MW
        // moved (sum of |change|). Now a post-outage network with nothing over its limit keeps its dispatch: the very
        // same model object, 0 MW moved.
        GridModel m = Uk();
        for (int i = 2; i <= 13; i++)   // the 12 meshed 400 kV lines L3-4 .. L5-10
        {
            GridModel post = m.CopyWith(branches: m.Branches.Where((_, j) => j != i).ToList());
            CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(post);
            Assert.Equal(RedispatchOutcome.NotNeeded, plan.Outcome);
            Assert.Same(post, plan.Model);
            Assert.Equal(0.0, plan.MovedMw);
            Assert.Same(post, ContingencyAnalysis.RedispatchToRelieve(post));
        }
    }

    [Fact]
    public void B4_stays_at_its_pre_outage_loading_before_the_second_outage()
    {
        // GB reduced, first outage L3-4 N England-Yorks. Before D01 the corrected dispatch carried B4 Upper North
        // from 48.435617734804836 % to 115.62311773480394 % of its 3,200 MW limit BEFORE any second outage, so every
        // converged N-1-1 pair violated B4. After: the dispatch is unchanged, so B4 on the corrected full topology is
        // the base case's loading to the last bit.
        GridModel m = Uk();
        GridModel post = m.CopyWith(branches: m.Branches.Where((_, j) => j != 2).ToList());
        GridModel corrected = m.CopyWith(generators: ContingencyAnalysis.RedispatchToRelieve(post).Generators);
        IPowerFlowSolver nr = SolverSelector.CreateNewton(m);
        double B4(GridModel g)
        {
            PowerFlowResult r = nr.Solve(g);
            Assert.True(r.Converged);
            return BoundaryAnalysis.Evaluate(g, r).Single(b => b.Name.StartsWith("B4", StringComparison.Ordinal)).LoadingPercent;
        }
        double baseB4 = B4(m);
        SameBits(48.435617734804836, baseB4, "base B4");
        SameBits(baseB4, B4(corrected), "B4 after the corrective step");
    }

    [Fact]
    public void Gb_reduced_exhaustive_N1_1_counts_before_and_after()
    {
        // Before D01: 552 ordered pairs, 0 secure, 128 with violations (every converged pair, all through B4 at
        // 115.6 %), 424 not solved. After: the meshed first outages need no redispatch, so each such pair is the N-2
        // pair (61 secure / 3 violating / 212 islanded, counted in both orders): 122 secure, 6 violating, 424 not solved.
        GridModel m = Uk();
        IReadOnlyList<ContingencyResult> r = ContingencyAnalysis.ScreenNMinus1Minus1(m, exhaustive: true);
        Assert.Equal(552, r.Count);
        Assert.Equal(122, r.Count(c => c.Secure));
        Assert.Equal(6, r.Count(c => c.Converged && !c.Secure));
        Assert.Equal(424, r.Count(c => !c.Converged));
    }

    [Fact]
    public void When_no_redispatch_is_needed_N1_1_pairs_equal_the_N2_pairs_bit_for_bit()
    {
        GridModel m = Uk();
        IReadOnlyList<ContingencyResult> seq = ContingencyAnalysis.ScreenNMinus1Minus1(m, exhaustive: true);
        IReadOnlyList<ContingencyResult> n2 = ContingencyAnalysis.ScreenNMinus2(m, exhaustive: true);
        // The first outages whose post-outage network needs no corrective action, by label.
        var notNeeded = new HashSet<string>();
        for (int i = 0; i < m.Branches.Count; i++)
        {
            GridModel post = m.CopyWith(branches: m.Branches.Where((_, j) => j != i).ToList());
            if (ContingencyAnalysis.PlanCorrectiveRedispatch(post).Outcome == RedispatchOutcome.NotNeeded)
                notNeeded.Add(Label(m.Branches[i]));
        }
        Assert.True(notNeeded.Count >= 12);
        int compared = 0;
        foreach (ContingencyResult s in seq)
        {
            string[] parts = s.Outage.Split(" => ");
            if (!notNeeded.Contains(parts[0])) continue;
            ContingencyResult pair = n2.First(p => p.Outage == $"{parts[0]} + {parts[1]}" || p.Outage == $"{parts[1]} + {parts[0]}");
            Assert.Equal(pair.Converged, s.Converged);
            SameBits(pair.MaxBranchLoadingPercent, s.MaxBranchLoadingPercent, s.Outage);
            SameBits(pair.WorstBoundaryLoadingPercent, s.WorstBoundaryLoadingPercent, s.Outage);
            SameBits(pair.MinVm, s.MinVm, s.Outage);
            SameBits(pair.MaxVm, s.MaxVm, s.Outage);
            Assert.Equal(pair.Violations, s.Violations);
            compared++;
        }
        Assert.True(compared >= 12 * 23);
    }

    private static string Label(Branch b) =>
        (string.IsNullOrEmpty(b.Name) ? $"{b.From}-{b.To}" : b.Name) + (b.IsTransformer ? " (tx)" : "");

    // A triangle of equal reactances: bus 1 (slack, G1 100 MW, Pmax 200), bus 2 (G2 0 MW, Pmax 200), bus 3 (load
    // 100 MW). With G1 supplying the load, line 1-3 carries 2/3 of it (66.67 MW) and 1-2-3 the other third. Moving D MW
    // from G1 to G2 changes the 1-3 flow by -D/3 and the 2-3 flow by +D/3.
    private static GridModel Triangle(double rate13 = 0, double rate23 = 0, IReadOnlyList<Boundary>? boundaries = null)
    {
        var buses = new List<Bus> { new(1, BusType.Slack), new(2, BusType.Pv), new(3, BusType.Pq, Pd: 100) };
        var gens = new List<Generator> { new(1, Pg: 100, Pmax: 200), new(2, Pg: 0, Pmax: 200) };
        var branches = new List<Branch>
        {
            new(1, 2, 0.0, 0.1) { Name = "L12" },
            new(2, 3, 0.0, 0.1) { Name = "L23", RateAMva = rate23 },
            new(1, 3, 0.0, 0.1) { Name = "L13", RateAMva = rate13 },
        };
        return new GridModel
        {
            BaseMva = 100, Buses = buses, Generators = gens, Branches = branches,
            Boundaries = boundaries ?? Array.Empty<Boundary>(),
        };
    }

    [Fact]
    public void Redispatch_relieves_an_overloaded_line_with_the_fewest_MW()
    {
        // L13 rated 50: 66.67 - D/3 <= 50 needs D >= 50, so G2 rises by 50 and G1 falls by 50 (100 MW moved), and L13
        // ends exactly at its limit. Values are checked against the hand calculation to 1e-9 MW.
        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(Triangle(rate13: 50));
        Assert.Equal(RedispatchOutcome.Applied, plan.Outcome);
        Assert.Equal(new[] { "branch L13" }, plan.OverloadedBefore);
        Assert.Equal(LpStatus.Optimal, plan.LpStatus);
        Assert.Equal(50.0, plan.Model.Generators[0].Pg, 9);
        Assert.Equal(50.0, plan.Model.Generators[1].Pg, 9);
        Assert.Equal(100.0, plan.MovedMw, 9);
    }

    [Fact]
    public void Redispatch_holds_a_boundary_limit()
    {
        // No line is rated; a boundary over L13 with a 50 MW limit is over (66.67 MW). The old corrective action did
        // not see boundaries at all; the new one relieves it exactly as it relieves a rated line.
        var b = new Boundary("B13", 50, new[] { new BoundaryBranch("L13") });
        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(Triangle(boundaries: new[] { b }));
        Assert.Equal(RedispatchOutcome.Applied, plan.Outcome);
        Assert.Equal(new[] { "boundary B13" }, plan.OverloadedBefore);
        Assert.Equal(50.0, plan.Model.Generators[1].Pg, 9);
    }

    [Fact]
    public void A_limit_the_redispatch_would_break_is_added_and_enforced()
    {
        // Relieving L13 (rate 50) moves 50 MW onto G2, which raises the 2-3 flow from 33.33 to 50 MW. With a boundary
        // over L23 limited to 45 MW (not over before the redispatch) the lazily added row makes the problem infeasible:
        // the dispatch is left unchanged rather than trading one overload for another.
        var b23 = new Boundary("B23", 45, new[] { new BoundaryBranch("L23") });
        GridModel m = Triangle(rate13: 50, boundaries: new[] { b23 });
        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(m);
        Assert.Equal(RedispatchOutcome.Infeasible, plan.Outcome);
        Assert.Same(m, plan.Model);
        Assert.Equal(0.0, plan.MovedMw);

        // With room on B23 (55 MW) the same redispatch is applied and B23 ends at 50 MW, inside its limit.
        var roomy = new Boundary("B23", 55, new[] { new BoundaryBranch("L23") });
        CorrectiveRedispatch ok = ContingencyAnalysis.PlanCorrectiveRedispatch(Triangle(rate13: 50, boundaries: new[] { roomy }));
        Assert.Equal(RedispatchOutcome.Applied, ok.Outcome);
        Assert.Equal(50.0, ok.Model.Generators[1].Pg, 9);
    }

    [Fact]
    public void An_islanded_network_is_redispatched_island_by_island()
    {
        // N069. Island A is the triangle with nothing over its limit; island B is buses 4 (G3 100 MW) and 5 (G4 0 MW,
        // load 100 MW) joined by one line rated 60 MW, carrying 100 MW. Before N069 a network of more than one island
        // kept its dispatch. Now island B moves 40 MW from G3 to G4 on its own balance; island A is untouched to the bit.
        GridModel tri = Triangle();
        var buses = tri.Buses.ToList();
        buses.Add(new Bus(4, BusType.Pv));
        buses.Add(new Bus(5, BusType.Pq, Pd: 100));
        var gens = tri.Generators.ToList();
        gens.Add(new Generator(4, Pg: 100, Pmax: 200));
        gens.Add(new Generator(5, Pg: 0, Pmax: 200));
        var branches = tri.Branches.ToList();
        branches.Add(new Branch(4, 5, 0.0, 0.1) { Name = "L45", RateAMva = 60 });
        var m = new GridModel { BaseMva = 100, Buses = buses, Generators = gens, Branches = branches };
        Assert.Equal(2, DistributionFactors.FindIslands(m).Count);

        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(m);
        Assert.Equal(RedispatchOutcome.Applied, plan.Outcome);
        Assert.Equal(new[] { "branch L45" }, plan.OverloadedBefore);
        SameBits(100.0, plan.Model.Generators[0].Pg, "G1");
        SameBits(0.0, plan.Model.Generators[1].Pg, "G2");
        Assert.Equal(60.0, plan.Model.Generators[2].Pg, 9);
        Assert.Equal(40.0, plan.Model.Generators[3].Pg, 9);
        Assert.Equal(80.0, plan.MovedMw, 9);
    }

    [Fact]
    public void A_line_no_generator_can_influence_does_not_block_the_redispatch()
    {
        // A dead island (no generator) with an overloaded line cannot be helped by redispatch; it is left out of the LP
        // and the live island is still relieved.
        GridModel tri = Triangle(rate13: 50);
        var buses = tri.Buses.ToList();
        // The dead island's reference is its first bus (21), so the load at 22 is carried over C12: 30 MW on 10 MW.
        buses.Add(new Bus(21, BusType.Pq));
        buses.Add(new Bus(22, BusType.Pq, Pd: 30));
        var branches = tri.Branches.ToList();
        branches.Add(new Branch(21, 22, 0.0, 0.2) { Name = "C12", RateAMva = 10 });
        var m = new GridModel { BaseMva = 100, Buses = buses, Generators = tri.Generators, Branches = branches };
        CorrectiveRedispatch plan = ContingencyAnalysis.PlanCorrectiveRedispatch(m);
        Assert.Equal(RedispatchOutcome.Applied, plan.Outcome);
        Assert.Equal(50.0, plan.Model.Generators[1].Pg, 9);
    }

    [Fact]
    public void The_redispatch_is_deterministic()
    {
        GridModel m = Triangle(rate13: 50);
        CorrectiveRedispatch a = ContingencyAnalysis.PlanCorrectiveRedispatch(m);
        CorrectiveRedispatch b = ContingencyAnalysis.PlanCorrectiveRedispatch(m);
        for (int i = 0; i < m.Generators.Count; i++)
            SameBits(a.Model.Generators[i].Pg, b.Model.Generators[i].Pg, $"G{i + 1}");
        SameBits(a.MovedMw, b.MovedMw, "moved");
    }

    [Fact]
    public void A_redispatch_input_with_an_inconsistent_generator_range_fails_loud()
    {
        GridModel tri = Triangle(rate13: 50);
        var gens = tri.Generators.ToList();
        gens[1] = gens[1] with { Pmin = 300 };   // Pmin above Pmax 200
        var m = new GridModel { BaseMva = 100, Buses = tri.Buses, Generators = gens, Branches = tri.Branches };
        Assert.Throws<InvalidDataException>(() => ContingencyAnalysis.PlanCorrectiveRedispatch(m));
    }
}
