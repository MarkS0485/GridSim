using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

public class ContingencyTests
{
    private static GridModel Uk() => JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "uk"));

    [Fact]
    public void Screens_every_branch()
    {
        var m = Uk();
        var results = ContingencyAnalysis.ScreenBranches(m);
        Assert.Equal(m.Branches.Count, results.Count);
    }

    [Fact]
    public void Radial_gsp_transformer_outage_islands_its_demand()
    {
        var results = ContingencyAnalysis.ScreenBranches(Uk());
        foreach (ContingencyResult c in results.Where(c => c.Outage.Contains("GSP")))
            Assert.False(c.Converged, $"{c.Outage} should island (radial)");
    }

    [Fact]
    public void Meshed_line_outage_stays_secure()
    {
        var results = ContingencyAnalysis.ScreenBranches(Uk());
        ContingencyResult midLondon = results.First(c => c.Outage.Contains("Midlands-London"));
        Assert.True(midLondon.Converged);
        Assert.True(midLondon.Secure, "a meshed 400 kV line outage should be N-1 secure here");
    }

    [Fact]
    public void Reports_a_mix_of_secure_and_islanded()
    {
        var results = ContingencyAnalysis.ScreenBranches(Uk());
        Assert.Contains(results, c => c.Secure);
        Assert.Contains(results, c => !c.Converged);
    }

    // --- N-k: the generalised outage core and the N-2 screen ----------------------------------

    [Fact]
    public void Single_outage_via_general_core_matches_the_N1_screen_exactly()
    {
        // The k=1 path of the general core must reproduce the N-1 numbers bit-for-bit - the refactor
        // moves orchestration, never the solve, so every field is identical (machine-epsilon preserved).
        var m = Uk();
        var n1 = ContingencyAnalysis.ScreenBranches(m);
        for (int i = 0; i < m.Branches.Count; i++)
        {
            ContingencyResult one = ContingencyAnalysis.ScreenOutage(m, new[] { i });
            Assert.Equal(n1[i].Outage, one.Outage);
            Assert.Equal(n1[i].Converged, one.Converged);
            Assert.Equal(n1[i].MaxBranchLoadingPercent, one.MaxBranchLoadingPercent);   // exact, not approx
            Assert.Equal(n1[i].WorstBoundaryLoadingPercent, one.WorstBoundaryLoadingPercent);
            Assert.Equal(n1[i].MinVm, one.MinVm);
            Assert.Equal(n1[i].MaxVm, one.MaxVm);
            Assert.Equal(n1[i].Violations, one.Violations);
        }
    }

    [Fact]
    public void Exhaustive_N2_screens_every_unordered_pair()
    {
        var m = Uk();
        int n = m.Branches.Count;
        var pairs = ContingencyAnalysis.ScreenNMinus2(m, exhaustive: true);
        Assert.Equal(n * (n - 1) / 2, pairs.Count);
        Assert.All(pairs, p => Assert.Contains(" + ", p.Outage));   // every result is a two-element outage
    }

    [Fact]
    public void Pruned_N2_keeps_only_the_candidate_pairs()
    {
        var m = Uk();
        const int candidates = 6;
        var pairs = ContingencyAnalysis.ScreenNMinus2(m, candidateCount: candidates);
        Assert.Equal(candidates * (candidates - 1) / 2, pairs.Count);
    }

    [Fact]
    public void N2_is_at_least_as_severe_as_N1_and_is_ordered_worst_first()
    {
        var m = Uk();
        var pairs = ContingencyAnalysis.ScreenNMinus2(m, exhaustive: true);
        // Worst-first ordering by stress score.
        for (int i = 1; i < pairs.Count; i++)
            Assert.True(ContingencyAnalysis.StressScore(pairs[i - 1]) >= ContingencyAnalysis.StressScore(pairs[i]));
        // A double outage can only ever be at least as stressful as its worst single member: this grid
        // has radial GSP transformers, so at least one pair must island where the network otherwise holds.
        Assert.Contains(pairs, p => !p.Converged);
    }

    [Fact]
    public void N2_enumeration_is_deterministic_despite_the_parallel_solve()
    {
        // The concern the whole app is built around: a parallel sweep must give an identical, repeatable
        // answer. Results land in fixed slots and are ordered by a pure score with a stable sort, so two
        // runs agree field-for-field.
        var m = Uk();
        var a = ContingencyAnalysis.ScreenNMinus2(m, exhaustive: true);
        var b = ContingencyAnalysis.ScreenNMinus2(m, exhaustive: true);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Outage, b[i].Outage);
            Assert.Equal(a[i].Converged, b[i].Converged);
            Assert.Equal(a[i].MaxBranchLoadingPercent, b[i].MaxBranchLoadingPercent);
            Assert.Equal(a[i].MinVm, b[i].MinVm);
            Assert.Equal(a[i].MaxVm, b[i].MaxVm);
        }
    }

    [Fact]
    public void NMinusK_with_k1_is_the_plain_N1_screen()
    {
        var m = Uk();
        var k1 = ContingencyAnalysis.ScreenNMinusK(m, 1);
        Assert.Equal(m.Branches.Count, k1.Count);
    }

    // --- N-1-1: first outage, corrective redispatch, then a second outage --------------------

    [Fact]
    public void Exhaustive_N1_1_screens_every_ordered_pair()
    {
        var m = Uk();
        int n = m.Branches.Count;
        var seq = ContingencyAnalysis.ScreenNMinus1Minus1(m, exhaustive: true);
        Assert.Equal(n * (n - 1), seq.Count);                 // ordered, so n*(n-1) not C(n,2)
        Assert.All(seq, r => Assert.Contains(" => ", r.Outage)); // "first => second"
    }

    [Fact]
    public void N1_1_with_no_corrective_action_collapses_to_the_N2_pair()
    {
        // The corrective step is the ONLY thing that separates N-1-1 from N-2: with an identity
        // corrective, losing A-then-B must land on exactly the simultaneous {A,B} double-outage state,
        // field-for-field. This pins the semantics and guards against the corrective path leaking numbers.
        var m = Uk();
        var seq = ContingencyAnalysis.ScreenNMinus1Minus1(m, correctiveAction: model => model, exhaustive: true);
        foreach (ContingencyResult s in seq)
        {
            string[] parts = s.Outage.Split(" => ");
            var pair = ContingencyAnalysis.ScreenNMinus2(m, exhaustive: true)
                .First(p => p.Outage == $"{parts[0]} + {parts[1]}" || p.Outage == $"{parts[1]} + {parts[0]}");
            Assert.Equal(pair.Converged, s.Converged);
            Assert.Equal(pair.MaxBranchLoadingPercent, s.MaxBranchLoadingPercent);
            Assert.Equal(pair.MinVm, s.MinVm);
            Assert.Equal(pair.MaxVm, s.MaxVm);
        }
    }

    [Fact]
    public void N1_1_is_deterministic_despite_the_parallel_solve()
    {
        var m = Uk();
        var a = ContingencyAnalysis.ScreenNMinus1Minus1(m, exhaustive: true);
        var b = ContingencyAnalysis.ScreenNMinus1Minus1(m, exhaustive: true);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Outage, b[i].Outage);
            Assert.Equal(a[i].Converged, b[i].Converged);
            Assert.Equal(a[i].MaxBranchLoadingPercent, b[i].MaxBranchLoadingPercent);
            Assert.Equal(a[i].MinVm, b[i].MinVm);
            Assert.Equal(a[i].MaxVm, b[i].MaxVm);
        }
    }
}
