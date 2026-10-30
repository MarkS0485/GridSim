using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N039: the credible loss (the largest single secured infeed both shells grade against) counted synchronous machines
/// only. NETS SQSS v2.10 para 5.1.2 secures "a single generating unit (or several generating units sharing a common
/// circuit breaker), a single power park module, or a single DC converter", and NESO FRCR Methodology v3 s.4.1.1 Table 2
/// defines a BMU-only event as "an event that disconnects one BMU", of any technology (FRCR Data Handbook s.6.1: the BMU
/// failure data includes interconnectors). So converter plant and interconnector imports now count. A grid infeed (the
/// network behind a model boundary) is not a unit and never counts; an idle or absorbing unit is not an infeed.
/// </summary>
public sealed partial class B4GbLossThresholdsTests
{
    [Fact]
    public void N039_a_converter_wind_farm_or_an_interconnector_import_can_be_the_credible_loss()
    {
        var wind = new[]
        {
            new Generator(1, 1320, Type: "nuclear", Name: "Unit 1"),
            new Generator(1, 1400, Type: "wind-offshore", Name: "Offshore wind farm"),
        };
        Assert.Equal((1400.0, "Offshore wind farm"), FrequencyRiskPresenter.LargestSecuredUnit(wind));

        var link = new[]
        {
            new Generator(1, 1000, Type: "CCGT", Name: "CCGT"),
            new Generator(1, 1800, Type: "interconnector", Name: "Link import"),
            new Generator(1, 900, Type: "storage", Name: "Battery"),
            new Generator(1, 700, Type: "solar", Name: "Solar park"),
        };
        Assert.Equal((1800.0, "Link import"), FrequencyRiskPresenter.LargestSecuredUnit(link));
    }

    [Fact]
    public void N039_an_exporting_link_a_charging_battery_or_a_grid_infeed_is_never_the_credible_loss()
    {
        var gens = new[]
        {
            new Generator(1, -2000, Type: "interconnector", Name: "Exporting link"),   // outfeed, not an infeed
            new Generator(1, -500, Type: "storage", Name: "Charging battery"),
            new Generator(1, 5000, Type: "grid", Name: "Grid supply point"),          // the network behind the boundary
            new Generator(1, 300, Type: "CCGT", Name: "Local CCGT"),
        };
        Assert.Equal((300.0, "Local CCGT"), FrequencyRiskPresenter.LargestSecuredUnit(gens));
        Assert.False(FrequencyRiskPresenter.IsSingleInfeedUnit("grid"));
        Assert.False(FrequencyRiskPresenter.IsSingleInfeedUnit("Grid infeed"));
        Assert.True(FrequencyRiskPresenter.IsSingleInfeedUnit("interconnector"));
        Assert.True(FrequencyRiskPresenter.IsSingleInfeedUnit("wind-onshore"));
        Assert.True(FrequencyRiskPresenter.IsSingleInfeedUnit(null));   // unknown plant is still a unit
        Assert.Equal((0.0, "-"), FrequencyRiskPresenter.LargestSecuredUnit(new[] { new Generator(1, 900, Type: "grid") }));
    }

    [Fact]
    public void N039_a_fleet_whose_largest_infeed_is_synchronous_is_unchanged_bit_for_bit()
    {
        // The old rule (synchronous only), verbatim, against the new one on fleets whose biggest infeed is synchronous.
        static (double, string) Old(IEnumerable<Generator> gens)
        {
            double largest = 0.0; string name = "-";
            foreach (Generator g in gens)
            {
                FuelDescriptor fd = FuelTaxonomy.Classify(g.Type);
                if (!fd.Synchronous) continue;
                if (g.Pg > largest) { largest = g.Pg; name = g.Name ?? fd.Name; }
            }
            return (largest, name);
        }

        var fleets = new[]
        {
            new[] { new Generator(1, 1320.000000001, Type: "nuclear"), new Generator(1, 1320, Type: "wind-offshore") },
            new[] { new Generator(1, 660, Type: "Coal", Name: "A"), new Generator(1, 660, Type: "CCGT", Name: "B"),
                    new Generator(1, 400, Type: "interconnector") },
            new[] { new Generator(1, 0.1, Type: "biomass"), new Generator(1, -5, Type: "storage") },
        };
        foreach (Generator[] fleet in fleets)
        {
            (double oldMw, string oldName) = Old(fleet);
            (double newMw, string newName) = FrequencyRiskPresenter.LargestSecuredUnit(fleet);
            AssertBits(oldMw, newMw, "credible loss");
            Assert.Equal(oldName, newName);
        }
    }

    [Fact]
    public void N039_both_shells_and_the_wpf_dashboard_read_the_one_presenter()
    {
        // The web FuelBreakdown, the WPF RP1 / OPERATOR view-models and (since N039) the WPF headline "secure minimum
        // inertia" all take the credible loss from FrequencyRiskPresenter.LargestSecuredUnit.
        Assert.Contains("FrequencyRiskPresenter.LargestSecuredUnit(model.Generators)", Src("src", "GridSim.Web", "Projection", "FuelBreakdown.cs"));
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/NesoRp1Vm.cs moved to
        // src/GridSim.Ui/Tabs/NesoRp1/NesoRp1Model.cs; assertion unchanged, and strengthened: the WPF view-model must
        // derive from that model and may not take the credible loss itself, nor scan for the largest Pg.
        Assert.Contains("FrequencyRiskPresenter.LargestSecuredUnit(f.Model.Generators)", Src("src", "GridSim.Ui", "Tabs", "NesoRp1", "NesoRp1Model.cs"));
        string rp1Adapter = Src("src", "GridSim.Wpf", "Views", "NesoRp1Vm.cs");
        Assert.Contains("public sealed class NesoRp1Vm : NesoRp1Model", rp1Adapter);
        Assert.DoesNotContain("LargestSecuredUnit(", rp1Adapter);
        Assert.DoesNotContain(".Select(g => g.Pg).DefaultIfEmpty(0).Max()", rp1Adapter);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/OperatorVm.cs moved to
        // src/GridSim.Ui/Tabs/Operator/OperatorModel.cs; assertion unchanged, and strengthened: the WPF view-model must
        // derive from that model and may not set the credible loss itself, nor scan for the largest Pg.
        Assert.Contains("FrequencyRiskPresenter.LargestSecuredUnit(m.Generators)", Src("src", "GridSim.Ui", "Tabs", "Operator", "OperatorModel.cs"));
        string operatorAdapter = Src("src", "GridSim.Wpf", "Views", "OperatorVm.cs");
        Assert.Contains("public sealed class OperatorVm : OperatorModel", operatorAdapter);
        Assert.DoesNotContain("LargestLoss =", operatorAdapter);
        Assert.DoesNotContain(".Select(g => g.Pg).DefaultIfEmpty(0).Max()", operatorAdapter);
        // Test retargeted 2026-10-20 (UI programme 0.2): the dashboard's headline framing moved to
        // src/GridSim.Ui/Dashboard/DashboardModel.cs; assertions unchanged, and strengthened: the old largest-Pg scan is
        // refused in the WPF view-model too.
        string dash = Src("src", "GridSim.Ui", "Dashboard", "DashboardModel.cs");
        Assert.Contains("FrequencyRiskPresenter.LargestSecuredUnit(model.Generators).Mw", dash);
        Assert.DoesNotContain(".Select(g => g.Pg).DefaultIfEmpty(0).Max()", dash);
        Assert.DoesNotContain(".Select(g => g.Pg).DefaultIfEmpty(0).Max()", Src("src", "GridSim.Wpf", "DashboardViewModel.cs"));
    }

    [Fact]
    public void N039_the_web_rp1_grades_against_the_converter_unit_when_it_is_the_largest()
    {
        SimFrame f = WebProjectionFixtures.Solve(WebProjectionFixtures.MixedFleet());
        var fb = new FuelBreakdown(f.Model);
        Assert.Equal("Wind farm", fb.LargestUnitName);
        Assert.Equal(2000.0, fb.LargestUnitMw);
        Rp1Dto rp1 = TabProjections.Rp1(f, fb);
        Assert.Equal(2000.0, rp1.CredibleLossMw);
    }
}
