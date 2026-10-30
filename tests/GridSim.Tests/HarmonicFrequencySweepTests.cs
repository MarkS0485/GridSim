using System.Numerics;
using System.Text.Json;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R063 (October 2026 review): the impedance "frequency scan" sampled only the analysed integer orders, so a
/// sharp parallel resonance between orders was missed and reported at the nearest order with a much smaller
/// peak. <see cref="HarmonicFrequencySweep"/> sweeps a fine grid and refines the peak. Pinned here against an
/// exact analytic resonance.
/// </summary>
public class HarmonicFrequencySweepTests
{
    // One bus: a machine (X = 0.2 pu) in parallel with a shunt capacitor B and a resistive load G = 0.1 pu.
    // Y(h) = G + j(h*B - 1/(h*X)) has |Y| minimum G at h_r = 1/sqrt(B*X), so |Z| peaks at exactly 1/G = 10 pu.
    private const double X = 0.2;
    private const double TargetOrder = 6.2;
    private static readonly double B = 1.0 / (TargetOrder * TargetOrder * X);   // pu on 100 MVA

    private static readonly HarmonicModelOptions Model = HarmonicModelOptions.Study;

    [Fact]
    public void Finds_a_resonance_between_integer_orders_at_its_analytic_frequency()
    {
        // Two grounded buses; the capacitor at bus 1 is sized to resonate at exactly h = 6, between the 5th
        // and 7th (the balanced study does not analyse the triplen 6th).
        GridModel m = ParallelResonanceGrounded();
        double hr = AnalyticResonance();
        Assert.True(Math.Abs(hr - 6.0) < 1e-12);

        FrequencySweepResult sweep = HarmonicFrequencySweep.DrivingPoint(m, 1, Model);
        Assert.True(Math.Abs(sweep.PeakOrder - hr) < 1e-5, $"peak at h={sweep.PeakOrder:R}, analytic {hr:R}");
        Assert.True(sweep.PeakMagnitude > 0.0);
        Assert.Equal(sweep.PeakOrder * 50.0, sweep.PeakHz);
        Assert.Equal(HarmonicSequenceKind.Positive, sweep.PeakSequence);
        Assert.Null(sweep.NegativeZ);                      // no phase shifter: one sequence network

        // The old order-by-order scan misses it: its best integer order is far below the true peak.
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(m, 1, Model);
        Assert.True(scan.PeakMagnitude < 0.5 * sweep.PeakMagnitude,
            $"integer scan peak {scan.PeakMagnitude:R} at h{scan.ResonanceOrder} vs true {sweep.PeakMagnitude:R}");
    }

    [Fact]
    public void Refined_peak_magnitude_matches_the_exact_single_bus_value()
    {
        // Isolated single bus (no tie): |Z| at the peak is exactly 1/G = 10 pu, at h_r = 1/sqrt(B*X) = 6.2.
        GridModel m = new()
        {
            Name = "single-bus",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, Pd: 10, Bs: B * 100, BaseKv: 33) },
            Branches = Array.Empty<Branch>(),
            Generators = new[] { new Generator(1, Pg: 0, MBase: 100, SubtransientXdPu: X) },
        };
        FrequencySweepResult sweep = HarmonicFrequencySweep.DrivingPoint(m, 1, Model);
        Assert.True(Math.Abs(sweep.PeakOrder - TargetOrder) < 1e-5, $"peak at h={sweep.PeakOrder:R}");
        Assert.True(Math.Abs(sweep.PeakMagnitude - 10.0) < 1e-9, $"|Z| peak {sweep.PeakMagnitude:R}");
    }

    [Fact]
    public void Repeated_sweeps_are_bit_identical()
    {
        GridModel m = ParallelResonanceGrounded();
        FrequencySweepResult a = HarmonicFrequencySweep.DrivingPoint(m, 1, Model);
        FrequencySweepResult b = HarmonicFrequencySweep.DrivingPoint(m, 1, Model);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.PeakOrder), BitConverter.DoubleToInt64Bits(b.PeakOrder));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.PeakMagnitude), BitConverter.DoubleToInt64Bits(b.PeakMagnitude));
        Assert.Equal(a.Orders, b.Orders);
        for (int p = 0; p < a.PositiveZ.Length; p++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.PositiveZ[p].Real), BitConverter.DoubleToInt64Bits(b.PositiveZ[p].Real));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.PositiveZ[p].Imaginary), BitConverter.DoubleToInt64Bits(b.PositiveZ[p].Imaginary));
        }
    }

    [Fact]
    public void Grid_points_are_min_plus_k_step_never_accumulated()
    {
        FrequencySweepResult s = HarmonicFrequencySweep.DrivingPoint(ParallelResonanceGrounded(), 1, Model,
            new FrequencySweepOptions { MinOrder = 2.0, MaxOrder = 3.0, Step = 0.1 });
        Assert.Equal(11, s.Orders.Count);
        for (int p = 0; p < s.Orders.Count; p++) Assert.Equal(2.0 + p * 0.1, s.Orders[p]);
    }

    [Fact]
    public void A_phase_shifter_sweeps_the_negative_sequence_network_too()
    {
        GridModel m = ParallelResonanceGrounded(phaseShiftDeg: 15.0);
        FrequencySweepResult s = HarmonicFrequencySweep.DrivingPoint(m, 1, Model);
        Assert.NotNull(s.NegativeZ);
        Assert.Equal(s.PositiveZ.Length, s.NegativeZ!.Length);
    }

    [Fact]
    public void An_ungrounded_network_fails_loud_at_the_lowest_frequency()
    {
        GridModel m = new()
        {
            Name = "ungrounded",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 33), new Bus(2, BusType.Pq, BaseKv: 33) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
            Generators = Array.Empty<Generator>(),
        };
        var ex = Assert.Throws<SingularHarmonicNetworkException>(
            () => HarmonicFrequencySweep.DrivingPoint(m, 1, new HarmonicModelOptions()));
        Assert.Equal(1.5, ex.Order);
        Assert.Equal("Harmonic frequency sweep", ex.Study);
    }

    [Theory]
    [InlineData(0.0, 50.0, 0.1)]     // MinOrder not positive
    [InlineData(5.0, 5.0, 0.1)]      // MaxOrder not above MinOrder
    [InlineData(1.5, 50.0, 0.0)]     // Step not positive
    [InlineData(1.5, 50.0, 1e-6)]    // far too many grid points
    [InlineData(1.5, 50.0, double.NaN)]
    public void Invalid_sweep_settings_are_refused(double min, double max, double step)
    {
        Assert.Throws<ArgumentException>(() => HarmonicFrequencySweep.DrivingPoint(ParallelResonanceGrounded(), 1, Model,
            new FrequencySweepOptions { MinOrder = min, MaxOrder = max, Step = step }));
    }

    [Fact]
    public void Unknown_bus_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HarmonicFrequencySweep.DrivingPoint(ParallelResonanceGrounded(), 99, Model));
    }

    [Fact]
    public void Export_records_the_refined_sweep_peak()
    {
        GridModel m = ParallelResonanceGrounded();
        var sources = new HarmonicSourceSet { Case = m.Name, Sources = new[] { HarmonicSourceLibrary.Make("6-pulse", 1, 0.1) } };
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(m, sources.Sources, Model);
        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(pen);
        FrequencySweepResult sweep = HarmonicFrequencySweep.DrivingPoint(m, 1, Model);
        var meta = new SolveExportMeta(m.Name, "harmonics", "test", "2026-10-02T00:00:00Z", 0.0, "test");

        using JsonDocument doc = JsonDocument.Parse(HarmonicExport.ToJson(m, sources, pen, idx, meta, sweep: sweep));
        JsonElement r = doc.RootElement.GetProperty("resonanceSweep");
        Assert.Equal(BitConverter.DoubleToInt64Bits(sweep.PeakOrder), BitConverter.DoubleToInt64Bits(r.GetProperty("peakOrder").GetDouble()));
        Assert.Equal(BitConverter.DoubleToInt64Bits(sweep.PeakMagnitude), BitConverter.DoubleToInt64Bits(r.GetProperty("zMag").GetDouble()));
        Assert.Equal(sweep.Orders.Count, r.GetProperty("gridPoints").GetInt32());
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>Two buses tied by a 0.5 pu reactance, each grounded by a machine; the parallel resonance (machine
    /// + capacitor + load) sits on bus 1.</summary>
    private static GridModel ParallelResonanceGrounded(double phaseShiftDeg = 0.0) => new()
    {
        Name = "parallel-resonance",
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Pd: 10, Bs: BGrounded * 100, BaseKv: 33),
            new Bus(2, BusType.Pv, BaseKv: 33),
        },
        Branches = new[] { new Branch(1, 2, 0.0, Xt, PhaseShiftDeg: phaseShiftDeg, IsTransformer: phaseShiftDeg != 0) },
        Generators = new[]
        {
            new Generator(1, Pg: 0, MBase: 100, SubtransientXdPu: X),
            new Generator(2, Pg: 0, MBase: 100, SubtransientXdPu: X2),
        },
    };

    // Tie reactance and bus-2 machine reactance in ParallelResonanceGrounded.
    private const double Xt = 0.5, X2 = 0.3;

    /// <summary>Capacitor (pu) that puts the two-bus resonance at exactly h = 6.</summary>
    private static readonly double BGrounded = (1.0 / X + 1.0 / (Xt + X2)) / 36.0;

    /// <summary>The exact resonance of <see cref="ParallelResonanceGrounded"/> at bus 1: |Y_eq(h)| minimum, where
    /// Y_eq(h) = G + j h B + 1/(j h X1) + 1/(j h (Xt + X2)) (bus 2's machine seen through the lossless tie). The
    /// susceptance zero is h^2 = (1/X1 + 1/(Xt + X2)) / B.</summary>
    private static double AnalyticResonance() => Math.Sqrt((1.0 / X + 1.0 / (Xt + X2)) / BGrounded);
}
