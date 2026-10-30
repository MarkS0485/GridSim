using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Dynamics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Storage;
using GridSim.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>DOC-16 N262: see the summary of <see cref="Doc16NumericsDeterminismTests"/>.</summary>
public partial class Doc16NumericsDeterminismTests
{
    // =================================================================================================================
    // N262: the near-tie rule in the corrective redispatch and the storage scheduler.
    // =================================================================================================================

    /// <summary>The cases the corrective redispatch is measured on (rated lines, so post-outage overloads arise), and the
    /// largest number of single-branch outages taken per case (evenly spread over the branch list).</summary>
    public static IEnumerable<object[]> CorrectiveCases() => new[]
    {
        new object[] { "lib/case9.m", 1000 }, new object[] { "lib/case14.m", 1000 }, new object[] { "lib/case30.m", 1000 },
        new object[] { "lib/case57.m", 1000 }, new object[] { "lib/case118.m", 1000 }, new object[] { "lib/case24_ieee_rts.m", 1000 },
        new object[] { "lib/case145.m", 200 }, new object[] { "lib/case300.m", 200 },
        new object[] { "nesta/pglib_opf_case5_pjm.m", 1000 }, new object[] { "nesta/pglib_opf_case30_as.m", 1000 },
        new object[] { "rts/pglib_opf_case73_ieee_rts.m", 1000 }, new object[] { "nesta/pglib_opf_case162_ieee_dtc.m", 200 },
        new object[] { "nesta/pglib_opf_case240_pserc.m", 200 }, new object[] { "nesta/pglib_opf_case588_sdet.m", 150 },
        new object[] { "uk", 1000 }, new object[] { "intl/ireland-all-island", 1000 }, new object[] { "gb-synthetic", 200 },
    };

    private static double LpObjective(BoundedSimplex.CapturedLp lp)
    {
        double s = 0;
        for (int j = 0; j < lp.C.Length; j++) s += lp.C[j] * lp.X[j];
        return s;
    }

    /// <summary>Every entry of the LP's matrix that is not 0 or +-1 (the PTDF sensitivities; the balance rows and slack
    /// columns are exact +-1) scaled by 1 + e, e uniform in [-1e-13, 1e-13]: DOC-11's perturbation, the size of a change
    /// in summation order.</summary>
    private static double[,] Nudged(double[,] a, int seed)
    {
        var rng = new Random(seed);
        var b = (double[,])a.Clone();
        for (int i = 0; i < b.GetLength(0); i++)
            for (int j = 0; j < b.GetLength(1); j++)
                if (b[i, j] != 0.0 && Math.Abs(b[i, j]) != 1.0) b[i, j] *= 1 + 1e-13 * (rng.NextDouble() * 2 - 1);
        return b;
    }

    [Theory]
    [MemberData(nameof(CorrectiveCases))]
    public void N262_corrective_redispatch_is_bit_identical_where_no_near_tie_is_met(string name, int maxOutages)
    {
        GridModel m = Load(name);
        int nb = m.Branches.Count;
        int stride = Math.Max(1, (nb + maxOutages - 1) / maxOutages);
        int planned = 0, lpPlans = 0, tied = 0, moved = 0, thrown = 0;
        double worstMove = 0;
        var tiedLps = new List<BoundedSimplex.CapturedLp>();
        for (int i = 0; i < nb; i += stride)
        {
            if (!m.Branches[i].InService) continue;
            GridModel post = m.CopyWith(branches: m.Branches.Where((_, j) => j != i).ToList());
            var sink = new List<BoundedSimplex.CapturedLp>();
            CorrectiveRedispatch on, off;
            try
            {
                using (BoundedSimplex.CaptureScope(sink)) on = ContingencyAnalysis.PlanCorrectiveRedispatch(post);
            }
            catch (InvalidDataException ex)
            {
                // Both rules refuse the same input (the LP never ran).
                Assert.Equal(ex.Message, Assert.Throws<InvalidDataException>(() => ContingencyAnalysis.PlanCorrectiveRedispatch(post, 0.0)).Message);
                thrown++;
                continue;
            }
            off = ContingencyAnalysis.PlanCorrectiveRedispatch(post, 0.0);
            planned++;
            Assert.All(sink, lp => Assert.Equal(BoundedSimplex.NearTiePricingMargin, lp.PricingTieMargin));   // production passes the rule
            if (sink.Count > 0) lpPlans++;
            int ties = sink.Sum(lp => lp.TieBreaks);
            Assert.Equal(off.Outcome, on.Outcome);
            Assert.Equal(off.LpStatus, on.LpStatus);
            bool same = Bits(on.MovedMw) == Bits(off.MovedMw);
            for (int g = 0; g < m.Generators.Count && same; g++)
                same = Bits(on.Model.Generators[g].Pg) == Bits(off.Model.Generators[g].Pg);
            if (ties == 0)
            {
                // Proof by construction: no pivot was decided by the margin, so the plan is the strict rule's to the bit.
                Assert.True(same, $"{name} outage {i}: no tie-break, yet the plan differs from the strict rule");
                continue;
            }
            tied++;
            tiedLps.AddRange(sink.Where(lp => lp.TieBreaks > 0));
            if (same) continue;
            moved++;
            // A tie-broken plan is another optimum of the same minimum-change LP: the same objective, to round-off.
            var offSink = new List<BoundedSimplex.CapturedLp>();
            using (BoundedSimplex.CaptureScope(offSink)) ContingencyAnalysis.PlanCorrectiveRedispatch(post, 0.0);
            double objOn = LpObjective(sink[^1]), objOff = LpObjective(offSink[^1]);
            double move = 0;
            for (int g = 0; g < m.Generators.Count; g++) move = Math.Max(move, Math.Abs(on.Model.Generators[g].Pg - off.Model.Generators[g].Pg));
            worstMove = Math.Max(worstMove, move);
            _out.WriteLine($"{name} outage {i}: MOVED (tie-breaks {ties}); moved MW {R(off.MovedMw)} -> {R(on.MovedMw)}; "
                + $"LP objective {R(objOff)} -> {R(objOn)}; largest unit change {R(move)} MW");
            if (sink.Count == offSink.Count)
                Assert.True(Math.Abs(objOn - objOff) <= 1e-9 * Math.Max(1.0, Math.Abs(objOff)), $"{name} outage {i}: objective {R(objOn)} vs {R(objOff)}");
        }
        _out.WriteLine($"{name}: {planned} outages planned ({thrown} refused by both), {lpPlans} ran the LP, {tied} met a near-tie, "
            + $"{moved} moved; largest unit change on a moved plan {R(worstMove)} MW");

        // DOC-11's robustness measure on the LPs where a tie was broken: 20 perturbations of the sensitivities each.
        double worstOn = 0, worstOff = 0;
        int trialsOffMoved = 0, trials = 0;
        foreach (BoundedSimplex.CapturedLp lp in tiedLps.Take(30))
        {
            var baseOn = BoundedSimplex.SolveCounted(lp.A, lp.B, lp.C, lp.Lo, lp.Hi, lp.Tol, lp.MaxIter, BoundedSimplex.NearTiePricingMargin);
            var baseOff = BoundedSimplex.SolveCounted(lp.A, lp.B, lp.C, lp.Lo, lp.Hi, lp.Tol, lp.MaxIter, 0.0);
            for (int seed = 1; seed <= 20; seed++)
            {
                double[,] a = Nudged(lp.A, seed);
                var nOn = BoundedSimplex.SolveCounted(a, lp.B, lp.C, lp.Lo, lp.Hi, lp.Tol, lp.MaxIter, BoundedSimplex.NearTiePricingMargin);
                var nOff = BoundedSimplex.SolveCounted(a, lp.B, lp.C, lp.Lo, lp.Hi, lp.Tol, lp.MaxIter, 0.0);
                double mOn = 0, mOff = 0;
                for (int j = 0; j < lp.X.Length; j++)
                {
                    if (nOn.Status == LpStatus.Optimal && baseOn.Status == LpStatus.Optimal) mOn = Math.Max(mOn, Math.Abs(nOn.X[j] - baseOn.X[j]));
                    if (nOff.Status == LpStatus.Optimal && baseOff.Status == LpStatus.Optimal) mOff = Math.Max(mOff, Math.Abs(nOff.X[j] - baseOff.X[j]));
                }
                worstOn = Math.Max(worstOn, mOn);
                worstOff = Math.Max(worstOff, mOff);
                if (mOff > 1e-6) trialsOffMoved++;
                trials++;
            }
        }
        _out.WriteLine($"{name}: perturbation (1e-13 relative on every sensitivity, 20 seeds x {Math.Min(30, tiedLps.Count)} tie-broken LPs = {trials} trials): "
            + $"largest move tie-break on {R(worstOn)}, strict rule {R(worstOff)} ({trialsOffMoved} strict trials moved more than 1e-6)");
    }

    /// <summary>A plausible battery envelope (10 MW / 20 MWh usable, half full, 95 % each way).</summary>
    private static StorageEnvelope Battery() => new(10.0, 10.0, 2.0, 22.0, 12.0, 0.95, 0.95);

    public static IEnumerable<object[]> PriceSeries() => new[]
    {
        new object[] { "gb-day-48-half-hours", 0.0 }, new object[] { "gb-day-48-half-hours", 5.0 },
        new object[] { "gb-day-24-hours", 0.0 }, new object[] { "gb-two-days-96-half-hours", 0.0 },
        new object[] { "flat-48", 0.0 }, new object[] { "flat-48", 5.0 }, new object[] { "two-level-48", 0.0 },
        new object[] { "random-48", 0.0 }, new object[] { "random-48", 5.0 },
    };

    private static (double[] Prices, double StepHours) Series(string name)
    {
        switch (name)
        {
            case "gb-day-48-half-hours": return (Enumerable.Range(0, 48).Select(k => SyntheticPrices.GbDay(k * 0.5)).ToArray(), 0.5);
            case "gb-day-24-hours": return (Enumerable.Range(0, 24).Select(k => SyntheticPrices.GbDay(k)).ToArray(), 1.0);
            case "gb-two-days-96-half-hours": return (Enumerable.Range(0, 96).Select(k => SyntheticPrices.GbDay((k % 48) * 0.5)).ToArray(), 0.5);
            case "flat-48": return (Enumerable.Repeat(50.0, 48).ToArray(), 0.5);
            case "two-level-48": return (Enumerable.Range(0, 48).Select(k => k % 12 < 6 ? 40.0 : 60.0).ToArray(), 0.5);
            case "random-48":
            {
                var rng = new Random(20261005);
                return (Enumerable.Range(0, 48).Select(_ => 20.0 + 100.0 * rng.NextDouble()).ToArray(), 0.5);
            }
            default: throw new ArgumentException(name, nameof(name));
        }
    }

    [Theory]
    [MemberData(nameof(PriceSeries))]
    public void N262_storage_schedule_is_bit_identical_where_no_near_tie_is_met(string series, double degradation)
    {
        (double[] prices, double dt) = Series(series);
        StorageEnvelope env = Battery();
        var sink = new List<BoundedSimplex.CapturedLp>();
        StorageLpScheduler.Plan on;
        using (BoundedSimplex.CaptureScope(sink)) on = StorageLpScheduler.Solve(env, prices, dt, degradation);
        StorageLpScheduler.Plan off = StorageLpScheduler.Solve(env, prices, dt, degradation, null, 0.0);
        Assert.Single(sink);
        Assert.Equal(BoundedSimplex.NearTiePricingMargin, sink[0].PricingTieMargin);
        Assert.Equal(off.Status, on.Status);
        int ties = sink[0].TieBreaks;
        int movedSteps = 0;
        double worst = 0;
        for (int t = 0; t < prices.Length; t++)
        {
            if (Bits(on.NetMw[t]) != Bits(off.NetMw[t]) || Bits(on.EnergyMwh[t]) != Bits(off.EnergyMwh[t])) movedSteps++;
            worst = Math.Max(worst, Math.Abs(on.NetMw[t] - off.NetMw[t]));
        }
        bool objSame = Bits(on.ObjectiveGbp) == Bits(off.ObjectiveGbp);
        _out.WriteLine($"{series}, degradation {R(degradation)}: tie-breaks {ties}; steps moved {movedSteps} of {prices.Length} "
            + $"(largest |dMW| {R(worst)}); objective {R(off.ObjectiveGbp)} -> {R(on.ObjectiveGbp)}");
        if (ties == 0)
        {
            Assert.Equal(0, movedSteps);   // no pivot decided by the margin: the strict schedule, bit for bit
            Assert.True(objSame);
        }
        else if (on.Status == LpStatus.Optimal)
        {
            Assert.True(Math.Abs(on.ObjectiveGbp - off.ObjectiveGbp) <= 1e-9 * Math.Max(1.0, Math.Abs(off.ObjectiveGbp)),
                $"{series}: objective {R(on.ObjectiveGbp)} vs {R(off.ObjectiveGbp)}");
        }

        // DOC-11's robustness measure: every price scaled by 1 + e, e uniform in [-1e-13, 1e-13], 20 seeds.
        double worstOn = 0, worstOff = 0;
        int offMoved = 0;
        for (int seed = 1; seed <= 20; seed++)
        {
            var rng = new Random(seed);
            double[] p = prices.Select(v => v * (1 + 1e-13 * (rng.NextDouble() * 2 - 1))).ToArray();
            StorageLpScheduler.Plan nOn = StorageLpScheduler.Solve(env, p, dt, degradation);
            StorageLpScheduler.Plan nOff = StorageLpScheduler.Solve(env, p, dt, degradation, null, 0.0);
            double mOn = 0, mOff = 0;
            for (int t = 0; t < prices.Length; t++)
            {
                mOn = Math.Max(mOn, Math.Abs(nOn.NetMw[t] - on.NetMw[t]));
                mOff = Math.Max(mOff, Math.Abs(nOff.NetMw[t] - off.NetMw[t]));
            }
            worstOn = Math.Max(worstOn, mOn);
            worstOff = Math.Max(worstOff, mOff);
            if (mOff > 1e-6) offMoved++;
        }
        _out.WriteLine($"{series}, degradation {R(degradation)}: 1e-13 price perturbation, 20 seeds: largest schedule move tie-break on {R(worstOn)} MW, "
            + $"strict rule {R(worstOff)} MW ({offMoved} of 20 strict trials moved more than 1e-6 MW)");
    }
}
