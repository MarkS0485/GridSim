using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The generic protections layer: reduction gate, each protection type operating at its threshold, and
/// dynamic LFDD arresting a fall. Protections plug into the existing asset-dynamics fleet, so they inherit
/// the machine-epsilon reduction gate (absent/never-operating ⇒ byte-identical to the lumped baseline).
/// </summary>
public class ProtectionTests
{
    private const double BaseDemandMw = 30_000.0;
    private static readonly FrequencyEvent[] Loss = { new(1.0, -1000.0) };
    private static readonly FrequencyEvent[] BigLoss = { new(1.0, -8000.0) };

    private static AssetClass ProtClass(string name, params (string key, double val)[] p)
    {
        var pd = p.ToDictionary(x => x.key, x => x.val);
        var prov = new Dictionary<string, AssetClassProvenance>
        {
            [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "generic protection [VERIFY]"),
        };
        return new AssetClass(name, AssetKind.Protection, "protection", pd, prov);
    }

    private static GridModel Model(params ProtectionInstance[] instances)
    {
        AssetClassLibrary? lib = null;
        ProtectionModel? pm = null;
        if (instances.Length > 0)
        {
            // Every referenced class is supplied by the caller through a shared library built here.
            lib ??= _library!;
            pm = new ProtectionModel(lib, instances);
        }
        return new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400, Name: "GB") },
            Generators = new List<Generator> { new(1, Pg: 20_000, MBase: 10_000, H: 4.0, Type: "ccgt") },
            Branches = System.Array.Empty<Branch>(),
            Protections = pm,
        };
    }

    private static AssetClassLibrary? _library;
    private static GridModel ModelWith(AssetClass[] classes, params ProtectionInstance[] instances)
    {
        _library = new AssetClassLibrary(classes);
        return Model(instances);
    }

    private static FrequencyResponse Baseline(GridModel m, DynamicsConfig cfg)
        => FrequencyDynamics.Simulate(m.SystemInertiaGvaS * 1000.0, BaseDemandMw, Loss, cfg);

    [Fact]
    public void Reduction_gate_no_protections_is_byte_identical()
    {
        GridModel m = Model();   // Protections == null
        var cfg = new DynamicsConfig { EnableAssetDynamics = true };
        AssetFrequencyResult asset = AssetFrequencyDynamics.Simulate(m, BaseDemandMw, Loss, cfg);
        Assert.True(Baseline(m, cfg).Points.SequenceEqual(asset.Response.Points));
    }

    [Fact]
    public void A_protection_that_never_operates_is_byte_identical()
    {
        // RoCoF threshold 10 Hz/s: the event RoCoF (~0.625) never reaches it.
        GridModel m = ModelWith(
            new[] { ProtClass("lom", (AssetParams.RocofTripHzPerS, 10.0), (AssetParams.RocofTripConfirmS, 0.0)) },
            new ProtectionInstance("p1", "lom", MonitorBus: 1, CapacityMw: 500));
        var cfg = new DynamicsConfig { EnableAssetDynamics = true };
        AssetFrequencyResult asset = AssetFrequencyDynamics.Simulate(m, BaseDemandMw, Loss, cfg);
        Assert.True(Baseline(m, cfg).Points.SequenceEqual(asset.Response.Points));
    }

    [Fact]
    public void Rocof_lom_protection_trips_and_deepens_the_nadir()
    {
        GridModel m = ModelWith(
            new[] { ProtClass("lom", (AssetParams.RocofTripHzPerS, 0.3), (AssetParams.RocofTripConfirmS, 0.0)) },
            new ProtectionInstance("dg", "lom", MonitorBus: 1, CapacityMw: 1500));
        var cfg = new DynamicsConfig { EnableAssetDynamics = true };

        AssetFrequencyResult r = AssetFrequencyDynamics.Simulate(m, BaseDemandMw, Loss, cfg);
        Assert.True(r.TrippedCount > 0, "the RoCoF protection must operate on the event");
        Assert.True(r.TotalTrippedMw >= 1500 - 1e-6, "tripped infeed == protection capacity");
        Assert.True(r.Response.NadirHz < Baseline(m, cfg).NadirHz, "the lost infeed must deepen the nadir");
    }

    [Fact]
    public void Under_frequency_protection_trips_when_frequency_falls_through_its_stage()
    {
        // UF at 49.7 Hz. The 1000 MW loss drives frequency below it -> the stage operates.
        GridModel m = ModelWith(
            new[] { ProtClass("uf", (AssetParams.UfTripHz, 49.7), (AssetParams.UfTripDwellS, 0.0)) },
            new ProtectionInstance("uf1", "uf", MonitorBus: 1, CapacityMw: 400));
        var cfg = new DynamicsConfig { EnableAssetDynamics = true };
        AssetFrequencyResult r = AssetFrequencyDynamics.Simulate(m, BaseDemandMw, Loss, cfg);
        Assert.True(r.TrippedCount > 0);
        Assert.True(r.TotalTrippedMw >= 400 - 1e-6);
    }

    [Fact]
    public void Dynamic_lfdd_sheds_demand_and_arrests_the_fall()
    {
        // A 3000 MW loss would nadir well below 48.8 Hz; an LFDD stage sheds a 900 MW block when it does.
        AssetClass[] classes = { ProtClass("lfdd",
            (AssetParams.LfddStageHz, 48.8), (AssetParams.LfddDwellS, 0.0), (AssetParams.LfddBlockMw, 900.0)) };

        GridModel withLfdd = ModelWith(classes, new ProtectionInstance("lfdd1", "lfdd", MonitorBus: 1, CapacityMw: 900));
        GridModel noLfdd = Model();   // same grid, no protection
        var cfg = new DynamicsConfig { EnableAssetDynamics = true };

        AssetFrequencyResult with = AssetFrequencyDynamics.Simulate(withLfdd, BaseDemandMw, BigLoss, cfg);
        FrequencyResponse without = FrequencyDynamics.Simulate(noLfdd.SystemInertiaGvaS * 1000.0, BaseDemandMw, BigLoss, cfg);

        Assert.True(without.NadirHz < 48.8, $"precondition: the event must fall through the LFDD stage (got {without.NadirHz})");
        Assert.Equal(0, with.TrippedCount);   // LFDD sheds demand, it is not a "trip" of infeed
        Assert.True(with.Response.NadirHz > without.NadirHz, $"LFDD must arrest the fall: {without.NadirHz} -> {with.Response.NadirHz}");
    }

    [Fact]
    public void Protection_provenance_verify_rule_is_enforced()
    {
        var bad = new AssetClass("lom", AssetKind.Protection, "protection",
            new Dictionary<string, double> { [AssetParams.RocofTripHzPerS] = 1.0 },
            new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "no token") });
        Assert.Throws<System.IO.InvalidDataException>(() => new AssetClassLibrary(new[] { bad }).ValidateOrThrow());
    }
}
