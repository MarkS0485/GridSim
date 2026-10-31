using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>The repository root (the folder holding GridSim.slnx), found by walking up from the test binaries.</summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(System.IO.Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException($"GridSim.slnx not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>
/// UI programme Phase 1.1: the Avalonia theme is a port of the WPF Theme.xaml, so pages ported later find the same
/// colours under the same names. This reads the WPF file (the parity reference) at test time and checks every colour,
/// brush, font and size in it against the running app's resources, so the two cannot drift while both ship.
/// </summary>
public sealed class ThemeParityTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument WpfTheme()
        => XDocument.Load(Path.Combine(RepoRoot.Path, "src", "GridSim.Wpf", "Theme.xaml"));

    private static object Resource(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Dark, out object? value), $"resource {key} is missing from the Avalonia theme");
        return value!;
    }

    [AvaloniaFact]
    public void Every_WPF_colour_is_in_the_Avalonia_palette_with_the_same_value()
    {
        List<XElement> colours = WpfTheme().Root!.Elements(Wpf + "Color").ToList();
        Assert.True(colours.Count >= 11, $"only {colours.Count} colours read from the WPF theme");
        foreach (XElement c in colours)
        {
            string key = (string)c.Attribute(X + "Key")!;
            Color expected = Color.Parse(c.Value.Trim());
            Assert.Equal(expected, Assert.IsType<Color>(Resource(key)));
        }
    }

    [AvaloniaFact]
    public void Every_WPF_brush_is_in_the_Avalonia_palette_with_the_same_colour()
    {
        XElement root = WpfTheme().Root!;
        Dictionary<string, Color> colours = root.Elements(Wpf + "Color")
            .ToDictionary(c => (string)c.Attribute(X + "Key")!, c => Color.Parse(c.Value.Trim()), StringComparer.Ordinal);
        List<XElement> brushes = root.Elements(Wpf + "SolidColorBrush").ToList();
        Assert.True(brushes.Count >= 11, $"only {brushes.Count} brushes read from the WPF theme");
        foreach (XElement b in brushes)
        {
            string key = (string)b.Attribute(X + "Key")!;
            // WPF: Color="{StaticResource BgColor}".
            string colourKey = ((string)b.Attribute("Color")!).Replace("{StaticResource", "", StringComparison.Ordinal).TrimEnd('}').Trim();
            ISolidColorBrush brush = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(key));
            Assert.Equal(colours[colourKey], brush.Color);
        }
    }

    [AvaloniaFact]
    public void The_WPF_fonts_and_sizes_are_in_the_Avalonia_palette()
    {
        XNamespace sys = "clr-namespace:System;assembly=mscorlib";
        XElement root = WpfTheme().Root!;
        // Changed 2.1.001 (Mark's decision 2026-10-31, after the first GitHub CI run on Linux and macOS): the Avalonia app
        // no longer copies WPF's Windows-only font lists ("Bahnschrift, Segoe UI, Arial" and "Consolas, Cascadia Mono,
        // Courier New"), which fell back to wider fonts off Windows and overflowed the header there. It bundles one font
        // per key so every OS lays out the same: UiFont = Barlow (narrow, DIN-style, close to Bahnschrift), MonoFont =
        // Cascadia Mono (already second in WPF's list). Every WPF font key must still exist in the palette (parity of
        // keys), and each must name exactly its bundled family. Before: each key's family list equalled WPF's.
        var bundled = new Dictionary<string, string> { ["UiFont"] = "Barlow", ["MonoFont"] = "Cascadia Mono" };
        List<XElement> fonts = root.Elements(Wpf + "FontFamily").ToList();
        Assert.Equal(bundled.Keys.OrderBy(k => k, StringComparer.Ordinal), fonts.Select(f => (string)f.Attribute(X + "Key")!).OrderBy(k => k, StringComparer.Ordinal));
        foreach (XElement f in fonts)
        {
            string key = (string)f.Attribute(X + "Key")!;
            FontFamily family = Assert.IsType<FontFamily>(Resource(key));
            Assert.Equal(bundled[key], Assert.Single(family.FamilyNames));
            Assert.Equal("avares://GridSim.App/Fonts", family.Key?.Source.ToString().TrimEnd('/'));
        }
        List<XElement> sizes = root.Elements(sys + "Double").ToList();
        Assert.True(sizes.Count >= 7, $"only {sizes.Count} sizes read from the WPF theme");
        foreach (XElement d in sizes)
            Assert.Equal(double.Parse(d.Value, CultureInfo.InvariantCulture), Assert.IsType<double>(Resource((string)d.Attribute(X + "Key")!)));
    }

    [AvaloniaFact]
    public void The_row_link_colours_inline_in_WPF_are_named_with_the_same_values()
    {
        string wpf = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "GridSim.Wpf", "Theme.xaml"));
        Assert.Contains("Background=\"#0F151C\"", wpf, StringComparison.Ordinal);
        Assert.Contains("Value=\"#17222E\"", wpf, StringComparison.Ordinal);
        Assert.Equal(Color.Parse("#0F151C"), Assert.IsType<Color>(Resource("RowColor")));
        Assert.Equal(Color.Parse("#17222E"), Assert.IsType<Color>(Resource("RowHoverColor")));
    }

    [AvaloniaFact]
    public void Every_WPF_keyed_style_has_an_Avalonia_style_class_of_the_same_name()
    {
        // WPF Style="{StaticResource FlatBtn}" becomes Classes="FlatBtn": the same name, so a ported page swaps one
        // attribute for the other. Read the class names from the Avalonia styles file itself.
        string styles = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "GridSim.App", "Theme", "ControlRoom.axaml"));
        foreach (XElement s in WpfTheme().Root!.Elements(Wpf + "Style"))
        {
            string? key = (string?)s.Attribute(X + "Key");
            if (key is null) continue;   // an implicit style (every TextBlock, every ComboBox): a type selector here
            string type = (string)s.Attribute("TargetType")!;
            Assert.Contains($"Selector=\"{type}.{key}\"", styles, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void The_app_is_dark_and_the_styles_apply_the_palette()
    {
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        var flat = new Button { Content = "x", Classes = { "FlatBtn" } };
        var label = new TextBlock { Text = "x", Classes = { "Label" } };
        var plain = new TextBlock { Text = "x" };
        var card = new Border { Classes = { "Card" }, Child = new StackPanel { Children = { flat, label, plain } } };
        var w = new Window { Content = card };
        w.Show();
        try
        {
            static Color Of(IBrush? b) => Assert.IsAssignableFrom<ISolidColorBrush>(b).Color;
            Assert.Equal(Color.Parse("#FF1B2430"), Of(flat.Background));   // PanelHi
            Assert.Equal(Color.Parse("#FF26323F"), Of(flat.BorderBrush));  // Border
            Assert.Equal(new CornerRadius(4), flat.CornerRadius);
            Assert.Equal(Color.Parse("#FF8492A0"), Of(label.Foreground));  // Muted
            Assert.Equal(11, label.FontSize);
            Assert.Equal(Color.Parse("#FFE6EDF3"), Of(plain.Foreground));  // Text
            Assert.Equal(12, plain.FontSize);                               // BaseFontSize
            Assert.Equal(Color.Parse("#FF131A23"), Of(card.Background));   // Panel

            using var temp = new TempFolder();
            var main = new MainWindow(TestShell.Options(temp));
            Assert.Equal(Color.Parse("#FF0A0E13"), Of(main.Background));   // Bg, as the WPF window
            Assert.Equal(1580, main.Width);
            Assert.Equal(940, main.Height);
            Assert.Equal(1100, main.MinWidth);
            Assert.Equal(700, main.MinHeight);
            Assert.Equal(WindowState.Maximized, main.WindowState);
        }
        finally { w.Close(); }
    }
}
