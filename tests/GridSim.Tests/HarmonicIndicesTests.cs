using System;
using System.Linq;
using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>Harmonic indices and compliance: the transformer K-factor of a converter spectrum, voltage THD
/// from a penetration result, the resonance-sharpness damping metric, and the IEEE-519 / G5/5 limit ladders
/// and a per-bus pass/fail.</summary>
public class HarmonicIndicesTests
{
    [Fact]
    public void Six_pulse_load_has_a_transformer_k_factor_near_nine()
    {
        HarmonicSource src = HarmonicSourceLibrary.Make("6-pulse", bus: 1, fundamentalCurrentPu: 1.0);
        double k = HarmonicIndices.KFactor(src);
        // Each characteristic term I_h^2 h^2 = I_1^2, and there are 8 of them, over a denominator ~1.06.
        Assert.True(k > 7.0 && k < 10.0, $"K-factor {k}");

        // A pure fundamental is K = 1.
        var clean = new HarmonicSource(1, "clean", 1.0, Array.Empty<HarmonicComponent>());
        Assert.Equal(1.0, HarmonicIndices.KFactor(clean), 9);
    }

    [Fact]
    public void Voltage_thd_is_the_rms_of_the_harmonic_voltages_over_the_fundamental()
    {
        // Bus 0 carries 3% at the 5th and 2% at the 7th -> THD = sqrt(0.03^2 + 0.02^2) = 3.606%.
        var res = new HarmonicPenetrationResult(
            Orders: new[] { 5, 7 },
            BusIds: new[] { 10, 20 },
            Voltage: new[]
            {
                new[] { new Complex(0.03, 0), Complex.Zero },   // order 5
                new[] { new Complex(0.02, 0), Complex.Zero },   // order 7
            });

        HarmonicIndicesResult idx = HarmonicIndices.FromPenetration(res);
        Assert.Equal(Math.Sqrt(0.03 * 0.03 + 0.02 * 0.02) * 100.0, idx.ThdVoltagePct[0], 6);
        Assert.Equal(0.0, idx.ThdVoltagePct[1], 9);
        Assert.Equal(10, idx.WorstBusId);
    }

    [Fact]
    public void Ieee519_voltage_limits_tighten_with_voltage_level()
    {
        Assert.Equal((8.0, 5.0), HarmonicCompliance.Ieee519VoltageLimits(0.4));
        Assert.Equal((5.0, 3.0), HarmonicCompliance.Ieee519VoltageLimits(33.0));
        Assert.Equal((2.5, 1.5), HarmonicCompliance.Ieee519VoltageLimits(132.0));
        Assert.Equal((1.5, 1.0), HarmonicCompliance.Ieee519VoltageLimits(400.0));
    }

    [Fact]
    public void Compliance_passes_at_lv_and_fails_the_same_distortion_at_ehv()
    {
        // Same ~3.6% THD (3% at the 5th) at two buses: one at 400 V (limit 8%), one at 132 kV (limit 2.5%).
        var model = new GridModel
        {
            BaseMva = 100,
            FrequencyHz = 50,
            Buses = new[] { new Bus(0, BusType.Pq, BaseKv: 0.4), new Bus(1, BusType.Pq, BaseKv: 132) },
            Generators = Array.Empty<Generator>(),
            Branches = Array.Empty<Branch>(),
        };
        var res = new HarmonicPenetrationResult(
            Orders: new[] { 5 },
            BusIds: new[] { 0, 1 },
            Voltage: new[] { new[] { new Complex(0.036, 0), new Complex(0.036, 0) } });

        var report = HarmonicCompliance.AssessIeee519(model, res).ToList();
        Assert.True(report[0].Compliant, $"LV bus THD {report[0].ThdPct}% under {report[0].ThdLimitPct}%");
        Assert.False(report[1].Compliant, $"EHV bus THD {report[1].ThdPct}% over {report[1].ThdLimitPct}%");
    }

    [Fact]
    public void Damping_sharpness_is_high_for_a_tall_resonance()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            FrequencyHz = 50,
            Buses = new[] { new Bus(0, BusType.Slack, Bs: -500), new Bus(1, BusType.Pq, Bs: 8) },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(0, 1, 0.002, 0.1) },
        };
        var opt = new HarmonicModelOptions { Orders = Enumerable.Range(2, 39).ToArray() };
        ImpedanceScanResult scan = HarmonicImpedanceScan.DrivingPoint(model, busId: 1, opt);
        Assert.True(HarmonicIndices.DampingSharpness(scan) > 3.0, "a lightly-damped resonance is sharp");
    }
}
