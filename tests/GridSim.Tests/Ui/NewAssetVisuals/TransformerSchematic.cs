// TEST COPY (UI programme ASSETS A1): src/GridSim.Wpf/Visuals/TransformerSchematic.cs as it is now, drawing from src/GridSim.Ui/Visuals.
// TEST COPY Only the using and namespace lines differ (the WPF types are the recording stand-ins in UiVisualsWpfShim.cs);
// TEST COPY UiAssetVisualsParityTests checks the rest equals the real file, line for line.
using GridSim.Tests.Ui.WpfShim;
using GridSim.Ui.Visuals;

namespace GridSim.Tests.Ui.NewAssetVisuals;

/// <summary>
/// Front-elevation schematic of a three-phase power transformer: tank outline, 3-limb
/// core with HV/LV windings, porcelain bushings at top (HV) and bottom (LV), cooling
/// radiator banks, OLTC box with tap pointer, and annotated data overlays for live
/// electrical and thermal state. Renders in one pass via <see cref="OnRender"/>; no
/// template, same pattern as <see cref="Gauge"/> and <see cref="Phasor"/>.
/// UI programme ASSETS A1: the layout and colours (canvas scaling, cooling banks, tank, core, bushings, OLTC box, overlays,
/// labels and their colour rules) moved to the platform-neutral <see cref="TransformerSchematicGeometry"/>
/// (src/GridSim.Ui/Visuals), operation for operation; it returns the old drawing calls in order and this control only
/// replays them (<see cref="VisualDisplayListPainter"/>), so its pixels are unchanged (UiAssetVisualsParityTests pins
/// that against a verbatim copy of the old code, bit for bit).
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

    protected override void OnRender(DrawingContext dc)
    {
        // The whole schematic worked out by the shared geometry (null = too small, under 200 x 150: as before).
        VisualDisplayList? list = TransformerSchematicGeometry.Compute(ActualWidth, ActualHeight, new TransformerSchematicInput
        {
            LoadingPct = LoadingPct,
            HvKvNom = HvKvNom,
            LvKvNom = LvKvNom,
            HvKvLive = HvKvLive,
            LvKvLive = LvKvLive,
            FlowMw = FlowMw,
            RatingMva = RatingMva,
            TapRatio = TapRatio,
            TopOilC = TopOilC,
            HotSpotC = HotSpotC,
            HasOltc = HasOltc,
            TapMin = TapMin,
            TapMax = TapMax,
        });
        if (list is null) return;

        double pps = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        VisualDisplayListPainter.Paint(dc, list, pps);
    }
}
