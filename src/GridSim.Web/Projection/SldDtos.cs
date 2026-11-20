using GridSim.Core.Presentation.Workbench.Sld;

namespace GridSim.Web.Projection;

/// <summary>
/// R4-18: the web client's view of the Core single-line diagram (<c>GridSim.Core.Presentation.Workbench.Sld</c>). Plain projections:
/// every box, port, connection point and colour is copied from Core's <see cref="SldGeometry"/> and <see cref="SldColourSet"/>, and the
/// SVG the page shows is Core's <see cref="SldExport"/>, so this page and the Avalonia view draw the same diagram (the parity tests
/// compare both to the Core geometry). Nothing here computes.
/// </summary>
public sealed record SldPointDto(double X, double Y);

public sealed record SldPortDto(string Id, double X, double Y);

public sealed record SldSymbolDto(string Id, string Kind, string Label, double Left, double Top, double Right, double Bottom,
    bool InService, double BaseKv, string Colour, IReadOnlyList<SldPortDto> Ports);

public sealed record SldConnectionDto(string Id, string ElementId, string FromPort, string ToPort, string Colour, IReadOnlyList<SldPointDto> Points);

public sealed record SldLegendDto(string Label, string Colour);

/// <summary>A diagram: its symbols and connections with colours, bounds, the layout used, the omitted elements, the crossings the router reported, the geometry's canonical SHA-256 and the SVG.</summary>
public sealed record SldDiagramDto(string Mode, string Note, bool CanMove, double Left, double Top, double Right, double Bottom,
    IReadOnlyList<SldSymbolDto> Symbols, IReadOnlyList<SldConnectionDto> Connections, IReadOnlyList<SldLegendDto> Legend,
    IReadOnlyList<string> Omissions, int Crossings, string Sha256, string Svg)
{
    public static SldDiagramDto From(SldDiagram d, SldColourSet colours, string title = "Single-line diagram", double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(colours);
        SldGeometry g = d.Geometry;
        return new SldDiagramDto(d.Mode.ToString(), d.Note, d.CanMove, g.Bounds.Left, g.Bounds.Top, g.Bounds.Right, g.Bounds.Bottom,
            g.Symbols.Select((s, i) => new SldSymbolDto(s.Id, s.Kind.ToString(), s.Label, s.Box.Left, s.Box.Top, s.Box.Right, s.Box.Bottom,
                s.InService, s.BaseKv, colours.Symbols[i], s.Ports.Select(p => new SldPortDto(p.Id, p.X, p.Y)).ToList())).ToList(),
            g.Connections.Select((c, i) => new SldConnectionDto(c.Id, c.ElementId, c.FromPort, c.ToPort, colours.Connections[i],
                c.Points.Select(p => new SldPointDto(p.X, p.Y)).ToList())).ToList(),
            colours.Legend.Select(l => new SldLegendDto(l.Label, l.Colour)).ToList(),
            g.Omitted.ToList(),
            g.Crossings.Count, g.Sha256(), SldExport.ToSvg(g, colours, title, scale));
    }
}
