using GridSim.Core.Events;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N135: NETS SQSS v2.10 (08 April 2025, docs/reference S036, chapter 11 "Terms and Definitions") defines the Normal
/// Infeed Loss Risk as 1320 MW and the Infrequent Infeed Loss Risk as 1800 MW (both from 1 April 2014). The built-in
/// screening event <c>gb-largest-loss-1320</c> was titled "largest-credible-loss screening", which 1320 MW is not. The
/// title now names the normal infeed loss risk; the id, the step and every number are unchanged.
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    [Fact]
    public void N135_the_1320_mw_screening_event_is_titled_as_the_normal_infeed_loss_risk()
    {
        GridEvent e = EventCatalog.Get("gb-largest-loss-1320");
        Assert.Equal(EventCatalog.NormalInfeedLossScreeningTitle, e.Title);
        Assert.Contains("normal infeed loss risk", e.Title);
        Assert.DoesNotContain("largest-credible-loss", e.Title);
        Assert.DoesNotContain("largest credible", e.Title, StringComparison.OrdinalIgnoreCase);

        // Every number unchanged (the step, the operating point and the placeholder outcome).
        PowerStep step = Assert.Single(e.Losses);
        Assert.Equal(1.0, step.AtSeconds);
        Assert.Equal(-1320.0, step.DeltaPMw);
        Assert.Equal(1320.0, e.TotalLossMw);
        Assert.Equal(200_000.0, e.PreEventInertiaMwS);
        Assert.Equal(35_000.0, e.PreEventDemandMw);
        Assert.Equal(-0.165, e.Outcome.InitialRocofHzPerS);
        Assert.Equal(49.2, e.Outcome.NadirHz);
    }

    [Fact]
    public void N135_the_bundled_catalog_carries_the_same_title()
    {
        string bundled = Path.Combine(CliProcess.Root(), "data", "events", "gb-events.json");
        GridEvent e = EventCatalog.Load(bundled).Single(x => x.Id == "gb-largest-loss-1320");
        Assert.Equal(EventCatalog.NormalInfeedLossScreeningTitle, e.Title);
    }

    [Fact]
    public void N135_no_source_calls_1320_mw_the_largest_credible_loss()
    {
        // The two places that carried the wrong wording: the built-in event and the bundled catalog file.
        string catalog = Src("src", "GridSim.Core", "Events", "EventCatalog.cs");
        string file = Src("data", "events", "gb-events.json");
        Assert.DoesNotContain("largest-credible-loss screening", catalog);
        Assert.DoesNotContain("largest-credible-loss screening", file);
    }
}
