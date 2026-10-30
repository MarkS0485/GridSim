using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N110 (October 2026): the passive-plant fleet held a transformer's tap whenever the MODEL typed its controlled bus PV
/// or slack, decided once at build. N071 had already corrected the same rule in <see cref="SteadyStateSolver"/>: the
/// hold follows the bus type the solve ENDED with. A PV-typed bus with no in-service unit (solved as PQ since R283), or
/// a PV bus whose units the reactive-limit loop pinned, has no AVR holding its voltage, so its tap changer must
/// regulate it.
/// </summary>
public class PassivePlantSolvedTypeHoldTests
{
    private static readonly Dictionary<string, AssetClassProvenance> Prov = new()
    {
        [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test OLTC constants [VERIFY]"),
    };

    // An OLTC class with short delays so a test sees a step within a few ticks (as in PassivePlantWiringTests).
    private static AssetDynamicsModel TransformerBound() => new(
        new AssetClassLibrary(new[]
        {
            new AssetClass("tx-test", AssetKind.Transformer, "on-load-tap-changer",
                new Dictionary<string, double>
                {
                    [AssetParams.TargetVpu] = 1.0,
                    [AssetParams.TapStepPu] = 0.0125,
                    [AssetParams.TapDeadbandPu] = 0.01,
                    [AssetParams.MinTap] = 0.9,
                    [AssetParams.MaxTap] = 1.1,
                    [AssetParams.InterTapDelayS] = 5.0,
                    [AssetParams.InitialTapDelayS] = 10.0,
                    [AssetParams.ThermalTauMin] = 1.0,
                    [AssetParams.DerateAtFullThermal] = 0.1,
                }, Prov),
        }),
        new AssetBindingSet(new[] { new AssetBinding("transformer", "tx-test") }));

    /// <summary>A 1.0 pu slack feeds bus 2 through transformer T1 (From 1, To 2); bus 2 carries a load that sags it
    /// below the OLTC band. Bus 2 is typed PV; <paramref name="withUnit"/> puts a unit there holding 1.03 pu.</summary>
    private static GridModel TwoBus(bool withUnit)
    {
        var gens = new List<Generator> { new(1, 60, Vg: 1.0) };
        if (withUnit) gens.Add(new Generator(2, 10, Vg: 1.03));
        return new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
                new Bus(2, BusType.Pv, Pd: 40, Qd: 25, Vm: 1.03, BaseKv: 33),
            },
            Generators = gens,
            Branches = new[] { new Branch(1, 2, 0.005, 0.10, RateAMva: 100, Name: "T1", IsTransformer: true) },
            AssetDynamics = TransformerBound(),
        };
    }

    private static (PassivePlantFleet Fleet, List<double> Taps) Run(GridModel m, int ticks)
    {
        PassivePlantFleet fleet = PassivePlantFleet.Build(m);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        var taps = new List<double>();
        for (int k = 0; k < ticks; k++)
        {
            IReadOnlyList<BranchPlantState> u = fleet.Advance(10.0, k * 10.0, r, m);
            taps.Add(u.Single(s => s.BranchName == "T1").Update.TapRatio!.Value);
        }
        return (fleet, taps);
    }

    [Fact]
    public void A_pv_typed_bus_that_solves_as_pq_is_regulated_by_its_tap_changer()
    {
        GridModel m = TwoBus(withUnit: false);
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.Equal(BusType.Pq, r.SolvedBusTypes![r.BusIds.ToList().IndexOf(2)]);   // R283: no unit, solved as PQ
        Assert.True(r.Vm[r.BusIds.ToList().IndexOf(2)] < 0.99, "the load must sag bus 2 below the OLTC band for this test");

        (PassivePlantFleet fleet, List<double> taps) = Run(m, ticks: 6);
        Assert.Equal(1, fleet.TapsHeldAtGeneratorBus);   // the model types it PV (the build-time count is unchanged)
        Assert.Equal(0, fleet.TapsHeldLastAdvance);      // but the solve left it PQ, so nothing held
        // Before N110 the tap stayed at 1.0 on every tick; a sagging To bus now lowers it (V_to ~ V_from / a).
        Assert.True(taps[^1] < 1.0, $"tap after 6 ticks: {taps[^1]:R}");
    }

    [Fact]
    public void A_bus_that_solves_as_pv_still_holds_the_tap()
    {
        (PassivePlantFleet fleet, List<double> taps) = Run(TwoBus(withUnit: true), ticks: 6);
        Assert.Equal(1, fleet.TapsHeldAtGeneratorBus);
        Assert.Equal(1, fleet.TapsHeldLastAdvance);
        Assert.All(taps, t => Assert.Equal(1.0, t));
    }

    [Fact]
    public void Before_any_solved_advance_the_per_tick_count_is_unset()
        => Assert.Equal(-1, PassivePlantFleet.Build(TwoBus(withUnit: true)).TapsHeldLastAdvance);

    /// <summary>
    /// The measurement on the one bundled case with passive plant bound (data/gb-full): its first live tick, solved as
    /// LiveSim and the GDA replay loop solve a model of its size (sparse Newton, 40 iterations, reactive limits off, the
    /// superseded OLTC records removed). With reactive limits off a PV bus stays PV, so only a PV-typed controlled bus
    /// with no in-service unit could change its hold; the count pinned here says how many held taps the solved-type
    /// rule releases on gb-full.
    /// </summary>
    [Fact]
    public void Gb_full_first_tick_hold_count_under_the_solved_type_rule()
    {
        GridModel gb = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "data", "gb-full"));
        PassivePlantFleet fleet = PassivePlantFleet.Build(gb);
        GridModel solveModel = fleet.WithoutSupersededOltcs(gb);
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(solveModel, new PowerFlowOptions
        {
            MaxIterations = GridSim.Core.Simulation.LiveSolveSettings.SparseMaxIterations, EnforceQLimits = false,
        });
        Assert.True(r.Converged);
        fleet.Advance(1800.0, 0.0, r, solveModel);
        Assert.Equal(fleet.TapsHeldAtGeneratorBus, fleet.TapsHeldLastAdvance);
    }
}
