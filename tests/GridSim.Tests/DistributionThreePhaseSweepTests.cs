using System;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The three-phase backward/forward-sweep power flow. On a balanced network it must reproduce the scalar
/// per-phase fixed point to machine precision (computed independently here), keep the phases balanced, drop
/// voltage down a radial feeder, and split the phases apart under an unbalanced load.
/// </summary>
public class DistributionThreePhaseSweepTests
{
    // Independent scalar reference: iterate the single-phase constant-power fixed point V = Vsrc - z*conj(S/V).
    private static Complex ScalarFixedPoint(Complex vSrc, Complex z, Complex s)
    {
        Complex v = vSrc;
        for (int k = 0; k < 500; k++) v = vSrc - z * Complex.Conjugate(s / v);
        return v;
    }

    [Fact]
    public void Balanced_two_bus_matches_the_independent_scalar_fixed_point()
    {
        Complex[] src = MultiPhaseModel.BalancedNominal(1.0);
        var z = new Complex(0.01, 0.03);
        var s = new Complex(0.5, 0.2);

        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, PhaseImpedance.Diagonal(z)) },
            new[] { new PhaseLoad(1, new[] { s, s, s }) });

        MultiPhaseResult r = new ThreePhaseSweep().Solve(model);
        Assert.True(r.Converged);

        // Phase A must match the independently-iterated scalar root to machine precision.
        Complex expectedA = ScalarFixedPoint(src[0], z, s);
        Assert.True((r.V[1][0] - expectedA).Magnitude < 1e-9,
            $"phase A {r.V[1][0]} vs independent {expectedA}");

        // Balanced load => equal magnitudes, 120 degrees apart.
        double[] vm = r.Vm(1);
        Assert.True(Math.Abs(vm[0] - vm[1]) < 1e-9 && Math.Abs(vm[1] - vm[2]) < 1e-9);
        double angAB = (r.V[1][0].Phase - r.V[1][1].Phase) * 180.0 / Math.PI;
        Assert.True(Math.Abs(angAB - 120.0) < 1e-6, $"A-B angle {angAB}");
    }

    [Fact]
    public void Voltage_drops_along_a_radial_feeder()
    {
        Complex[] src = MultiPhaseModel.BalancedNominal(1.0);
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.05));
        var s = new Complex(0.3, 0.1);

        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false), new PhaseBus(2, false) },
            new[] { new PhaseBranch(0, 1, z), new PhaseBranch(1, 2, z) },
            new[] { new PhaseLoad(1, new[] { s, s, s }), new PhaseLoad(2, new[] { s, s, s }) });

        MultiPhaseResult r = new ThreePhaseSweep().Solve(model);
        Assert.True(r.Converged);

        double v1 = r.Vm(1)[0], v2 = r.Vm(2)[0];
        Assert.True(v1 < 1.0 && v2 < v1, $"expected 1 > v1 ({v1}) > v2 ({v2})");
    }

    [Fact]
    public void Positive_sequence_collapse_of_a_balanced_solution_is_phase_a()
    {
        Complex[] src = MultiPhaseModel.BalancedNominal(1.0);
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.05));
        var s = new Complex(0.3, 0.1);
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z) },
            new[] { new PhaseLoad(1, new[] { s, s, s }) });

        MultiPhaseResult r = new ThreePhaseSweep().Solve(model);
        (double[] vm, double[] vaDeg) = r.PositiveSequence();
        // Balanced: the positive-sequence voltage equals phase A exactly.
        Assert.True(Math.Abs(vm[1] - r.Vm(1)[0]) < 1e-12, $"pos-seq Vm {vm[1]} vs phase-A {r.Vm(1)[0]}");
        Assert.True(Math.Abs(vaDeg[1] - r.V[1][0].Phase * 180.0 / Math.PI) < 1e-9);
    }

    [Fact]
    public void Voltage_unbalance_factor_is_zero_when_balanced_and_positive_when_not()
    {
        Complex[] src = MultiPhaseModel.BalancedNominal(1.0);
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.05));
        var s = new Complex(0.3, 0.1);

        var balanced = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z) },
            new[] { new PhaseLoad(1, new[] { s, s, s }) });
        MultiPhaseResult rb = new ThreePhaseSweep().Solve(balanced);
        Assert.True(rb.VoltageUnbalancePercent(1) < 1e-9);

        var unbalanced = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z) },
            new[] { new PhaseLoad(1, new[] { new Complex(0.8, 0.3), new Complex(0.1, 0.02), new Complex(0.4, 0.1) }) });
        MultiPhaseResult ru = new ThreePhaseSweep().Solve(unbalanced);
        Assert.True(ru.VoltageUnbalancePercent(1) > 0.1, $"unbalance {ru.VoltageUnbalancePercent(1)}%");
    }

    [Fact]
    public void Unbalanced_load_splits_the_phase_voltages()
    {
        Complex[] src = MultiPhaseModel.BalancedNominal(1.0);
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.05));

        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z) },
            new[] { new PhaseLoad(1, new[] { new Complex(0.8, 0.3), new Complex(0.2, 0.05), new Complex(0.4, 0.1) }) });

        MultiPhaseResult r = new ThreePhaseSweep().Solve(model);
        Assert.True(r.Converged);

        double[] vm = r.Vm(1);
        // The heavily-loaded phase A must sag below the lightly-loaded phase B.
        Assert.True(vm[0] < vm[1] - 1e-4, $"phase A {vm[0]} should sag below phase B {vm[1]}");
    }
    // GR-601: a dead bus has no unbalance factor. It used to read 0 % (perfectly balanced); it must be NaN.
    [Fact]
    public void Voltage_unbalance_factor_of_a_dead_bus_is_NaN_not_balanced()
    {
        var r = new MultiPhaseResult
        {
            Status = MultiPhaseStatus.Converged,
            Converged = true,
            Iterations = 1,
            MaxDelta = 0.0,
            BusIds = new[] { 0, 1 },
            V = new[] { MultiPhaseModel.BalancedNominal(1.0), new Complex[3] },
        };
        Assert.True(double.IsNaN(r.VoltageUnbalancePercent(1)), $"dead bus unbalance {r.VoltageUnbalancePercent(1)}");
        // The live bus is untouched by the change: exactly balanced still reads (essentially) 0.
        Assert.True(r.VoltageUnbalancePercent(0) < 1e-9);
    }

    // GR-575: a constant-power load on a phase held at exactly zero voltage (here the source's phase c is 0) has no
    // defined current. The sweep used to let it draw nothing and report Converged; it must stop NonFinite.
    [Theory]
    [InlineData(PhaseLoadModel.ConstantPower)]
    [InlineData(PhaseLoadModel.ConstantCurrent)]
    public void A_power_or_current_load_on_a_zero_voltage_phase_is_not_converged(PhaseLoadModel loadModel)
    {
        Complex[] nominal = MultiPhaseModel.BalancedNominal(1.0);
        var src = new[] { nominal[0], nominal[1], Complex.Zero };
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.05));
        var s = new Complex(0.3, 0.1);
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z) },
            new[] { new PhaseLoad(1, new[] { s, s, s }, loadModel) });

        MultiPhaseResult r = new ThreePhaseSweep().Solve(model);
        Assert.False(r.Converged);
        Assert.Equal(MultiPhaseStatus.NonFinite, r.Status);
    }

    // GR-575 companion: a constant-impedance load at zero voltage draws exactly zero current, which is defined, so
    // the same feeder still converges and the dead phase stays at 0.
    [Fact]
    public void A_constant_impedance_load_on_a_zero_voltage_phase_still_converges()
    {
        Complex[] nominal = MultiPhaseModel.BalancedNominal(1.0);
        var src = new[] { nominal[0], nominal[1], Complex.Zero };
        var z = PhaseImpedance.Diagonal(new Complex(0.02, 0.05));
        var s = new Complex(0.3, 0.1);
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, src), new PhaseBus(1, false) },
            new[] { new PhaseBranch(0, 1, z) },
            new[] { new PhaseLoad(1, new[] { s, s, s }, PhaseLoadModel.ConstantImpedance) });

        MultiPhaseResult r = new ThreePhaseSweep().Solve(model);
        Assert.True(r.Converged, $"status {r.Status}");
        Assert.Equal(Complex.Zero, r.V[1][2]);
    }
}
