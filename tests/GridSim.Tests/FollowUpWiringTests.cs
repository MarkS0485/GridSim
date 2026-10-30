using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Follow-ups N015, N018, N024 and N028 (raised while merging the WS6 branches): a protection placement check that
/// existed but was never called, a SCADA voltage band that repeated the shared table's values, the estimate verb's
/// phase-angle anchor, and an O(n^2) slack search in the storage time series.
/// </summary>
public class FollowUpWiringTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void The_shipped_case_with_protections_loads_and_passes_the_placement_check()
    {
        // N015: JsonGridLoader now calls ProtectionModel.ValidateAgainst; the shipped gb-synthetic devices are valid.
        GridModel m = JsonGridLoader.LoadDirectory(Path.Combine(Root(), "data", "gb-synthetic"));
        Assert.NotNull(m.Protections);
        m.Protections!.ValidateAgainst(m);
    }

    [Fact]
    public void The_loader_calls_the_protection_placement_check()
    {
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Core", "IO", "Json", "JsonGridLoader.cs"));
        Assert.Contains("model.Protections?.ValidateAgainst(model);", src);
    }

    [Fact]
    public void Scada_voltage_band_is_the_shared_operational_band()
    {
        // N018: the values are referenced, not repeated.
        Assert.Equal(VoltageLimits.OperationalLowPu, ScadaThresholds.VoltageBandLowPu);
        Assert.Equal(VoltageLimits.OperationalHighPu, ScadaThresholds.VoltageBandHighPu);
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Core", "Presentation", "ScadaThresholds.cs"));
        Assert.DoesNotContain("VoltageBandLowPu = 0.94", src);
        Assert.DoesNotContain("VoltageBandHighPu = 1.06", src);
    }

    [Fact]
    public void Estimate_anchors_a_non_slack_phase_angle_datum_from_the_solved_prior_or_warm_start()
    {
        // N024: the CLI resolves --prior before the feed and passes the datum's present-state angle as the anchor.
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs"));
        Assert.Contains("DatumAnchorDeg = anchorDeg", src);
        Assert.Contains("PowerFlowResult conditioned = preSolvedPrior ?? MeasuredInjectionPrior.Solve(model, set, rawSolver);", src);
        Assert.Contains("needs its present-state angle as an anchor", src);
    }

    [Fact]
    public void Storage_time_series_finds_the_slack_through_a_map()
    {
        // N028: no linear First() search per result bus.
        string src = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Core", "Storage", "StorageTimeSeries.cs"));
        Assert.DoesNotContain("withStorage.Buses.First(b => b.Id == pf.BusIds[i])", src);
        Assert.Contains("busById.TryAdd(b.Id, b);", src);
    }
}
