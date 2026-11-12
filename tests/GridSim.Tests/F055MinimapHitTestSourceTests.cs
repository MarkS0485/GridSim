using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F-055: FastSchematic.DrawMinimap returned early (no nodes, empty world, panel too small, window too short) without
/// clearing _mmRect, and the click hit test checked only _showMinimap, so clicks on the now-empty corner still
/// recentred the view from a phantom minimap. The test project targets net10.0 (not net10.0-windows) and cannot load
/// WPF, so, like the CLI flag contract tests, these are source-level anti-drift checks over FastSchematic.cs.
/// </summary>
public class F055MinimapHitTestSourceTests
{
    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        string path = Path.Combine(dir!.FullName, "src", "GridSim.Wpf", "Visuals", "FastSchematic.cs");
        Assert.True(File.Exists(path), $"source not found: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>The method body from its signature to the first line that is exactly "    }" (end of a member).</summary>
    private static string Member(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"member not found: {signature}");
        int end = src.IndexOf("\n    }\n", i, StringComparison.Ordinal);
        Assert.True(end > i, $"end of member not found: {signature}");
        return src[i..end];
    }

    [Fact]
    public void F055_draw_minimap_clears_the_rect_before_any_early_return()
    {
        string body = Member(Source(), "private void DrawMinimap(DrawingContext dc)");
        int clear = body.IndexOf("_mmRect = Rect.Empty;", StringComparison.Ordinal);
        int firstReturn = body.IndexOf("return;", StringComparison.Ordinal);
        Assert.True(clear >= 0, "DrawMinimap must clear _mmRect");
        Assert.True(firstReturn > clear, "_mmRect must be cleared BEFORE the first early return");
    }

    [Fact]
    public void F055_a_hidden_minimap_leaves_no_clickable_rect()
    {
        string src = Source();
        Assert.Contains("if (_showMinimap) DrawMinimap(dc);\n        else _mmRect = Rect.Empty;", src);
    }

    [Fact]
    public void F055_click_hit_test_requires_a_drawn_panel_and_a_positive_scale()
    {
        string body = Member(Source(), "protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)");
        Assert.Contains("_showMinimap && !_mmRect.IsEmpty && _mmScale > 0 && _mmRect.Contains(down)", body);
        // The old ungated form must not come back.
        Assert.DoesNotContain("if (_showMinimap && _mmRect.Contains(down))", body);
    }
}
