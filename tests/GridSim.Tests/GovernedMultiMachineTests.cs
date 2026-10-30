using System;
using System.Numerics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of governor-in-the-loop transient stability (<see cref="GovernedMultiMachine"/>) against the
/// two oracles it is built to meet:
///   - the REDUCTION IDENTITY: with no governors the governed swing is bit-for-bit the constant-Pm
///     classical swing (the governor hook is a true no-op), so the machine-epsilon numerics of the trusted
///     kernel are inherited unchanged;
///   - the ANALYTIC DROOP: for an islanded machine pair a sustained mechanical-reference step deltaP
///     settles the speed deviation to omegabar_ss = deltaP / sum(1/R_i + D_i), the closed-form final value.
/// Plus a stationarity check that a governed machine started in equilibrium stays there.
/// </summary>
public class GovernedMultiMachineTests
{
    private const double F0 = 50.0;

    /// <summary>Reduced admittance of a single lossless reactance X between two internal nodes (G = 0).</summary>
    private static Complex[,] ReactanceLink(double x) => new Complex[,]
    {
        { new Complex(0, -1.0 / x), new Complex(0, 1.0 / x) },
        { new Complex(0, 1.0 / x), new Complex(0, -1.0 / x) },
    };

    private static Tgov1 Governor(double r) => new(
        droopPu: r, valveTimeSeconds: 0.3, reheatLeadSeconds: 0.0, reheatLagSeconds: 2.0,
        turbineDampingPu: 0.0, valveMinPu: -10.0, valveMaxPu: 10.0);

    // ---- Reduction identity: no governors => bit-for-bit the classical swing ----
    [Fact]
    public void Governed_swing_with_no_governors_is_bit_for_bit_the_classical_swing()
    {
        double[] h = { 3.5, 4.0 }, d = { 0.5, 0.6 }, pm = { 0.8, -0.8 }, eq = { 1.05, 1.02 };
        double[] delta0 = { 0.35, 0.0 };
        Complex[,] y = ReactanceLink(0.4);
        var fault = new ClassicalMultiMachine.Fault(0.1, 0.25, new Complex[2, 2]); // bolted, self-clearing

        ClassicalMultiMachine.Result classical = ClassicalMultiMachine.Simulate(
            h, d, pm, eq, delta0, y, F0, 0.002, 4.0, fault);
        ClassicalMultiMachine.Result governed = GovernedMultiMachine.Simulate(
            h, d, pm, eq, delta0, y, new Tgov1?[] { null, null }, F0, 0.002, 4.0, fault);

        Assert.Equal(classical.StayedInSynchronism, governed.StayedInSynchronism);
        for (int m = 0; m < 2; m++)
        {
            var c = classical.Machines[m];
            var g = governed.Machines[m];
            Assert.Equal(c.DeltaRad.Count, g.DeltaRad.Count);
            for (int k = 0; k < c.DeltaRad.Count; k++)
            {
                Assert.True(c.DeltaRad[k] == g.DeltaRad[k]);   // exact, not approximate
                Assert.True(c.SpeedPu[k] == g.SpeedPu[k]);
            }
        }
    }

    // ---- Stationarity: a governed machine started in equilibrium stays put ----
    [Fact]
    public void A_governed_machine_started_in_equilibrium_is_stationary()
    {
        const double x = 0.5, theta = 0.3;
        double p = Math.Sin(theta) / x;               // Pe of machine 0 at this angle
        double[] h = { 4.0, 4.0 }, d = { 0.0, 0.0 }, pm = { p, -p }, eq = { 1.0, 1.0 };
        double[] delta0 = { theta, 0.0 };

        ClassicalMultiMachine.Result res = GovernedMultiMachine.Simulate(
            h, d, pm, eq, delta0, ReactanceLink(x),
            new Tgov1?[] { Governor(0.05), Governor(0.05) }, F0, 0.005, 20.0);

        Assert.True(res.StayedInSynchronism);
        int last = res.Machines[0].SpeedPu.Count - 1;
        for (int m = 0; m < 2; m++)
        {
            Assert.True(Math.Abs(res.Machines[m].SpeedPu[last]) < 1e-6);           // no speed drift
            Assert.Equal(delta0[m], res.Machines[m].DeltaRad[last], 6);            // no angle drift
        }
    }

    // ---- Analytic droop: islanded pair, sustained set-point step ----
    [Fact]
    public void Islanded_pair_settles_to_the_analytic_droop_speed()
    {
        const double r = 0.05, damp = 2.0, deltaP = 1.0;
        double[] h = { 4.0, 4.0 }, d = { damp, damp }, pm0 = { 0.0, 0.0 }, eq = { 1.0, 1.0 };
        double[] delta0 = { 0.0, 0.0 };                          // pm0 = Pe = 0: an equilibrium

        // Step machine 0's mechanical reference up by deltaP; machine 1 unchanged.
        ClassicalMultiMachine.Result res = GovernedMultiMachine.Simulate(
            h, d, pm0, eq, delta0, ReactanceLink(0.5),
            new Tgov1?[] { Governor(r), Governor(r) },
            F0, 0.01, 80.0, fault: null, slipLimitRad: 2.0 * Math.PI,
            mechanicalReference: new[] { deltaP, 0.0 });

        Assert.True(res.StayedInSynchronism);

        // omegabar_ss = deltaP / sum(1/R_i + D_i) = 1 / ((20+2)+(20+2)) = 1/44.
        double expected = deltaP / ((1.0 / r + damp) + (1.0 / r + damp));
        int last = res.Machines[0].SpeedPu.Count - 1;
        double m0 = res.Machines[0].SpeedPu[last], m1 = res.Machines[1].SpeedPu[last];

        Assert.True(Math.Abs(m0 - m1) < 1e-6);                  // synchronised: one common speed
        Assert.True(Math.Abs(m0 - expected) < 0.01 * expected); // within 1% of the closed-form droop
    }

    // ---- GR-560: machine-base governors on a system-base swing ----
    [Fact]
    public void Gr560_unit_base_ratios_are_bit_for_bit_the_system_base_path()
    {
        double[] h = { 4.0, 4.0 }, d = { 2.0, 2.0 }, pm0 = { 0.0, 0.0 }, eq = { 1.0, 1.0 };
        double[] delta0 = { 0.0, 0.0 };
        ClassicalMultiMachine.Result plain = GovernedMultiMachine.Simulate(
            h, d, pm0, eq, delta0, ReactanceLink(0.5), new Tgov1?[] { Governor(0.05), Governor(0.05) },
            F0, 0.01, 10.0, mechanicalReference: new[] { 0.5, 0.0 });
        ClassicalMultiMachine.Result unit = GovernedMultiMachine.Simulate(
            h, d, pm0, eq, delta0, ReactanceLink(0.5), new Tgov1?[] { Governor(0.05), Governor(0.05) },
            F0, 0.01, 10.0, mechanicalReference: new[] { 0.5, 0.0 }, machineToSystemBase: new[] { 1.0, 1.0 });
        for (int m = 0; m < 2; m++)
        {
            Assert.Equal(plain.Machines[m].SpeedPu, unit.Machines[m].SpeedPu);
            Assert.Equal(plain.Machines[m].DeltaRad, unit.Machines[m].DeltaRad);
        }
    }

    [Fact]
    public void Gr560_a_governor_on_a_larger_machine_base_gives_proportionally_stiffer_droop()
    {
        // Machine 0 rated at twice the system base (k = 2), machine 1 at the system base (k = 1). On the system base a
        // machine-base droop R gives k / R, so omegabar_ss = deltaP / ((2/R + D) + (1/R + D)) = 1 / (42 + 22) = 1/64.
        const double r = 0.05, damp = 2.0, deltaP = 1.0;
        double[] h = { 4.0, 4.0 }, d = { damp, damp }, pm0 = { 0.0, 0.0 }, eq = { 1.0, 1.0 };
        double[] delta0 = { 0.0, 0.0 };
        ClassicalMultiMachine.Result res = GovernedMultiMachine.Simulate(
            h, d, pm0, eq, delta0, ReactanceLink(0.5), new Tgov1?[] { Governor(r), Governor(r) },
            F0, 0.01, 80.0, mechanicalReference: new[] { deltaP, 0.0 }, machineToSystemBase: new[] { 2.0, 1.0 });

        Assert.True(res.StayedInSynchronism);
        double expected = deltaP / ((2.0 / r + damp) + (1.0 / r + damp));
        int last = res.Machines[0].SpeedPu.Count - 1;
        double m0 = res.Machines[0].SpeedPu[last], m1 = res.Machines[1].SpeedPu[last];
        Assert.True(Math.Abs(m0 - m1) < 1e-6);
        Assert.True(Math.Abs(m0 - expected) < 0.01 * expected, $"speed {m0:R} against the droop {expected:R}");
    }

    [Fact]
    public void Gr560_a_non_positive_base_ratio_is_refused()
    {
        double[] h = { 4.0, 4.0 }, d = { 0.0, 0.0 }, pm0 = { 0.0, 0.0 }, eq = { 1.0, 1.0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedMultiMachine.Simulate(
            h, d, pm0, eq, new[] { 0.0, 0.0 }, ReactanceLink(0.5), new Tgov1?[] { Governor(0.05), null },
            F0, 0.01, 1.0, machineToSystemBase: new[] { 0.0, 1.0 }));
    }
}
