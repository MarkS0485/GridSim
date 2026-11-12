using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Tabs.HumanScale;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-11-01: #181, HUMAN SCALE writes its angles invariantly and no longer parses its own display
/// text back to order the machines or to measure the corridor.
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    private static SimFrame GbReducedFrame()
    {
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(r.Converged);
        return new SimFrame(m, r, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: r.PInjMw.Where(p => p > 0).Sum(), DemandMw: m.Buses.Sum(b => b.Pd));
    }

    [Fact]
    public void B181_the_twist_read_outs_are_the_same_on_a_comma_decimal_machine()
    {
        SimFrame f = GbReducedFrame();
        var english = new HumanScaleModel();
        english.Update(f, "00:00");
        var german = UnderGerman(() => { var g = new HumanScaleModel(); g.Update(f, "00:00"); return g; });

        Assert.NotEmpty(german.Machines);
        Assert.NotEmpty(german.Regions);
        Assert.Equal(english.Machines.Select(m => (m.Key, m.DegText)), german.Machines.Select(m => (m.Key, m.DegText)));
        Assert.Equal(english.Regions.Select(r => r.DegText), german.Regions.Select(r => r.DegText));
        Assert.Equal(english.TwistCorridor, german.TwistCorridor);
        Assert.Equal(english.TwistTotal, german.TwistTotal);
        Assert.All(german.Machines, m => Assert.DoesNotContain(",", m.DegText, StringComparison.Ordinal));
        Assert.All(german.Regions, r => Assert.DoesNotContain(",", r.DegText, StringComparison.Ordinal));
    }

    [Fact]
    public void B181_the_machine_order_is_deterministic_and_nothing_is_parsed_back_from_display_text()
    {
        SimFrame f = GbReducedFrame();
        var a = new HumanScaleModel();
        a.Update(f, "00:00");
        a.Update(f, "00:00");   // a second update re-orders the existing rows
        var b = new HumanScaleModel();
        b.Update(f, "00:00");
        Assert.Equal(a.Machines.Select(m => m.Key), b.Machines.Select(m => m.Key));
        string src = ReadRepo("src/GridSim.Ui/Tabs/HumanScale/HumanScaleModel.cs");
        Assert.DoesNotContain("ParseDeg", src, StringComparison.Ordinal);
        Assert.Contains("double gap = regionMeans[i] - regionMeans[i + 1];", src, StringComparison.Ordinal);
    }
}
