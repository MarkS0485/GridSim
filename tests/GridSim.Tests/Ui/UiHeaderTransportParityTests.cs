using System.Text.RegularExpressions;
using System.Xml.Linq;
using GridSim.Ui.Schematic;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme slice 1.7: the header and transport-bar extras. The WPF window (src/GridSim.Wpf/MainWindow.xaml and
/// MainWindow.xaml.cs) is the oracle and is read here as text, because its rules live in private handlers of a Windows
/// only project this test project cannot reference:
/// <list type="bullet">
/// <item><description>the COLOR button's labels (MainWindow.ColorLabel) and its cycle (MainWindow.Color_Click) against
/// <see cref="MapCommands.ColourButtonLabel"/> and <see cref="MapCommands.NextColourMode"/>, every mode;</description></item>
/// <item><description>the toggle buttons' opacities (1.0 shown, 0.55 hidden or without geography) against
/// <see cref="MapCommands.ToggleButtonOpacity"/>;</description></item>
/// <item><description>the replay timeline-reset rule of the WPF scrubber, restart and step back, which the Avalonia run
/// view model states in the same words;</description></item>
/// <item><description>every ported header and transport control's WPF size, margin, text and tool tip against the
/// Avalonia window's (src/GridSim.App/MainWindow.axaml).</description></item>
/// </list>
/// </summary>
public sealed class UiHeaderTransportParityTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Ava = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static string WpfCode() => File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Wpf", "MainWindow.xaml.cs")).Replace("\r\n", "\n");

    // The body of the WPF method that starts at the first line containing signature, up to the next blank line.
    private static string MethodBody(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"WPF MainWindow.xaml.cs has no {signature}");
        int end = code.IndexOf("\n\n", at, StringComparison.Ordinal);
        return code[at..(end < 0 ? code.Length : end)];
    }

    [Fact]
    public void The_COLOR_button_names_and_cycles_the_modes_as_WPF()
    {
        string code = WpfCode();

        // WPF ColorLabel: "MapColourMode.X => \" X\"" arms plus a default that is LOADING.
        string label = MethodBody(code, "private static string ColorLabel(MapColourMode mode)");
        var labels = Regex.Matches(label, @"MapColourMode\.(\w+) => ""([^""]*)""")
            .ToDictionary(m => Enum.Parse<MapColourMode>(m.Groups[1].Value), m => m.Groups[2].Value);
        string fallback = Regex.Match(label, @"_ => ""([^""]*)""").Groups[1].Value;
        // WPF Color_Click: "MapColourMode.A => MapColourMode.B" arms plus a default that goes to LOADING.
        string click = MethodBody(code, "private void Color_Click(");
        var next = Regex.Matches(click, @"MapColourMode\.(\w+) => MapColourMode\.(\w+)")
            .ToDictionary(m => Enum.Parse<MapColourMode>(m.Groups[1].Value), m => Enum.Parse<MapColourMode>(m.Groups[2].Value));
        MapColourMode nextFallback = Enum.Parse<MapColourMode>(Regex.Match(click, @"_ => MapColourMode\.(\w+)").Groups[1].Value);
        Assert.Equal(3, labels.Count);
        Assert.Equal(3, next.Count);

        foreach (MapColourMode mode in Enum.GetValues<MapColourMode>())
        {
            string wpfLabel = labels.TryGetValue(mode, out string? l) ? l : fallback;
            Assert.Equal(wpfLabel, MapCommands.ColourButtonLabel(mode));
            MapColourMode wpfNext = next.TryGetValue(mode, out MapColourMode n) ? n : nextFallback;
            Assert.Equal(wpfNext, MapCommands.NextColourMode(mode));
        }
        // The cycle visits all four modes and comes back.
        var seen = new List<MapColourMode>();
        MapColourMode at = MapColourMode.Loading;
        for (int i = 0; i < 4; i++) { seen.Add(at); at = MapCommands.NextColourMode(at); }
        Assert.Equal(MapColourMode.Loading, at);
        Assert.Equal(4, seen.Distinct().Count());
    }

    [Fact]
    public void The_toggle_buttons_use_the_WPF_opacities()
    {
        string code = WpfCode();
        // Every WPF toggle-button opacity line is "? 1.0 : 0.55".
        foreach (string line in new[]
        {
            "FleetBtn.Opacity = _renderer.ShowFleet ? 1.0 : 0.55;",
            "DnoBtn.Opacity = _renderer.ShowDnoAreas ? 1.0 : 0.55;",
            "GspRegionBtn.Opacity = _renderer.ShowGspRegions ? 1.0 : 0.55;",
            "DnoBtn.Opacity = _renderer.HasGeography && _settings.DnoAreas ? 1.0 : 0.55;",
            "GspRegionBtn.Opacity = _renderer.HasGeography && _settings.GspRegions ? 1.0 : 0.55;",
            "DnoBtn.IsEnabled = GspRegionBtn.IsEnabled = AssetsBtn.IsEnabled = _renderer.HasGeography;",
        })
            Assert.Contains(line, code, StringComparison.Ordinal);
        Assert.Equal(1.0, MapCommands.ButtonOnOpacity);
        Assert.Equal(0.55, MapCommands.ButtonOffOpacity);
        Assert.Equal(1.0, MapCommands.ToggleButtonOpacity(shown: true));
        Assert.Equal(0.55, MapCommands.ToggleButtonOpacity(shown: false));
        Assert.Equal(0.55, MapCommands.ToggleButtonOpacity(shown: true, available: false));
        Assert.Equal(0.55, MapCommands.ToggleButtonOpacity(shown: false, available: false));
    }

    [Fact]
    public void The_replay_moves_reset_the_timeline_where_WPF_does()
    {
        string code = WpfCode();
        string wpfRestart = MethodBody(code, "private void Restart_Click(");
        string wpfBack = MethodBody(code, "private void StepBack_Click(");
        string wpfFwd = MethodBody(code, "private void StepForward_Click(");
        string wpfScrub = MethodBody(code, "private void Scrubber_ValueChanged(");
        // WPF: restart and step back reset after the move and before the render; next frame never; a scrub on a
        // backward move or onto frame 0.
        Assert.Matches(@"ReplayRestart\(\);\s*\n\s*_vm\.ResetTimeline\(\);[^\n]*\n\s*RenderReplayFrame", wpfRestart);
        Assert.Matches(@"ReplayRewind\(\);[\s\S]*_vm\.ResetTimeline\(\);\s*\n\s*RenderReplayFrame", wpfBack);
        Assert.DoesNotContain("ResetTimeline", wpfFwd, StringComparison.Ordinal);
        Assert.Contains("if (at.Cursor.Index == 0 || at.Cursor.Index < at.Before) _vm.ResetTimeline();", wpfScrub, StringComparison.Ordinal);
        Assert.Contains("ReplaySeek((int)Math.Round(e.NewValue))", wpfScrub, StringComparison.Ordinal);

        // The Avalonia run view model makes the same calls in the same order (its source, read the same way).
        string app = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.App", "Run", "RunViewModel.cs")).Replace("\r\n", "\n");
        Assert.Matches(@"Host\.ReplayRestart\(\);\s*\n\s*TimelineReset\?\.Invoke\(this, EventArgs\.Empty\);[^\n]*\n\s*ShowReplayFrame", app);
        Assert.Matches(@"Host\.ReplayRewind\(\);\s*\n\s*TimelineReset\?\.Invoke\(this, EventArgs\.Empty\);\s*\n\s*ShowReplayFrame", app);
        Assert.Contains("if (at.Cursor.Index == 0 || at.Cursor.Index < at.Before) TimelineReset?.Invoke(this, EventArgs.Empty);", app, StringComparison.Ordinal);
        Assert.Contains("SeekReplay((int)Math.Round(value));", app, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_title_is_built_as_the_WPF_title()
    {
        // WPF: the XAML title, then OnLoaded appends the version and the licence tier.
        XElement wpf = XDocument.Load(Path.Combine(Root(), "src", "GridSim.Wpf", "MainWindow.xaml")).Root!;
        Assert.Equal("GridSim  -  GB Transmission Control", (string?)wpf.Attribute("Title"));
        Assert.Contains("Title = $\"{Title}  -  v{GridSim.Core.BuildInfo.Version}  -  {App.Licence.Entitlement.Tier.DisplayName()} licence\";",
            WpfCode(), StringComparison.Ordinal);

        // Avalonia: the same stem, then the run pipeline's tier line, built from the same two values in the same words.
        string vm = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.App", "MainWindowViewModel.cs"));
        string run = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.App", "Run", "RunViewModel.cs"));
        Assert.Contains("public const string TitleStem = \"GridSim  -  GB Transmission Control\";", vm, StringComparison.Ordinal);
        Assert.Contains("Title = $\"{TitleStem}  -  {Run.LicenceTierText}\";", vm, StringComparison.Ordinal);
        Assert.Contains("LicenceTierText = $\"v{GridSim.Core.BuildInfo.Version}  -  {licence.Entitlement.Tier.DisplayName()} licence\";",
            run, StringComparison.Ordinal);
        // And the window binds its title to it.
        XElement ava = XDocument.Load(Path.Combine(Root(), "src", "GridSim.App", "MainWindow.axaml")).Root!;
        Assert.Equal("{Binding Title}", (string?)ava.Attribute("Title"));
    }

    [Fact]
    public void The_ported_header_and_transport_controls_keep_the_WPF_sizes_texts_and_tips()
    {
        XElement wpf = XDocument.Load(Path.Combine(Root(), "src", "GridSim.Wpf", "MainWindow.xaml")).Root!;
        XElement ava = XDocument.Load(Path.Combine(Root(), "src", "GridSim.App", "MainWindow.axaml")).Root!;
        XElement Named(XElement root, string name) =>
            Assert.Single(root.Descendants(), e => (string?)e.Attribute(X + "Name") == name);

        // WPF name -> Avalonia name, and the attributes that must match (the tip is ToolTip in WPF, ToolTip.Tip here).
        (string Wpf, string Ava, string[] Attrs)[] pairs =
        {
            ("SourcePicker", "SourcePicker", new[] { "Width" }),
            ("SolverPicker", "SolverPicker", new[] { "Width" }),
            ("SearchBox", "SearchBox", new[] { "Width", "Margin", "ToolTip" }),
            ("LoadRunBtn", "LoadRunButton", new[] { "Content", "Margin", "ToolTip" }),
            ("AuditLogBtn", "AuditLogButton", new[] { "Content", "Margin", "ToolTip" }),
            ("RestartBtn", "RestartButton", new[] { "Content", "Width", "Margin", "ToolTip" }),
            ("StepBackBtn", "StepBackButton", new[] { "Content", "Width", "Margin", "ToolTip" }),
            ("SpeedBtn", "SpeedButton", new[] { "Width", "Margin" }),
            ("ColorBtn", "ColorButton", new[] { "Content", "Width", "Margin", "ToolTip" }),
            ("FleetBtn", "FleetButton", new[] { "Content", "Width", "Margin", "ToolTip" }),
            ("DnoBtn", "DnoButton", new[] { "Content", "Width", "Margin", "ToolTip" }),
            ("GspRegionBtn", "GspRegionButton", new[] { "Content", "Width", "Margin", "ToolTip" }),
            ("FrameReadout", "FrameReadout", new[] { "Margin", "Foreground" }),
            ("Scrubber", "Scrubber", new[] { "Minimum", "Margin", "IsSnapToTickEnabled", "TickFrequency", "SmallChange", "LargeChange", "ToolTip" }),
        };
        foreach ((string w, string a, string[] attrs) in pairs)
        {
            XElement we = Named(wpf, w), ae = Named(ava, a);
            Assert.Equal(we.Name.LocalName, ae.Name.LocalName);   // the same kind of control
            foreach (string attr in attrs)
            {
                string? wv = (string?)we.Attribute(attr);
                string? av = (string?)ae.Attribute(attr == "ToolTip" ? "ToolTip.Tip" : attr);
                Assert.True(wv is not null, $"WPF {w} has no {attr}");
                Assert.True(wv == av, $"{attr}: WPF {w} has '{wv}', the Avalonia {a} has '{av}'");
            }
        }
        // The replay-only controls are shown only for a replay, the STEP picker only for a live case, as in WPF.
        foreach (string a in new[] { "RestartButton", "StepBackButton", "FrameReadout", "Scrubber" })
            Assert.Equal("{Binding Run.IsReplay}", (string?)Named(ava, a).Attribute("IsVisible"));
        Assert.Equal("{Binding Run.IsLive}", (string?)Named(ava, "StepPicker").Attribute("IsVisible"));
    }
}
