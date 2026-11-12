using GridSim.Core.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Ui.Hosting;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// #198 (found while fixing #172): the desktop hosts gate a replay run and a bridge topology through the licence gate,
/// with the same content rule as the web (bundled only by the shipped network's topology).
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    // A run folder of 'model' (one exported state), in the layout PresolvedReplay reads.
    private static string MakeRunOf(GridModel model, string caseName)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-bh1101-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var trace = new SolveTrace();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(model, new PowerFlowOptions { Trace = trace });
        Assert.True(r.Converged);
        var meta = new SolveExportMeta(caseName, "cpu", "c", "2026-07-02T00:00:00Z", 1.0, "test");
        SolveExport.WriteJson(Path.Combine(dir, "exports", "state-000000.json"), model, r, trace, meta);
        File.WriteAllText(Path.Combine(dir, "state-estimate.csv"), "index,time_utc\n0,2024-06-01T09:00:00Z\n");
        return dir;
    }

    [Fact]
    public void B198_a_desktop_replay_of_a_large_user_network_is_refused_on_the_free_tier()
    {
        string big = MakeRunOf(BigUserNetwork("gb-full"), "gb-full");
        string small = MakeRunOf(IeeeCases.Case9(), "case9");
        try
        {
            var free = new LicenceGate(Entitlement.FreeDefault);
            var host = new SimHost();
            ReplayLoadResult refused = host.LoadReplay(big, free);
            Assert.False(refused.Loaded);
            Assert.Contains("buses", refused.Refusal!, StringComparison.Ordinal);
            // The one-argument form gates at the free tier too (the safe direction for a caller that passes no gate).
            Assert.False(new SimHost().LoadReplay(big).Loaded);
            // A small run plays.
            ReplayLoadResult ok = host.LoadReplay(small, free);
            Assert.True(ok.Loaded, ok.Refusal);
        }
        finally
        {
            try { Directory.Delete(big, recursive: true); } catch (IOException) { }
            try { Directory.Delete(small, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void B198_the_shipped_topology_rule_needs_the_name_and_the_shape()
    {
        Assert.True(BundledNetworks.IsShippedTopology(IeeeCases.Case118(), "case118"));
        Assert.True(BundledNetworks.IsShippedTopology(IeeeCases.Case118(), "CASE118"));
        Assert.False(BundledNetworks.IsShippedTopology(IeeeCases.Case118(), "case14"));
        Assert.False(BundledNetworks.IsShippedTopology(BigUserNetwork("gb-full"), "gb-full"));
        Assert.False(BundledNetworks.IsShippedTopology(IeeeCases.Case14(), "my-case"));
        Assert.True(BundledNetworks.IsShippedTopology(JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "gb-full")), "gb-full"));
    }

    [Fact]
    public void B198_every_desktop_replay_and_bridge_path_passes_its_gate()
    {
        Assert.Contains("Host.LoadReplay(s.ReplayDir!, _licence);", ReadRepo("src/GridSim.App/Run/RunViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("new BridgeSession(bridgeSettings, new BridgeDisplay(this), _licence);", ReadRepo("src/GridSim.App/Run/RunViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("_host.LoadReplay(s.ReplayDir!, App.Licence);", ReadRepo("src/GridSim.Wpf/MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("BundledNetworks.IsShippedTopology(model, _bridgeCase)", ReadRepo("src/GridSim.Wpf/MainWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("BundledNetworks.IsShippedTopology(model, _bridgeCase)", ReadRepo("src/GridSim.Ui/Hosting/BridgeSession.cs"), StringComparison.Ordinal);
    }
}
