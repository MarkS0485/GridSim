using System;
using System.IO;
using GridSim.Core.Diagnostics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The harmonic-source ingest seam (gridsim-harmonics/1): it round-trips, rejects a wrong schema version,
/// guards a non-historical TakenUtc like every dated input, validates against the model, and the default
/// library gives the classic idealised converter spectrum. This is the swappable, measurement-ready input
/// the grounded engine runs on - assumed spectra load as CuratedPlaceholder, measured ones as Recorded.
/// </summary>
public class HarmonicSourceSetTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    // GR-884: one folder per test run (a fixed name was shared by concurrent test processes, which then read each
    // other's files).
    private static readonly string RunDir = Path.Combine(Path.GetTempPath(), $"gridsim-harmonic-tests-{Guid.NewGuid():N}");

    private static string TempFile(string name)
    {
        string dir = RunDir;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var set = new HarmonicSourceSet
        {
            Case = "case9",
            Sources = new[] { HarmonicSourceLibrary.Make("6-pulse", bus: 5, fundamentalCurrentPu: 0.8) },
        };
        string path = TempFile("roundtrip.json");
        set.Save(path);
        HarmonicSourceSet back = HarmonicSourceSet.Load(path);

        Assert.Equal(HarmonicSourceSet.SchemaId, back.Schema);
        Assert.Single(back.Sources);
        Assert.Equal(5, back.Sources[0].Bus);
        Assert.Equal("6-pulse", back.Sources[0].SourceType);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, back.Sources[0].Provenance);
    }

    [Fact]
    public void Rejects_a_wrong_schema_version()
    {
        string path = TempFile("wrong-schema.json");
        File.WriteAllText(path, "{ \"schema\": \"gridsim-harmonics/2\", \"sources\": [] }");
        Assert.Throws<InvalidDataException>(() => HarmonicSourceSet.Load(path));
    }

    [Fact]
    public void A_fresh_timestamp_is_refused_but_a_historical_one_is_accepted()
    {
        string path = TempFile("dated.json");

        new HarmonicSourceSet { TakenUtc = Now }.Save(path);
        Assert.Throws<ForwardInferenceException>(() => HarmonicSourceSet.Load(path, Now));

        new HarmonicSourceSet { TakenUtc = Now.AddHours(-(ForwardInferenceGuard.HorizonHours + 5)) }.Save(path);
        HarmonicSourceSet ok = HarmonicSourceSet.Load(path, Now);   // historical -> accepted
        Assert.NotNull(ok.TakenUtc);
    }

    [Fact]
    public void Validate_flags_a_source_on_a_bus_the_model_lacks()
    {
        GridModel model = IeeeCases.Case9();
        var bad = new HarmonicSourceSet
        {
            Sources = new[] { HarmonicSourceLibrary.Make("pv-inverter", bus: 9999, fundamentalCurrentPu: 0.5) },
        };
        Assert.Throws<InvalidDataException>(() => bad.Validate(model));
    }

    [Fact]
    public void The_six_pulse_library_spectrum_injects_one_over_h()
    {
        HarmonicSource src = HarmonicSourceLibrary.Make("6-pulse", bus: 1, fundamentalCurrentPu: 1.0);
        // 5th harmonic magnitude is 100/5 = 20% of the 1.0 pu fundamental current.
        Assert.Equal(0.20, src.InjectionAt(5).Magnitude, 6);
        Assert.Equal(1.0 / 7.0, src.InjectionAt(7).Magnitude, 6);
        Assert.Equal(0.0, src.InjectionAt(6).Magnitude, 12);  // no even orders
    }

    [Fact]
    public void The_heat_pump_inverter_spectrum_is_low_order_odd_and_scales_with_the_fundamental()
    {
        // The domestic inverter HP is a first-class library type (gridsim-harmonics + GDA heatpump load).
        Assert.True(HarmonicSourceLibrary.ByType.ContainsKey("heat-pump-inverter"));
        HarmonicSource src = HarmonicSourceLibrary.Make("heat-pump-inverter", bus: 3, fundamentalCurrentPu: 0.5);

        // Assumed-typical spectrum, so it loads as CuratedPlaceholder ([VERIFY]) - a measured HP spectrum
        // would swap the tag, not the code path.
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, src.Provenance);

        // 3rd is the dominant order (12% of fundamental) and scales with FundamentalCurrentPu: 0.12 * 0.5.
        Assert.Equal(0.12 * 0.5, src.InjectionAt(3).Magnitude, 6);
        // low-order odd dominance: |I3| > |I5| > |I7|, and no even orders.
        Assert.True(src.InjectionAt(3).Magnitude > src.InjectionAt(5).Magnitude);
        Assert.True(src.InjectionAt(5).Magnitude > src.InjectionAt(7).Magnitude);
        Assert.Equal(0.0, src.InjectionAt(2).Magnitude, 12);
        Assert.Equal(0.0, src.InjectionAt(4).Magnitude, 12);
    }
}
