using System;
using System.Collections.Generic;
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the classical multi-machine transient-stability kernel against EXACT analytic
/// references - the checks that need no external data and cannot be fabricated:
///   - the single-machine-infinite-bus (SMIB) small-signal mode frequency omega_n = sqrt(omega_s*P_s/2H);
///   - the equal-area critical clearing time (stable just below it, unstable just above);
///   - undamped energy/amplitude boundedness of the RK4 integrator over many cycles;
///   - two-machine COI conservation (an isolated pair does not accelerate its centre of inertia).
/// These relocate machine-epsilon to the places a closed-form oracle exists, per the distributed-model
/// validation discipline (docs/validation/distributed-inertia.md).
/// </summary>
public class ClassicalMultiMachineTests
{
    private const double F0 = 50.0;
    private static double Ws => 2.0 * Math.PI * F0;

    /// <summary>Reduced admittance of a single reactance X between two internal nodes (G = 0).</summary>
    private static Complex[,] ReactanceLink(double x) => new Complex[,]
    {
        { new Complex(0, -1.0 / x), new Complex(0, 1.0 / x) },
        { new Complex(0, 1.0 / x), new Complex(0, -1.0 / x) },
    };

    // ---- SMIB: the classic machine-against-infinite-bus small-signal oscillation ----
    // Pmax = |E1||E2|/X; equilibrium delta0 = asin(Pm/Pmax); Ps = Pmax*cos delta0; omega_n = sqrt(omega_s*Ps/2H).
    [Fact]
    public void Smib_small_signal_mode_matches_the_analytic_frequency()
    {
        const double x = 0.5, e = 1.0, pm = 1.0, hMach = 3.5;
        double pmax = e * e / x;                    // = 2.0
        double dEq = Math.Asin(pm / pmax);          // 30 deg = 0.5236 rad
        double ps = pmax * Math.Cos(dEq);           // synchronising coefficient
        double wn = Math.Sqrt(Ws * ps / (2.0 * hMach));
        double analyticPeriod = 2.0 * Math.PI / wn;

        // Machine 0 = finite mass; machine 1 = infinite bus (huge H pins it at delta = 0).
        var res = ClassicalMultiMachine.Simulate(
            h: new[] { hMach, 1e8 }, d: new[] { 0.0, 0.0 }, pm: new[] { pm, 0.0 },
            eq: new[] { e, e }, delta0: new[] { dEq + 0.02, 0.0 },   // released from a small offset peak
            yReduced: ReactanceLink(x), f0: F0, stepSeconds: 0.001, durationSeconds: 3.0);

        double measured = FirstReturnPeriod(res.Machines[0].TimeSeconds, res.Machines[0].DeltaRad);
        Assert.True(res.StayedInSynchronism);
        Assert.Equal(analyticPeriod, measured, 2);   // ~0.713 s, to 2 dp (<1%)
    }

    // ---- Equal-area criterion: the critical clearing time bounds stability exactly ----
    // Bolted terminal fault (Pe = 0 during the fault) then full restore. deltacr = arccos[(pi-2delta0)sindelta0 - cosdelta0];
    // CCT = sqrt(4H(deltacr-delta0)/(omega_s*Pm)). Clear below => stable; clear above => loss of synchronism.
    [Fact]
    public void Equal_area_critical_clearing_time_bounds_stability()
    {
        const double x = 0.5, e = 1.0, pm = 1.0, hMach = 3.5;
        double pmax = e * e / x;
        double dEq = Math.Asin(pm / pmax);
        double dCr = Math.Acos((Math.PI - 2 * dEq) * Math.Sin(dEq) - Math.Cos(dEq));
        double cct = Math.Sqrt(4.0 * hMach * (dCr - dEq) / (Ws * pm));   // ~ 0.196 s

        var faulted = new Complex[2, 2];   // all-zero => no transfer => Pe = 0 during the fault

        ClassicalMultiMachine.Result StableClear(double clearAt) => ClassicalMultiMachine.Simulate(
            h: new[] { hMach, 1e8 }, d: new[] { 0.0, 0.0 }, pm: new[] { pm, 0.0 },
            eq: new[] { e, e }, delta0: new[] { dEq, 0.0 }, yReduced: ReactanceLink(x),
            f0: F0, stepSeconds: 0.0005, durationSeconds: 6.0,
            fault: new ClassicalMultiMachine.Fault(0.1, 0.1 + clearAt, faulted));

        // A comfortable margin either side of the analytic CCT (~196 ms).
        Assert.True(StableClear(cct - 0.06).StayedInSynchronism, "clearing well before CCT must stay stable");
        Assert.False(StableClear(cct + 0.06).StayedInSynchronism, "clearing well after CCT must lose synchronism");
    }

    // ---- RK4 is energy-behaved: an undamped free oscillation neither grows nor decays ----
    [Fact]
    public void Undamped_oscillation_amplitude_is_bounded_over_many_cycles()
    {
        const double x = 0.5, e = 1.0, pm = 1.0, hMach = 3.5;
        double dEq = Math.Asin(pm / (e * e / x));

        var res = ClassicalMultiMachine.Simulate(
            h: new[] { hMach, 1e8 }, d: new[] { 0.0, 0.0 }, pm: new[] { pm, 0.0 },
            eq: new[] { e, e }, delta0: new[] { dEq + 0.15, 0.0 }, yReduced: ReactanceLink(x),
            f0: F0, stepSeconds: 0.001, durationSeconds: 20.0);   // ~28 cycles

        // Peak excursion in the last second must match the first swing to <1% - no numerical drift.
        IReadOnlyList<double> d0 = res.Machines[0].DeltaRad;
        int perSecond = 1000;
        double firstAmp = MaxAbsDeviation(d0, 0, perSecond, dEq);
        double lastAmp = MaxAbsDeviation(d0, d0.Count - perSecond, d0.Count, dEq);
        Assert.True(res.StayedInSynchronism);
        Assert.Equal(firstAmp, lastAmp, 2);
    }

    // ---- Two isolated equal machines: the centre of inertia does not accelerate ----
    [Fact]
    public void Two_machine_pair_conserves_the_centre_of_inertia()
    {
        const double x = 0.4, e = 1.0, hMach = 4.0;
        // Balanced: machine 0 generates +Pm, machine 1 absorbs -Pm; internal power circulates, COI is fixed.
        double pmax = e * e / x;
        double dRel = Math.Asin(0.8 / pmax);   // relative angle giving Pe = 0.8 pu
        var res = ClassicalMultiMachine.Simulate(
            h: new[] { hMach, hMach }, d: new[] { 0.0, 0.0 }, pm: new[] { 0.8, -0.8 },
            eq: new[] { e, e }, delta0: new[] { dRel + 0.05, 0.0 }, yReduced: ReactanceLink(x),
            f0: F0, stepSeconds: 0.001, durationSeconds: 4.0);

        // COI angle = (H0 delta0 + H1 delta1)/(H0+H1); with equal H and antisymmetric power it must not run away.
        IReadOnlyList<double> d0 = res.Machines[0].DeltaRad, d1 = res.Machines[1].DeltaRad;
        double coi0 = 0.5 * (d0[0] + d1[0]);
        double coiMaxDrift = 0.0;
        for (int k = 0; k < d0.Count; k++)
            coiMaxDrift = Math.Max(coiMaxDrift, Math.Abs(0.5 * (d0[k] + d1[k]) - coi0));
        Assert.True(res.StayedInSynchronism);
        Assert.True(coiMaxDrift < 1e-6, $"COI drifted {coiMaxDrift} rad - the pair should conserve it");
    }

    // ---- WSCC-9 end-to-end: solve -> classical init -> the pre-fault point is a fixed point ----
    // Anderson & Fouad transient reactances (machine base): gen1 0.0608, gen2 0.1198, gen3 0.1813.
    [Fact]
    public void Wscc9_prefault_operating_point_is_stationary()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);

        var xdpByBus = new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = xdpByBus[model.Generators[i].Bus];

        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, pf, xdp);

        // With Pm set to Pe(delta0), an undisturbed run must not move: every rotor angle stays put.
        ClassicalMultiMachine.Result res = ClassicalMultiMachine.Simulate(
            p.H, new double[p.H.Length], p.Pm, p.Eq, p.Delta0, p.YReduced,
            f0: F0, stepSeconds: 0.005, durationSeconds: 3.0);

        Assert.True(res.StayedInSynchronism);
        foreach (ClassicalMultiMachine.MachineTrace tr in res.Machines)
        {
            double drift = 0.0;
            for (int k = 0; k < tr.DeltaRad.Count; k++) drift = Math.Max(drift, Math.Abs(tr.DeltaRad[k] - tr.DeltaRad[0]));
            Assert.True(drift < 1e-3, $"machine drifted {drift} rad from a supposed equilibrium");
        }
    }

    // ---- WSCC-9 dynamics: a rotor-angle bump oscillates but the fleet holds synchronism ----
    [Fact]
    public void Wscc9_small_disturbance_oscillates_and_stays_in_synchronism()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        var xdpByBus = new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = xdpByBus[model.Generators[i].Bus];
        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, pf, xdp);

        double[] bumped = (double[])p.Delta0.Clone();
        bumped[1] += 0.15;   // perturb the second machine

        ClassicalMultiMachine.Result res = ClassicalMultiMachine.Simulate(
            p.H, new double[p.H.Length], p.Pm, p.Eq, bumped, p.YReduced,
            f0: F0, stepSeconds: 0.002, durationSeconds: 5.0);

        Assert.True(res.StayedInSynchronism, "a 0.15 rad bump on WSCC-9 must not lose synchronism");
        // The perturbed machine must actually swing (not frozen) yet return towards the fleet.
        double swing = 0.0;
        IReadOnlyList<double> d1 = res.Machines[1].DeltaRad;
        for (int k = 0; k < d1.Count; k++) swing = Math.Max(swing, Math.Abs(d1[k] - d1[0]));
        Assert.InRange(swing, 0.02, 1.5);
    }

    // ---- WSCC-9 critical clearing time: a finite, physical stability boundary exists ----
    [Fact]
    public void Wscc9_has_a_finite_critical_clearing_time()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        var xdpByBus = new Dictionary<int, double> { [1] = 0.0608, [2] = 0.1198, [3] = 0.1813 };
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = xdpByBus[model.Generators[i].Bus];

        // Self-clearing three-phase fault at bus 7 (the classic WSCC study location).
        double cct = ClassicalInit.CriticalClearingTimeSeconds(model, pf, xdp, faultBusId: 7);

        // A real transient-stability boundary: neither instantaneous-unstable (0) nor never-unstable (max).
        Assert.InRange(cct, 0.02, 0.99);
    }

    // ----- helpers -----

    /// <summary>Period from released-at-peak data: time of the first interior local maximum.</summary>
    private static double FirstReturnPeriod(IReadOnlyList<double> t, IReadOnlyList<double> x)
    {
        for (int k = 5; k < x.Count - 1; k++)
            if (x[k] >= x[k - 1] && x[k] > x[k + 1])
                return t[k];
        throw new InvalidOperationException("no oscillation peak found");
    }

    private static double MaxAbsDeviation(IReadOnlyList<double> x, int from, int to, double about)
    {
        double m = 0.0;
        for (int k = Math.Max(0, from); k < Math.Min(x.Count, to); k++)
            m = Math.Max(m, Math.Abs(x[k] - about));
        return m;
    }
}
