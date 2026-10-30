using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls;

/// <summary>
/// The substation page's electrical single-line picture (UI programme ASSETS A3, the port of the WPF
/// <c>Visuals/SubstationMap.cs</c>): the busbar with its voltage badge, a stub per connection fanned left and right with
/// its flow arrow, a ring per connected generator and a load arrow for the demand.
///
/// <para>It has the WPF control's one property under the same name (<see cref="Data"/>, the page's <c>MapInput</c>). The
/// whole drawing is the A1 geometry's display list (<see cref="SubstationMapGeometry"/>, the code the WPF control draws
/// from since slice A1), replayed call for call by <see cref="DisplayListPainter"/>, so the two apps draw the same
/// picture. Nothing is drawn when the control is under 300 x 200 or has no data, as in WPF.</para>
/// </summary>
public sealed class SubstationMap : Control
{
    /// <summary>The frame's substation data (the page's <c>MapInput</c>), or null.</summary>
    public static readonly StyledProperty<SubstationMapInput?> DataProperty =
        AvaloniaProperty.Register<SubstationMap, SubstationMapInput?>(nameof(Data));

    static SubstationMap()
    {
        AffectsRender<SubstationMap>(DataProperty);
    }

    /// <inheritdoc cref="DataProperty"/>
    public SubstationMapInput? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>The display list this control draws at its current size (null: nothing drawn), for tests.</summary>
    public VisualDisplayList? CurrentDrawing() => SubstationMapGeometry.Compute(Bounds.Width, Bounds.Height, Data);

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        VisualDisplayList? list = CurrentDrawing();
        if (list is null) return;
        DisplayListPainter.Paint(context, list, this);
    }
}
