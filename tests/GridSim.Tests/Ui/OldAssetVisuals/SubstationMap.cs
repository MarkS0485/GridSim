// TEST ORACLE (UI programme ASSETS A1): src/GridSim.Wpf/Visuals/SubstationMap.cs exactly as it was at commit 9ff7ef41 (2.0.38),
// TEST ORACLE before its layout and colours moved to src/GridSim.Ui/Visuals. Only the using and namespace lines differ (the
// TEST ORACLE WPF types are the recording stand-ins in UiVisualsWpfShim.cs); UiAssetVisualsParityTests checks a SHA-256 of
// TEST ORACLE the rest against the original file.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;

namespace GridSim.Tests.Ui.OldAssetVisuals;

/// <summary>Snapshot of one connection for the substation single-line diagram.</summary>
public sealed record MapConnection(
    string Name, string OtherEnd, string Kind, double FlowMw, bool Exporting, double LoadingFrac);

/// <summary>Snapshot of one generator for the substation single-line diagram.</summary>
public sealed record MapGenerator(string Name, string Fuel, Color FuelColor, double OutputMw);

/// <summary>All the data the <see cref="SubstationMap"/> needs for one frame, built by the VM.</summary>
public sealed class SubstationMapData
{
    public double VoltagePu { get; init; }
    public double AngleDeg { get; init; }
    public double BaseKv { get; init; }
    public double DemandMw { get; init; }
    public double ReactiveMvar { get; init; }
    public double GenerationMw { get; init; }
    public string BusType { get; init; } = "";
    public double NetInjectionMw { get; init; }
    public IReadOnlyList<MapConnection> Connections { get; init; } = [];
    public IReadOnlyList<MapGenerator> Generators { get; init; } = [];
}

/// <summary>
/// Single-line busbar diagram for a substation: a horizontal bus bar at centre with the
/// voltage/angle state, connections radiating left and right to other buses (lines drawn
/// as simple conductors, transformers with a winding symbol), generators shown as circled G
/// below the bar, and a load arrow for the demand. Renders in one pass via OnRender.
/// </summary>
public sealed class SubstationMap : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(SubstationMapData), typeof(SubstationMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SubstationMapData? Data { get => (SubstationMapData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    // --- colours ---
    private static readonly Color BusBarColor = Palette.FromHex("#22D3EE");
    private static readonly Color DimSlate = Palette.FromHex("#64748B");
    private static readonly Color LightSlate = Palette.FromHex("#94A3B8");
    private static readonly Color DarkBg = Palette.FromHex("#0F172A");
    private static readonly Color TxSymbol = Palette.FromHex("#A855F7");

    private static readonly Pen BusPen = Fr(new Pen(Br(BusBarColor), 6));
    private static readonly Pen LinePen = Fr(new Pen(Br(LightSlate), 3));
    private static readonly Pen TxPen = Fr(new Pen(Br(TxSymbol), 3));
    private static readonly Pen ArrowPen = Fr(new Pen(Br(Palette.FromHex("#F59E0B")), 3.5));
    private static readonly Pen GenCirclePen = Fr(new Pen(Br(Palette.FromHex("#22C55E")), 3));
    private static readonly Pen LoadPen = Fr(new Pen(Br(Palette.FromHex("#EF4444")), 3.5));
    private static readonly Pen DimPen = Fr(new Pen(Br(DimSlate), 1));
    private static readonly Pen InfoBoxPen = Fr(new Pen(Br(Palette.FromHex("#1E3A5F")), 1.5));

    private static readonly Brush TextWhite = Br(Palette.FromHex("#E6EDF3"));
    private static readonly Brush TextSlate = Br(LightSlate);
    private static readonly Brush TextDim = Br(DimSlate);
    private static readonly Brush CyanBrush = Br(BusBarColor);
    private static readonly Brush AmberBrush = Br(Palette.FromHex("#F59E0B"));
    private static readonly Brush RedBrush = Br(Palette.FromHex("#EF4444"));
    private static readonly Brush GreenBrush = Br(Palette.FromHex("#22C55E"));
    private static readonly Brush DarkBgBrush = Br(DarkBg);
    private static readonly Brush TxBrush = Br(TxSymbol);

    private static readonly Typeface Mono =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface MonoLight =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>
    /// The diagram's own amber "watch" margin, 0.02 pu inside each edge of the shared 0.94 - 1.06 pu operational band
    /// (so 0.96 and 1.04 pu). It is a display cue for this diagram only: no alarm, lamp or other view uses it, and it
    /// matches where <see cref="Palette.Voltage"/> leaves its blue and green zones (0.96 and 1.03 to 1.06 pu) only
    /// approximately. Named here so it is not mistaken for a shared limit.
    /// </summary>
    private const double VoltageWatchLowPu = 0.96, VoltageWatchHighPu = 1.04;

    private static Brush Br(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static T Fr<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double aw = ActualWidth, ah = ActualHeight;
        if (aw < 300 || ah < 200) return;

        SubstationMapData? d = Data;
        if (d is null) return;

        double pps = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Layout: bus bar at ~40% height, generous margins
        double barY = ah * 0.40;
        double barLeft = 80, barRight = aw - 80;
        double barMid = (barLeft + barRight) / 2;

        // --- BUS BAR ---
        dc.DrawLine(BusPen, new Point(barLeft, barY), new Point(barRight, barY));
        dc.DrawLine(BusPen, new Point(barLeft, barY - 12), new Point(barLeft, barY + 12));
        dc.DrawLine(BusPen, new Point(barRight, barY - 12), new Point(barRight, barY + 12));

        // --- VOLTAGE BADGE below the bar, far left ---
        // Red outside the shared operational band (ScadaThresholds, the lamp's 0.94 / 1.06 pu); amber in the named
        // watch margin inside it; cyan otherwise.
        double v = d.VoltagePu;
        Brush vBrush = v < GridSim.Core.Presentation.ScadaThresholds.VoltageBandLowPu || v > GridSim.Core.Presentation.ScadaThresholds.VoltageBandHighPu ? RedBrush
                      : v < VoltageWatchLowPu || v > VoltageWatchHighPu ? AmberBrush : CyanBrush;
        double vbX = barLeft + 120;
        dc.DrawRoundedRectangle(DarkBgBrush, InfoBoxPen, new Rect(vbX - 120, barY - 50, 240, 42), 8, 8);
        Txt(dc, $"{d.BusType}   {d.BaseKv:F0} kV", MonoLight, 10, TextDim, vbX, barY - 46, pps);
        string vText = $"{d.VoltagePu:F3} pu   {d.VoltagePu * d.BaseKv:F1} kV   {d.AngleDeg:+0.0;-0.0;0.0} deg";
        Txt(dc, vText, Mono, 14, vBrush, vbX, barY - 30, pps);

        // --- CONNECTIONS radiating up from the bus bar ---
        int nConn = d.Connections.Count;
        if (nConn > 0)
        {
            double spacing = Math.Min(160, (barRight - barLeft - 60) / Math.Max(nConn, 1));
            double startX = barMid - (nConn - 1) * spacing / 2;
            double topY = 50;

            for (int i = 0; i < nConn; i++)
            {
                MapConnection c = d.Connections[i];
                double cx = startX + i * spacing;

                // Vertical stub from bus bar up
                dc.DrawLine(LinePen, new Point(cx, barY), new Point(cx, topY + 55));

                if (c.Kind == "TRANSFORMER")
                {
                    // Transformer winding symbol: two overlapping circles
                    dc.DrawEllipse(null, TxPen, new Point(cx, topY + 48), 12, 12);
                    dc.DrawEllipse(null, TxPen, new Point(cx, topY + 30), 12, 12);
                    dc.DrawLine(LinePen, new Point(cx, topY + 18), new Point(cx, topY));
                }

                // Flow arrow on the stub
                if (Math.Abs(c.FlowMw) > 0.5)
                {
                    double arrowY = barY - 28;
                    bool up = c.Exporting;
                    double tipY = up ? arrowY - 12 : arrowY + 12;
                    dc.DrawLine(ArrowPen, new Point(cx, arrowY + 6), new Point(cx, arrowY - 6));
                    dc.DrawLine(ArrowPen, new Point(cx, tipY), new Point(cx - 6, up ? tipY + 7 : tipY - 7));
                    dc.DrawLine(ArrowPen, new Point(cx, tipY), new Point(cx + 6, up ? tipY + 7 : tipY - 7));
                }

                Brush loadBr = c.LoadingFrac > 0.9 ? RedBrush : c.LoadingFrac > 0.7 ? AmberBrush : TextSlate;

                // Other end name at the top
                Txt(dc, c.OtherEnd, MonoLight, 12, TextWhite, cx, topY - 22, pps);
                Txt(dc, $"{Math.Abs(c.FlowMw):F0} MW", Mono, 11, loadBr, cx, topY - 8, pps);

                // Loading % beside the stub
                if (c.LoadingFrac > 0)
                    Txt(dc, $"{c.LoadingFrac * 100:F0}%", Mono, 11, loadBr, cx + 22, barY - 18, pps, TextAlignment.Left);
            }
        }

        // --- GENERATORS below the bus bar ---
        int nGen = d.Generators.Count;
        double genRowY = barY + 80;
        if (nGen > 0)
        {
            double gSpacing = Math.Min(160, (barRight - barLeft - 60) / Math.Max(nGen, 1));
            double gStartX = barMid - (nGen - 1) * gSpacing / 2;

            for (int i = 0; i < nGen; i++)
            {
                MapGenerator g = d.Generators[i];
                double gx = gStartX + i * gSpacing;

                // Vertical stub down from bus
                dc.DrawLine(LinePen, new Point(gx, barY), new Point(gx, genRowY - 22));

                // Generator circle with G
                Brush fuelBr = Fr(new SolidColorBrush(g.FuelColor));
                Pen fuelPen = Fr(new Pen(fuelBr, 3));
                dc.DrawEllipse(DarkBgBrush, fuelPen, new Point(gx, genRowY), 20, 20);
                Txt(dc, "G", Mono, 16, fuelBr, gx, genRowY - 10, pps);

                // Name + output below
                Txt(dc, g.Name, MonoLight, 11, TextWhite, gx, genRowY + 28, pps);
                Txt(dc, $"{g.OutputMw:F0} MW", Mono, 12, fuelBr, gx, genRowY + 44, pps);
                Txt(dc, g.Fuel, MonoLight, 10, TextDim, gx, genRowY + 60, pps);
            }
        }

        // --- DEMAND load symbol (right side, below bar) ---
        if (Math.Abs(d.DemandMw) > 0.1)
        {
            double ldX = barRight - 50;
            double ldY = barY + 24;
            dc.DrawLine(LoadPen, new Point(ldX, barY), new Point(ldX, ldY));

            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(ldX - 14, ldY), true, true);
                g.LineTo(new Point(ldX + 14, ldY), true, false);
                g.LineTo(new Point(ldX, ldY + 22), true, false);
            }
            tri.Freeze();
            dc.DrawGeometry(Fr(new SolidColorBrush(Palette.FromHex("#EF4444"))), null, tri);

            Txt(dc, $"{d.DemandMw:F0} MW", Mono, 14, RedBrush, ldX, ldY + 28, pps);
            Txt(dc, $"{d.ReactiveMvar:F0} MVAr", MonoLight, 11, TextDim, ldX, ldY + 46, pps);
            Txt(dc, "DEMAND", MonoLight, 10, TextDim, ldX, ldY + 62, pps);
        }

        // --- NET INJECTION label (left side) ---
        dc.DrawRoundedRectangle(DarkBgBrush, InfoBoxPen,
            new Rect(barLeft - 15, barY + 20, 140, 50), 8, 8);
        Txt(dc, "NET INJECTION", MonoLight, 10, TextDim, barLeft + 55, barY + 24, pps);
        Brush injBr = d.NetInjectionMw > 5 ? GreenBrush : d.NetInjectionMw < -5 ? RedBrush : TextSlate;
        Txt(dc, $"{d.NetInjectionMw:+#,0;-#,0;0} MW", Mono, 16, injBr, barLeft + 55, barY + 40, pps);
    }

    private static void Txt(DrawingContext dc, string text, Typeface face, double size, Brush brush,
        double x, double y, double pps, TextAlignment align = TextAlignment.Center)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            face, size, brush, Math.Max(pps, 1))
        { TextAlignment = align, MaxTextWidth = 400 };
        double dx = align == TextAlignment.Center ? x - 200 : x;
        dc.DrawText(ft, new Point(dx, y));
    }
}
