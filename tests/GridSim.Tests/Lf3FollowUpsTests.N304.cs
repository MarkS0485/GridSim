using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N304 (LF3; ruled by main under Ruling R1: fix): a protection frequency setting (ufTripHz, ofTripHz, lfddStageHz) more
/// than 5 Hz from the nominal it is written for (settingsNominalHz, absent: 50 Hz) is refused at load by name. It catches
/// native 60 Hz settings written without settingsNominalHz, which N297 would otherwise move by +10 Hz on a 60 Hz case.
/// N318 (LF6; ruled by main under Ruling R1) replaced the symmetric 5 Hz bound with a direction-aware rule: UF and LFDD
/// settings strictly below the settings nominal, OF settings strictly above it, each within 10 Hz on its own side
/// (AssetClassSupport.MaxFrequencySettingDistanceHz). The tests below that encoded the 5 Hz edges are re-baselined in
/// place with a "Moved 2026-10" comment; the new rule's own tests are in Lf6CalibrationBoundsTests.N318.cs.
/// </summary>
public sealed partial class Lf3FollowUpsTests
{
    private static AssetClass N304Prot(params (string Key, double Value)[] p) => new("n304", AssetKind.Protection, "protection",
        p.ToDictionary(x => x.Key, x => x.Value),
        new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test [VERIFY]") });

    // N318 (LF6, Ruling R1): the constant keeps its value. It no longer bounds protection settings (that is
    // MaxFrequencySettingDistanceHz = 10, pinned in Lf6CalibrationBoundsTests.N318.cs); the fit-frequency trace reader
    // still uses it as its two-sided "not a 50 Hz trace" margin, so the pin stays and still holds.
    [Fact]
    public void N304_the_bound_is_5_hz()
        => Assert.Equal(5.0, AssetClassSupport.MaxFrequencySettingOffsetHz);

    [Fact]
    public void N304_native_60_hz_settings_without_settings_nominal_are_refused_at_load_by_name()
    {
        AssetClass native = N304Prot((AssetParams.UfTripHz, 59.3), (AssetParams.UfTripDwellS, 0.0));
        var ex = Assert.Throws<InvalidDataException>(() => new AssetClassLibrary(new[] { native }));
        Assert.Contains("protection class 'n304' sets 'ufTripHz' = 59.3 Hz", ex.Message);
        Assert.Contains("'settingsNominalHz' absent, so the GB 50 Hz", ex.Message);
        // The direct-construction path refuses it too (no library in between).
        var ex2 = Assert.Throws<InvalidDataException>(() => new ProtectionController(native, 1, 100, systemNominalHz: 60.0));
        Assert.Contains("'ufTripHz' = 59.3 Hz", ex2.Message);
    }

    [Fact]
    public void N304_the_same_settings_with_their_nominal_declared_load()
    {
        AssetClass native = N304Prot((AssetParams.UfTripHz, 59.3), (AssetParams.UfTripDwellS, 0.0),
            (AssetParams.OfTripHz, 61.8), (AssetParams.OfTripDwellS, 0.5),
            (AssetParams.LfddStageHz, 58.9), (AssetParams.LfddDwellS, 0.0), (AssetParams.LfddBlockMw, 10.0),
            (AssetParams.SettingsNominalHz, 60.0));
        Assert.Empty(AssetClassSupport.ProtectionFrequencyProblems(native));
        Assert.Equal(1, new AssetClassLibrary(new[] { native }).Count);
        _ = new ProtectionController(native, 1, 100, systemNominalHz: 60.0);
    }

    [Theory]
    // Moved 2026-10 (LF6 N318, Ruling R1 (Mark delegated EE calls 2026-10-04), ruled by main; source NERC PRC-024-3
    // (S294) Table 3, Quebec instantaneous trips at 55.5 / 66.0 Hz about 60 Hz, i.e. -4.5 / +6.0 Hz): the symmetric 5 Hz
    // bound refused a real +6.0 Hz over-frequency stage, so the rule is now direction-aware (UF / LFDD strictly below
    // the nominal, OF strictly above it, each within 10 Hz on its own side). Before / after of each row:
    //   ufTripHz 45.0     accepted -> accepted (5 Hz below, inside 10 Hz)
    //   ufTripHz 44.99    REFUSED  -> accepted (5.01 Hz below, inside 10 Hz): expectation false -> true
    //   ofTripHz 55.0     accepted -> accepted
    //   ofTripHz 55.01    REFUSED  -> accepted (5.01 Hz above, inside 10 Hz): expectation false -> true
    //   lfddStageHz 45.0  accepted -> accepted
    //   lfddStageHz 59.0  REFUSED  -> REFUSED (was: 9 Hz away; now: above the nominal, the wrong side for LFDD)
    // The new rule's edges (exactly 10 Hz, exactly at nominal, the wrong side by a hair) follow the moved rows.
    [InlineData("ufTripHz", 45.0, true)]
    [InlineData("ufTripHz", 44.99, true)]
    [InlineData("ofTripHz", 55.0, true)]
    [InlineData("ofTripHz", 55.01, true)]
    [InlineData("lfddStageHz", 45.0, true)]
    [InlineData("lfddStageHz", 59.0, false)]
    [InlineData("ufTripHz", 40.0, true)]       // exactly 10 Hz below: accepted (the rule is "more than")
    [InlineData("ufTripHz", 39.99, false)]     // just beyond 10 Hz below
    [InlineData("ufTripHz", 50.0, false)]      // at the nominal: the wrong side ("strictly below")
    [InlineData("ufTripHz", 50.01, false)]     // above the nominal: the wrong side
    [InlineData("ofTripHz", 60.0, true)]       // exactly 10 Hz above
    [InlineData("ofTripHz", 60.01, false)]     // just beyond 10 Hz above
    [InlineData("ofTripHz", 50.0, false)]      // at the nominal
    [InlineData("ofTripHz", 49.99, false)]     // below the nominal: the wrong side
    [InlineData("lfddStageHz", 40.0, true)]
    [InlineData("lfddStageHz", 39.99, false)]
    [InlineData("lfddStageHz", 50.0, false)]
    // Every real GB setting: G99 Table 10.1 (47.0, 47.5, 52.0) and the LFDD stages (48.8 .. 47.8).
    [InlineData("ufTripHz", 47.0, true)]
    [InlineData("ufTripHz", 47.5, true)]
    [InlineData("ofTripHz", 52.0, true)]
    [InlineData("lfddStageHz", 48.8, true)]
    [InlineData("lfddStageHz", 47.8, true)]
    public void N304_each_frequency_key_is_judged_against_its_nominal(string key, double hz, bool accepted)
    {
        string dwell = key switch
        {
            "ufTripHz" => AssetParams.UfTripDwellS,
            "ofTripHz" => AssetParams.OfTripDwellS,
            _ => AssetParams.LfddDwellS,
        };
        AssetClass cls = N304Prot((key, hz), (dwell, 0.0));
        IReadOnlyList<string> problems = AssetClassSupport.ProtectionFrequencyProblems(cls);
        Assert.Equal(accepted, problems.Count == 0);
        Assert.Equal(accepted, AssetClassSupport.Problems(cls).Count == 0);
        if (!accepted) Assert.Contains($"sets '{key}' = {hz:R} Hz", problems[0]);
    }

    [Fact]
    public void N304_several_problems_are_listed_in_a_fixed_key_order()
    {
        AssetClass cls = N304Prot((AssetParams.LfddStageHz, 58.8), (AssetParams.LfddDwellS, 0.0),
            (AssetParams.OfTripHz, 62.0), (AssetParams.OfTripDwellS, 0.0),
            (AssetParams.UfTripHz, 59.3), (AssetParams.UfTripDwellS, 0.0));
        IReadOnlyList<string> problems = AssetClassSupport.ProtectionFrequencyProblems(cls);
        Assert.Equal(3, problems.Count);
        Assert.Contains("'ufTripHz'", problems[0]);
        Assert.Contains("'ofTripHz'", problems[1]);
        Assert.Contains("'lfddStageHz'", problems[2]);
    }

    [Fact]
    public void N304_a_settings_nominal_that_is_not_a_frequency_is_refused_at_load()
    {
        AssetClass cls = N304Prot((AssetParams.UfTripHz, 47.5), (AssetParams.UfTripDwellS, 0.0), (AssetParams.SettingsNominalHz, 0.0));
        IReadOnlyList<string> problems = AssetClassSupport.ProtectionFrequencyProblems(cls);
        Assert.Single(problems);
        Assert.Contains("'settingsNominalHz' = 0", problems[0]);
        Assert.Throws<InvalidDataException>(() => new AssetClassLibrary(new[] { cls }));
    }

    [Fact]
    public void N304_a_class_that_is_not_a_protection_is_never_judged()
    {
        AssetClass gfl = AssetTestHelpers.GflClass("g", rocofTripHzPerS: 1.0);
        Assert.Empty(AssetClassSupport.ProtectionFrequencyProblems(gfl));
    }

    [Fact]
    public void N304_every_bundled_protection_class_still_loads()
    {
        // Every bundled class is GB data within 1.2 Hz of 50 Hz; scan the files so a new one cannot slip past.
        foreach (string rel in new[] { "data/gb-synthetic/asset-classes.json", "data/gb-full/asset-classes.json" })
        {
            string path = Path.Combine(PresentationConstantsAntiDriftTests.RepoRoot(), rel);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            System.Text.Json.JsonElement root = doc.RootElement;
            System.Text.Json.JsonElement classes = root.ValueKind == System.Text.Json.JsonValueKind.Array ? root : root.GetProperty("classes");
            foreach (System.Text.Json.JsonElement c in classes.EnumerateArray())
            {
                if (c.GetProperty("kind").GetString() != "protection" || !c.TryGetProperty("params", out var ps)) continue;
                double nominal = ps.TryGetProperty(AssetParams.SettingsNominalHz, out var n) ? n.GetDouble() : 50.0;
                foreach (string key in new[] { AssetParams.UfTripHz, AssetParams.OfTripHz, AssetParams.LfddStageHz })
                    if (ps.TryGetProperty(key, out var v))
                    {
                        // The original 5 Hz check is kept (it is tighter than the rule and still holds for bundled data).
                        Assert.True(Math.Abs(v.GetDouble() - nominal) <= AssetClassSupport.MaxFrequencySettingOffsetHz,
                            $"{rel}: class '{c.GetProperty("name").GetString()}' {key} = {v.GetDouble():R}");
                        // N318 (LF6, Ruling R1): and each lies on its own side of the nominal (UF / LFDD below, OF above).
                        bool below = key != AssetParams.OfTripHz;
                        Assert.True(below ? v.GetDouble() < nominal : v.GetDouble() > nominal,
                            $"{rel}: class '{c.GetProperty("name").GetString()}' {key} = {v.GetDouble():R} is on the wrong side of {nominal:R} Hz");
                    }
            }
        }
    }
}
