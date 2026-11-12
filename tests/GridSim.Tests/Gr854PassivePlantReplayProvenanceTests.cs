using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Gda;
using GridSim.Core.Model;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-854: a GDA replay with the passive plant on kept the DerivedFromRecorded tag although its modelled OLTC taps and
/// thermal derate changed the reconstruction. A replay whose plant actually moved a tap or derated a rating is now
/// tagged Interpolated (recorded inputs augmented by modelled behaviour: usable to improve fidelity, never an event
/// source); a replay whose plant changed nothing, or with the plant off, stays DerivedFromRecorded. The fixture is the
/// three-bus OLTC case of PassivePlantWiringTests (the bus 2 sag makes the tap step on the second tick).
/// </summary>
public class Gr854PassivePlantReplayProvenanceTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, AssetClassProvenance> Prov = new()
    {
        [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test OLTC/thermal constants [VERIFY]"),
    };

    private static AssetClass Oltc() => new(
        "tx-test", AssetKind.Transformer, "on-load-tap-changer",
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
        }, Prov);

    private static AssetClass Cable() => new(
        "cable-test", AssetKind.Line, "dynamic-thermal-rating",
        new Dictionary<string, double>
        {
            [AssetParams.ThermalTauMin] = 1.0,
            [AssetParams.DerateAtFullThermal] = 0.2,
        }, Prov);

    private static AssetDynamicsModel Bound() => new(new AssetClassLibrary(new[] { Oltc(), Cable() }),
        new AssetBindingSet(new[] { new AssetBinding("transformer", "tx-test"), new AssetBinding("line", "cable-test") }));

    private static GridModel ThreeBus(AssetDynamicsModel? dyn) => new()
    {
        BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, Vm: 1.0, BaseKv: 132),
            new Bus(2, BusType.Pq, Pd: 40, Qd: 25, BaseKv: 33),
            new Bus(3, BusType.Pq, Pd: 20, Qd: 10, BaseKv: 33),
        },
        Generators = new[] { new Generator(1, 60, Vg: 1.0) },
        Branches = new[]
        {
            new Branch(1, 2, 0.005, 0.10, TapRatio: 1.0, RateAMva: 100, Name: "T1", IsTransformer: true),
            new Branch(2, 3, 0.02, 0.05, B: 0.001, RateAMva: 50, Name: "L23"),
        },
        AssetDynamics = dyn,
    };

    private static GdaReplayResult Replay(GridModel m, bool passive) =>
        GdaReplayLoop.Replay(m, GdaTimeSeries.Synthetic("t", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 6, 30),
            utcNow: Now, passivePlant: passive);

    [Fact]
    public void Gr854_a_replay_whose_passive_plant_changed_the_model_is_interpolated()
        => Assert.Equal(ProvenanceTag.Interpolated, Replay(ThreeBus(Bound()), passive: true).Provenance);

    [Fact]
    public void Gr854_the_same_replay_with_the_plant_off_stays_derived_from_recorded()
        => Assert.Equal(ProvenanceTag.DerivedFromRecorded, Replay(ThreeBus(Bound()), passive: false).Provenance);

    [Fact]
    public void Gr854_a_replay_with_the_plant_on_but_nothing_bound_stays_derived_from_recorded()
        => Assert.Equal(ProvenanceTag.DerivedFromRecorded, Replay(ThreeBus(null), passive: true).Provenance);
}
