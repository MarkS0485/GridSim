using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N318 (LF6; ruled by main under Ruling R1): the protection frequency setting rule is direction-aware. An under-frequency
/// (ufTripHz) or LFDD (lfddStageHz) setting must lie strictly below its settings nominal (settingsNominalHz, absent:
/// 50 Hz), an over-frequency (ofTripHz) setting strictly above it, each no more than 10 Hz away on its own side. It admits
/// the widest real settings (Quebec, NERC PRC-024-3 Table 3: 55.5 / 66.0 Hz about 60 Hz) and still refuses the 50 / 60 Hz
/// unit mistake in both directions, because the mistake puts settings on the wrong side.
/// </summary>
public sealed partial class Lf6CalibrationBoundsTests
{
    private static AssetClass N318Prot(params (string Key, double Value)[] p) => new("n318", AssetKind.Protection, "protection",
        p.ToDictionary(x => x.Key, x => x.Value),
        new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "test [VERIFY]") });

    [Fact]
    public void N318_the_distance_bound_is_10_hz_and_the_trace_margin_is_untouched()
    {
        Assert.Equal(10.0, AssetClassSupport.MaxFrequencySettingDistanceHz);
        // The fit-frequency trace margin keeps its own 5 Hz (not a protection setting; two-sided by nature).
        Assert.Equal(5.0, AssetClassSupport.MaxFrequencySettingOffsetHz);
    }

    [Fact]
    public void N318_quebec_instantaneous_settings_are_admitted_at_60_hz()
    {
        // PRC-024-3 Table 3 (Quebec): instantaneous trip below 55.5 Hz and above 66.0 Hz, -4.5 / +6.0 Hz about 60 Hz.
        // N304's symmetric 5 Hz refused the +6.0 Hz over-frequency stage.
        AssetClass quebec = N318Prot((AssetParams.UfTripHz, 55.5), (AssetParams.UfTripDwellS, 0.0),
            (AssetParams.OfTripHz, 66.0), (AssetParams.OfTripDwellS, 0.0),
            (AssetParams.SettingsNominalHz, 60.0));
        Assert.Empty(AssetClassSupport.ProtectionFrequencyProblems(quebec));
        Assert.Empty(AssetClassSupport.Problems(quebec));
        Assert.Equal(1, new AssetClassLibrary(new[] { quebec }).Count);
        _ = new ProtectionController(quebec, 1, 100, systemNominalHz: 60.0);
    }

    [Fact]
    public void N318_native_60_hz_settings_read_as_gb_50_hz_are_refused_on_the_wrong_side()
    {
        // A real 60 Hz UF stage (59.3 Hz) written without settingsNominalHz lies ABOVE 50 Hz: the wrong side.
        AssetClass uf = N318Prot((AssetParams.UfTripHz, 59.3), (AssetParams.UfTripDwellS, 0.0));
        IReadOnlyList<string> p = AssetClassSupport.ProtectionFrequencyProblems(uf);
        Assert.Single(p);
        Assert.Contains("sets 'ufTripHz' = 59.3 Hz, which is not below the 50 Hz nominal", p[0]);
        Assert.Contains("'settingsNominalHz' absent, so the GB 50 Hz", p[0]);
        Assert.Contains("for example 60 for a 60 Hz system", p[0]);
        var ex = Assert.Throws<InvalidDataException>(() => new ProtectionController(uf, 1, 100, systemNominalHz: 60.0));
        Assert.Contains("'ufTripHz' = 59.3 Hz, which is not below", ex.Message);

        // A real 60 Hz LFDD-like stage (58.9 Hz): also above 50 Hz.
        AssetClass lfdd = N318Prot((AssetParams.LfddStageHz, 58.9), (AssetParams.LfddDwellS, 0.0), (AssetParams.LfddBlockMw, 10.0));
        Assert.Contains("which is not below", Assert.Single(AssetClassSupport.ProtectionFrequencyProblems(lfdd)));

        // A real 60 Hz OF stage (61.7 Hz, the lowest instantaneous OF bound in PRC-024-3 Attachment 1): on the right side
        // of 50 Hz, but 11.7 Hz above it, more than 10 Hz.
        AssetClass of = N318Prot((AssetParams.OfTripHz, 61.7), (AssetParams.OfTripDwellS, 0.0));
        string q = Assert.Single(AssetClassSupport.ProtectionFrequencyProblems(of));
        Assert.Contains("sets 'ofTripHz' = 61.7 Hz, more than 10 Hz above the 50 Hz nominal", q);
    }

    [Fact]
    public void N318_gb_settings_declared_at_60_hz_are_refused_too()
    {
        // The reverse mistake: GB settings with settingsNominalHz = 60. The GB OF stage (52.0 Hz, G99 Table 10.1) lies
        // below 60 Hz (the wrong side); the GB UF stage (47.5 Hz) lies 12.5 Hz below 60 Hz (beyond 10 Hz).
        AssetClass gb = N318Prot((AssetParams.UfTripHz, 47.5), (AssetParams.UfTripDwellS, 0.0),
            (AssetParams.OfTripHz, 52.0), (AssetParams.OfTripDwellS, 0.0),
            (AssetParams.SettingsNominalHz, 60.0));
        IReadOnlyList<string> p = AssetClassSupport.ProtectionFrequencyProblems(gb);
        Assert.Equal(2, p.Count);
        // Fixed key order: UF first, then OF.
        Assert.Contains("sets 'ufTripHz' = 47.5 Hz, more than 10 Hz below the 60 Hz nominal", p[0]);
        Assert.Contains("('settingsNominalHz' = 60)", p[0]);
        Assert.Contains("sets 'ofTripHz' = 52 Hz, which is not above the 60 Hz nominal", p[1]);
        Assert.Contains("an over-frequency stage", p[1]);
        Assert.Throws<InvalidDataException>(() => new AssetClassLibrary(new[] { gb }));
    }

    [Fact]
    public void N318_every_real_gb_setting_still_loads()
    {
        // G99 Issue 2 Table 10.1 (47.0 / 47.5 UF, 52.0 OF) and the Grid Code LFDD stages (48.8 down to 47.8 Hz).
        foreach (double uf in new[] { 47.0, 47.5, 49.0, 49.7 })
            Assert.Empty(AssetClassSupport.ProtectionFrequencyProblems(N318Prot((AssetParams.UfTripHz, uf), (AssetParams.UfTripDwellS, 0.0))));
        Assert.Empty(AssetClassSupport.ProtectionFrequencyProblems(N318Prot((AssetParams.OfTripHz, 52.0), (AssetParams.OfTripDwellS, 0.5))));
        foreach (double lfdd in new[] { 48.8, 48.7, 48.6, 48.5, 48.4, 48.2, 48.0, 47.8 })
            Assert.Empty(AssetClassSupport.ProtectionFrequencyProblems(
                N318Prot((AssetParams.LfddStageHz, lfdd), (AssetParams.LfddDwellS, 0.0), (AssetParams.LfddBlockMw, 10.0))));
    }

    [Fact]
    public void N318_a_non_finite_setting_is_still_refused_and_not_also_judged_for_side()
    {
        AssetClass cls = N318Prot((AssetParams.OfTripHz, double.NaN), (AssetParams.OfTripDwellS, 0.0));
        string p = Assert.Single(AssetClassSupport.ProtectionFrequencyProblems(cls));
        Assert.Contains("which is not a finite frequency", p);
    }

    [Fact]
    public void N318_the_problem_list_is_identical_on_every_call()
    {
        // Determinism: fixed key order and invariant formatting of the same doubles.
        AssetClass cls = N318Prot((AssetParams.LfddStageHz, 58.8), (AssetParams.LfddDwellS, 0.0),
            (AssetParams.OfTripHz, 62.0), (AssetParams.OfTripDwellS, 0.0),
            (AssetParams.UfTripHz, 59.3), (AssetParams.UfTripDwellS, 0.0));
        IReadOnlyList<string> a = AssetClassSupport.ProtectionFrequencyProblems(cls);
        IReadOnlyList<string> b = AssetClassSupport.ProtectionFrequencyProblems(cls);
        Assert.Equal(a, b);
        Assert.Equal(3, a.Count);
        Assert.Contains("'ufTripHz' = 59.3 Hz, which is not below", a[0]);
        Assert.Contains("'ofTripHz' = 62 Hz, more than 10 Hz above", a[1]);
        Assert.Contains("'lfddStageHz' = 58.8 Hz, which is not below", a[2]);
    }
}
