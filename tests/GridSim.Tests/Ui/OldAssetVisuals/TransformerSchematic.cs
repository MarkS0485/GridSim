// TEST ORACLE (UI programme ASSETS A1): src/GridSim.Wpf/Visuals/TransformerSchematic.cs exactly as it was at commit 9ff7ef41 (2.0.38),
// TEST ORACLE before its layout and colours moved to src/GridSim.Ui/Visuals. Only the using and namespace lines differ (the
// TEST ORACLE WPF types are the recording stand-ins in UiVisualsWpfShim.cs); UiAssetVisualsParityTests checks a SHA-256 of
// TEST ORACLE the rest against the original file.
using System.Globalization;
using GridSim.Tests.Ui.WpfShim;

namespace GridSim.Tests.Ui.OldAssetVisuals;

/// <summary>
/// Front-elevation schematic of a three-phase power transformer: tank outline, 3-limb
/// core with HV/LV windings, porcelain bushings at top (HV) and bottom (LV), cooling
/// radiator banks, OLTC box with tap pointer, and annotated data overlays for live
/// electrical and thermal state. Renders in one pass via <see cref="OnRender"/>; no
/// template, same pattern as <see cref="Gauge"/> and <see cref="Phasor"/>.
/// </summary>
public sealed class TransformerSchematic : FrameworkElement
{
    // --- dependency properties for live data binding ---
    public static readonly DependencyProperty LoadingPctProperty = Dp(nameof(LoadingPct), 0.0);
    public static readonly DependencyProperty HvKvNomProperty = Dp(nameof(HvKvNom), 132.0);
    public static readonly DependencyProperty LvKvNomProperty = Dp(nameof(LvKvNom), 33.0);
    public static readonly DependencyProperty HvKvLiveProperty = Dp(nameof(HvKvLive), 0.0);
    public static readonly DependencyProperty LvKvLiveProperty = Dp(nameof(LvKvLive), 0.0);
    public static readonly DependencyProperty FlowMwProperty = Dp(nameof(FlowMw), 0.0);
    public static readonly DependencyProperty RatingMvaProperty = Dp(nameof(RatingMva), 0.0);
    public static readonly DependencyProperty TapRatioProperty = Dp(nameof(TapRatio), 1.0);
    public static readonly DependencyProperty TopOilCProperty = Dp(nameof(TopOilC), 58.0);
    public static readonly DependencyProperty HotSpotCProperty = Dp(nameof(HotSpotC), 71.0);
    public static readonly DependencyProperty HasOltcProperty = DependencyProperty.Register(
        nameof(HasOltc), typeof(bool), typeof(TransformerSchematic),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TapMinProperty = Dp(nameof(TapMin), 0.9);
    public static readonly DependencyProperty TapMaxProperty = Dp(nameof(TapMax), 1.1);

    private static DependencyProperty Dp(string name, double dflt) => DependencyProperty.Register(
        name, typeof(double), typeof(TransformerSchematic),
        new FrameworkPropertyMetadata(dflt, FrameworkPropertyMetadataOptions.AffectsRender));

    public double LoadingPct { get => (double)GetValue(LoadingPctProperty); set => SetValue(LoadingPctProperty, value); }
    public double HvKvNom { get => (double)GetValue(HvKvNomProperty); set => SetValue(HvKvNomProperty, value); }
    public double LvKvNom { get => (double)GetValue(LvKvNomProperty); set => SetValue(LvKvNomProperty, value); }
    public double HvKvLive { get => (double)GetValue(HvKvLiveProperty); set => SetValue(HvKvLiveProperty, value); }
    public double LvKvLive { get => (double)GetValue(LvKvLiveProperty); set => SetValue(LvKvLiveProperty, value); }
    public double FlowMw { get => (double)GetValue(FlowMwProperty); set => SetValue(FlowMwProperty, value); }
    public double RatingMva { get => (double)GetValue(RatingMvaProperty); set => SetValue(RatingMvaProperty, value); }
    public double TapRatio { get => (double)GetValue(TapRatioProperty); set => SetValue(TapRatioProperty, value); }
    public double TopOilC { get => (double)GetValue(TopOilCProperty); set => SetValue(TopOilCProperty, value); }
    public double HotSpotC { get => (double)GetValue(HotSpotCProperty); set => SetValue(HotSpotCProperty, value); }
    public bool HasOltc { get => (bool)GetValue(HasOltcProperty); set => SetValue(HasOltcProperty, value); }
    public double TapMin { get => (double)GetValue(TapMinProperty); set => SetValue(TapMinProperty, value); }
    public double TapMax { get => (double)GetValue(TapMaxProperty); set => SetValue(TapMaxProperty, value); }

    // --- brushes and pens ---
    private static readonly Color TankStroke = Palette.FromHex("#3B5EA6");
    private static readonly Color TankFill = Palette.FromHex("#0C1A35");
    private static readonly Color CoreBlue = Palette.FromHex("#1D4ED8");
    private static readonly Color PhaseA = Palette.FromHex("#3B82F6");
    private static readonly Color PhaseB = Palette.FromHex("#F59E0B");
    private static readonly Color PhaseC = Palette.FromHex("#A855F7");
    private static readonly Color SlateLight = Palette.FromHex("#94A3B8");
    private static readonly Color SlateDim = Palette.FromHex("#64748B");
    private static readonly Color SlateDark = Palette.FromHex("#334155");
    private static readonly Color DarkBg = Palette.FromHex("#0F172A");
    private static readonly Color Cyan = Palette.FromHex("#22D3EE");
    private static readonly Color Amber = Palette.FromHex("#F59E0B");
    private static readonly Color Red = Palette.FromHex("#EF4444");

    private static readonly Pen TankPen = Freeze(new Pen(Freeze(new SolidColorBrush(TankStroke)), 2.5));
    private static readonly Pen CorePen = Freeze(new Pen(Freeze(new SolidColorBrush(CoreBlue)), 1.2));
    private static readonly Pen SlatePen = Freeze(new Pen(Freeze(new SolidColorBrush(SlateDark)), 1.5));
    private static readonly Pen BushingStem = Freeze(new Pen(Freeze(new SolidColorBrush(SlateLight)), 5));
    private static readonly Pen BushingStemHv = Freeze(new Pen(Freeze(new SolidColorBrush(SlateLight)), 6));
    private static readonly Pen FinStroke = Freeze(new Pen(Freeze(new SolidColorBrush(Palette.FromHex("#2D4A6F"))), 1));
    private static readonly Pen PipePen = Freeze(new Pen(Freeze(new SolidColorBrush(SlateDark)), 1.5));
    private static readonly Pen ConnectPen = Freeze(new Pen(Freeze(new SolidColorBrush(SlateDark)), 2.5));
    private static readonly Pen InfoBoxPen = Freeze(new Pen(Freeze(new SolidColorBrush(Palette.FromHex("#1E3A5F"))), 1.5));

    private static readonly Brush TankBrush = Freeze(new SolidColorBrush(TankFill));
    private static readonly Brush CoreBrush = Freeze(new SolidColorBrush(Palette.FromHex("#132A57")));
    private static readonly Brush FinBrush = Freeze(new SolidColorBrush(Palette.FromHex("#1A2741")));
    private static readonly Brush DarkBgBrush = Freeze(new SolidColorBrush(DarkBg));
    private static readonly Brush ShedBrush = Freeze(new SolidColorBrush(Palette.FromHex("#CBD5E1")));
    private static readonly Brush TextWhite = Freeze(new SolidColorBrush(Palette.FromHex("#E6EDF3")));
    private static readonly Brush TextSlate = Freeze(new SolidColorBrush(SlateLight));
    private static readonly Brush TextDim = Freeze(new SolidColorBrush(SlateDim));
    private static readonly Brush CyanBrush = Freeze(new SolidColorBrush(Cyan));
    private static readonly Brush AmberBrush = Freeze(new SolidColorBrush(Amber));
    private static readonly Brush RedBrush = Freeze(new SolidColorBrush(Red));
    private static readonly Brush FanCircleBrush = Freeze(new SolidColorBrush(DarkBg));

    private static readonly Brush PhaseABrush = Freeze(new SolidColorBrush(PhaseA));
    private static readonly Brush PhaseBBrush = Freeze(new SolidColorBrush(PhaseB));
    private static readonly Brush PhaseCBrush = Freeze(new SolidColorBrush(PhaseC));
    private static readonly Brush[] PhaseBrushes = [PhaseABrush, PhaseBBrush, PhaseCBrush];
    private static readonly Color[] PhaseColors = [PhaseA, PhaseB, PhaseC];

    private static readonly Typeface Mono =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface MonoLight =
        new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static Pen MakePhase(Color c, double w, DashStyle? dash = null)
    {
        var p = new Pen(Freeze(new SolidColorBrush(c)), w);
        if (dash is not null) p.DashStyle = dash;
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double aw = ActualWidth, ah = ActualHeight;
        if (aw < 200 || ah < 150) return;

        double pps = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // The SVG viewBox was 920x485; we scale uniformly to fit the element, centered.
        double vw = 920, vh = 485;
        double scale = Math.Min(aw / vw, ah / vh);
        double ox = (aw - vw * scale) / 2, oy = (ah - vh * scale) / 2;

        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(scale, scale));

        DrawCoolingBank(dc, 58, false);  // left
        DrawCoolingBank(dc, 692, true);  // right (standby)
        DrawTank(dc);
        DrawCore(dc);
        DrawBushings(dc, pps);
        if (HasOltc) DrawOltcBox(dc, pps);
        DrawInfoOverlays(dc, pps);
        DrawSideLabels(dc, pps);

        dc.Pop(); // scale
        dc.Pop(); // translate
    }

    private static void DrawCoolingBank(DrawingContext dc, double x, bool standby)
    {
        double opacity = standby ? 0.6 : 0.92;
        dc.PushOpacity(opacity);

        // Header / footer pipes
        dc.DrawRoundedRectangle(FinBrush, PipePen, new Rect(x, 146, 170, 7), 3, 3);
        dc.DrawRoundedRectangle(FinBrush, PipePen, new Rect(x, 330, 170, 7), 3, 3);

        // 11 fin tubes
        for (int i = 0; i < 11; i++)
            dc.DrawRoundedRectangle(FinBrush, FinStroke, new Rect(x + 4 + i * 14, 153, 8, 177), 3, 3);

        // Fan circle
        double fc = x + 85;
        dc.DrawEllipse(FanCircleBrush, SlatePen, new Point(fc, 355), 24, 24);
        var dashPen = MakePhase(standby ? SlateDark : Cyan, 1.5, DashStyles.Dash);
        dc.DrawEllipse(null, dashPen, new Point(fc, 355), 16, 16);
        Brush label = standby ? TextDim : CyanBrush;
        Txt(dc, standby ? "STANDBY" : "ACTIVE", MonoLight, 7, label, fc, 360, pps: 1);

        // Connecting legs to tank
        bool left = x < 400;
        double tankEdge = left ? 240 : 680;
        double pipeEnd = left ? x + 170 : x;
        dc.DrawLine(ConnectPen, new Point(pipeEnd, 149), new Point(tankEdge, 172));
        dc.DrawLine(ConnectPen, new Point(pipeEnd, 337), new Point(tankEdge, 320));

        dc.Pop(); // opacity
    }

    private static void DrawTank(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(TankBrush, TankPen, new Rect(240, 112, 440, 246), 16, 16);
    }

    private static void DrawCore(DrawingContext dc)
    {
        // Top and bottom yokes
        dc.DrawRoundedRectangle(CoreBrush, CorePen, new Rect(284, 145, 352, 16), 4, 4);
        dc.DrawRoundedRectangle(CoreBrush, CorePen, new Rect(284, 309, 352, 16), 4, 4);

        double[] limbs = [320, 440, 560];
        string[] labels = ["A", "B", "C"];

        for (int i = 0; i < 3; i++)
        {
            double cx = limbs[i];
            // Limb
            dc.DrawRoundedRectangle(CoreBrush, CorePen, new Rect(cx - 10, 145, 20, 180), 4, 4);

            // HV winding (outer ellipse, dashed)
            var hvPen = MakePhase(PhaseColors[i], 2.5, DashStyles.DashDot);
            dc.DrawEllipse(null, hvPen, new Point(cx, 234), 38, 76);

            // LV winding (inner ellipse, dashed)
            var lvPen = MakePhase(PhaseColors[i], 1.8, DashStyles.Dash);
            dc.PushOpacity(0.55);
            dc.DrawEllipse(null, lvPen, new Point(cx, 234), 22, 56);
            dc.Pop();

            // Phase label inside tank
            dc.PushOpacity(0.8);
            Txt(dc, labels[i], Mono, 10, PhaseBrushes[i], cx, 170, pps: 1);
            dc.Pop();

            // Bushing entry lines into tank
            var entryPen = MakePhase(PhaseColors[i], 2.5);
            dc.DrawLine(entryPen, new Point(cx, 112), new Point(cx, 145));
            dc.DrawLine(entryPen, new Point(cx, 325), new Point(cx, 358));
        }
    }

    private void DrawBushings(DrawingContext dc, double pps)
    {
        double[] xs = [320, 440, 560];
        string[] phases = ["A", "B", "C"];

        for (int i = 0; i < 3; i++)
        {
            double cx = xs[i];
            // --- HV bushing (top) ---
            dc.DrawLine(BushingStemHv, new Point(cx, 112), new Point(cx, 12));

            // Porcelain sheds
            double[] shedY = [32, 50, 65, 79, 91, 101];
            double[] shedRx = [24, 22, 19, 17, 15, 12];
            double[] shedRy = [6, 5, 5, 4, 4, 3];
            for (int s = 0; s < shedY.Length; s++)
                dc.DrawEllipse(ShedBrush, null, new Point(cx, shedY[s]), shedRx[s], shedRy[s]);

            // Terminal cap
            dc.DrawEllipse(PhaseBrushes[i], null, new Point(cx, 9), 7, 7);

            // Phase label
            Txt(dc, phases[i], Mono, 10, PhaseBrushes[i], cx + 16, 8, pps: pps, align: TextAlignment.Left);

            // Voltage badge: the phase-to-neutral voltage, V_LL / sqrt(3), identical on all three phases because the
            // power flow is balanced. It used to print V_LL / 3 with an invented +/-0.3 % spread between phases A, B and
            // C (133 kV per phase on a 400 kV unit, true value 230.9 kV), and fall back to the LINE voltage before the
            // first frame. The fallback is now the nominal phase voltage, in the same unit.
            double liveV = (HvKvLive > 0 ? HvKvLive : HvKvNom) / Math.Sqrt(3.0);
            dc.DrawRoundedRectangle(DarkBgBrush, null, new Rect(cx - 28, -20, 56, 16), 4, 4);
            Txt(dc, $"{liveV:F1} kV", Mono, 9, PhaseBrushes[i], cx, -11, pps: pps);

            // --- LV bushing (bottom) ---
            dc.DrawLine(BushingStem, new Point(cx, 358), new Point(cx, 446));
            double[] lvShedY = [375, 389, 401, 413];
            double[] lvShedRx = [18, 16, 14, 12];
            double[] lvShedRy = [4, 4, 3, 3];
            for (int s = 0; s < lvShedY.Length; s++)
                dc.DrawEllipse(ShedBrush, null, new Point(cx, lvShedY[s]), lvShedRx[s], lvShedRy[s]);

            dc.DrawEllipse(PhaseBrushes[i], null, new Point(cx, 449), 6, 6);
            Txt(dc, phases[i].ToLowerInvariant(), Mono, 10, PhaseBrushes[i], cx + 13, 448, pps: pps, align: TextAlignment.Left);

            double lvV = (LvKvLive > 0 ? LvKvLive : LvKvNom) / Math.Sqrt(3.0);   // phase-to-neutral, as the HV badge
            dc.DrawRoundedRectangle(DarkBgBrush, null, new Rect(cx - 24, 455, 48, 15), 4, 4);
            Txt(dc, $"{lvV:F1} kV", Mono, 9, PhaseBrushes[i], cx, 464, pps: pps);
        }

        // Neutral earth symbol at centre LV
        var earthPen = Freeze(new Pen(TextDim, 2));
        dc.DrawLine(earthPen, new Point(440, 358), new Point(440, 378));
        dc.DrawLine(earthPen, new Point(428, 378), new Point(452, 378));
        dc.DrawLine(earthPen, new Point(432, 384), new Point(448, 384));
        dc.DrawLine(earthPen, new Point(436, 390), new Point(444, 390));
        Txt(dc, "N", MonoLight, 8, TextDim, 454, 387, pps: pps, align: TextAlignment.Left);
    }

    private void DrawOltcBox(DrawingContext dc, double pps)
    {
        Color oltcColor = Cyan;
        Brush oltcBrush = CyanBrush;
        var borderPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x55, Cyan.R, Cyan.G, Cyan.B))), 2));

        dc.DrawRoundedRectangle(DarkBgBrush, borderPen, new Rect(648, 162, 90, 125), 8, 8);
        Txt(dc, "OLTC", Mono, 9, TextSlate, 693, 176, pps: pps);

        // Tap scale
        var scalePen = Freeze(new Pen(Freeze(new SolidColorBrush(Palette.FromHex("#1E3A5F"))), 2));
        var tickPen = Freeze(new Pen(Freeze(new SolidColorBrush(SlateDark)), 1));
        dc.DrawLine(scalePen, new Point(668, 188), new Point(668, 270));

        for (int i = 0; i < 17; i++)
        {
            double y = 188 + i * 5.1;
            double half = i % 4 == 0 ? 4 : 2;
            dc.DrawLine(tickPen, new Point(668 - half, y), new Point(668 + half, y));
        }

        // Tap pointer position
        double tapFrac = TapMax > TapMin ? (TapRatio - TapMin) / (TapMax - TapMin) : 0.5;
        tapFrac = Math.Clamp(tapFrac, 0, 1);
        double tapY = 188 + tapFrac * 82;

        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(new Point(672, tapY), true, true);
            g.LineTo(new Point(685, tapY - 5), true, false);
            g.LineTo(new Point(685, tapY + 5), true, false);
        }
        arrow.Freeze();
        dc.DrawGeometry(Freeze(new SolidColorBrush(oltcColor)), null, arrow);

        Txt(dc, $"{TapRatio:F3}", Mono, 10, oltcBrush, 710, tapY, pps: pps, align: TextAlignment.Left);

        // OLTC connection to tank (dashed)
        var dashPen = MakePhase(Cyan, 1.5, DashStyles.Dash);
        dc.PushOpacity(0.6);
        dc.DrawLine(dashPen, new Point(680, 218), new Point(648, 218));
        dc.Pop();
    }

    private void DrawInfoOverlays(DrawingContext dc, double pps)
    {
        Brush loadColor = LoadingPct > 90 ? RedBrush : LoadingPct > 75 ? AmberBrush : CyanBrush;
        double hotSpot = HotSpotC;
        Brush thermalColor = hotSpot > 98 ? RedBrush : hotSpot > 85 ? AmberBrush : CyanBrush;

        // Top-right: load + rating
        dc.DrawRoundedRectangle(DarkBgBrush, InfoBoxPen, new Rect(760, 54, 148, 55), 8, 8);
        Txt(dc, "LOAD", MonoLight, 8, TextDim, 769, 66, pps: pps, align: TextAlignment.Left);
        Txt(dc, $"{LoadingPct:F1} %", Mono, 14, loadColor, 769, 79, pps: pps, align: TextAlignment.Left);
        Txt(dc, "RATING", MonoLight, 8, TextDim, 845, 66, pps: pps, align: TextAlignment.Left);
        Txt(dc, $"{RatingMva:F0} MVA", Mono, 14, CyanBrush, 845, 79, pps: pps, align: TextAlignment.Left);

        // Bottom-right: temperatures
        dc.DrawRoundedRectangle(DarkBgBrush, InfoBoxPen, new Rect(760, 392, 148, 82), 8, 8);
        Txt(dc, "TOP OIL", MonoLight, 8, TextDim, 769, 404, pps: pps, align: TextAlignment.Left);
        Txt(dc, $"{TopOilC:F1} degC", Mono, 13, thermalColor, 769, 417, pps: pps, align: TextAlignment.Left);
        Txt(dc, "HOT-SPOT", MonoLight, 8, TextDim, 769, 437, pps: pps, align: TextAlignment.Left);
        Txt(dc, $"{HotSpotC:F1} degC", Mono, 13, thermalColor, 769, 450, pps: pps, align: TextAlignment.Left);

        // Bottom-centre: power
        dc.DrawRoundedRectangle(DarkBgBrush, InfoBoxPen, new Rect(302, 392, 200, 82), 8, 8);
        Txt(dc, "ACTIVE POWER", MonoLight, 8, TextDim, 402, 406, pps: pps);
        Txt(dc, $"{FlowMw:F1} MW", Mono, 18, loadColor, 402, 422, pps: pps);
        Txt(dc, $"TAP {TapRatio:F3}", MonoLight, 9, TextSlate, 402, 450, pps: pps);
        string hvLv = $"{HvKvNom:F0} / {LvKvNom:F0} kV";
        Txt(dc, hvLv, MonoLight, 8, TextDim, 402, 462, pps: pps);

        // Side labels
    }

    private void DrawSideLabels(DrawingContext dc, double pps)
    {
        Txt(dc, $"{HvKvNom:F0} kV", Mono, 10, TextSlate, 14, 91, pps: pps, align: TextAlignment.Left);
        Txt(dc, "PRIMARY (HV)", MonoLight, 8, TextDim, 14, 104, pps: pps, align: TextAlignment.Left);
        Txt(dc, $"{LvKvNom:F0} kV", Mono, 10, TextSlate, 14, 371, pps: pps, align: TextAlignment.Left);
        Txt(dc, "SECONDARY (LV)", MonoLight, 8, TextDim, 14, 384, pps: pps, align: TextAlignment.Left);
    }

    private static void Txt(DrawingContext dc, string text, Typeface face, double size, Brush brush,
        double x, double y, double pps = 1, TextAlignment align = TextAlignment.Center)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            face, size, brush, Math.Max(pps, 1))
        { TextAlignment = align, MaxTextWidth = 400 };
        double dx = align == TextAlignment.Center ? x - 200 : x;
        dc.DrawText(ft, new Point(dx, y));
    }
}
