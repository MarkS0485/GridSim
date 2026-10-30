using System;
using System.Linq;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The coupled iterative harmonic power flow: with no coupling it reduces to linear penetration in one
/// iteration; with a damping (Norton) coupling model the resonant voltage is reduced and the fixed point
/// converges; and the assumed coupling model is honestly provenance-tagged.
/// </summary>
public class HarmonicCoupledTests
{
    private static GridModel ResonantFeeder() => new()
    {
        BaseMva = 100,
        FrequencyHz = 50,
        Buses = new[] { new Bus(0, BusType.Slack, Bs: -500), new Bus(1, BusType.Pq, Bs: 8) },
        Generators = Array.Empty<Generator>(),
        Branches = new[] { new Branch(0, 1, 0.002, 0.1) },
    };

    [Fact]
    public void With_no_coupling_it_matches_linear_penetration()
    {
        GridModel model = ResonantFeeder();
        var opt = new HarmonicModelOptions { Orders = new[] { 5, 7 } };
        var sources = new[] { HarmonicSourceLibrary.Make("6-pulse", bus: 1, fundamentalCurrentPu: 0.5) };

        HarmonicPenetrationResult plain = HarmonicPenetration.Solve(model, sources, opt);
        CoupledHarmonicResult coupled =
            CoupledHarmonicPowerFlow.Solve(model, sources, new NortonCouplingModel(0.0, 0.0), opt);

        Assert.True(coupled.Converged);
        for (int oi = 0; oi < opt.Orders.Count; oi++)
            for (int bi = 0; bi < model.BusCount; bi++)
                Assert.True((plain.Voltage[oi][bi] - coupled.Result.Voltage[oi][bi]).Magnitude < 1e-9);
    }

    [Fact]
    public void A_damping_coupling_model_reduces_the_resonant_voltage_and_converges()
    {
        GridModel model = ResonantFeeder();
        var opt = new HarmonicModelOptions { Orders = new[] { 7 } };   // a source order near the parallel resonance
        var sources = new[] { HarmonicSourceLibrary.Make("6-pulse", bus: 1, fundamentalCurrentPu: 0.5) };

        HarmonicPenetrationResult plain = HarmonicPenetration.Solve(model, sources, opt);
        CoupledHarmonicResult coupled =
            CoupledHarmonicPowerFlow.Solve(model, sources, new NortonCouplingModel(couplingConductance: 0.2), opt);

        Assert.True(coupled.Converged, $"fixed point converged in {coupled.Iterations} iters");
        double vPlain = plain.Voltage[0][1].Magnitude;      // bus 1
        double vCoupled = coupled.Result.Voltage[0][1].Magnitude;
        Assert.True(vCoupled < vPlain, $"damping reduces the resonant voltage: {vCoupled:F4} < {vPlain:F4}");
    }

    [Fact]
    public void The_coupling_model_is_honestly_tagged_as_assumed()
    {
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, new NortonCouplingModel().Provenance);
        var res = CoupledHarmonicPowerFlow.Solve(
            ResonantFeeder(),
            new[] { HarmonicSourceLibrary.Make("6-pulse", 1, 0.5) },
            new NortonCouplingModel(),
            new HarmonicModelOptions { Orders = new[] { 5 } });
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, res.CouplingProvenance);
    }
}
