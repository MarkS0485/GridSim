using GridSim.Core.Presentation;
using GridSim.Ui.Presentation;
using GridSim.Ui.Schematic;

namespace GridSim.Ui.Visuals;

/// <summary>Snapshot of one connection for the substation single-line diagram (the WPF <c>MapConnection</c>, framework-free).</summary>
/// <param name="Name">The branch name.</param>
/// <param name="OtherEnd">The name of the bus at the far end (printed above the stub).</param>
/// <param name="Kind">"TRANSFORMER" draws the winding symbol; anything else is a plain conductor.</param>
/// <param name="FlowMw">The real power flow (its magnitude is printed; above 0.5 MW an arrow is drawn).</param>
/// <param name="Exporting">True when the power leaves this substation along the connection (the arrow points up).</param>
/// <param name="LoadingFrac">Loading as a fraction of the rating (0 = not printed).</param>
public sealed record SubstationMapConnection(
    string Name, string OtherEnd, string Kind, double FlowMw, bool Exporting, double LoadingFrac);

/// <summary>Snapshot of one generator for the substation single-line diagram (the WPF <c>MapGenerator</c>, framework-free).</summary>
/// <param name="Name">The unit name.</param>
/// <param name="Fuel">The fuel label.</param>
/// <param name="FuelColour">The fuel colour (the circle, the "G" and the output text).</param>
/// <param name="OutputMw">The unit's output.</param>
public sealed record SubstationMapGenerator(string Name, string Fuel, UiColour FuelColour, double OutputMw);

/// <summary>All the data the substation single-line diagram needs for one frame (the WPF <c>SubstationMapData</c>).</summary>
public sealed class SubstationMapInput
{
    /// <summary>The bus voltage magnitude, per unit.</summary>
    public double VoltagePu { get; init; }

    /// <summary>The bus voltage angle, degrees.</summary>
    public double AngleDeg { get; init; }

    /// <summary>The bus base voltage, kV.</summary>
    public double BaseKv { get; init; }

    /// <summary>The demand at the bus, MW.</summary>
    public double DemandMw { get; init; }

    /// <summary>The reactive demand at the bus, MVAr.</summary>
    public double ReactiveMvar { get; init; }

    /// <summary>The generation at the bus, MW (not drawn; carried as the WPF data carried it).</summary>
    public double GenerationMw { get; init; }

    /// <summary>The bus type label (PQ, PV, SLACK ...).</summary>
    public string BusType { get; init; } = "";

    /// <summary>Generation minus demand, MW.</summary>
    public double NetInjectionMw { get; init; }

    /// <summary>The connections, drawn left to right above the bar.</summary>
    public IReadOnlyList<SubstationMapConnection> Connections { get; init; } = [];

    /// <summary>The generators, drawn left to right below the bar.</summary>
    public IReadOnlyList<SubstationMapGenerator> Generators { get; init; } = [];
}

/// <summary>
/// The pure layout and colours of the substation single-line diagram (UI programme ASSETS A1): a horizontal bus bar with
/// the voltage badge, connections rising from the bar to the other buses (transformers with a two-circle winding
/// symbol), generators as circled G below the bar, the demand as a load arrow, and the net injection box.
///
/// <para>This is the arithmetic of the WPF <c>Visuals/SubstationMap.cs</c> <c>OnRender</c>, moved here operation for
/// operation and call for call: <see cref="Compute"/> returns the very drawing calls the old code made, in the same
/// order, with the same numbers and colours, so the WPF control (which now only replays them) draws the same pixels.
/// <c>UiAssetVisualsParityTests</c> pins this against a verbatim copy of the old WPF code, bit for bit. The text is
/// formatted with the current culture, as the old interpolated strings were (a decimal comma shows as a comma).</para>
///
/// <para>No trigonometry here; the diagram is straight lines, circles and rounded boxes.</para>
/// </summary>
public static class SubstationMapGeometry
{
    /// <summary>Below this width (pixels) the diagram draws nothing.</summary>
    public const double MinWidth = 300;

    /// <summary>Below this height (pixels) the diagram draws nothing.</summary>
    public const double MinHeight = 200;

    /// <summary>
    /// The diagram's own amber "watch" margin, 0.02 pu inside each edge of the shared 0.94 - 1.06 pu operational band
    /// (so 0.96 and 1.04 pu). It is a display cue for this diagram only: no alarm, lamp or other view uses it, and it
    /// matches where the voltage colour ramp leaves its blue and green zones (0.96 and 1.03 to 1.06 pu) only
    /// approximately. Named here so it is not mistaken for a shared limit. (Moved from the WPF control unchanged.)
    /// </summary>
    public const double VoltageWatchLowPu = 0.96, VoltageWatchHighPu = 1.04;

    // ---- the colours (the same hex values the WPF control's pens and brushes were built from) ----

    private static readonly UiColour BusBarColour = ColourRamps.FromHex("#22D3EE");
    private static readonly UiColour DimSlate = ColourRamps.FromHex("#64748B");
    private static readonly UiColour LightSlate = ColourRamps.FromHex("#94A3B8");
    private static readonly UiColour DarkBg = ColourRamps.FromHex("#0F172A");
    private static readonly UiColour TxSymbol = ColourRamps.FromHex("#A855F7");

    /// <summary>The bus bar and its end caps.</summary>
    public static readonly VisualPen BusPen = new(BusBarColour, 6);

    /// <summary>The conductor stubs (connections and generators).</summary>
    public static readonly VisualPen LinePen = new(LightSlate, 3);

    /// <summary>The transformer winding circles.</summary>
    public static readonly VisualPen TxPen = new(TxSymbol, 3);

    /// <summary>The flow arrows.</summary>
    public static readonly VisualPen ArrowPen = new(ColourRamps.FromHex("#F59E0B"), 3.5);

    /// <summary>The demand stub.</summary>
    public static readonly VisualPen LoadPen = new(ColourRamps.FromHex("#EF4444"), 3.5);

    /// <summary>The outline of the voltage badge and the net injection box.</summary>
    public static readonly VisualPen InfoBoxPen = new(ColourRamps.FromHex("#1E3A5F"), 1.5);

    /// <summary>Other-end names and generator names.</summary>
    public static readonly VisualFill TextWhite = new(ColourRamps.FromHex("#E6EDF3"));

    /// <summary>Normal loading text and a balanced net injection.</summary>
    public static readonly VisualFill TextSlate = new(LightSlate);

    /// <summary>Captions.</summary>
    public static readonly VisualFill TextDim = new(DimSlate);

    /// <summary>An in-band voltage.</summary>
    public static readonly VisualFill Cyan = new(BusBarColour);

    /// <summary>A watch-margin voltage or a loading above 70 percent.</summary>
    public static readonly VisualFill Amber = new(ColourRamps.FromHex("#F59E0B"));

    /// <summary>An out-of-band voltage, a loading above 90 percent, the demand, a net import.</summary>
    public static readonly VisualFill Red = new(ColourRamps.FromHex("#EF4444"));

    /// <summary>A net export.</summary>
    public static readonly VisualFill Green = new(ColourRamps.FromHex("#22C55E"));

    /// <summary>The dark fill of the boxes and the generator circles.</summary>
    public static readonly VisualFill DarkBgFill = new(DarkBg);

    /// <summary>
    /// The voltage text colour: red outside the shared operational band (ScadaThresholds, the lamp's 0.94 / 1.06 pu);
    /// amber in the named watch margin inside it; cyan otherwise. NaN is cyan (every comparison is false), as before.
    /// </summary>
    public static VisualFill VoltageFill(double v) =>
        v < ScadaThresholds.VoltageBandLowPu || v > ScadaThresholds.VoltageBandHighPu ? Red
        : v < VoltageWatchLowPu || v > VoltageWatchHighPu ? Amber : Cyan;

    /// <summary>A connection's loading colour: red above 0.9, amber above 0.7, slate otherwise (NaN is slate).</summary>
    public static VisualFill LoadingFill(double loadingFrac) =>
        loadingFrac > 0.9 ? Red : loadingFrac > 0.7 ? Amber : TextSlate;

    /// <summary>The net injection colour: green above +5 MW, red below -5 MW, slate in between (NaN is slate).</summary>
    public static VisualFill InjectionFill(double netMw) => netMw > 5 ? Green : netMw < -5 ? Red : TextSlate;

    /// <summary>
    /// The drawing calls of the diagram for a control of <paramref name="aw"/> by <paramref name="ah"/> pixels, or null
    /// when it is smaller than <see cref="MinWidth"/> by <see cref="MinHeight"/> or there is no data (the old control
    /// drew nothing then). A NaN size passes the size check, as it did in the old control.
    /// </summary>
    /// <param name="aw">Control width.</param>
    /// <param name="ah">Control height.</param>
    /// <param name="d">The frame's data, or null.</param>
    public static VisualDisplayList? Compute(double aw, double ah, SubstationMapInput? d)
    {
        if (aw < MinWidth || ah < MinHeight) return null;
        if (d is null) return null;
        var dc = new VisualDisplayList();

        // Layout: bus bar at ~40% height, generous margins
        double barY = ah * 0.40;
        double barLeft = 80, barRight = aw - 80;
        double barMid = (barLeft + barRight) / 2;

        // --- BUS BAR ---
        dc.Line(BusPen, new ScreenPoint(barLeft, barY), new ScreenPoint(barRight, barY));
        dc.Line(BusPen, new ScreenPoint(barLeft, barY - 12), new ScreenPoint(barLeft, barY + 12));
        dc.Line(BusPen, new ScreenPoint(barRight, barY - 12), new ScreenPoint(barRight, barY + 12));

        // --- VOLTAGE BADGE below the bar, far left ---
        VisualFill vBrush = VoltageFill(d.VoltagePu);
        double vbX = barLeft + 120;
        dc.Rect(DarkBgFill, InfoBoxPen, vbX - 120, barY - 50, 240, 42, 8, 8);
        Txt(dc, $"{d.BusType}   {d.BaseKv:F0} kV", VisualTextWeight.SemiBold, 10, TextDim, vbX, barY - 46);
        string vText = $"{d.VoltagePu:F3} pu   {d.VoltagePu * d.BaseKv:F1} kV   {d.AngleDeg:+0.0;-0.0;0.0} deg";
        Txt(dc, vText, VisualTextWeight.Bold, 14, vBrush, vbX, barY - 30);

        // --- CONNECTIONS radiating up from the bus bar ---
        int nConn = d.Connections.Count;
        if (nConn > 0)
        {
            double spacing = Math.Min(160, (barRight - barLeft - 60) / Math.Max(nConn, 1));
            double startX = barMid - (nConn - 1) * spacing / 2;
            double topY = 50;

            for (int i = 0; i < nConn; i++)
            {
                SubstationMapConnection c = d.Connections[i];
                double cx = startX + i * spacing;

                // Vertical stub from bus bar up
                dc.Line(LinePen, new ScreenPoint(cx, barY), new ScreenPoint(cx, topY + 55));

                if (c.Kind == "TRANSFORMER")
                {
                    // Transformer winding symbol: two overlapping circles
                    dc.Ellipse(null, TxPen, new ScreenPoint(cx, topY + 48), 12, 12);
                    dc.Ellipse(null, TxPen, new ScreenPoint(cx, topY + 30), 12, 12);
                    dc.Line(LinePen, new ScreenPoint(cx, topY + 18), new ScreenPoint(cx, topY));
                }

                // Flow arrow on the stub
                if (Math.Abs(c.FlowMw) > 0.5)
                {
                    double arrowY = barY - 28;
                    bool up = c.Exporting;
                    double tipY = up ? arrowY - 12 : arrowY + 12;
                    dc.Line(ArrowPen, new ScreenPoint(cx, arrowY + 6), new ScreenPoint(cx, arrowY - 6));
                    dc.Line(ArrowPen, new ScreenPoint(cx, tipY), new ScreenPoint(cx - 6, up ? tipY + 7 : tipY - 7));
                    dc.Line(ArrowPen, new ScreenPoint(cx, tipY), new ScreenPoint(cx + 6, up ? tipY + 7 : tipY - 7));
                }

                VisualFill loadBr = LoadingFill(c.LoadingFrac);

                // Other end name at the top
                Txt(dc, c.OtherEnd, VisualTextWeight.SemiBold, 12, TextWhite, cx, topY - 22);
                Txt(dc, $"{Math.Abs(c.FlowMw):F0} MW", VisualTextWeight.Bold, 11, loadBr, cx, topY - 8);

                // Loading % beside the stub
                if (c.LoadingFrac > 0)
                    Txt(dc, $"{c.LoadingFrac * 100:F0}%", VisualTextWeight.Bold, 11, loadBr, cx + 22, barY - 18, VisualTextAlign.Left);
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
                SubstationMapGenerator g = d.Generators[i];
                double gx = gStartX + i * gSpacing;

                // Vertical stub down from bus
                dc.Line(LinePen, new ScreenPoint(gx, barY), new ScreenPoint(gx, genRowY - 22));

                // Generator circle with G (the fuel colour for the ring, the letter and the output)
                var fuelBr = new VisualFill(g.FuelColour);
                var fuelPen = new VisualPen(g.FuelColour, 3);
                dc.Ellipse(DarkBgFill, fuelPen, new ScreenPoint(gx, genRowY), 20, 20);
                Txt(dc, "G", VisualTextWeight.Bold, 16, fuelBr, gx, genRowY - 10);

                // Name + output below
                Txt(dc, g.Name, VisualTextWeight.SemiBold, 11, TextWhite, gx, genRowY + 28);
                Txt(dc, $"{g.OutputMw:F0} MW", VisualTextWeight.Bold, 12, fuelBr, gx, genRowY + 44);
                Txt(dc, g.Fuel, VisualTextWeight.SemiBold, 10, TextDim, gx, genRowY + 60);
            }
        }

        // --- DEMAND load symbol (right side, below bar) ---
        if (Math.Abs(d.DemandMw) > 0.1)
        {
            double ldX = barRight - 50;
            double ldY = barY + 24;
            dc.Line(LoadPen, new ScreenPoint(ldX, barY), new ScreenPoint(ldX, ldY));

            // The load arrow: a filled triangle pointing down, no outline.
            dc.Polygon(Red, new ScreenPoint(ldX - 14, ldY), new ScreenPoint(ldX + 14, ldY), new ScreenPoint(ldX, ldY + 22));

            Txt(dc, $"{d.DemandMw:F0} MW", VisualTextWeight.Bold, 14, Red, ldX, ldY + 28);
            Txt(dc, $"{d.ReactiveMvar:F0} MVAr", VisualTextWeight.SemiBold, 11, TextDim, ldX, ldY + 46);
            Txt(dc, "DEMAND", VisualTextWeight.SemiBold, 10, TextDim, ldX, ldY + 62);
        }

        // --- NET INJECTION label (left side) ---
        dc.Rect(DarkBgFill, InfoBoxPen, barLeft - 15, barY + 20, 140, 50, 8, 8);
        Txt(dc, "NET INJECTION", VisualTextWeight.SemiBold, 10, TextDim, barLeft + 55, barY + 24);
        VisualFill injBr = InjectionFill(d.NetInjectionMw);
        Txt(dc, $"{d.NetInjectionMw:+#,0;-#,0;0} MW", VisualTextWeight.Bold, 16, injBr, barLeft + 55, barY + 40);
        return dc;
    }

    /// <summary>A text call, as the old <c>Txt</c> helper made it (the device scale; centred unless told otherwise).</summary>
    private static void Txt(VisualDisplayList dc, string text, VisualTextWeight weight, double size, VisualFill fill,
        double x, double y, VisualTextAlign align = VisualTextAlign.Centre) =>
        dc.Text(text, weight, size, fill, new ScreenPoint(x, y), align);
}
