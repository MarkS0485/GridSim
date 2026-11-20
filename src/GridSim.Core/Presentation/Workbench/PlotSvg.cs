using System.Globalization;
using System.Net;
using System.Text;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>
/// Draws a <see cref="PlotSpec"/> as a self-contained SVG string (no script, no external reference, literal colours), used
/// by the web client and by the HTML report. The output is a pure function of the geometry: numbers are written with two
/// decimals in the invariant culture, so the same plot gives the same SVG on every machine.
/// </summary>
public static class PlotSvg
{
    /// <summary>The series colours, in style order (Okabe-Ito, which keeps its categories apart for the common colour-vision types).</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#0072B2", "#D55E00", "#009E73", "#CC79A7", "#E69F00", "#56B4E9", "#F0E442", "#000000",
    ];

    private const string Ink = "#333333", Grid = "#d9d9d9", Frame = "#888888";

    public static string Render(PlotSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var sb = new StringBuilder();
        string W = F(spec.Width), H = F(spec.Height);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(W).Append(' ').Append(H)
          .Append("\" width=\"").Append(W).Append("\" height=\"").Append(H).Append("\" role=\"img\" font-family=\"sans-serif\" font-size=\"11\">");
        sb.Append("<title>").Append(Esc(spec.Title)).Append("</title>");
        sb.Append("<rect width=\"").Append(W).Append("\" height=\"").Append(H).Append("\" fill=\"#ffffff\"/>");
        sb.Append("<text x=\"").Append(F(spec.Width / 2)).Append("\" y=\"18\" text-anchor=\"middle\" font-size=\"13\" font-weight=\"bold\" fill=\"").Append(Ink).Append("\">")
          .Append(Esc(spec.Title)).Append("</text>");
        foreach (PlotPanel p in spec.Panels) Panel(sb, p);
        if (spec.Legend.Count > 1)
        {
            double x = 64;
            for (int i = 0; i < spec.Legend.Count; i++)
            {
                string c = Palette[i % Palette.Count];
                sb.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(spec.Height - 12)).Append("\" width=\"10\" height=\"10\" fill=\"").Append(c).Append("\"/>");
                sb.Append("<text x=\"").Append(F(x + 14)).Append("\" y=\"").Append(F(spec.Height - 3)).Append("\" fill=\"").Append(Ink).Append("\">").Append(Esc(spec.Legend[i])).Append("</text>");
                x += 24 + spec.Legend[i].Length * 6.5;
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Panel(StringBuilder sb, PlotPanel p)
    {
        double right = p.Left + p.Width, bottom = p.Top + p.Height;
        foreach (PlotTick t in p.X.Ticks)
        {
            sb.Append("<line x1=\"").Append(F(t.Position)).Append("\" y1=\"").Append(F(p.Top)).Append("\" x2=\"").Append(F(t.Position)).Append("\" y2=\"").Append(F(bottom))
              .Append("\" stroke=\"").Append(Grid).Append("\" stroke-width=\"1\"/>");
            sb.Append("<text x=\"").Append(F(t.Position)).Append("\" y=\"").Append(F(bottom + 14)).Append("\" text-anchor=\"middle\" fill=\"").Append(Ink).Append("\">").Append(Esc(t.Label)).Append("</text>");
        }
        foreach (PlotTick t in p.Y.Ticks)
        {
            sb.Append("<line x1=\"").Append(F(p.Left)).Append("\" y1=\"").Append(F(t.Position)).Append("\" x2=\"").Append(F(right)).Append("\" y2=\"").Append(F(t.Position))
              .Append("\" stroke=\"").Append(Grid).Append("\" stroke-width=\"1\"/>");
            sb.Append("<text x=\"").Append(F(p.Left - 6)).Append("\" y=\"").Append(F(t.Position + 4)).Append("\" text-anchor=\"end\" fill=\"").Append(Ink).Append("\">").Append(Esc(t.Label)).Append("</text>");
        }
        sb.Append("<rect x=\"").Append(F(p.Left)).Append("\" y=\"").Append(F(p.Top)).Append("\" width=\"").Append(F(p.Width)).Append("\" height=\"").Append(F(p.Height))
          .Append("\" fill=\"none\" stroke=\"").Append(Frame).Append("\"/>");
        if (p.X.Label.Length > 0)
            sb.Append("<text x=\"").Append(F(p.Left + p.Width / 2)).Append("\" y=\"").Append(F(bottom + 32)).Append("\" text-anchor=\"middle\" fill=\"").Append(Ink).Append("\">").Append(Esc(p.X.Label)).Append("</text>");
        if (p.Y.Label.Length > 0)
            sb.Append("<text transform=\"rotate(-90 14 ").Append(F(p.Top + p.Height / 2)).Append(")\" x=\"14\" y=\"").Append(F(p.Top + p.Height / 2)).Append("\" text-anchor=\"middle\" fill=\"").Append(Ink).Append("\">").Append(Esc(p.Y.Label)).Append("</text>");

        foreach (PlotItem item in p.Items)
        {
            switch (item)
            {
                case PlotRule r:
                    if (Finite(r.X1, r.Y1, r.X2, r.Y2))
                        sb.Append("<line x1=\"").Append(F(r.X1)).Append("\" y1=\"").Append(F(r.Y1)).Append("\" x2=\"").Append(F(r.X2)).Append("\" y2=\"").Append(F(r.Y2))
                          .Append("\" stroke=\"#666666\" stroke-width=\"1\" stroke-dasharray=\"4 3\"><title>").Append(Esc(r.Name)).Append("</title></line>");
                    break;
                case PlotGap g:
                    if (double.IsFinite(g.Px))
                        sb.Append("<line x1=\"").Append(F(g.Px)).Append("\" y1=\"").Append(F(p.Top)).Append("\" x2=\"").Append(F(g.Px)).Append("\" y2=\"").Append(F(bottom))
                          .Append("\" stroke=\"#b00020\" stroke-width=\"1\" stroke-dasharray=\"2 3\"><title>").Append(Esc(g.Series + ": " + g.Reason)).Append("</title></line>");
                    break;
                case PlotPolyline pl:
                    Polyline(sb, pl);
                    break;
                case PlotBar b:
                    sb.Append("<rect x=\"").Append(F(b.Px - b.Width / 2)).Append("\" y=\"").Append(F(b.Py)).Append("\" width=\"").Append(F(b.Width)).Append("\" height=\"").Append(F(b.Height))
                      .Append("\" fill=\"").Append(Colour(b.Style)).Append("\"><title>").Append(Esc(b.Series + " " + WorkbenchText.Canon(b.DataX) + ": " + WorkbenchText.Canon(b.DataY))).Append("</title></rect>");
                    break;
                case PlotMarker m:
                    Marker(sb, m);
                    break;
                case PlotText t:
                    sb.Append("<text x=\"").Append(F(t.Px)).Append("\" y=\"").Append(F(t.Py)).Append("\" text-anchor=\"").Append(Esc(t.Anchor)).Append("\" fill=\"").Append(Ink).Append("\">").Append(Esc(t.Text)).Append("</text>");
                    break;
            }
        }
    }

    private static void Polyline(StringBuilder sb, PlotPolyline pl)
    {
        string colour = Colour(pl.Style);
        var run = new List<PlotPoint>();
        void Flush()
        {
            if (run.Count >= 2)
            {
                sb.Append(pl.Closed ? "<polygon" : "<polyline").Append(" fill=\"none\" stroke=\"").Append(colour).Append("\" stroke-width=\"1.5\" points=\"");
                sb.AppendJoin(' ', run.Select(q => F(q.Px) + "," + F(q.Py)));
                sb.Append("\"><title>").Append(Esc(pl.Series)).Append("</title></").Append(pl.Closed ? "polygon" : "polyline").Append('>');
            }
            else if (run.Count == 1)
                sb.Append("<circle cx=\"").Append(F(run[0].Px)).Append("\" cy=\"").Append(F(run[0].Py)).Append("\" r=\"2\" fill=\"").Append(colour).Append("\"/>");
            run.Clear();
        }
        foreach (PlotPoint pt in pl.Points)
        {
            if (pt.Missing || !double.IsFinite(pt.Py) || !double.IsFinite(pt.Px)) Flush();
            else run.Add(pt);
        }
        Flush();
    }

    private static void Marker(StringBuilder sb, PlotMarker m)
    {
        if (!Finite(m.Px, m.Py)) return;
        string c = Colour(m.Style);
        double x = m.Px, y = m.Py;
        switch (m.Shape)
        {
            case PlotMarkerShape.Cross:
                sb.Append("<path d=\"M").Append(F(x - 5)).Append(' ').Append(F(y - 5)).Append('L').Append(F(x + 5)).Append(' ').Append(F(y + 5))
                  .Append('M').Append(F(x - 5)).Append(' ').Append(F(y + 5)).Append('L').Append(F(x + 5)).Append(' ').Append(F(y - 5))
                  .Append("\" stroke=\"").Append(c).Append("\" stroke-width=\"2\" fill=\"none\"/>");
                break;
            case PlotMarkerShape.Square:
                sb.Append("<rect x=\"").Append(F(x - 3)).Append("\" y=\"").Append(F(y - 3)).Append("\" width=\"6\" height=\"6\" fill=\"").Append(c).Append("\"/>");
                break;
            default:
                sb.Append("<circle cx=\"").Append(F(x)).Append("\" cy=\"").Append(F(y)).Append("\" r=\"3.5\" fill=\"").Append(c).Append("\"/>");
                break;
        }
        if (!string.IsNullOrEmpty(m.Label))
            sb.Append("<text x=\"").Append(F(x + 7)).Append("\" y=\"").Append(F(y - 6)).Append("\" fill=\"").Append(Ink).Append("\">").Append(Esc(m.Label)).Append("</text>");
    }

    private static string Colour(int style) => Palette[((style % Palette.Count) + Palette.Count) % Palette.Count];
    private static bool Finite(params double[] v) { foreach (double d in v) if (!double.IsFinite(d)) return false; return true; }
    private static string F(double v)
    {
        string s = v.ToString("0.00", CultureInfo.InvariantCulture);
        return s == "-0.00" ? "0.00" : s;
    }
    private static string Esc(string s) => WebUtility.HtmlEncode(s);
}
