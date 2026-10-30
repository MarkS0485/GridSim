using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R101 (October 2026 review): WPF dead code from abandoned work (SchematicRenderer, PlaceholderPageVm and its
/// template), a CycleVm whose ResidentCycleService worker threads were never disposed, and a base-solve time that was
/// always passed as 0 so the CYCLE tab showed " - ". Source-level (the test project does not reference WPF).
/// </summary>
public class WpfDeadCodeAndCycleTests
{
    private static string Wpf(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        return Path.Combine(new[] { dir!.FullName, "src", "GridSim.Wpf" }.Concat(parts).ToArray());
    }

    [Fact]
    public void Dead_renderer_and_placeholder_page_are_gone()
    {
        Assert.False(File.Exists(Wpf("Visuals", "SchematicRenderer.cs")));
        Assert.DoesNotContain("PlaceholderPageVm", File.ReadAllText(Wpf("Views", "Assets", "AssetPages.cs")));
        Assert.DoesNotContain("PlaceholderPageVm", File.ReadAllText(Wpf("Views", "Assets", "AssetExplorerView.xaml")));
        // Test retargeted 2026-10-20 (UI programme ASSETS A2): the page logic of src/GridSim.Wpf/Views/Assets/AssetPages.cs
        // moved to src/GridSim.Ui/Assets/Pages/AssetPages.cs; assertions unchanged (both WPF files still checked), and
        // strengthened: every file under src/GridSim.Ui/Assets (and under src/GridSim.App/Assets once the views exist) and
        // every file in the WPF Views/Assets folder is checked too, so no placeholder page can come back in the move.
        string src = Path.GetDirectoryName(Wpf())!;
        foreach (string folder in new[] { Path.Combine(src, "GridSim.Ui", "Assets"), Path.Combine(src, "GridSim.App", "Assets"), Wpf("Views", "Assets") })
        {
            if (!Directory.Exists(folder)) { Assert.EndsWith(Path.Combine("GridSim.App", "Assets"), folder, StringComparison.Ordinal); continue; }
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("PlaceholderPageVm", File.ReadAllText(file), StringComparison.Ordinal);
        }
        Assert.True(Directory.GetFiles(Path.Combine(src, "GridSim.Ui", "Assets"), "*.cs", SearchOption.AllDirectories).Length >= 5);
    }

    [Fact]
    public void Cycle_service_is_disposed_on_close_and_fed_the_measured_base_solve()
    {
        string window = File.ReadAllText(Wpf("MainWindow.xaml.cs"));
        Assert.Contains("_vm.Cycle.Dispose();", window);
        Assert.Contains("_vm.Update(f, boundaries, solveMs);", window);
        // Test retargeted 2026-10-20 (UI programme 1.6): src/GridSim.Wpf/Views/CycleVm.cs moved to
        // src/GridSim.Ui/Tabs/Cycle/CycleModel.cs; assertions unchanged, and strengthened: the WPF view-model must derive
        // from that model (so the window's _vm.Cycle.Dispose() is that model's Dispose), offer no frame itself, and
        // still marshal a completed cycle through the WPF dispatcher, which the model calls for every completed cycle.
        string cycle = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Wpf())!, "GridSim.Ui", "Tabs", "Cycle", "CycleModel.cs"));
        Assert.Contains("_svc.Offer(f, baseSolveMs);", cycle);
        Assert.DoesNotContain("_svc.Offer(f, 0);", cycle);
        Assert.Contains("private void OnCycleCompleted() => _postToUi(ApplyLatest);", cycle);
        Assert.Contains("_svc.Updated += OnCycleCompleted;", cycle);
        string adapter = File.ReadAllText(Wpf("Views", "CycleVm.cs"));
        Assert.Contains("public sealed class CycleVm : CycleModel", adapter);
        Assert.Contains("public CycleVm() : base(apply => System.Windows.Application.Current?.Dispatcher.BeginInvoke(apply)) { }", adapter);
        Assert.DoesNotContain("_svc", adapter);
        Assert.DoesNotContain("Offer(", adapter);
    }
}
