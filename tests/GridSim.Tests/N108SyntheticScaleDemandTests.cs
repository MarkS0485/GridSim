using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Synthetic;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using GridSim.Gda.Npg;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N108 (Ruling R1, Mark delegated 2026-10-04): below scale 1.0 `synth --scale` shrinks each zone's demand, embedded
/// plant and transmission fleet with its 11 kV layer, so a small scale is a smaller GB that solves; at 1.0 and above the
/// network is unchanged.
/// </summary>
public sealed class N108SyntheticScaleDemandTests
{
    private static string Root() => CliProcess.Root();

    // =============================================================================================================
    // N108: below scale 1.0 the synthetic GB's demand and plant shrink with its 11 kV layer
    // =============================================================================================================

    [Fact]
    public void N108_zone_scale_factor_is_one_at_and_above_unit_scale_and_the_secondary_ratio_below()
    {
        Assert.Equal(1.0, SyntheticGb.ZoneScaleFactor(1.0, 100, 100));
        Assert.Equal(1.0, SyntheticGb.ZoneScaleFactor(2.0, 200, 100));
        Assert.Equal(1.0, SyntheticGb.ZoneScaleFactor(1.0000001, 3, 7));   // never consulted at or above 1.0
        Assert.Equal(40.0 / 110.0, SyntheticGb.ZoneScaleFactor(0.3, 40, 110));
        Assert.Equal(1.0, SyntheticGb.ZoneScaleFactor(0.95, 110, 110));
        // Below 1.0 a zone always builds between 1 and its scale-1.0 count of secondaries; anything else is refused.
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticGb.ZoneScaleFactor(0.5, 0, 110));
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticGb.ZoneScaleFactor(0.5, 111, 110));
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticGb.ZoneScaleFactor(0.5, 10, 0));
    }

    [Fact]
    public void N108_scale_one_and_above_build_exactly_the_same_network_as_the_default()
    {
        // 0.95 rounds every per-primary draw (4 to 7) back to itself, so it builds the 1.0 network with factor 1.0.
        GridModel unit = SyntheticGb.Build();
        foreach (double s in new[] { 1.0, 0.95 })
        {
            GridModel m = SyntheticGb.Build(new SyntheticGb.Options { Scale = s });
            Assert.Equal(unit.Buses, m.Buses);
            Assert.Equal(unit.Branches, m.Branches);
            Assert.Equal(unit.Generators.Select(g => g with { Cost = null }), m.Generators.Select(g => g with { Cost = null }));
        }
        // Above 1.0 nothing is scaled: GB demand stays (the 11 kV layer only thickens).
        GridModel two = SyntheticGb.Build(new SyntheticGb.Options { Scale = 2.0 });
        Assert.Equal(3946, two.BusCount);
        Assert.Equal(unit.Generators.Take(29).Select(g => (g.Pmax, g.MBase)), two.Generators.Take(29).Select(g => (g.Pmax, g.MBase)));
    }

    [Fact]
    public void N108_a_small_scale_is_a_smaller_gb_that_solves()
    {
        var opt = new PowerFlowOptions { MaxIterations = 60, EnforceQLimits = false };   // the synth verb's check solve
        GridModel unit = SyntheticGb.Build();
        double unitDemand = unit.Buses.Sum(b => b.Pd);
        foreach (double s in new[] { 0.02, 0.3, 0.4, 0.5, 0.55 })
        {
            GridModel m = SyntheticGb.Build(new SyntheticGb.Options { Scale = s });
            double demand = m.Buses.Sum(b => b.Pd);
            Assert.True(demand < unitDemand, $"scale {s}: demand {demand} not below {unitDemand}");
            Assert.True(new SparseNewtonRaphsonPowerFlow().Solve(m, opt).Converged, $"scale {s} did not converge");
        }
        // The floor of 2 secondaries per primary binds at every scale up to 0.36: one network, one factor per zone, so
        // 0.02 and 0.3 are the same model, about 37 % of GB demand (14.5 GW of 39.3 GW in the synth summary).
        GridModel tiny = SyntheticGb.Build(new SyntheticGb.Options { Scale = 0.02 });
        GridModel small = SyntheticGb.Build(new SyntheticGb.Options { Scale = 0.3 });
        Assert.Equal(tiny.Buses, small.Buses);
        Assert.InRange(tiny.Buses.Sum(b => b.Pd) / unitDemand, 0.36, 0.38);
        // The transmission fleet shrinks with its zone: the slack unit (Midlands, zone 5) and every unit's MBase.
        Assert.True(tiny.Generators[13].Pmax < unit.Generators[13].Pmax);
        Assert.Equal(unit.Generators[13].H, tiny.Generators[13].H);   // H is per unit on MBase: unchanged
    }
}
