using System.Text.Json;
using GridSim.Core.IO.Json;
using GridSim.Core.Simulation;
using GridSim.Core.Viz;
using Xunit;

namespace GridSim.Tests;

public class VizExportTests
{
    private static string UkDir => Path.Combine(AppContext.BaseDirectory, "data", "uk");

    [Fact]
    public void Exports_scene_with_buses_branches_and_frames()
    {
        var model = JsonGridLoader.LoadDirectory(UkDir);
        VizScene scene = VizExport.Build(model, frames: 12);

        Assert.Equal(model.BusCount, scene.Buses.Length);
        Assert.Equal(model.Branches.Count, scene.Branches.Length);
        Assert.Equal(12, scene.Frames.Length);

        // Layout coordinates carried through.
        Assert.Contains(scene.Buses, b => b.X > 0 && b.Y > 0);

        // Each frame's per-bus arrays match the bus count and look sane.
        foreach (VizFrame f in scene.Frames)
        {
            Assert.Equal(scene.Buses.Length, f.Vm.Length);
            Assert.All(f.Vm, v => Assert.InRange(v, 0.8f, 1.2f));
            Assert.True(f.LoadGw > 0 && f.GenGw > 0);
        }
    }

    [Fact]
    public void Demand_follows_the_daily_curve_across_frames()
    {
        var model = JsonGridLoader.LoadDirectory(UkDir);
        VizScene scene = VizExport.Build(model, frames: 24);
        float min = scene.Frames.Min(f => f.LoadGw);
        float max = scene.Frames.Max(f => f.LoadGw);
        Assert.True(max > min * 1.2f, "expected a meaningful day/night demand swing");
    }

    [Fact]
    public void Json_is_camelcase_and_round_trips()
    {
        var model = JsonGridLoader.LoadDirectory(UkDir);
        string json = VizExport.ToJson(VizExport.Build(model, frames: 3));
        Assert.Contains("\"buses\"", json);
        Assert.Contains("\"frames\"", json);
        Assert.Contains("\"vm\"", json);

        using JsonDocument doc = JsonDocument.Parse(json); // valid JSON
        Assert.Equal(3, doc.RootElement.GetProperty("frames").GetArrayLength());
    }

    [Fact]
    public void Load_profile_peaks_above_its_trough()
    {
        double trough = LoadProfile.DailyFactor(4);   // ~04:00
        double peak = LoadProfile.DailyFactor(16);     // ~16:00
        Assert.True(peak > trough);
        Assert.InRange(trough, 0.55, 0.75);
        Assert.InRange(peak, 0.95, 1.05);
    }
}
