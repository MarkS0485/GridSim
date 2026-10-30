using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Diagnostics;
using GridSim.Core.Events;
using GridSim.Core.Harmonics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S3 G02 (October 2026): the harmonic planning levels and emission profiles grounded in ENA EREC G5 Issue 5 (2020,
/// docs/reference S139), and the screening event's 1320 MW step grounded in NETS SQSS v2.10 (S036). New tests; the one
/// moved expectation (the strictest G5/5 level, 2.0 -> 3.0) is in HarmonicComplianceUnknownKvTests with its comment.
/// </summary>
public class S3HarmonicAndEventGroundingTests
{
    // ---- G5 Issue 5 Table 1, p.21: THDV planning levels at the PCC nominal voltage ----

    [Theory]
    [InlineData(0.23, 5.0)]     // V <= 0.4 kV
    [InlineData(0.4, 5.0)]      // the 0.4 kV boundary belongs to the LV row
    [InlineData(1.0, 4.5)]      // 0.4 < V <= 25 kV (was 5.0 under the unsourced <= 1 kV row)
    [InlineData(11.0, 4.5)]     // (was 4.0)
    [InlineData(25.0, 4.5)]     // the 25 kV boundary
    [InlineData(33.0, 3.7)]     // 25 < V <= 66 kV (was 4.0)
    [InlineData(66.0, 3.7)]     // the 66 kV boundary
    [InlineData(132.0, 3.0)]    // 66 < V <= 230 kV (unchanged)
    [InlineData(230.0, 3.0)]    // the 230 kV boundary
    [InlineData(275.0, 3.0)]    // V > 230 kV (was 2.0)
    [InlineData(400.0, 3.0)]    // (was 2.0)
    public void G55_planning_level_follows_G5_Issue_5_Table_1(double kv, double expectedPct)
        => Assert.Equal(expectedPct, HarmonicCompliance.G55ThdPlanningLevelPct(kv));

    // ---- G5 Issue 5 Table B.3, pp.99-100: the technology emission assumptions, added as new library types ----

    private static double ThdPct(System.Collections.Generic.IReadOnlyList<HarmonicComponent> spectrum)
        => System.Math.Sqrt(spectrum.Sum(c => c.MagnitudePercent * c.MagnitudePercent));

    [Fact]
    public void G5_six_pulse_profile_reproduces_the_printed_THDI()
    {
        var s = HarmonicSourceLibrary.SixPulseG5;
        Assert.Equal(31.4, s.Single(c => c.Order == 5).MagnitudePercent);
        Assert.Equal(10.9, s.Single(c => c.Order == 7).MagnitudePercent);
        Assert.Equal(7.0, s.Single(c => c.Order == 11).MagnitudePercent);
        Assert.Equal(3.9, s.Single(c => c.Order == 13).MagnitudePercent);
        Assert.DoesNotContain(s, c => c.Order % 3 == 0 || c.Order % 2 == 0);   // no triplen or even orders printed
        Assert.Equal(34.41, ThdPct(s), 2);                                       // Table B.3 THDI 0.3441
    }

    [Fact]
    public void G5_twelve_pulse_profile_reproduces_the_printed_THDI()
    {
        var s = HarmonicSourceLibrary.TwelvePulseG5;
        Assert.Equal(4.8, s.Single(c => c.Order == 11).MagnitudePercent);
        Assert.Equal(3.2, s.Single(c => c.Order == 13).MagnitudePercent);
        Assert.Equal(0.4, s.Single(c => c.Order == 5).MagnitudePercent);       // residual 5th, not zero
        Assert.Equal(5.96, ThdPct(s), 2);                                        // Table B.3 THDI 0.0596
    }

    [Fact]
    public void G5_active_front_end_profile_is_within_the_rounding_of_the_printed_THDI()
    {
        var s = HarmonicSourceLibrary.ActiveFrontEndG5;
        Assert.Equal(3.4, s.Single(c => c.Order == 5).MagnitudePercent);
        Assert.Equal(1.5, s.Single(c => c.Order == 7).MagnitudePercent);
        // Printed THDI 0.0381; the three-decimal entries give 0.0386 (rounding of the 0.001 entries). Within 0.1 %.
        Assert.InRange(ThdPct(s), 3.81, 3.91);
    }

    [Theory]
    [InlineData("6-pulse-g5")]
    [InlineData("12-pulse-g5")]
    [InlineData("afe-g5")]
    public void The_G5_profiles_are_library_types_and_stay_CuratedPlaceholder(string type)
    {
        HarmonicSource src = HarmonicSourceLibrary.Make(type, bus: 1, fundamentalCurrentPu: 1.0);
        Assert.Equal(type, src.SourceType);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, src.Provenance);   // a typical profile, not a measured converter
    }

    [Fact]
    public void The_idealised_six_pulse_spectrum_is_unchanged()
    {
        // Additive grounding: the existing 1/h spectrum (the CLI's default source) keeps every value bit for bit.
        var s = HarmonicSourceLibrary.SixPulse;
        Assert.Equal(new[] { 5, 7, 11, 13, 17, 19, 23, 25 }, s.Select(c => c.Order).ToArray());
        foreach (HarmonicComponent c in s) Assert.Equal(100.0 / c.Order, c.MagnitudePercent);
    }

    // ---- NETS SQSS v2.10 (S036) chapter 11: Normal Infeed Loss Risk 1320 MW from 1 April 2014 ----

    [Fact]
    public void The_screening_event_step_is_the_SQSS_normal_infeed_loss_risk()
    {
        GridEvent e = EventCatalog.Get("gb-largest-loss-1320");
        PowerStep step = Assert.Single(e.Losses);
        Assert.Equal(-1320.0, step.DeltaPMw);                 // value unchanged
        Assert.Equal(ProvenanceTag.Recorded, step.Provenance);
        Assert.Contains("SQSS", step.What);
        Assert.Equal(ProvenanceTag.CuratedPlaceholder, e.Provenance);   // the rest of the event stays a placeholder
    }

    [Fact]
    public void The_catalog_file_carries_the_same_step()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null);
        GridEvent e = EventCatalog.Load(Path.Combine(dir!.FullName, "data", "events", "gb-events.json"))
            .Single(x => x.Id == "gb-largest-loss-1320");
        PowerStep step = Assert.Single(e.Losses);
        Assert.Equal(-1320.0, step.DeltaPMw);
        Assert.Equal(ProvenanceTag.Recorded, step.Provenance);
        Assert.Equal(EventCatalog.Get("gb-largest-loss-1320").Losses[0].What, step.What);
    }
}
