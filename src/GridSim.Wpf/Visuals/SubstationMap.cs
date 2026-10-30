using System.Windows;
using System.Windows.Media;
using GridSim.Ui.Presentation;
using GridSim.Ui.Visuals;

namespace GridSim.Wpf.Visuals;

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
/// UI programme ASSETS A1: the layout and colours (bar, badge, stubs, arrows, symbols, text and its colour rules) moved
/// to the platform-neutral <see cref="SubstationMapGeometry"/> (src/GridSim.Ui/Visuals), operation for operation; it
/// returns the old drawing calls in order and this control only replays them (<see cref="VisualDisplayListPainter"/>),
/// so its pixels are unchanged (UiAssetVisualsParityTests pins that against a verbatim copy of the old code, bit for bit).
/// </summary>
public sealed class SubstationMap : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(SubstationMapData), typeof(SubstationMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SubstationMapData? Data { get => (SubstationMapData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        // The whole diagram worked out by the shared geometry (null = too small, under 300 x 200, or no data: as before).
        SubstationMapData? d = Data;
        VisualDisplayList? list = SubstationMapGeometry.Compute(ActualWidth, ActualHeight, d is null ? null : ToInput(d));
        if (list is null) return;

        double pps = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        VisualDisplayListPainter.Paint(dc, list, pps);
    }

    /// <summary>The frame's data as the geometry's framework-free input: the same values, the fuel colour as its four bytes.</summary>
    private static SubstationMapInput ToInput(SubstationMapData d) => new()
    {
        VoltagePu = d.VoltagePu,
        AngleDeg = d.AngleDeg,
        BaseKv = d.BaseKv,
        DemandMw = d.DemandMw,
        ReactiveMvar = d.ReactiveMvar,
        GenerationMw = d.GenerationMw,
        BusType = d.BusType,
        NetInjectionMw = d.NetInjectionMw,
        Connections = d.Connections.Select(c => new SubstationMapConnection(c.Name, c.OtherEnd, c.Kind, c.FlowMw, c.Exporting, c.LoadingFrac)).ToList(),
        Generators = d.Generators.Select(g => new SubstationMapGenerator(g.Name, g.Fuel,
            new UiColour(g.FuelColor.A, g.FuelColor.R, g.FuelColor.G, g.FuelColor.B), g.OutputMw)).ToList(),
    };
}
