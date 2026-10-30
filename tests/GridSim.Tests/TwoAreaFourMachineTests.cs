using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Intermediate-N transient validation on the symmetric two-area four-machine system
/// (<see cref="IeeeCases.TwoAreaFourMachine"/>) - the band between the analytic N=1/2 oracles and the
/// 3-machine WSCC-9. The defining property of a two-area system is modal SEPARATION: the two areas swing
/// against each other through the weak tie in a slow INTER-AREA mode, while the two machines inside an area
/// swing against each other through the stiff intra-area links in a faster LOCAL mode. By construction
/// (5x weaker tie) the inter-area mode must be the slower one, and the machines in an area must stay
/// coherent when the inter-area mode is excited.
/// </summary>
public class TwoAreaFourMachineTests
{
    // Moved 2026-10 (D2 N017, Ruling R1: Mark delegated EE calls 2026-10-04; source: Kundur, "Power System Stability and
    // Control", 1994, Example 12.6, and IEEE PES TR-18 (2015): the two-area system is a 60 Hz system): the swing is
    // integrated at the case's own nominal frequency instead of a literal 50 Hz. Before: F0 = 50.0. After: the model's
    // FrequencyHz, 60.0. Every assertion here is a ratio or a bound (mode ordering, coherence, boundedness), which the
    // undamped classical swing keeps under the time scaling t -> t * sqrt(60 / 50) that the change amounts to.
    // Moved 2026-10 (END-PASS VERIFY row 4, end pass; the two-area system's 60 Hz confirmed via Kundur Example 12.6 and the
    // IEEE PES PSDP report, TR-18's chapter not confirmed (the task force orders two-area as Benchmark 3), Ruling R1 and
    // Mark's go on the end-pass moves 2026-10-11): comment-only citation correction above, no number moves. Before ->
    // after: "IEEE PES TR-18 (2015) ch. 2" -> "IEEE PES TR-18 (2015)".
    private static readonly double F0 = IeeeCases.TwoAreaFourMachine().FrequencyHz;

    private static ClassicalInit.Prepared Prepare()
    {
        GridModel model = IeeeCases.TwoAreaFourMachine();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged, "the two-area case must solve");
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = 0.05; // identical transient reactance
        return ClassicalInit.FromSolvedCase(model, pf, xdp);
    }

    [Fact]
    public void Prefault_operating_point_is_stationary()
    {
        ClassicalInit.Prepared p = Prepare();
        ClassicalMultiMachine.Result res = ClassicalMultiMachine.Simulate(
            p.H, new double[p.H.Length], p.Pm, p.Eq, p.Delta0, p.YReduced,
            f0: F0, stepSeconds: 0.005, durationSeconds: 3.0);

        Assert.True(res.StayedInSynchronism);
        foreach (ClassicalMultiMachine.MachineTrace tr in res.Machines)
        {
            double drift = 0.0;
            for (int k = 0; k < tr.DeltaRad.Count; k++) drift = Math.Max(drift, Math.Abs(tr.DeltaRad[k] - tr.DeltaRad[0]));
            Assert.True(drift < 2e-3, $"machine drifted {drift} rad from equilibrium");
        }
    }

    [Fact]
    public void Inter_area_mode_is_slower_than_the_local_mode_and_areas_stay_coherent()
    {
        ClassicalInit.Prepared p = Prepare();
        const double dt = 0.002, dur = 12.0, bump = 0.10;

        // Excite the INTER-AREA mode: area 1 (machines 0,1) leads, area 2 (machines 2,3) lags.
        double[] inter = (double[])p.Delta0.Clone();
        inter[0] += bump; inter[1] += bump; inter[2] -= bump; inter[3] -= bump;
        ClassicalMultiMachine.Result ri = ClassicalMultiMachine.Simulate(
            p.H, new double[p.H.Length], p.Pm, p.Eq, inter, p.YReduced, f0: F0, stepSeconds: dt, durationSeconds: dur);
        Assert.True(ri.StayedInSynchronism);

        // Excite the LOCAL mode: machine 0 leads machine 1 WITHIN area 1 (area 2 undisturbed).
        double[] local = (double[])p.Delta0.Clone();
        local[0] += bump; local[1] -= bump;
        ClassicalMultiMachine.Result rl = ClassicalMultiMachine.Simulate(
            p.H, new double[p.H.Length], p.Pm, p.Eq, local, p.YReduced, f0: F0, stepSeconds: dt, durationSeconds: dur);
        Assert.True(rl.StayedInSynchronism);

        // Inter-area coordinate: (area1 mean) - (area2 mean). Local coordinate: machine0 - machine1.
        double[] interSig = Diff(Mean(ri, 0, 1), Mean(ri, 2, 3));
        double[] localSig = Diff(Series(rl, 0), Series(rl, 1));

        double interPeriod = DominantPeriod(interSig, dt);
        double localPeriod = DominantPeriod(localSig, dt);

        Assert.True(interPeriod > localPeriod * 1.3,
            $"inter-area period {interPeriod:F2}s should exceed local period {localPeriod:F2}s (weak tie => slower mode)");

        // Coherency: when the inter-area mode is excited, the two machines inside area 1 stay close together
        // relative to how far the two areas separate.
        double intraAreaSpread = MaxAbs(Diff(Series(ri, 0), Series(ri, 1)));
        double interAreaSep = MaxAbs(interSig);
        Assert.True(intraAreaSpread < 0.3 * interAreaSep,
            $"area-1 machines not coherent: intra spread {intraAreaSpread:F3} vs inter separation {interAreaSep:F3}");
    }

    [Fact]
    public void A_disturbance_produces_bounded_oscillation_about_the_centre_of_inertia()
    {
        // In a LOADED, lossy network the COI is NOT conserved (sum(Pm - Pe) != 0 - that idealised
        // invariant is tested at N=2 on a lossless link). The meaningful N=4 property is that the
        // COI-REFERENCED rotor angles oscillate and stay bounded: the fleet holds synchronism.
        ClassicalInit.Prepared p = Prepare();
        double[] d0 = (double[])p.Delta0.Clone();
        d0[0] += 0.08; d0[1] += 0.08; d0[2] -= 0.08; d0[3] -= 0.08;
        ClassicalMultiMachine.Result res = ClassicalMultiMachine.Simulate(
            p.H, new double[p.H.Length], p.Pm, p.Eq, d0, p.YReduced, f0: F0, stepSeconds: 0.001, durationSeconds: 6.0);

        Assert.True(res.StayedInSynchronism);
        double htot = 0; for (int i = 0; i < p.H.Length; i++) htot += p.H[i];
        int n = res.Machines[0].DeltaRad.Count;
        double maxRel = 0, minRel0 = double.MaxValue, maxRel0 = double.MinValue;
        for (int k = 0; k < n; k++)
        {
            double coi = Coi(res, p.H, htot, k);
            for (int i = 0; i < p.H.Length; i++)
            {
                double rel = res.Machines[i].DeltaRad[k] - coi;
                maxRel = Math.Max(maxRel, Math.Abs(rel));
                if (i == 0) { minRel0 = Math.Min(minRel0, rel); maxRel0 = Math.Max(maxRel0, rel); }
            }
        }
        Assert.True(maxRel < 1.0, $"relative rotor angle {maxRel:F3} rad - fleet not holding together");
        Assert.True(maxRel0 - minRel0 > 0.05, "machine 0 should oscillate about the COI, not sit frozen");
    }

    // ---- helpers ----
    private static double[] Series(ClassicalMultiMachine.Result r, int machine)
    {
        IReadOnlyList<double> d = r.Machines[machine].DeltaRad;
        var a = new double[d.Count];
        for (int k = 0; k < d.Count; k++) a[k] = d[k];
        return a;
    }

    private static double[] Mean(ClassicalMultiMachine.Result r, int m0, int m1)
    {
        IReadOnlyList<double> a = r.Machines[m0].DeltaRad, b = r.Machines[m1].DeltaRad;
        var s = new double[a.Count];
        for (int k = 0; k < a.Count; k++) s[k] = 0.5 * (a[k] + b[k]);
        return s;
    }

    private static double[] Diff(double[] a, double[] b)
    {
        var s = new double[a.Length];
        for (int k = 0; k < a.Length; k++) s[k] = a[k] - b[k];
        return s;
    }

    private static double MaxAbs(double[] a)
    {
        double m = 0; foreach (double v in a) m = Math.Max(m, Math.Abs(v)); return m;
    }

    private static double Coi(ClassicalMultiMachine.Result r, double[] h, double htot, int k)
    {
        double s = 0;
        for (int i = 0; i < h.Length; i++) s += h[i] * r.Machines[i].DeltaRad[k];
        return s / htot;
    }

    // Dominant oscillation period via mean-crossing counting: period ~ 2 * window / (number of crossings).
    private static double DominantPeriod(double[] sig, double dt)
    {
        double mean = 0; foreach (double v in sig) mean += v; mean /= sig.Length;
        int crossings = 0;
        double prev = sig[0] - mean;
        for (int k = 1; k < sig.Length; k++)
        {
            double cur = sig[k] - mean;
            if ((prev <= 0 && cur > 0) || (prev >= 0 && cur < 0)) crossings++;
            prev = cur;
        }
        double window = (sig.Length - 1) * dt;
        return crossings > 0 ? 2.0 * window / crossings : window;
    }
}
