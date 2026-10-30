using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R137: the same authored key, <c>rocofTripConfirmS</c>, fell back to 0.5 s (grid-following inverter), 1.0 s
/// (grid-forming inverter) and 0 s (protection controller, as did every other protection dwell). Now there is one
/// table (<see cref="AssetParamDefaults"/>): both inverter kinds fall back to 0.5 s (EREC G99 Issue 2 s.10.6.7.1
/// Table 10.1, LoM (RoCoF) time delay 0.5 s), and a protection class must author the dwell of each stage it sets
/// (refused otherwise, naming the keys).
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    private static AssetClass R137Prot(params (string Key, double Value)[] p) => new("prot", AssetKind.Protection, "protection",
        p.ToDictionary(x => x.Key, x => x.Value),
        new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test [VERIFY]") });

    [Fact]
    public void R137_one_rocof_confirm_default_for_both_inverter_kinds()
    {
        Assert.Equal(0.5, AssetParamDefaults.RocofTripConfirmS);
        Assert.Contains("cls.Param(AssetParams.RocofTripConfirmS, AssetParamDefaults.RocofTripConfirmS)",
            Src("src", "GridSim.Core", "Assets", "GridFollowingInverter.cs"));
        Assert.Contains("cls.Param(AssetParams.RocofTripConfirmS, AssetParamDefaults.RocofTripConfirmS)",
            Src("src", "GridSim.Core", "Assets", "GridFormingInverter.cs"));
        Assert.DoesNotContain("cls.Param(AssetParams.RocofTripConfirmS, 1.0)", Src("src", "GridSim.Core", "Assets", "GridFormingInverter.cs"));
    }

    [Theory]
    [InlineData(AssetParams.RocofTripHzPerS, 1.0, AssetParams.RocofTripConfirmS)]
    [InlineData(AssetParams.DfDtTripHzPerS, 1.0, AssetParams.DfDtTripDwellS)]
    [InlineData(AssetParams.UfTripHz, 47.5, AssetParams.UfTripDwellS)]
    [InlineData(AssetParams.OfTripHz, 52.0, AssetParams.OfTripDwellS)]
    [InlineData(AssetParams.UvTripPu, 0.8, AssetParams.UvTripDwellS)]
    [InlineData(AssetParams.OvTripPu, 1.14, AssetParams.OvTripDwellS)]
    [InlineData(AssetParams.OverloadTripFraction, 1.2, AssetParams.OverloadTripDwellS)]
    [InlineData(AssetParams.LfddStageHz, 48.8, AssetParams.LfddDwellS)]
    public void R137_a_protection_stage_without_its_dwell_is_refused_and_with_it_builds(string threshold, double value, string dwell)
    {
        var ex = Assert.Throws<InvalidDataException>(() => new ProtectionController(R137Prot((threshold, value)), 1, 100));
        Assert.Contains($"'{threshold}' is set but its dwell '{dwell}' is not", ex.Message);
        Assert.Contains("'prot'", ex.Message);

        // Authored (0 is a legitimate instantaneous setting): builds, one stage.
        var c = new ProtectionController(R137Prot((threshold, value), (dwell, 0.0)), 1, 100);
        Assert.Equal(1, c.StageCount);
    }

    [Fact]
    public void R137_every_missing_dwell_is_named_at_once_and_instantaneous_stages_need_none()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new ProtectionController(
            R137Prot((AssetParams.UfTripHz, 47.5), (AssetParams.OfTripHz, 52.0), (AssetParams.OfTripDwellS, 0.5),
                     (AssetParams.LfddStageHz, 48.8), (AssetParams.LfddBlockMw, 100.0)), 1, 100));
        Assert.Contains(AssetParams.UfTripDwellS, ex.Message);
        Assert.Contains(AssetParams.LfddDwellS, ex.Message);
        Assert.DoesNotContain($"'{AssetParams.OfTripDwellS}' is not", ex.Message);

        // LVRT and vector shift are instantaneous by definition: no dwell key exists, none is required.
        var c = new ProtectionController(R137Prot((AssetParams.LvrtVoltagePu, 0.8), (AssetParams.VectorShiftTripDeg, 12.0)), 1, 100);
        Assert.Equal(2, c.StageCount);
    }

    [Theory]
    [InlineData("gb-full")]
    [InlineData("gb-synthetic")]
    public void R137_every_bundled_class_is_unaffected(string dataset)
    {
        // Every bundled protection class authors its dwells (so it still builds), and every bundled inverter class that
        // sets a RoCoF threshold either authors its confirmation time or sets a threshold no RoCoF reaches (1e9 Hz/s),
        // so the inverter fall-back change (grid-forming 1.0 -> 0.5 s) moves no bundled number.
        string path = Path.Combine(CliProcess.Root(), "data", dataset, "asset-classes.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        int protections = 0;
        foreach (JsonElement c in doc.RootElement.EnumerateArray())
        {
            string kind = c.GetProperty("kind").GetString()!;
            JsonElement ps = c.GetProperty("params");
            bool Has(string k) => ps.TryGetProperty(k, out _);
            if (kind == "protection")
            {
                protections++;
                foreach ((string thr, string dwell) in AssetParamDefaults.ProtectionDwellKeys)
                    if (Has(thr)) Assert.True(Has(dwell), $"{dataset} {c.GetProperty("name").GetString()}: {thr} without {dwell}");
            }
            else if (Has(AssetParams.RocofTripHzPerS) && !Has(AssetParams.RocofTripConfirmS))
            {
                Assert.Equal("gridFormingInverter", kind);
                Assert.True(ps.GetProperty(AssetParams.RocofTripHzPerS).GetDouble() >= 1e9,
                    $"{dataset} {c.GetProperty("name").GetString()} relies on the confirm-time fall-back with a reachable threshold");
            }
        }
        if (dataset == "gb-synthetic") Assert.Equal(2, protections);   // prot-lom-legacy, prot-lfdd-stage1
    }
}
