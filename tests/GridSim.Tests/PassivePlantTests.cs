using System.Collections.Generic;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Passive-plant dynamics on the steady-state tick: an OLTC does not move within its mechanical delay
/// and does after; line thermal derate rises monotonically with loading. Different (seconds-to-minutes)
/// timescale from the frequency window - tap/rating updates, never swing power.
/// </summary>
public class PassivePlantTests
{
    private static AssetClass Transformer(double interTapDelayS, double initialTapDelayS)
        => new("tx", AssetKind.Transformer, "transformer",
            new Dictionary<string, double>
            {
                [AssetParams.TargetVpu] = 1.0,
                [AssetParams.TapStepPu] = 0.0125,
                [AssetParams.TapDeadbandPu] = 0.01,
                [AssetParams.InterTapDelayS] = interTapDelayS,
                [AssetParams.InitialTapDelayS] = initialTapDelayS,
            },
            new Dictionary<string, AssetClassProvenance>
            {
                [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "defaulted OLTC timings [VERIFY]"),
            });

    private static AssetClass Line(double derateFull)
        => new("cable", AssetKind.Line, "line",
            new Dictionary<string, double>
            {
                [AssetParams.ThermalTauMin] = 1.0,     // 1-minute tau so the test converges quickly
                [AssetParams.DerateAtFullThermal] = derateFull,
            },
            new Dictionary<string, AssetClassProvenance>
            {
                [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "IEEE-738 defaults [VERIFY]"),
            });

    [Fact]
    public void Oltc_does_not_move_within_the_mechanical_delay_and_moves_after()
    {
        var tx = new TransformerDynamics(Transformer(interTapDelayS: 5.0, initialTapDelayS: 10.0), "T1");
        // Controlled bus sits high (1.05 vs target 1.0) - the tap should LOWER, but only after 10 s.
        var ctx = new AssetStepContext(50, 0, 0, DtSeconds: 1.0, ClockSeconds: 0, LocalVoltagePu: 1.05);

        PlantStateUpdate u = default;
        for (int s = 0; s < 9; s++) u = tx.Advance(in ctx);
        Assert.Equal(1.0, u.TapRatio!.Value, 6);     // still un-moved at t = 9 s (< 10 s initial delay)

        u = tx.Advance(in ctx);                       // t = 10 s
        Assert.True(u.TapRatio!.Value < 1.0, "tap must step down once the initial delay elapses");
    }

    [Fact]
    public void Line_thermal_derate_is_monotone_in_loading()
    {
        double DerateAt(double loading)
        {
            var line = new LineThermalDynamics(Line(derateFull: 0.2), "L1");
            var ctx = new AssetStepContext(50, 0, 0, DtSeconds: 10.0, ClockSeconds: 0, LoadingFraction: loading);
            PlantStateUpdate u = default;
            for (int s = 0; s < 60; s++) u = line.Advance(in ctx);   // 600 s >> 60 s tau: reach steady state
            return u.RatingDerateFraction;
        }

        double low = DerateAt(0.3), mid = DerateAt(0.6), high = DerateAt(0.95);
        Assert.True(low < mid && mid < high, $"derate must rise with loading: {low} < {mid} < {high}");
        Assert.True(high <= 0.2 + 1e-9, "derate is capped at the full-thermal derate");
    }

    [Fact]
    public void An_active_class_bound_to_a_branch_is_ignored()
    {
        // A GFL (active) class routed through the passive factory yields no controller.
        Assert.Null(AssetControllerFactory.CreatePassive(AssetTestHelpers.GflClass("x"), "T1"));
    }

    [Fact]
    public void ApplyTo_overrides_tap_and_derates_rating()
    {
        var model = new GridSim.Core.Model.GridModel
        {
            BaseMva = 100,
            Buses = new[] { new GridSim.Core.Model.Bus(1, GridSim.Core.Model.BusType.Slack), new GridSim.Core.Model.Bus(2, GridSim.Core.Model.BusType.Pq) },
            Generators = System.Array.Empty<GridSim.Core.Model.Generator>(),
            Branches = new[] { new GridSim.Core.Model.Branch(1, 2, 0.01, 0.1, RateAMva: 1000, Name: "T1", IsTransformer: true) },
        };
        var updates = new[] { new BranchPlantState("T1", new PlantStateUpdate(TapRatio: 0.95, RatingDerateFraction: 0.2, ThermalStatePu: 0.5)) };

        GridSim.Core.Model.GridModel applied = PassivePlantFleet.ApplyTo(model, updates);
        GridSim.Core.Model.Branch t = applied.Branches[0];
        Assert.Equal(0.95, t.TapRatio, 6);
        Assert.Equal(800.0, t.RateAMva, 6);          // 1000 * (1 - 0.2)
    }
}
