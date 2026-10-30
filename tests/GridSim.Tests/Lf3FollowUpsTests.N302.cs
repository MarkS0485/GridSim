using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N302 (LF3; B4 NF-2 and NF-3): the WPF TripCompare trips the largest SECURED unit through the shared presenter (it took
/// the largest Pg of any generator, grid infeed included), and the WPF frequency dial colours amber / red by the lamp's
/// band-edge rule (it compared a strict deviation, so at exactly 49.8 / 50.2 Hz the dial was red while the lamp was
/// normal). The WPF assembly is not referenced by the tests, so its use is pinned by source text; the Core rules it calls
/// are pinned by value.
/// </summary>
public sealed partial class Lf3FollowUpsTests
{
    [Fact]
    public void N302_largest_secured_generator_is_the_unit_largest_secured_unit_names()
    {
        var gens = new[]
        {
            new Generator(1, 5000, Type: "grid", Name: "Grid supply point"),   // never a unit
            new Generator(2, 660, Type: "Coal", Name: "A"),
            new Generator(3, 660, Type: "CCGT", Name: "B"),                     // tie: the first stays
            new Generator(4, -800, Type: "interconnector", Name: "Exporting link"),
        };
        Generator? g = FrequencyRiskPresenter.LargestSecuredGenerator(gens);
        Assert.NotNull(g);
        Assert.Same(gens[1], g);
        Assert.Equal((660.0, "A"), FrequencyRiskPresenter.LargestSecuredUnit(gens));

        // Nothing generating: no generator, and the (0, "-") pair LargestSecuredUnit always returned.
        var idle = new[] { new Generator(1, 900, Type: "grid"), new Generator(2, 0, Type: "CCGT") };
        Assert.Null(FrequencyRiskPresenter.LargestSecuredGenerator(idle));
        Assert.Equal((0.0, "-"), FrequencyRiskPresenter.LargestSecuredUnit(idle));

        // A unit with no name is named by its fuel, as before the split.
        var unnamed = new[] { new Generator(1, 100, Type: "nuclear") };
        Assert.Equal((100.0, FuelTaxonomy.Classify("nuclear").Name), FrequencyRiskPresenter.LargestSecuredUnit(unnamed));

        // Fail loud on a non-finite output (both entry points).
        var bad = new[] { new Generator(1, double.NaN, Type: "CCGT", Name: "Broken") };
        Assert.Throws<ArgumentException>(() => FrequencyRiskPresenter.LargestSecuredGenerator(bad));
        Assert.Throws<ArgumentException>(() => FrequencyRiskPresenter.LargestSecuredUnit(bad));
        Assert.Throws<ArgumentNullException>(() => FrequencyRiskPresenter.LargestSecuredGenerator(null!));
    }

    [Fact]
    public void N302_on_a_model_without_a_grid_infeed_the_compare_trips_the_same_unit_as_before()
    {
        // The old WPF selection, verbatim, against the new one on fleets with no grid infeed and no non-finite output.
        static Generator? Old(IEnumerable<Generator> gens) => gens.Where(g => g.Pg > 0).OrderByDescending(g => g.Pg).FirstOrDefault();
        var fleets = new[]
        {
            new[] { new Generator(1, 1320, Type: "nuclear", Name: "N"), new Generator(2, 1400, Type: "wind-offshore", Name: "W") },
            new[] { new Generator(1, 660, Type: "Coal", Name: "A"), new Generator(2, 660, Type: "CCGT", Name: "B") },
            new[] { new Generator(1, -5, Type: "storage"), new Generator(2, 0.1, Type: "biomass") },
            new[] { new Generator(1, 0, Type: "CCGT") },
        };
        foreach (Generator[] fleet in fleets)
            Assert.Same(Old(fleet), FrequencyRiskPresenter.LargestSecuredGenerator(fleet));

        // With a grid infeed bigger than every unit the two differ: the old one tripped the grid infeed.
        var withInfeed = new[] { new Generator(1, 3000, Type: "grid", Name: "GSP"), new Generator(2, 400, Type: "CCGT", Name: "C") };
        Assert.Same(withInfeed[0], Old(withInfeed));
        Assert.Same(withInfeed[1], FrequencyRiskPresenter.LargestSecuredGenerator(withInfeed));
    }

    [Theory]
    // At the band edges the lamp reads normal; the dial (amber unless an event) now agrees.
    [InlineData(49.8, false)]
    [InlineData(50.2, false)]
    [InlineData(49.79, true)]
    [InlineData(50.21, true)]
    [InlineData(50.0, false)]
    public void N302_the_dial_and_the_lamp_share_one_band_edge_rule_at_50_hz(double hz, bool frequencyEvent)
    {
        Assert.Equal(frequencyEvent, StatusLampRules.IsFrequencyDeviationEvent(hz, FrequencyBands.ForNominal(50.0)));
        Assert.Equal(frequencyEvent, StatusLampRules.IsFrequencyDeviationEvent(hz));
        // The defect the old dial had: the strict deviation test called 50.2 an event (deviation 0.20000000000000284).
        if (hz == 50.2) Assert.False(Math.Abs(hz - 50.0) < StatusLampRules.FrequencyEventBandHz);
    }

    [Fact]
    public void N302_the_wpf_compare_and_dial_call_the_shared_rules()
    {
        // Test retargeted 2026-10-20 (UI programme 0.2): the TSGB compare and the frequency dial moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertions unchanged, and strengthened: the two refused patterns are
        // refused in the WPF view-model too.
        string vm = Src("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs");
        Assert.Contains("Generator? gen = FrequencyRiskPresenter.LargestSecuredGenerator(m.Generators);", vm);
        Assert.DoesNotContain("OrderByDescending(g => g.Pg)", vm);
        Assert.Contains("StatusLampRules.IsFrequencyDeviationEvent(hz, GridSim.Core.Dynamics.FrequencyBands.ForNominal(nominalHz))", vm);
        Assert.DoesNotContain("Math.Abs(dev) < FrequencyEventBandHz", vm);
        string wpf = Src("src", "GridSim.Wpf", "DashboardViewModel.cs");
        Assert.DoesNotContain("OrderByDescending(g => g.Pg)", wpf);
        Assert.DoesNotContain("Math.Abs(dev) < FrequencyEventBandHz", wpf);
    }
}
