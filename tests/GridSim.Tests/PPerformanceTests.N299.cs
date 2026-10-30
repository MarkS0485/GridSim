using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Stream P (October 2026 performance items). Every test here is new. Each optimisation must give results identical to
/// the bit to the code it replaces, so each is checked against the old path, kept reachable as a test oracle, field for
/// field.
/// </summary>
public partial class PPerformanceTests
{
    // =================================================================================================================
    // N299: the protection cascade's post-trip re-solve and the loss sweep's re-solves take the N232 short cut on the
    // same provably unsolvable islands the contingency screens already skip.
    // =================================================================================================================

    /// <summary>A pass-through solver the loss sweep does not recognise as a built-in Newton engine, so it always calls
    /// it: the pre-N299 path, used as the oracle. It solves with exactly the engine it wraps.</summary>
    private sealed class N299Opaque(IPowerFlowSolver inner) : IPowerFlowSolver
    {
        public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null) => inner.Solve(model, options);
    }

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static GridModel N299Case(string name) => name switch
    {
        "case9" => IeeeCases.Case9(),
        "case14" => IeeeCases.Case14(),
        "case30" => IeeeCases.Case30(),
        "case57" => IeeeCases.Case57(),
        "case118" => IeeeCases.Case118(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not an N299 test case"),
    };

    public static TheoryData<string> N299LossCases => new() { "case9", "case14", "case30", "case57", "case118" };

    private static void N299AssertIdentical(LossContingencyResult expected, LossContingencyResult actual)
    {
        string who = expected.Outage;
        Assert.Equal(expected.Outage, actual.Outage);
        Assert.True(expected.Converged == actual.Converged, $"{who}: Converged {expected.Converged} -> {actual.Converged}");
        Assert.True(SameBits(expected.BaseLossMw, actual.BaseLossMw), $"{who}: BaseLossMw {expected.BaseLossMw:R} -> {actual.BaseLossMw:R}");
        Assert.True(SameBits(expected.PostLossMw, actual.PostLossMw), $"{who}: PostLossMw {expected.PostLossMw:R} -> {actual.PostLossMw:R}");
        Assert.True(SameBits(expected.DeltaLossMw, actual.DeltaLossMw), $"{who}: DeltaLossMw {expected.DeltaLossMw:R} -> {actual.DeltaLossMw:R}");
        Assert.True(SameBits(expected.MaxBranchLoadingPercent, actual.MaxBranchLoadingPercent),
            $"{who}: MaxBranchLoadingPercent {expected.MaxBranchLoadingPercent:R} -> {actual.MaxBranchLoadingPercent:R}");
    }

    [Theory]
    [MemberData(nameof(N299LossCases))]
    public void N299_every_loss_sweep_row_is_bit_identical_to_the_full_solve(string name)
    {
        // Every branch outage of the case, swept by the built-in engine (which now skips the provably unsolvable states)
        // and by the same engine behind an opaque wrapper (always solved: the pre-N299 path), sequential and parallel.
        GridModel m = N299Case(name);
        PowerFlowResult baseCase = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(baseCase.Converged);

        IReadOnlyList<LossContingencyResult> fast = LossContingency.Sweep(m, baseCase, SolverSelector.CreateNewton(m));
        IReadOnlyList<LossContingencyResult> full = LossContingency.Sweep(m, baseCase, new N299Opaque(SolverSelector.CreateNewton(m)));
        Assert.Equal(full.Count, fast.Count);
        for (int i = 0; i < full.Count; i++) N299AssertIdentical(full[i], fast[i]);

        IReadOnlyList<LossContingencyResult> fastParallel = LossContingency.SweepParallel(m, baseCase, () => SolverSelector.CreateNewton(m));
        IReadOnlyList<LossContingencyResult> fullParallel = LossContingency.SweepParallel(m, baseCase,
            () => new N299Opaque(SolverSelector.CreateNewton(m)));
        Assert.Equal(full.Count, fastParallel.Count);
        Assert.Equal(full.Count, fullParallel.Count);
        for (int i = 0; i < full.Count; i++)
        {
            N299AssertIdentical(full[i], fastParallel[i]);
            N299AssertIdentical(full[i], fullParallel[i]);
        }
    }

    [Fact]
    public void N299_the_case9_islanding_outages_take_the_short_cut_in_the_loss_sweep_and_keep_their_row()
    {
        // case9 1-4, 3-6 and 8-2 each leave a part the N232 proof covers (a load part; two lossless generator buses), so
        // the sweep reports them without solving. Their rows are the not-converged row, as the full solve gave.
        GridModel m = IeeeCases.Case9();
        PowerFlowResult baseCase = SolverSelector.CreateNewton(m).Solve(m);
        IReadOnlyList<LossContingencyResult> rows = LossContingency.Sweep(m, baseCase, SolverSelector.CreateNewton(m));
        double tol = new PowerFlowOptions().Tolerance;
        foreach ((int from, int to) in new[] { (1, 4), (3, 6), (8, 2) })
        {
            int k = Enumerable.Range(0, m.Branches.Count).Single(j => m.Branches[j].From == from && m.Branches[j].To == to);
            GridModel without = m.CopyWith(branches: m.Branches.Where((_, j) => j != k).ToList());
            Assert.NotNull(ContingencyAnalysis.ProvablyUnsolvablePart(without, tol));
            LossContingencyResult r = rows[k];
            Assert.False(r.Converged);
            Assert.True(SameBits(baseCase.TotalLossMw, r.BaseLossMw));
            Assert.Equal(0.0, r.PostLossMw);
            Assert.Equal(0.0, r.DeltaLossMw);
            Assert.Equal(0.0, r.MaxBranchLoadingPercent);
        }
    }

    [Fact]
    public void N299_a_caller_solver_is_always_called_by_the_loss_sweep()
    {
        // The short cut is for GridSim's own Newton engines only; a caller's solver sees every outage, islanding or not.
        GridModel m = IeeeCases.Case9();
        PowerFlowResult baseCase = SolverSelector.CreateNewton(m).Solve(m);
        var counting = new N299Counting(SolverSelector.CreateNewton(m));
        _ = LossContingency.Sweep(m, baseCase, counting);
        Assert.Equal(m.Branches.Count, counting.Calls);
    }

    private sealed class N299Counting(IPowerFlowSolver inner) : IPowerFlowSolver
    {
        public int Calls { get; private set; }
        public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null) { Calls++; return inner.Solve(model, options); }
    }

    // ---- the protection cascade ----

    private static void N299AssertIdentical(CascadeResult expected, CascadeResult actual, string who)
    {
        Assert.True(expected.FinalConverged == actual.FinalConverged, $"{who}: FinalConverged");
        Assert.Equal(expected.StopReason, actual.StopReason);
        Assert.Equal(expected.Steps.Count, actual.Steps.Count);
        for (int i = 0; i < expected.Steps.Count; i++)
        {
            CascadeStep e = expected.Steps[i], a = actual.Steps[i];
            Assert.Equal((e.Order, e.Device, e.Function, e.BranchName, e.From, e.To, e.Reason, e.ResolveConverged, e.OverloadedBranchesAfter),
                (a.Order, a.Device, a.Function, a.BranchName, a.From, a.To, a.Reason, a.ResolveConverged, a.OverloadedBranchesAfter));
            Assert.Equal(e.TripTimeS.HasValue, a.TripTimeS.HasValue);
            if (e.TripTimeS.HasValue) Assert.True(SameBits(e.TripTimeS!.Value, a.TripTimeS!.Value), $"{who}: step {i} TripTimeS");
        }
    }

    public static TheoryData<string> N299CascadeCases => new() { "case9", "case14", "case30" };

    [Theory]
    [MemberData(nameof(N299CascadeCases))]
    public void N299_every_single_branch_stress_cascade_is_bit_identical_to_the_full_solve(string name)
    {
        // For every branch carrying power, rate it at 40 % of its flow (the stress FaultAndCascadeTests uses) and run the
        // cascade twice: as production runs it (short cut on) and inside the full-solve reference scope (every post-trip
        // network solved: the pre-N299 path). Every step and the stop reason are identical. Some of these cascades open a
        // radial spur or cut a load part off, which is where the short cut acts; the others prove nothing else moved.
        GridModel m = N299Case(name);
        PowerFlowResult probe = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(probe.Converged);
        int stressed = 0;
        for (int k = 0; k < m.Branches.Count; k++)
        {
            LineFlow f = probe.LineFlows[k];
            double sMva = Math.Sqrt(f.PFromMw * f.PFromMw + f.QFromMvar * f.QFromMvar);
            if (!(sMva > 1.0)) continue;
            GridModel s = m.CopyWith(branches: m.Branches.Select((br, j) => j == k ? br with { RateAMva = 0.4 * sMva } : br).ToList());
            PowerFlowResult pre = SolverSelector.CreateNewton(s).Solve(s);
            if (!pre.Converged) continue;

            CascadeResult fast = ProtectionCascade.Simulate(s, pre);
            CascadeResult full;
            using (ProtectionCascade.FullSolveReferenceScope()) full = ProtectionCascade.Simulate(s, pre);
            N299AssertIdentical(full, fast, $"{name} branch {k} ({m.Branches[k].From}-{m.Branches[k].To})");
            stressed++;
        }
        Assert.True(stressed > 0, "no branch was stressed");
    }

    [Fact]
    public void N299_the_case9_spur_trip_takes_the_short_cut_and_keeps_its_verdict()
    {
        // Rate case9 branch 3-6 (the only connection of the bus-3 unit) at 40 % of its flow: the thermal element opens it
        // and the unit's bus is left alone, lossless, with 85 MW it cannot export: a state the N232 proof covers. The
        // cascade stops non-convergent on that trip, as the full solve made it.
        GridModel m = IeeeCases.Case9();
        PowerFlowResult probe = SolverSelector.CreateNewton(m).Solve(m);
        int k = Enumerable.Range(0, m.Branches.Count).Single(j => m.Branches[j].From == 3 && m.Branches[j].To == 6);
        LineFlow f = probe.LineFlows[k];
        double sMva = Math.Sqrt(f.PFromMw * f.PFromMw + f.QFromMvar * f.QFromMvar);
        GridModel s = m.CopyWith(branches: m.Branches.Select((br, j) => j == k ? br with { RateAMva = 0.4 * sMva } : br).ToList());
        PowerFlowResult pre = SolverSelector.CreateNewton(s).Solve(s);

        CascadeResult fast = ProtectionCascade.Simulate(s, pre);
        Assert.True(fast.Cascaded);
        CascadeStep first = fast.Steps[0];
        Assert.Equal((3, 6), (first.From, first.To));
        Assert.False(first.ResolveConverged);
        Assert.False(fast.FinalConverged);
        Assert.StartsWith("non-convergent after opening", fast.StopReason, StringComparison.Ordinal);

        // The post-trip network is one the proof covers (so the re-solve was skipped) ...
        GridModel afterTrip = s.CopyWith(branches: s.Branches.Select((br, j) => j == k ? br with { InService = false } : br).ToList());
        ContingencyAnalysis.UnsolvablePart? part = ContingencyAnalysis.ProvablyUnsolvablePart(afterTrip, new PowerFlowOptions().Tolerance);
        Assert.NotNull(part);
        Assert.Equal(new[] { 3 }, part!.BusIds);

        // ... and the full solve reaches the same verdict.
        CascadeResult full;
        using (ProtectionCascade.FullSolveReferenceScope()) full = ProtectionCascade.Simulate(s, pre);
        N299AssertIdentical(full, fast, "case9 3-6");
    }
}
