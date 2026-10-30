using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// The transformer page's front-elevation schematic (UI programme ASSETS A3, the port of the WPF
/// <c>Visuals/TransformerSchematic.cs</c>): tank, core, bushings, cooling banks, the OLTC box and the live overlays.
///
/// <para>It has the WPF control's thirteen properties under the same names and defaults, so the page template binds it
/// the same way. The whole drawing is the A1 geometry's display list (<see cref="TransformerSchematicGeometry"/>, the code
/// the WPF control draws from since slice A1), replayed call for call by <see cref="DisplayListPainter"/>. Nothing is
/// drawn under 200 x 150, as in WPF.</para>
/// </summary>
public sealed class TransformerSchematic : Control
{
    /// <summary>Live loading, percent.</summary>
    public static readonly StyledProperty<double> LoadingPctProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(LoadingPct), 0.0);

    /// <summary>HV nominal kV.</summary>
    public static readonly StyledProperty<double> HvKvNomProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(HvKvNom), 132.0);

    /// <summary>LV nominal kV.</summary>
    public static readonly StyledProperty<double> LvKvNomProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(LvKvNom), 33.0);

    /// <summary>HV live kV.</summary>
    public static readonly StyledProperty<double> HvKvLiveProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(HvKvLive), 0.0);

    /// <summary>LV live kV.</summary>
    public static readonly StyledProperty<double> LvKvLiveProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(LvKvLive), 0.0);

    /// <summary>Flow through the unit, MW.</summary>
    public static readonly StyledProperty<double> FlowMwProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(FlowMw), 0.0);

    /// <summary>Rating, MVA.</summary>
    public static readonly StyledProperty<double> RatingMvaProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(RatingMva), 0.0);

    /// <summary>Tap ratio.</summary>
    public static readonly StyledProperty<double> TapRatioProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(TapRatio), 1.0);

    /// <summary>Top-oil temperature (the fixed demo value).</summary>
    public static readonly StyledProperty<double> TopOilCProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(TopOilC), 58.0);

    /// <summary>Hot-spot temperature (the fixed demo value).</summary>
    public static readonly StyledProperty<double> HotSpotCProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(HotSpotC), 71.0);

    /// <summary>Whether an OLTC is fitted.</summary>
    public static readonly StyledProperty<bool> HasOltcProperty = AvaloniaProperty.Register<TransformerSchematic, bool>(nameof(HasOltc));

    /// <summary>The OLTC's lowest tap.</summary>
    public static readonly StyledProperty<double> TapMinProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(TapMin), 0.9);

    /// <summary>The OLTC's highest tap.</summary>
    public static readonly StyledProperty<double> TapMaxProperty = AvaloniaProperty.Register<TransformerSchematic, double>(nameof(TapMax), 1.1);

    static TransformerSchematic()
    {
        AffectsRender<TransformerSchematic>(LoadingPctProperty, HvKvNomProperty, LvKvNomProperty, HvKvLiveProperty, LvKvLiveProperty,
            FlowMwProperty, RatingMvaProperty, TapRatioProperty, TopOilCProperty, HotSpotCProperty, HasOltcProperty, TapMinProperty,
            TapMaxProperty);
    }

    /// <inheritdoc cref="LoadingPctProperty"/>
    public double LoadingPct { get => GetValue(LoadingPctProperty); set => SetValue(LoadingPctProperty, value); }

    /// <inheritdoc cref="HvKvNomProperty"/>
    public double HvKvNom { get => GetValue(HvKvNomProperty); set => SetValue(HvKvNomProperty, value); }

    /// <inheritdoc cref="LvKvNomProperty"/>
    public double LvKvNom { get => GetValue(LvKvNomProperty); set => SetValue(LvKvNomProperty, value); }

    /// <inheritdoc cref="HvKvLiveProperty"/>
    public double HvKvLive { get => GetValue(HvKvLiveProperty); set => SetValue(HvKvLiveProperty, value); }

    /// <inheritdoc cref="LvKvLiveProperty"/>
    public double LvKvLive { get => GetValue(LvKvLiveProperty); set => SetValue(LvKvLiveProperty, value); }

    /// <inheritdoc cref="FlowMwProperty"/>
    public double FlowMw { get => GetValue(FlowMwProperty); set => SetValue(FlowMwProperty, value); }

    /// <inheritdoc cref="RatingMvaProperty"/>
    public double RatingMva { get => GetValue(RatingMvaProperty); set => SetValue(RatingMvaProperty, value); }

    /// <inheritdoc cref="TapRatioProperty"/>
    public double TapRatio { get => GetValue(TapRatioProperty); set => SetValue(TapRatioProperty, value); }

    /// <inheritdoc cref="TopOilCProperty"/>
    public double TopOilC { get => GetValue(TopOilCProperty); set => SetValue(TopOilCProperty, value); }

    /// <inheritdoc cref="HotSpotCProperty"/>
    public double HotSpotC { get => GetValue(HotSpotCProperty); set => SetValue(HotSpotCProperty, value); }

    /// <inheritdoc cref="HasOltcProperty"/>
    public bool HasOltc { get => GetValue(HasOltcProperty); set => SetValue(HasOltcProperty, value); }

    /// <inheritdoc cref="TapMinProperty"/>
    public double TapMin { get => GetValue(TapMinProperty); set => SetValue(TapMinProperty, value); }

    /// <inheritdoc cref="TapMaxProperty"/>
    public double TapMax { get => GetValue(TapMaxProperty); set => SetValue(TapMaxProperty, value); }

    /// <summary>The schematic's input now: the thirteen properties, in the WPF control's order.</summary>
    public TransformerSchematicInput Input => new()
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
    };

    /// <summary>The display list this control draws at its current size (null: nothing drawn), for tests.</summary>
    public VisualDisplayList? CurrentDrawing() => TransformerSchematicGeometry.Compute(Bounds.Width, Bounds.Height, Input);

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        VisualDisplayList? list = CurrentDrawing();
        if (list is null) return;
        DisplayListPainter.Paint(context, list, this);
    }
}
