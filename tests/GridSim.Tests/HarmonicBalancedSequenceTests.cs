using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R029 (October 2026 review): the positive-sequence harmonic Y(h) applied the fundamental transformer phase
/// shift to every order and carried zero-sequence triplens through every transformer; the load-shunt model gave
/// a net-exporting bus a negative resistance and scaled a leading load like a reactor. These tests pin the
/// balanced-harmonic mode (<see cref="HarmonicModelOptions.BalancedHarmonicSequences"/>, on in
/// <see cref="HarmonicModelOptions.Study"/>) and the corrected load shunt.
/// </summary>
public class HarmonicBalancedSequenceTests
{
    private static readonly HarmonicModelOptions Balanced = new() { BalancedHarmonicSequences = true };

    /// <summary>Two areas joined by a 10-degree phase-shifting transformer, each grounded by a machine, a
    /// shunt capacitor at bus 2 and a lagging load at bus 3.</summary>
    private static GridModel QuadBoosterCase(double shiftDeg = 10.0) => new()
    {
        Name = "quad-booster",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 400),
            new Bus(2, BusType.Pq, Pd: 50, Qd: 10, Bs: 20, BaseKv: 400),
            new Bus(3, BusType.Pq, Pd: 80, Qd: 20, BaseKv: 400),
        },
        Branches = new[]
        {
            new Branch(1, 2, 0.002, 0.02, 0.1),
            new Branch(2, 3, 0.001, 0.05, TapRatio: 1.0, PhaseShiftDeg: shiftDeg, IsTransformer: true),
        },
        Generators = new[]
        {
            new Generator(1, Pg: 100, MBase: 500, SubtransientXdPu: 0.2),
            new Generator(3, Pg: 30, MBase: 200, SubtransientXdPu: 0.25),
        },
    };

    private static void AssertSameBits(ComplexCsr a, ComplexCsr b)
    {
        Assert.Equal(a.RowPtr, b.RowPtr);
        Assert.Equal(a.ColIdx, b.ColIdx);
        for (int p = 0; p < a.G.Length; p++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.G[p]), BitConverter.DoubleToInt64Bits(b.G[p]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.B[p]), BitConverter.DoubleToInt64Bits(b.B[p]));
        }
    }

    // ---- Phase shift by sequence ------------------------------------------------------------------

    [Theory]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(17)]
    [InlineData(2)]
    public void A_negative_sequence_order_sees_the_phase_shift_reversed(int h)
    {
        // Balanced Y(h) for a negative-sequence order == the plain builder on the same case with -theta.
        ComplexCsr balanced = FrequencyDependentYBus.Build(QuadBoosterCase(10.0), h, Balanced);
        ComplexCsr reversed = FrequencyDependentYBus.Build(QuadBoosterCase(-10.0), h, new HarmonicModelOptions());
        AssertSameBits(reversed, balanced);

        // ... and genuinely differs from the old +theta result (the bug).
        ComplexCsr old = FrequencyDependentYBus.Build(QuadBoosterCase(10.0), h, new HarmonicModelOptions());
        Assert.NotEqual(old.B, balanced.B);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(49)]
    public void A_positive_sequence_order_keeps_the_phase_shift(int h)
    {
        AssertSameBits(
            FrequencyDependentYBus.Build(QuadBoosterCase(10.0), h, new HarmonicModelOptions()),
            FrequencyDependentYBus.Build(QuadBoosterCase(10.0), h, Balanced));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(45)]
    public void A_triplen_Y_h_cannot_be_built_in_balanced_mode(int h)
    {
        var ex = Assert.Throws<ArgumentException>(() => FrequencyDependentYBus.Build(QuadBoosterCase(), h, Balanced));
        Assert.Contains("triplen", ex.Message);
        // The plain operator still builds it (it is a frequency operator, not a sequence model).
        Assert.NotNull(FrequencyDependentYBus.Build(QuadBoosterCase(), h, new HarmonicModelOptions()));
    }

    [Theory]
    [InlineData(6.2)]
    [InlineData(0.5)]
    [InlineData(-5.0)]
    public void A_non_integer_or_non_positive_order_is_refused_in_balanced_mode(double h)
    {
        Assert.Throws<ArgumentException>(() => FrequencyDependentYBus.Build(QuadBoosterCase(), h, Balanced));
    }

    // ---- Triplen sources ------------------------------------------------------------------------------

    [Fact]
    public void Penetration_refuses_a_triplen_source_in_balanced_mode_naming_it()
    {
        var src = new[] { HarmonicSourceLibrary.Make("heat-pump-inverter", 3, fundamentalCurrentPu: 0.2) };
        var ex = Assert.Throws<ArgumentException>(() => HarmonicPenetration.Solve(QuadBoosterCase(), src, HarmonicModelOptions.Study));
        Assert.Contains("bus 3", ex.Message);
        Assert.Contains("order 3", ex.Message);
    }

    [Fact]
    public void Coupled_flow_refuses_a_triplen_source_in_balanced_mode()
    {
        var src = new[] { HarmonicSourceLibrary.Make("ev-charger", 3, fundamentalCurrentPu: 0.2) };
        Assert.Throws<ArgumentException>(() => CoupledHarmonicPowerFlow.Solve(
            QuadBoosterCase(), src, new NortonCouplingModel(0.0, 0.0), HarmonicModelOptions.Study));
    }

    [Fact]
    public void Study_penetration_with_a_six_pulse_source_gives_exact_zero_at_triplen_orders()
    {
        // A 6-pulse source has no triplens, so every triplen order carries no current: its voltage is exactly
        // zero and its (unbuildable) Y(h) is never needed.
        var src = new[] { HarmonicSourceLibrary.Make("6-pulse", 3, fundamentalCurrentPu: 0.3) };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(QuadBoosterCase(), src, HarmonicModelOptions.Study);
        for (int oi = 0; oi < pen.Orders.Count; oi++)
        {
            if (!HarmonicSequence.IsTriplen(pen.Orders[oi])) continue;
            Assert.All(pen.Voltage[oi], v => Assert.Equal(Complex.Zero, v));
        }
        Assert.True(pen.VoltageAt(5, 2).Magnitude > 0.0);
    }

    [Fact]
    public void Study_scan_skips_triplen_orders()
    {
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(QuadBoosterCase(), 2, HarmonicModelOptions.Study);
        Assert.DoesNotContain(scan.Orders, h => HarmonicSequence.IsTriplen(h));
        Assert.Equal(HarmonicModelOptions.DefaultOrders.Count(h => !HarmonicSequence.IsTriplen(h)), scan.Orders.Count);
        Assert.False(HarmonicSequence.IsTriplen(scan.ResonanceOrder));
    }

    [Fact]
    public void Study_preset_is_balanced()
    {
        Assert.True(HarmonicModelOptions.Study.BalancedHarmonicSequences);
        Assert.False(new HarmonicModelOptions().BalancedHarmonicSequences);
    }

    // ---- Load shunt -------------------------------------------------------------------------------------

    private static GridModel OneLoad(double pd, double qd) => new()
    {
        Name = "one-load",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 33), new Bus(2, BusType.Pq, Pd: pd, Qd: qd, BaseKv: 33) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = new[] { new Generator(1, Pg: 0) },
    };

    [Fact]
    public void A_net_exporting_bus_adds_no_conductance()
    {
        // Pd = -80 MW used to add -0.8 pu conductance: a negative resistance that can manufacture a resonance.
        GridModel m = OneLoad(-80, 10);
        ComplexCsr bare = FrequencyDependentYBus.Build(m, 5.0, new HarmonicModelOptions());
        ComplexCsr with = FrequencyDependentYBus.Build(m, 5.0, new HarmonicModelOptions { IncludeLoadShunts = true });
        int p = bare.DiagPtr[1];
        Assert.Equal(BitConverter.DoubleToInt64Bits(bare.G[p] + 0.0), BitConverter.DoubleToInt64Bits(with.G[p]));
        Assert.Equal(BitConverter.DoubleToInt64Bits(bare.B[p] + (-(10.0 / 5.0) / 100.0)), BitConverter.DoubleToInt64Bits(with.B[p]));
    }

    [Fact]
    public void A_leading_load_is_a_capacitor_whose_susceptance_grows_with_order()
    {
        GridModel m = OneLoad(20, -15);
        foreach (double h in new[] { 1.0, 5.0, 11.0 })
        {
            ComplexCsr bare = FrequencyDependentYBus.Build(m, h, new HarmonicModelOptions());
            ComplexCsr with = FrequencyDependentYBus.Build(m, h, new HarmonicModelOptions { IncludeLoadShunts = true });
            int p = bare.DiagPtr[1];
            Assert.Equal(BitConverter.DoubleToInt64Bits(bare.G[p] + 20.0 / 100.0), BitConverter.DoubleToInt64Bits(with.G[p]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(bare.B[p] + (-(-15.0 * h) / 100.0)), BitConverter.DoubleToInt64Bits(with.B[p]));
        }
    }

    [Fact]
    public void A_lagging_load_is_unchanged_a_reactor_whose_susceptance_falls_with_order()
    {
        GridModel m = OneLoad(20, 15);
        foreach (double h in new[] { 1.0, 5.0, 11.0 })
        {
            ComplexCsr bare = FrequencyDependentYBus.Build(m, h, new HarmonicModelOptions());
            ComplexCsr with = FrequencyDependentYBus.Build(m, h, new HarmonicModelOptions { IncludeLoadShunts = true });
            int p = bare.DiagPtr[1];
            Assert.Equal(BitConverter.DoubleToInt64Bits(bare.G[p] + 20.0 / 100.0), BitConverter.DoubleToInt64Bits(with.G[p]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(bare.B[p] + (-(15.0 / h) / 100.0)), BitConverter.DoubleToInt64Bits(with.B[p]));
        }
    }
}
