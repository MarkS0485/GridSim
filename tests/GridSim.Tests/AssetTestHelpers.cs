using System.Collections.Generic;
using GridSim.Core.Assets;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;

namespace GridSim.Tests;

/// <summary>Shared builders for the authored per-asset dynamics tests.</summary>
internal static class AssetTestHelpers
{
    public const double BaseDemandMw = 30_000.0;

    /// <summary>A one-bus GB-scale model: a synchronous slack (inertia) plus optional extra generators.</summary>
    public static GridModel Model(AssetDynamicsModel? assetDynamics, params Generator[] extra)
    {
        var gens = new List<Generator>
        {
            // ~4 s inertia constant on 10 GVA of synchronous plant => E = 40 000 MW.s.
            new Generator(Bus: 1, Pg: 20_000, MBase: 10_000, H: 4.0, Name: "sync", Type: "ccgt"),
        };
        gens.AddRange(extra);
        return new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 400, Name: "GB") },
            Generators = gens,
            Branches = System.Array.Empty<Branch>(),
            AssetDynamics = assetDynamics,
        };
    }

    /// <summary>An asset-class library + a bind-by-technology rule set, wrapped as a model bundle.</summary>
    public static AssetDynamicsModel Bundle(IEnumerable<AssetClass> classes, params AssetBinding[] rules)
        => new(new AssetClassLibrary(classes), new AssetBindingSet(rules));

    public static AssetClass GflClass(
        string name,
        double rocofTripHzPerS = double.PositiveInfinity,
        double headroomUpFraction = 0.2,
        double headroomDownFraction = 0.2,
        double droopGain = 2.0,
        double deadbandHz = 0.015,
        double actuateTauS = 0.25,
        double detectionLatencyS = 0.0,
        double computeLatencyS = 0.0,
        double rocofGain = 0.0,
        double localRocofUpliftFactor = 1.0)
    {
        var p = new Dictionary<string, double>
        {
            [AssetParams.DeadbandHz] = deadbandHz,
            [AssetParams.DroopGainMwPerHzPerMw] = droopGain,
            [AssetParams.RocofGainMwsPerHz] = rocofGain,
            [AssetParams.DetectionLatencyS] = detectionLatencyS,
            [AssetParams.ComputeLatencyS] = computeLatencyS,
            [AssetParams.ActuateTauS] = actuateTauS,
            [AssetParams.HeadroomUpFraction] = headroomUpFraction,
            [AssetParams.HeadroomDownFraction] = headroomDownFraction,
            [AssetParams.RocofTripHzPerS] = rocofTripHzPerS,
            [AssetParams.RocofTripConfirmS] = 0.0,   // trip on the first sample over threshold, for deterministic tests
            [AssetParams.LocalRocofUpliftFactor] = localRocofUpliftFactor,
        };
        var prov = new Dictionary<string, AssetClassProvenance>
        {
            [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test class; literature-approximated [VERIFY]"),
        };
        return new AssetClass(name, AssetKind.GridFollowingInverter, "grid-following", p, prov);
    }

    public static AssetClass GfmClass(string name, double synInertiaMwsPerHz, double dampingMwPerHz = 0.0)
    {
        var p = new Dictionary<string, double>
        {
            [AssetParams.SynInertiaMwsPerHz] = synInertiaMwsPerHz,
            [AssetParams.DampingMwPerHz] = dampingMwPerHz,
            [AssetParams.ActuateTauS] = 0.05,
            [AssetParams.HeadroomUpFraction] = 1.0,
            [AssetParams.HeadroomDownFraction] = 1.0,
        };
        var prov = new Dictionary<string, AssetClassProvenance>
        {
            [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test GFM class [VERIFY]"),
        };
        return new AssetClass(name, AssetKind.GridFormingInverter, "grid-forming", p, prov);
    }
}
