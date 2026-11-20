using System.Text;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>One axis tick: the data value, its pixel position along the axis and its label.</summary>
public sealed record PlotTick(double Value, double Position, string Label);

/// <summary>
/// One axis. <see cref="Min"/> and <see cref="Max"/> are the displayed range (after the stated range rule); <see cref="Ticks"/>
/// are the labelled positions. A log axis has its ticks at decades and <see cref="Min"/>/<see cref="Max"/> are decades.
/// </summary>
public sealed record PlotAxis(string Label, bool Log, double Min, double Max, IReadOnlyList<PlotTick> Ticks);

/// <summary>One data point with its pixel position. A missing value (NaN) has NaN data and pixel y: it breaks the line.</summary>
public sealed record PlotPoint(double DataX, double DataY, double Px, double Py)
{
    public bool Missing => double.IsNaN(DataY);
}

/// <summary>An element of the plot geometry. Every coordinate is computed in Core and is deterministic.</summary>
public abstract record PlotItem;

/// <summary>A series as a polyline through its points, in source order. A missing point breaks the line. <see cref="Closed"/>
/// joins the last point to the first (an impedance locus).</summary>
public sealed record PlotPolyline(string Series, int Style, IReadOnlyList<PlotPoint> Points, bool Closed = false) : PlotItem;

public enum PlotMarkerShape { Circle, Cross, Square }

/// <summary>A point marker with an optional label.</summary>
public sealed record PlotMarker(string Series, int Style, PlotMarkerShape Shape, double DataX, double DataY, double Px, double Py, string? Label) : PlotItem;

/// <summary>A bar. <see cref="Py"/> is its top edge and <see cref="Height"/> runs down to the baseline.</summary>
public sealed record PlotBar(string Series, int Style, double DataX, double DataY, double Px, double Py, double Width, double Height) : PlotItem;

/// <summary>A text at a pixel position; <see cref="Anchor"/> is start, middle or end.</summary>
public sealed record PlotText(double Px, double Py, string Text, string Anchor) : PlotItem;

/// <summary>A visible gap: the series has no value at this x. Drawn as a dashed vertical hint, never as a zero.</summary>
public sealed record PlotGap(string Series, double DataX, double Px, string Reason) : PlotItem;

/// <summary>A rule segment (a zero axis, the stability boundary, a limit line), in pixels.</summary>
public sealed record PlotRule(string Name, double X1, double Y1, double X2, double Y2) : PlotItem;

/// <summary>A plotting rectangle with its two axes and its items.</summary>
public sealed record PlotPanel(double Left, double Top, double Width, double Height, PlotAxis X, PlotAxis Y, IReadOnlyList<PlotItem> Items);

/// <summary>
/// A plot as plain geometry (R4-10): polylines, markers, bars, axes, ticks and text, all in pixel coordinates. It has a
/// canonical text form (<see cref="Canonical"/>) and its SHA-256 (<see cref="Sha256"/>), so a plot is as reproducible as a
/// number: the same data always gives the same text, on every machine. Both clients draw this one geometry.
/// </summary>
public sealed record PlotSpec(string Id, string Title, double Width, double Height, IReadOnlyList<PlotPanel> Panels, IReadOnlyList<string> Legend)
{
    public const string Format = "gridsim-plot/1";

    /// <summary>The canonical text: one line per fact, tab separated, every number in its round-trip form.</summary>
    public string Canonical()
    {
        var sb = new StringBuilder();
        void L(params string[] parts) => sb.AppendJoin('\t', parts).Append('\n');
        string N(double v) => WorkbenchText.Canon(v);
        L(Format);
        L("id", Id);
        L("title", Title);
        L("size", N(Width), N(Height));
        L("legend", string.Join("|", Legend));
        foreach (PlotPanel p in Panels)
        {
            L("panel", N(p.Left), N(p.Top), N(p.Width), N(p.Height));
            foreach ((string name, PlotAxis a) in new[] { ("x", p.X), ("y", p.Y) })
            {
                L("axis", name, a.Label, a.Log ? "log" : "linear", N(a.Min), N(a.Max));
                foreach (PlotTick t in a.Ticks) L("tick", N(t.Value), N(t.Position), t.Label);
            }
            foreach (PlotItem item in p.Items)
            {
                switch (item)
                {
                    case PlotPolyline pl:
                        L("polyline", pl.Series, pl.Style.ToString(System.Globalization.CultureInfo.InvariantCulture), pl.Closed ? "closed" : "open");
                        foreach (PlotPoint pt in pl.Points) L("pt", N(pt.DataX), N(pt.DataY), N(pt.Px), N(pt.Py));
                        break;
                    case PlotMarker m:
                        L("marker", m.Series, m.Style.ToString(System.Globalization.CultureInfo.InvariantCulture), m.Shape.ToString(), N(m.DataX), N(m.DataY), N(m.Px), N(m.Py), m.Label ?? "");
                        break;
                    case PlotBar b:
                        L("bar", b.Series, b.Style.ToString(System.Globalization.CultureInfo.InvariantCulture), N(b.DataX), N(b.DataY), N(b.Px), N(b.Py), N(b.Width), N(b.Height));
                        break;
                    case PlotText t:
                        L("text", N(t.Px), N(t.Py), t.Anchor, t.Text);
                        break;
                    case PlotGap g:
                        L("gap", g.Series, N(g.DataX), N(g.Px), g.Reason);
                        break;
                    case PlotRule r:
                        L("rule", r.Name, N(r.X1), N(r.Y1), N(r.X2), N(r.Y2));
                        break;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>The SHA-256 of <see cref="Canonical"/>.</summary>
    public string Sha256() => WorkbenchText.Sha256(Canonical());

    /// <summary>All polylines of every panel.</summary>
    public IEnumerable<PlotPolyline> Polylines => Panels.SelectMany(p => p.Items.OfType<PlotPolyline>());
    public IEnumerable<PlotMarker> Markers => Panels.SelectMany(p => p.Items.OfType<PlotMarker>());
    public IEnumerable<PlotBar> Bars => Panels.SelectMany(p => p.Items.OfType<PlotBar>());
    public IEnumerable<PlotGap> Gaps => Panels.SelectMany(p => p.Items.OfType<PlotGap>());
}
