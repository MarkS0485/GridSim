using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N048 (October 2026, best-judgement ruling): stability is not always monotone in the clearing time, so a CCT
/// bisection can land on the upper edge of a stable pocket above the first loss of synchronism. Both CCT searches
/// (RMS and classical) now share <see cref="CctSearch"/>: the unchanged bisection, a downward verification below the
/// stable end (re-searching lower when it finds a loss) and an upward probe above the unstable end, so the CCT is the
/// FIRST loss from below and a stable pocket is flagged (<see cref="CctBracket.NonMonotone"/>).
/// </summary>
public class CctFirstLossSearchTests
{
    private const double Step = 0.002;

    // A survival function with a trial counter, so the cost of a search is pinned too.
    private sealed class Oracle
    {
        private readonly Func<int, bool> _f;
        public int Calls;
        public Oracle(Func<int, bool> f) => _f = f;
        public bool Survives(int s) { Calls++; return _f(s); }
    }

    [Fact]
    public void A_monotone_case_gives_the_bisection_answer_and_no_flag()
    {
        var o = new Oracle(s => s <= 99);
        CctBracket b = CctSearch.Run(o.Survives, 500, Step, 18, CctSearch.DefaultScanSteps);
        Assert.Equal(99, b.StableSteps);
        Assert.Equal(100, b.UnstableSteps);
        Assert.False(b.NonMonotone);
        Assert.Null(b.PocketStableSteps);
        Assert.Equal(16, b.ScanSteps);
        Assert.Equal(o.Calls, b.Trials);   // every trial memoised: the count is exactly the simulations run
    }

    [Fact]
    public void A_scan_depth_of_zero_is_the_pre_N048_bisection_trial_for_trial()
    {
        // The bisection the searches ran before N048: trial 0, trial M, then integer halving.
        static (int Lo, int? Hi, int Calls) OldBisection(Func<int, bool> f, int max)
        {
            int calls = 0;
            bool S(int s) { calls++; return f(s); }
            if (!S(0)) return (0, 0, calls);
            if (S(max)) return (max, null, calls);
            int lo = 0, hi = max;
            for (int i = 0; i < 18 && hi - lo > 1; i++) { int mid = lo + (hi - lo) / 2; if (S(mid)) lo = mid; else hi = mid; }
            return (lo, hi, calls);
        }
        foreach (Func<int, bool> f in new Func<int, bool>[] { s => s <= 99, s => s <= 171 || s == 174, s => s <= 100 || (s >= 120 && s <= 130), s => true })
        {
            var o = new Oracle(f);
            CctBracket b = CctSearch.Run(o.Survives, 500, Step, 18, scanSteps: 0);
            (int lo, int? hi, int calls) = OldBisection(f, 500);
            Assert.Equal(lo, b.StableSteps);
            Assert.Equal(hi, b.UnstableSteps);
            Assert.Equal(calls, o.Calls);
            Assert.False(b.NonMonotone);
        }
    }

    [Fact]
    public void A_bisection_that_lands_on_a_pocket_is_sent_back_to_the_first_loss()
    {
        // Holds to 100 steps, loses 101-119, holds again 120-130 (a pocket), loses from 131. The bisection alone visits
        // 0 S, 250 U, 125 S, 187 U, 156 U, 140 U, 132 U, 128 S, 130 S, 131 U and reports 130 steps: wrong.
        var o = new Oracle(s => s <= 100 || (s >= 120 && s <= 130));
        Assert.Equal(130, CctSearch.Run(o.Survives, 250, Step, 18, scanSteps: 0).StableSteps);

        var o2 = new Oracle(s => s <= 100 || (s >= 120 && s <= 130));
        CctBracket b = CctSearch.Run(o2.Survives, 250, Step, 18, CctSearch.DefaultScanSteps);
        Assert.Equal(100, b.StableSteps);
        Assert.Equal(101, b.UnstableSteps);
        Assert.True(b.NonMonotone);
        Assert.Equal(130, b.PocketStableSteps);
        Assert.Equal(130 * Step, b.PocketStableSeconds);
        Assert.Equal(0.2, b.StableSeconds);
        Assert.Equal(o2.Calls, b.Trials);
    }

    [Fact]
    public void A_pocket_above_a_correct_landing_is_flagged_without_moving_the_cct()
    {
        // The WSCC-9 bus 2 classical pattern on the 2 ms grid (before N047): holds to 171, loses 172-173, holds at 174,
        // loses from 175. The bisection lands on 171 (right); the upward probe finds 174.
        CctBracket b = CctSearch.Run(s => s <= 171 || s == 174, 500, Step, 18, CctSearch.DefaultScanSteps);
        Assert.Equal(171, b.StableSteps);
        Assert.Equal(172, b.UnstableSteps);
        Assert.True(b.NonMonotone);
        Assert.Equal(174, b.PocketStableSteps);
    }

    [Fact]
    public void Repeated_pockets_fall_back_to_an_exact_walk_and_still_find_the_first_loss()
    {
        // Holds to 10 steps, then holds only on every fourth step: a pocket one step wide every four steps, so every
        // re-bisection can land on another pocket. Whatever path the search takes, the answer is the first loss, 11.
        var o = new Oracle(s => s <= 10 || s % 4 == 0);
        CctBracket b = CctSearch.Run(o.Survives, 500, Step, 18, CctSearch.DefaultScanSteps);
        Assert.Equal(10, b.StableSteps);
        Assert.Equal(11, b.UnstableSteps);
        Assert.True(b.NonMonotone);
        Assert.True(b.PocketStableSteps >= 12);
        Assert.Equal(0, b.PocketStableSteps!.Value % 4);
        Assert.True(o.Calls <= 501, $"the search ran {o.Calls} trials, more than a full linear scan");
    }

    [Fact]
    public void A_whole_window_that_survives_at_its_end_is_still_verified_below()
    {
        // Every clearing time holds except 495 steps: the end of the window holds, but it is a pocket above a loss.
        CctBracket b = CctSearch.Run(s => s != 495, 500, Step, 18, CctSearch.DefaultScanSteps);
        Assert.Equal(494, b.StableSteps);
        Assert.Equal(495, b.UnstableSteps);
        Assert.True(b.NonMonotone);
        Assert.Equal(500, b.PocketStableSteps);
    }

    [Fact]
    public void Unstable_at_zero_is_unchanged_and_costs_one_trial()
    {
        CctBracket b = CctSearch.Run(s => false, 500, Step, 18, CctSearch.DefaultScanSteps);
        Assert.True(b.UnstableAtZero);
        Assert.Equal(0.0, b.StableSeconds);
        Assert.Equal(1, b.Trials);
        Assert.False(b.NonMonotone);
    }

    [Fact]
    public void The_search_fails_loud_on_meaningless_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => CctSearch.Run(null!, 500, Step, 18, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => CctSearch.Run(s => true, 0, Step, 18, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => CctSearch.Run(s => true, 500, Step, 0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => CctSearch.Run(s => true, 500, Step, 18, -1));

        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9Classical();
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, scanSteps: -1));
        (GridModel m2, PowerFlowResult pf2, IReadOnlyList<SubtransientInit.MachineData> data) = Case9Rms();
        Assert.Throws<ArgumentOutOfRangeException>(() => RmsTransientStability.CriticalClearingBracket(m2, pf2, data, 7, scanSteps: -1));
    }

    // ---------------- WSCC-9 ----------------

    private static (GridModel Model, PowerFlowResult Pf, double[] Xdp) Case9Classical()
    {
        // Moved 2026-10 (D2 N017 / R238, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003,
        // Example 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now the WSCC system's 60 Hz and the CCT / modal paths
        // take the model's frequency. These tests pin the SEARCH / ASSEMBLY ALGORITHM on the network their numbers were captured
        // on, so they keep that network at its old 50 Hz nominal. Before: IeeeCases.Case9() (then 50 Hz). After: the same model
        // CopyWith(frequencyHz: 50.0); every pinned number below is unchanged. The 60 Hz case9 values are new pins.
        GridModel model = IeeeCases.Case9().CopyWith(frequencyHz: 50.0);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = IeeeCases.Wscc9TransientReactancePu[model.Generators[i].Bus];
        return (model, pf, xdp);
    }

    private static (GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Case9Rms()
    {
        // Moved 2026-10 (D2 N017 / R238, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003,
        // Example 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now the WSCC system's 60 Hz and the CCT / modal paths
        // take the model's frequency. These tests pin the SEARCH / ASSEMBLY ALGORITHM on the network their numbers were captured
        // on, so they keep that network at its old 50 Hz nominal. Before: IeeeCases.Case9() (then 50 Hz). After: the same model
        // CopyWith(frequencyHz: 50.0); every pinned number below is unchanged. The 60 Hz case9 values are new pins.
        GridModel model = IeeeCases.Case9().CopyWith(frequencyHz: 50.0);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
        {
            double xdp = IeeeCases.Wscc9TransientReactancePu[gen.Bus];
            data.Add(new SubtransientInit.MachineData(H: gen.H, Damping: 0.0,
                Xd: 3.0 * xdp, Xdp: xdp, Xdpp: 0.8 * xdp, Xq: 2.5 * xdp, Xqp: 1.5 * xdp, Xqpp: 0.8 * xdp,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05));
        }
        return (model, pf, data);
    }

    // Exhaustive classical scan of one bus over the whole 1 s window, exactly as the search builds each trial.
    private static bool[] ClassicalScan(GridModel model, PowerFlowResult pf, double[] xdp, int bus)
    {
        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, pf, xdp, bus, holdNonMachines: true);
        var ok = new bool[501];
        for (int s = 0; s <= 500; s++)
        {
            double clear = s * Step;
            var fault = new ClassicalMultiMachine.Fault(ClassicalInit.CctOnsetSeconds, ClassicalInit.CctOnsetSeconds + clear, p.YReducedFaulted!);
            ok[s] = ClassicalMultiMachine.Simulate(p.H, new double[p.H.Length], p.Pm, p.Eq, p.Delta0, p.YReduced,
                50.0, Step, ClassicalInit.CctOnsetSeconds + clear + ClassicalInit.CctPostClearWindowSeconds, fault).StayedInSynchronism;
        }
        return ok;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Classical_case9_cct_is_the_first_loss_of_an_exhaustive_scan_and_pockets_are_flagged(int bus)
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9Classical();
        bool[] ok = ClassicalScan(model, pf, xdp, bus);
        int firstLoss = Array.IndexOf(ok, false);
        Assert.True(firstLoss > 0);

        CctBracket b = ClassicalInit.CriticalClearingBracket(model, pf, xdp, bus, holdNonMachines: true);
        Assert.Equal(firstLoss - 1, b.StableSteps);
        Assert.Equal(firstLoss, b.UnstableSteps);

        // The flag is exactly "some step within the probe window above the first loss holds", and the pocket is the
        // highest such step.
        int? expectedPocket = null;
        for (int s = firstLoss + 1; s <= Math.Min(500, firstLoss + CctSearch.DefaultScanSteps); s++) if (ok[s]) expectedPocket = s;
        Assert.Equal(expectedPocket, b.PocketStableSteps);
    }

    [Fact]
    public void Rms_case9_bus7_is_monotone_and_unchanged()
    {
        // R106 pinned 302 steps; the RMS model is monotone around it, so N048 changes nothing but the cost.
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case9Rms();
        CctBracket b = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7);
        Assert.Equal(302, b.StableSteps);
        Assert.Equal(303, b.UnstableSteps);
        Assert.False(b.NonMonotone);
        Assert.Equal(CctSearch.DefaultScanSteps, b.ScanSteps);
        CctBracket old = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7, scanSteps: 0);
        Assert.Equal(b.StableSteps, old.StableSteps);
        Assert.Equal(b.UnstableSteps, old.UnstableSteps);
        Assert.True(b.Trials > old.Trials);
    }
}
