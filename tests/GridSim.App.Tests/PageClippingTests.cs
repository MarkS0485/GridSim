using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GridSim.App.Map;
using GridSim.App.Shell;
using GridSim.Core.Licensing;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Models;
using GridSim.Ui.Shell;
using Xunit;

namespace GridSim.App.Tests;

/// <summary>
/// The Phase 1 parity sign-off (UiSmoke against AssetSmoke, 2026-10-24) found pages where a panel given less height than
/// it asks for drew its parts over each other: WPF clips each element to its layout slot, Avalonia does not by default.
/// These pin, at the window's default size and at the sign-off size (1440x860), that every part of such a panel that is
/// squeezed below its natural height clips to its slot, so nothing is drawn over anything else.
/// </summary>
public sealed class PageClippingTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        throw new InvalidOperationException("The GridSim checkout (GridSim.slnx) was not found above " + AppContext.BaseDirectory);
    }

    private static ShellOptions Options(TempFolder temp, ModelSource source) => new()
    {
        SettingsStore = ShellSettingsStore.ForFile(temp.File("settings.json")),
        DiscoverCases = (_, _) => new List<ModelSource> { source },
        LicenceDescription = () => "Test licence",
        Licence = () => new LicenceGate(Entitlement.FreeDefault),
        UseNativeMenu = false,
        AutoLoadAtStart = false,
        LoadGeography = _ => null,
    };

    private static void PumpUntil(Func<bool> done, string what, int timeoutMs = 120_000)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(MainWindow w)
    {
        w.Close();
        if (w.StopLoop is { } stop)
        {
            PumpUntil(() => stop.IsCompleted, "the run loop to stop");
            stop.GetAwaiter().GetResult();
        }
    }

    // The uk case loaded (and a frame shown) in a window of the given size, on the given tab.
    private static MainWindow Open(TempFolder temp, double width, double height, string tab)
    {
        var uk = new ModelSource("GB reduced  10-zone", Path.Combine(RepoRoot(), "data", "uk"));
        var w = new MainWindow(Options(temp, uk)) { Width = width, Height = height };
        w.Show();
        w.ViewModel.SelectedSource = w.ViewModel.Sources[0];
        PumpUntil(() => w.ViewModel.Run.ActiveSolverText.StartsWith("auto * ", StringComparison.Ordinal), "the case to load");
        w.SelectTab(tab);
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    // Each child's natural height (unlimited room, without its margin), read once and the layout then restored as the
    // window has it: a squeezed control also ASKS for less, so its own DesiredSize cannot say whether it was squeezed.
    private static Dictionary<Control, double> NaturalHeights(Window w, Panel panel)
    {
        Control[] kids = panel.Children.Where(c => c.IsVisible).ToArray();
        var arranged = kids.ToDictionary(c => c, c => c.Bounds);
        var natural = new Dictionary<Control, double>();
        foreach (Control c in kids)
        {
            c.Measure(new Size(Math.Max(1, c.Bounds.Width + c.Margin.Left + c.Margin.Right), double.PositiveInfinity));
            natural[c] = c.DesiredSize.Height - c.Margin.Top - c.Margin.Bottom;
        }
        panel.InvalidateMeasure();
        w.UpdateLayout();
        Assert.All(kids, c => Assert.Equal(arranged[c], c.Bounds));   // the layout is back as it was
        return natural;
    }

    // Test changed 2026-10-24 (THE MACHINE rebalance, T4; under Mark's delegation, recorded in the commit and the
    // CHANGELOG). The 2.0.107 version pinned the squeezed layout: it REQUIRED the spinning grid to be squeezed at both
    // sizes ("otherwise this would test nothing") and only checked that squeezed parts clip, and the headline was whole
    // only at the default size. The page now scrolls, so this pins the stronger result: at both sizes NOTHING in the
    // spinning grid is squeezed (every part at its natural height, both traces at the WPF 70 px), the headline is whole
    // and shares its slot with nothing, the card still clips as a guard, and the side cards are capped at the spinning
    // grid's height so a long list never sets the scroll extent.
    [AvaloniaTheory]
    [InlineData(1580, 940)]   // the window's default size
    [InlineData(1440, 860)]   // the sign-off size
    public void THE_MACHINE_spinning_grid_has_room_for_its_headline_traces_and_shaft(double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, "THE MACHINE");
        try
        {
            var card = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SpinningGridCard");
            var panel = w.GetVisualDescendants().OfType<DockPanel>().Single(p => p.Name == "SpinningGridPanel");
            var headline = w.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "SpinningGridHeadline");
            var scroll = w.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "MachinePageScroll");
            Assert.True(card.ClipToBounds, "the card clips its content");
            Assert.Same(scroll, card.GetVisualAncestors().OfType<ScrollViewer>().First());

            // Nothing in the spinning grid is squeezed below its natural height.
            Dictionary<Control, double> natural = NaturalHeights(w, panel);
            foreach ((Control c, double h) in natural)
                Assert.True(c.Bounds.Height >= h - 0.5, $"{c.Name ?? c.GetType().Name} {width}x{height}: {c.Bounds.Height} high of {h}");

            // Both recent traces are drawn at the WPF 70 px.
            foreach (string name in new[] { "FreqSparkline", "VoltSparkline" })
            {
                Control trace = w.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
                Assert.Equal(70, trace.Bounds.Height);
            }

            // The headline stays inside the panel, and no trace or shaft row shares its slot.
            Assert.True(headline.Bounds.Top >= -0.5 && headline.Bounds.Bottom <= panel.Bounds.Height + 0.5, $"headline at {headline.Bounds}");
            foreach (Control other in panel.Children.Where(c => c != headline && c.IsVisible))
                Assert.False(other.Bounds.Height > 0 && headline.Bounds.Height > 0 && other.Bounds.Intersects(headline.Bounds),
                    $"{other.GetType().Name} at {other.Bounds} shares the headline's slot {headline.Bounds}");

            // The side cards are never taller than the spinning grid (their lists scroll inside them).
            foreach (string name in new[] { "FuelInCard", "PowerOutCard" })
            {
                Border side = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == name);
                Assert.True(side.Bounds.Height <= card.Bounds.Height + 0.5, $"{name} {side.Bounds.Height} high, spinning grid {card.Bounds.Height}");
            }
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData(1580, 940)]
    [InlineData(1440, 860)]
    public void The_transformer_page_scrolls_as_a_whole_so_no_card_is_squeezed_or_drawn_over_another(double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, MainWindow.AssetsTabHeader);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetTreeNode tx = nav.Tree.PreOrder().First(n => n.Page.Kind == AssetPageKind.Transformer);
            Assert.True(nav.Select(tx));
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var scroll = w.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "TransformerPageScroll");
            var schematic = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TransformerSchematicCard");
            var dials = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TransformerDialsCard");
            var page = (Panel)schematic.Parent!;
            Assert.Same(scroll, page.GetVisualAncestors().OfType<ScrollViewer>().First());

            // At these sizes the page is taller than the window, so it scrolls (otherwise this would test nothing) ...
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 0.5, $"the page fits at {width}x{height}: nothing to scroll");

            // ... and every card has its full height, the two cards never share space, and both still clip.
            Dictionary<Control, double> natural = NaturalHeights(w, page);
            foreach (Border card in new[] { schematic, dials })
            {
                Assert.True(card.Bounds.Height >= natural[card] - 0.5, $"{card.Name} {width}x{height}: {card.Bounds.Height} high of {natural[card]}");
                Assert.True(card.ClipToBounds, $"{card.Name} does not clip");
            }
            Assert.False(schematic.Bounds.Intersects(dials.Bounds), $"schematic {schematic.Bounds} and dials {dials.Bounds} overlap");

            // The dials can be scrolled wholly into view (they start below the window's bottom edge at these sizes).
            Point? before = dials.TranslatePoint(default, scroll);
            Assert.True(before is { } b && b.Y + dials.Bounds.Height > scroll.Viewport.Height, $"the dials card is already in view: top {before}");
            dials.BringIntoView();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Point? top = dials.TranslatePoint(default, scroll);
            Assert.True(top is { } t && t.Y >= -0.5 && t.Y + dials.Bounds.Height <= scroll.Viewport.Height + 0.5,
                $"the dials card is not wholly in view after BringIntoView: top {top}, viewport {scroll.Viewport}");
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData(1580, 940)]
    [InlineData(1440, 860)]
    public void GENERATION_and_INERTIA_keeps_values_clear_of_the_scroll_bars_and_the_trip_button_reachable(double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, "GENERATION & INERTIA");
        try
        {
            ScrollViewer Scroll(string name) => w.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == name);

            // The two lists overflow at these sizes; where the vertical bar shows, no text of a row runs under it.
            int barsSeen = 0;
            foreach (string name in new[] { "FuelMixScroll", "InertiaByClassScroll" })
            {
                ScrollViewer list = Scroll(name);
                ScrollBar? bar = list.GetVisualDescendants().OfType<ScrollBar>()
                    .FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Vertical && b.IsEffectivelyVisible && b.Bounds.Width > 0);
                if (bar is null) continue;
                barsSeen++;
                double barLeft = bar.TranslatePoint(default, list)!.Value.X;
                foreach (TextBlock t in list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
                {
                    double right = t.TranslatePoint(new Point(t.Bounds.Width, 0), list)!.Value.X;
                    Assert.True(right <= barLeft + 0.5, $"{name} {width}x{height}: '{t.Text}' ends at {right}, under the scroll bar from {barLeft}");
                }
            }
            Assert.True(barsSeen > 0, $"no list shows a scroll bar at {width}x{height}, so this tests nothing");

            // The side column scrolls, and the trip button can be brought wholly into view.
            ScrollViewer side = Scroll("GenerationSideScroll");
            Assert.True(side.Extent.Height > side.Viewport.Height + 0.5, $"the side column fits at {width}x{height}: nothing to scroll");
            Button trip = w.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TripButton");
            trip.BringIntoView();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Point? top = trip.TranslatePoint(default, side);
            Assert.True(top is { } t2 && t2.Y >= -0.5 && t2.Y + trip.Bounds.Height <= side.Viewport.Height + 0.5,
                $"the trip button is not wholly in view after BringIntoView: top {top}, viewport {side.Viewport}");
        }
        finally { Close(w); }
    }

    public static TheoryData<string, string, string, double, double> ScrolledUnitPages => new()
    {
        { "Transformer", "TransformerPageScroll", "TransformerSchematicCard", 1580, 940 },
        { "Transformer", "TransformerPageScroll", "TransformerSchematicCard", 1440, 860 },
        { "Substation", "SubstationPageScroll", "SubstationDiagramCard", 1580, 940 },
        { "Substation", "SubstationPageScroll", "SubstationDiagramCard", 1440, 860 },
    };

    [AvaloniaTheory]
    [MemberData(nameof(ScrolledUnitPages))]
    public void A_scrolled_unit_page_gives_its_diagram_card_full_height_and_keeps_text_clear_of_the_scroll_bar(
        string kind, string scrollName, string cardName, double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, MainWindow.AssetsTabHeader);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetPageKind pageKind = Enum.Parse<AssetPageKind>(kind);
            Assert.True(nav.Select(nav.Tree.PreOrder().First(n => n.Page.Kind == pageKind)));
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var scroll = w.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == scrollName);
            var card = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == cardName);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 0.5, $"{kind} page fits at {width}x{height}: nothing to scroll");

            // The diagram card has its full height and clips to it.
            Dictionary<Control, double> natural = NaturalHeights(w, (Panel)card.Parent!);
            Assert.True(card.Bounds.Height >= natural[card] - 0.5, $"{cardName} {width}x{height}: {card.Bounds.Height} high of {natural[card]}");
            Assert.True(card.ClipToBounds, $"{cardName} does not clip");

            // No text in view runs under the page's vertical scroll bar.
            ScrollBar bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                .First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical && b.TemplatedParent == scroll);
            Assert.True(bar.IsEffectivelyVisible && bar.Bounds.Width > 0, "the page's scroll bar is not shown");
            double barLeft = bar.TranslatePoint(default, scroll)!.Value.X;
            int checkedTexts = 0;
            foreach (TextBlock t in scroll.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
            {
                if (t.GetVisualAncestors().OfType<ScrollViewer>().First() != scroll) continue;   // a nested list's own text
                Point? at = t.TranslatePoint(default, scroll);
                if (at is not { } a || a.Y + t.Bounds.Height < 0 || a.Y > scroll.Viewport.Height) continue;   // not in view
                double right = a.X + t.Bounds.Width;
                checkedTexts++;
                Assert.True(right <= barLeft + 0.5, $"{kind} page {width}x{height}: '{t.Text}' ends at {right}, under the scroll bar from {barLeft}");
            }
            Assert.True(checkedTexts > 0, "no text in view was checked");
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData(1580, 940)]
    [InlineData(1440, 860)]
    public void The_map_legend_and_key_hint_are_never_drawn_over_the_minimap(double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, "NETWORK");
        try
        {
            SchematicControl map = w.MapControl;
            map.FitToView();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()?.Dispose();   // the minimap's rectangle is set when the map draws
            Rect mm = map.MinimapRect;
            Assert.True(mm.Width > 0 && mm.Height > 0, "the minimap is not shown");
            // The minimap in window coordinates.
            Point mmAt = map.TranslatePoint(mm.TopLeft, w)!.Value;
            var minimap = new Rect(mmAt, mm.Size);

            foreach (string name in new[] { "LegendBox", "MapHintText" })
            {
                Control c = w.GetVisualDescendants().OfType<Control>().Single(x => x.Name == name);
                Assert.True(c.IsEffectivelyVisible && c.Bounds.Width > 0, $"{name} is not shown");
                var box = new Rect(c.TranslatePoint(default, w)!.Value, c.Bounds.Size);
                Assert.False(box.Intersects(minimap), $"{name} {width}x{height} at {box} is drawn over the minimap at {minimap}");
            }
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData(1580, 940)]
    [InlineData(1440, 860)]
    public void MECHANICAL_per_machine_rows_line_up_with_their_header_columns(double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, "MECHANICAL");
        try
        {
            ItemsControl list = w.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "MachinesList");
            TextBlock header = w.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "MW" && Grid.GetColumn(t) == 3
                && t.GetVisualAncestors().OfType<ItemsControl>().All(i => i != list));
            double Right(Control c) => c.TranslatePoint(new Point(c.Bounds.Width, 0), w)!.Value.X;
            double headerRight = Right(header);

            // Each row's MW cell (column 3 of the row grid) ends where the header's MW column ends (within the header's
            // scroll-bar allowance and the row's padding): the row stretches across the list, as a WPF Button row does.
            var rows = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("RowLinkBtn") && b.IsEffectivelyVisible).ToList();
            Assert.NotEmpty(rows);
            foreach (Button row in rows)
            {
                Grid g = row.GetVisualDescendants().OfType<Grid>().First();
                TextBlock mw = g.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 3);
                double right = Right(mw);
                Assert.True(Math.Abs(right - headerRight) <= 24, $"{width}x{height}: a row's MW ends at {right}, the header's at {headerRight}");
            }
        }
        finally { Close(w); }
    }

    // The WPF RowLinkBtn template (src/GridSim.Wpf/Theme.xaml) draws a row's Border with Padding="10,7" and ignores the
    // Button's own Padding, so every WPF row link is padded 10,7 whatever its XAML says. The ported tabs had copied the
    // ignored values (2,1 / 3,1 / 3,2 / 10,6), which Avalonia honours, so their rows were tighter than WPF's (T6-T15
    // comparison, 2026-10-24). Every row link in the ported room tabs must be padded as the WPF rows are drawn.
    [Fact]
    public void Row_links_in_the_ported_room_tabs_are_padded_as_the_WPF_rows_are_drawn()
    {
        string wpfTheme = File.ReadAllText(Path.Combine(RepoRoot(), "src", "GridSim.Wpf", "Theme.xaml"));
        int style = wpfTheme.IndexOf("x:Key=\"RowLinkBtn\"", StringComparison.Ordinal);
        Assert.True(style >= 0, "the WPF RowLinkBtn style is gone");
        int border = wpfTheme.IndexOf("<Border x:Name=\"bd\"", style, StringComparison.Ordinal);
        Assert.True(border > style && wpfTheme.IndexOf("Padding=\"10,7\"", border, StringComparison.Ordinal) is int at && at > border
            && at < wpfTheme.IndexOf('>', border), "the WPF RowLinkBtn template no longer pads its row 10,7");

        var bad = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "src", "GridSim.App", "Tabs"), "*.axaml"))
        {
            string text = File.ReadAllText(file);
            for (int i = text.IndexOf("Classes=\"RowLinkBtn\"", StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf("Classes=\"RowLinkBtn\"", i + 1, StringComparison.Ordinal))
            {
                int end = text.IndexOf('>', i);
                string tag = text[i..end];
                int pad = tag.IndexOf("Padding=\"", StringComparison.Ordinal);
                if (pad < 0) continue;   // the style's 10,7
                string value = tag[(pad + 9)..tag.IndexOf('"', pad + 9)];
                if (value != "10,7") bad.Add($"{Path.GetFileName(file)}: Padding=\"{value}\"");
            }
        }
        Assert.Empty(bad);
    }

    [AvaloniaTheory]
    [InlineData(1580, 940)]
    public void MECHANICAL_per_machine_rows_are_padded_10_7_as_in_WPF(double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, "MECHANICAL");
        try
        {
            ItemsControl list = w.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "MachinesList");
            var rows = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("RowLinkBtn") && b.IsEffectivelyVisible).ToList();
            Assert.NotEmpty(rows);
            foreach (Button row in rows)
            {
                Assert.Equal(new Thickness(10, 7), row.Padding);
                Grid g = row.GetVisualDescendants().OfType<Grid>().First();
                Assert.True(row.Bounds.Height >= g.Bounds.Height + 14 - 0.5, $"row {row.Bounds.Height} high for content {g.Bounds.Height}");
            }
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData(1580, 940, true)]
    [InlineData(1440, 860, true)]
    [InlineData(1920, 1200, false)]   // a tall window: the body fills it as before, nothing to scroll
    public void NESO_RP1_cards_show_whole_and_the_body_scrolls_only_on_a_short_window(double width, double height, bool scrolls)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, "NESO RP1");
        try
        {
            var scroll = w.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "PageBodyScroll");
            var body = w.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PageBody");
            if (scrolls)
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 0.5, $"the body fits at {width}x{height}: nothing to scroll");
            else
            {
                Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5, $"the body scrolls at {width}x{height}");
                Assert.True(body.Bounds.Height >= scroll.Viewport.Height - 0.5, $"the body ({body.Bounds.Height}) does not fill the window ({scroll.Viewport.Height})");
            }

            // LIVE CREDIBLE LOSS shows every row: its content has its natural height inside the card.
            var loss = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CredibleLossCard");
            var content = (Control)loss.Child!;
            double laidOut = content.Bounds.Height;
            content.Measure(new Size(content.Bounds.Width, double.PositiveInfinity));
            double natural = content.DesiredSize.Height;
            loss.InvalidateMeasure();
            w.UpdateLayout();
            Assert.True(laidOut >= natural - 0.5, $"{width}x{height}: credible loss content {laidOut} high of {natural}");

            // The PURCHASED TIME dial has room to draw under its caption.
            var gauge = w.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PurchasedGauge");
            Assert.True(gauge.Bounds.Height >= 100, $"{width}x{height}: the purchased-time dial is {gauge.Bounds.Height} high");

            // The design-basis line stays inside the card (it wraps rather than running past the edge).
            var card = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PurchasedCard");
            var design = w.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PurchasedDesignText");
            double designRight = design.TranslatePoint(new Point(design.Bounds.Width, 0), card)!.Value.X;
            Assert.True(designRight <= card.Bounds.Width - card.Padding.Right + 0.5,
                $"{width}x{height}: the design-basis text ends at {designRight}, past the card's inner edge {card.Bounds.Width - card.Padding.Right}");
        }
        finally { Close(w); }
    }

    // Every list on the page whose vertical scroll bar shows: no text of it that is in view runs under the bar.
    // Returns how many bars were checked.
    private static int AssertNoTextUnderScrollBars(Control page, string where)
    {
        int bars = 0;
        foreach (ScrollViewer list in page.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible))
        {
            ScrollBar? bar = list.GetVisualDescendants().OfType<ScrollBar>()
                .FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Vertical && b.TemplatedParent == list && b.IsEffectivelyVisible && b.Bounds.Width > 0);
            if (bar is null) continue;
            bars++;
            double barLeft = bar.TranslatePoint(default, list)!.Value.X;
            foreach (TextBlock t in list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
            {
                if (t.GetVisualAncestors().OfType<ScrollViewer>().First() != list) continue;   // a nested list's own text
                Point? at = t.TranslatePoint(default, list);
                if (at is not { } a || a.Y + t.Bounds.Height < 0 || a.Y > list.Viewport.Height) continue;   // not in view
                double right = a.X + t.Bounds.Width;
                Assert.True(right <= barLeft + 0.5, $"{where}: '{t.Text}' ends at {right}, under the scroll bar from {barLeft}");
            }
        }
        return bars;
    }

    [AvaloniaTheory]
    [InlineData(1580, 940)]
    [InlineData(1440, 860)]
    public void MECHANICAL_lists_keep_text_clear_of_their_scroll_bars_and_the_PQ_diagram_stays_in_its_card(double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, "MECHANICAL");
        try
        {
            Control page = (Control)((TabItem)w.TabStrip.SelectedItem!).Content!;
            Assert.True(AssertNoTextUnderScrollBars(page, $"MECHANICAL {width}x{height}") >= 2,
                $"fewer than two lists show a scroll bar at {width}x{height}, so this tests little");

            var card = w.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ProtectionEnvelopeCard");
            var pq = w.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "SelectedPq");
            Assert.True(card.ClipToBounds, "the protection envelope card does not clip");
            double bottom = pq.TranslatePoint(new Point(0, pq.Bounds.Height), card)!.Value.Y;
            Assert.True(bottom <= card.Bounds.Height - card.Padding.Bottom + 0.5,
                $"{width}x{height}: the P-Q diagram ends at {bottom}, past the card's inner edge {card.Bounds.Height - card.Padding.Bottom}");
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData(1580, 940)]
    [InlineData(1440, 860)]
    public void OPERATOR_largest_loss_note_wraps_rather_than_being_cut(double width, double height)
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, width, height, "OPERATOR");
        try
        {
            var note = w.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DesignBasisLossText");
            Assert.False(string.IsNullOrEmpty(note.Text), "the uk case gives a design-basis note");
            double laidW = note.Bounds.Width, laidH = note.Bounds.Height;
            note.Measure(Size.Infinity);
            Size oneLine = note.DesiredSize;
            note.InvalidateMeasure();
            w.UpdateLayout();
            if (oneLine.Width > laidW + 0.5)   // too wide for its column on one line: it must take more than one line
                Assert.True(laidH > oneLine.Height + 1, $"{width}x{height}: the note needs {oneLine.Width} px on one line, has {laidW}, and is {laidH} high (cut, not wrapped)");
        }
        finally { Close(w); }
    }

    // ======================================== ASSETS A3 follow-ups (2026-10-24) ========================================

    [AvaloniaFact]
    public void A_substation_voltage_group_opens_the_SUBSTATIONS_page_filtered_to_its_level_and_the_class_node_unfiltered()
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, 1580, 940, MainWindow.AssetsTabHeader);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetTreeNode subs = nav.Tree.PreOrder().First(n => n.Page.Kind == AssetPageKind.SubstationFleet && n.GroupKv is null);
            AssetTreeNode kv132 = subs.Children.Single(n => n.GroupKv == 132);
            string Active() => ((IEnumerable<KvFilterChipModel>)nav.Current!.GetType().GetProperty("Filters")!.GetValue(nav.Current)!)
                .Single(c => c.Active).Label;

            Assert.True(nav.Select(kv132));
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("132 kV", Active());
            Assert.True(nav.Select(subs));
            Assert.Equal("ALL", Active());
            Assert.True(nav.Select(subs.Children.Single(n => n.GroupKv == 400)));
            Assert.Equal("400 kV", Active());
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void A_node_selected_from_elsewhere_is_scrolled_into_view_in_the_tree()
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, 1440, 860, MainWindow.AssetsTabHeader);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            TreeView tree = w.GetVisualDescendants().OfType<TreeView>().Single(t => t.Name == "Tree");
            ScrollViewer sv = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
            // Every node open, so the tree is far taller than the window; then the canonical node of the last bus.
            foreach (AssetTreeNode n in nav.Tree.PreOrder()) n.IsExpanded = true;
            for (int i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
            int lastBus = nav.Tree.PreOrder().Where(n => n.Page.Kind == AssetPageKind.Substation && n.Id.StartsWith("bus:", StringComparison.Ordinal))
                .Select(n => int.Parse(n.Id["bus:".Length..], System.Globalization.CultureInfo.InvariantCulture)).Max();
            Assert.True(nav.GoToBus(lastBus));
            AssetTreeNode far = nav.Selected!;
            for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
            Assert.True(sv.Offset.Y > 0, "the tree did not scroll at all");
            TreeViewItem item = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(t => ReferenceEquals(t.DataContext, far));
            double top = item.TranslatePoint(default, sv)!.Value.Y;
            Assert.True(top >= -0.5 && top + 20 <= sv.Viewport.Height + 0.5, $"the selected row is at {top}, viewport {sv.Viewport.Height}");
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void A_large_tree_group_makes_only_the_rows_in_view()
    {
        using var temp = new TempFolder();
        var big = new ModelSource("GB synthetic  distribution depth", Path.Combine(RepoRoot(), "data", "gb-synthetic"));
        var w = new MainWindow(Options(temp, big)) { Width = 1580, Height = 940 };
        w.Show();
        try
        {
            w.ViewModel.SelectedSource = w.ViewModel.Sources[0];
            PumpUntil(() => w.ViewModel.Run.ActiveSolverText.StartsWith("auto * ", StringComparison.Ordinal), "the case to load");
            w.SelectTab(MainWindow.AssetsTabHeader);
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            PumpUntil(() => nav.Tree.PreOrder().Any(n => n.GroupKv is not null), "the explorer tree's voltage groups");
            AssetTreeNode biggest = nav.Tree.PreOrder().Where(n => n.GroupKv is not null).OrderByDescending(n => n.Children.Count).First();
            Assert.True(biggest.Children.Count > 300, $"the biggest group has only {biggest.Children.Count} rows");
            biggest.Parent!.IsExpanded = true;
            biggest.IsExpanded = true;
            for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
            int made = w.GetVisualDescendants().OfType<TreeViewItem>().Count(t => t.DataContext is AssetTreeNode n && ReferenceEquals(n.Parent, biggest));
            Assert.True(made > 0 && made < 100, $"{made} of {biggest.Children.Count} rows of the open group were made");
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void A_circuit_band_group_opens_CABLES_and_LINES_filtered_to_its_band_and_the_class_node_unfiltered()
    {
        using var temp = new TempFolder();
        MainWindow w = Open(temp, 1580, 940, MainWindow.AssetsTabHeader);
        try
        {
            AssetNavigatorModel nav = w.ViewModel.Tabs.Assets;
            AssetTreeNode circuits = nav.Tree.PreOrder().First(n => n.Page.Kind == AssetPageKind.CircuitFleet && n.GroupBand is null);
            AssetTreeNode band = circuits.Children.First(n => n.GroupBand is not null);
            string On() => ((IEnumerable<BandChipModel>)nav.Current!.GetType().GetProperty("Bands")!.GetValue(nav.Current)!)
                .Single(c => c.IsOn).Label;

            Assert.True(nav.Select(band));
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(band.Label, On());
            Assert.True(nav.Select(circuits));
            Assert.Equal("ALL", On());
        }
        finally { Close(w); }
    }
}
