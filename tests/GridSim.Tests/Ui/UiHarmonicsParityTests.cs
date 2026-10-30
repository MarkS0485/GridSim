using System.Xml.Linq;
using GridSim.Ui.Tabs.Harmonics;
using Xunit;

namespace GridSim.Tests.Ui;

/// <summary>
/// UI programme slice 1.7: the HARMONICS tab. The WPF tab is four fixed texts written straight into
/// src/GridSim.Wpf/MainWindow.xaml (no view-model, no code-behind, nothing fed per frame), so the oracle is that XAML
/// itself: these tests read it and check that the shared <see cref="HarmonicsModel"/> holds exactly the WPF words, in the
/// WPF order (ordinal string equality, after the XML parser has decoded &amp;lt; and &amp;gt;), and that the Avalonia view
/// (src/GridSim.App/Tabs/HarmonicsView.axaml) lays them out with the same sizes, weights, margins, font and colour.
/// </summary>
public sealed class UiHarmonicsParityTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Ava = "https://github.com/avaloniaui";

    private static string Root()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    // The WPF HARMONICS TabItem: exactly one, found by its header.
    private static XElement WpfTab()
    {
        XDocument doc = XDocument.Load(Path.Combine(Root(), "src", "GridSim.Wpf", "MainWindow.xaml"));
        return Assert.Single(doc.Descendants(Wpf + "TabItem"), t => (string?)t.Attribute("Header") == HarmonicsModel.Header);
    }

    private static XElement AvaloniaView() =>
        XDocument.Load(Path.Combine(Root(), "src", "GridSim.App", "Tabs", "HarmonicsView.axaml")).Root!;

    [Fact]
    public void The_model_holds_exactly_the_WPF_texts_in_the_WPF_order()
    {
        string[] wpf = WpfTab().Descendants(Wpf + "TextBlock").Select(t => (string?)t.Attribute("Text") ?? "").ToArray();
        Assert.Equal(4, wpf.Length);
        Assert.Equal(wpf, HarmonicsModel.Texts);
        for (int i = 0; i < wpf.Length; i++)
            Assert.True(string.Equals(wpf[i], HarmonicsModel.Texts[i], StringComparison.Ordinal), $"text {i} differs");

        // The bindable properties give the same words as the constants, one for one.
        var m = new HarmonicsModel();
        Assert.Equal(new[] { m.Title, m.Summary, m.Command, m.Note }, HarmonicsModel.Texts);
        Assert.Equal("gridsim harmonics <case> [--spectra <gridsim-harmonics/1.json> | --synth-sources] [--enableExport]", m.Command);

        // The honesty rule pinned on the WPF text (ReadmeHonestyTests) holds for the shared text too.
        Assert.Contains("cannot be run from this app or the CLI yet", m.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Avalonia_view_lays_the_texts_out_as_WPF_does()
    {
        XElement wpfPanel = Assert.Single(WpfTab().Descendants(Wpf + "StackPanel"));
        XElement avaPanel = Assert.Single(AvaloniaView().Descendants(Ava + "StackPanel"));
        Assert.Equal((string?)wpfPanel.Attribute("Margin"), (string?)avaPanel.Attribute("Margin"));
        Assert.Equal((string?)wpfPanel.Attribute("MaxWidth"), (string?)avaPanel.Attribute("MaxWidth"));
        // Both scroll vertically when the window is short.
        Assert.Equal("Auto", (string?)Assert.Single(WpfTab().Descendants(Wpf + "ScrollViewer")).Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)Assert.Single(AvaloniaView().Descendants(Ava + "ScrollViewer")).Attribute("VerticalScrollBarVisibility"));

        XElement[] wpf = wpfPanel.Elements(Wpf + "TextBlock").ToArray();
        XElement[] ava = avaPanel.Elements(Ava + "TextBlock").ToArray();
        Assert.Equal(wpf.Length, ava.Length);
        string[] bindings = { "{Binding Title}", "{Binding Summary}", "{Binding Command}", "{Binding Note}" };
        string[] layout = { "FontSize", "FontWeight", "Margin", "TextWrapping", "FontFamily", "Foreground" };
        for (int i = 0; i < wpf.Length; i++)
        {
            // Each Avalonia text is bound to the model's property for the WPF text at the same place.
            Assert.Equal(bindings[i], (string?)ava[i].Attribute("Text"));
            foreach (string attr in layout)
                Assert.True((string?)wpf[i].Attribute(attr) == (string?)ava[i].Attribute(attr),
                    $"text {i}: {attr} is '{(string?)wpf[i].Attribute(attr)}' in WPF and '{(string?)ava[i].Attribute(attr)}' in the Avalonia view");
        }
    }
}
