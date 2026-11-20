using System.Globalization;
using System.Net;
using System.Text;

namespace GridSim.Core.Presentation.Workbench.Sld;

/// <summary>
/// The SVG of a single-line diagram (R4-18): a self-contained string with literal colours and no script, a pure function of the
/// geometry and the colour set (numbers with two decimals in the invariant culture, so the same diagram gives the same bytes on every
/// machine). The web client shows exactly this SVG, so its picture is the geometry the Avalonia view draws. PDF is deferred.
/// </summary>
public static class SldExport
{
    public static string ToSvg(SldGeometry g, SldColourSet? colours = null, string title = "Single-line diagram", double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(g);
        SldBox b = g.Bounds;
        double w = b.Width * scale, h = b.Height * scale;
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"").Append(F(b.Left)).Append(' ').Append(F(b.Top)).Append(' ').Append(F(b.Width)).Append(' ').Append(F(b.Height))
          .Append("\" width=\"").Append(F(w)).Append("\" height=\"").Append(F(h)).Append("\" role=\"img\" font-family=\"sans-serif\" font-size=\"9\">");
        sb.Append("<title>").Append(Esc(title)).Append("</title>");
        sb.Append("<rect x=\"").Append(F(b.Left)).Append("\" y=\"").Append(F(b.Top)).Append("\" width=\"").Append(F(b.Width)).Append("\" height=\"").Append(F(b.Height)).Append("\" fill=\"#ffffff\"/>");
        for (int i = 0; i < g.Connections.Count; i++)
        {
            SldConnection c = g.Connections[i];
            string col = colours is not null && i < colours.Connections.Count ? colours.Connections[i] : SldColouring.Ink;
            sb.Append("<polyline fill=\"none\" stroke=\"").Append(col).Append("\" stroke-width=\"1.5\" points=\"");
            sb.AppendJoin(' ', c.Points.Select(p => F(p.X) + "," + F(p.Y)));
            sb.Append("\"><title>").Append(Esc(c.Id)).Append("</title></polyline>");
        }
        for (int i = 0; i < g.Symbols.Count; i++)
        {
            SldSymbol s = g.Symbols[i];
            string col = colours is not null && i < colours.Symbols.Count ? colours.Symbols[i] : SldColouring.Ink;
            Symbol(sb, s, col);
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Symbol(StringBuilder sb, SldSymbol s, string col)
    {
        SldBox x = s.Box;
        double cx = x.CentreX, cy = x.CentreY;
        string dash = s.InService ? "" : " stroke-dasharray=\"3 2\"";
        sb.Append("<g data-id=\"").Append(Esc(s.Id)).Append("\">");
        switch (s.Kind)
        {
            case SldKind.Busbar:
                sb.Append("<rect x=\"").Append(F(x.Left)).Append("\" y=\"").Append(F(x.Top)).Append("\" width=\"").Append(F(x.Width)).Append("\" height=\"").Append(F(x.Height)).Append("\" fill=\"").Append(col).Append("\"/>");
                sb.Append("<text x=\"").Append(F(x.CentreX)).Append("\" y=\"").Append(F(x.Top - 3)).Append("\" text-anchor=\"middle\" fill=\"").Append(SldColouring.Ink).Append("\">").Append(Esc(s.Label)).Append("</text>");
                break;
            case SldKind.Line:
                sb.Append("<rect x=\"").Append(F(x.Left)).Append("\" y=\"").Append(F(x.Top)).Append("\" width=\"").Append(F(x.Width)).Append("\" height=\"").Append(F(x.Height))
                  .Append("\" fill=\"#ffffff\" stroke=\"").Append(col).Append("\" stroke-width=\"1.5\"").Append(dash).Append("/>");
                Label(sb, x, s.Label);
                break;
            case SldKind.Transformer:
            {
                double r = Math.Min(x.Width / 2, x.Height * 0.3);
                sb.Append("<circle cx=\"").Append(F(cx)).Append("\" cy=\"").Append(F(x.Top + x.Height * 0.35)).Append("\" r=\"").Append(F(r)).Append("\" fill=\"#ffffff\" stroke=\"").Append(col).Append("\" stroke-width=\"1.5\"").Append(dash).Append("/>");
                sb.Append("<circle cx=\"").Append(F(cx)).Append("\" cy=\"").Append(F(x.Top + x.Height * 0.65)).Append("\" r=\"").Append(F(r)).Append("\" fill=\"#ffffff\" stroke=\"").Append(col).Append("\" stroke-width=\"1.5\"").Append(dash).Append("/>");
                Label(sb, x, s.Label);
                break;
            }
            case SldKind.Breaker:
            case SldKind.LoadBreak:
                sb.Append("<rect x=\"").Append(F(x.Left)).Append("\" y=\"").Append(F(x.Top)).Append("\" width=\"").Append(F(x.Width)).Append("\" height=\"").Append(F(x.Height))
                  .Append("\" fill=\"").Append(s.InService ? col : "#ffffff").Append("\" stroke=\"").Append(col).Append("\" stroke-width=\"1.5\"/>");
                if (s.Kind == SldKind.LoadBreak)
                    sb.Append("<line x1=\"").Append(F(x.Left)).Append("\" y1=\"").Append(F(x.Bottom)).Append("\" x2=\"").Append(F(x.Right)).Append("\" y2=\"").Append(F(x.Top)).Append("\" stroke=\"#ffffff\" stroke-width=\"1\"/>");
                Label(sb, x, s.Label);
                break;
            case SldKind.Disconnector:
                // Closed: a vertical blade; open: the blade turned aside.
                sb.Append("<line x1=\"").Append(F(cx)).Append("\" y1=\"").Append(F(x.Top)).Append("\" x2=\"").Append(F(s.InService ? cx : x.Right)).Append("\" y2=\"").Append(F(x.Bottom)).Append("\" stroke=\"").Append(col).Append("\" stroke-width=\"2\"/>");
                Label(sb, x, s.Label);
                break;
            case SldKind.Generator:
                sb.Append("<circle cx=\"").Append(F(cx)).Append("\" cy=\"").Append(F(cy)).Append("\" r=\"").Append(F(x.Width / 2)).Append("\" fill=\"#ffffff\" stroke=\"").Append(col).Append("\" stroke-width=\"1.5\"").Append(dash).Append("/>");
                sb.Append("<text x=\"").Append(F(cx)).Append("\" y=\"").Append(F(cy + 3)).Append("\" text-anchor=\"middle\" fill=\"").Append(col).Append("\">G</text>");
                Label(sb, x, s.Label);
                break;
            case SldKind.Load:
                sb.Append("<polygon points=\"").Append(F(x.Left)).Append(',').Append(F(x.Top)).Append(' ').Append(F(x.Right)).Append(',').Append(F(x.Top)).Append(' ').Append(F(cx)).Append(',').Append(F(x.Bottom))
                  .Append("\" fill=\"").Append(col).Append("\"/>");
                Label(sb, x, s.Label);
                break;
            case SldKind.Shunt:
                sb.Append("<line x1=\"").Append(F(x.Left)).Append("\" y1=\"").Append(F(cy - 2)).Append("\" x2=\"").Append(F(x.Right)).Append("\" y2=\"").Append(F(cy - 2)).Append("\" stroke=\"").Append(col).Append("\" stroke-width=\"2\"/>");
                sb.Append("<line x1=\"").Append(F(x.Left)).Append("\" y1=\"").Append(F(cy + 2)).Append("\" x2=\"").Append(F(x.Right)).Append("\" y2=\"").Append(F(cy + 2)).Append("\" stroke=\"").Append(col).Append("\" stroke-width=\"2\"/>");
                Label(sb, x, s.Label);
                break;
            case SldKind.EarthSwitch:
                for (int k = 0; k < 3; k++)
                {
                    double w2 = x.Width / 2 * (1 - 0.3 * k), yy = x.Top + 3 + k * 3;
                    sb.Append("<line x1=\"").Append(F(cx - w2)).Append("\" y1=\"").Append(F(yy)).Append("\" x2=\"").Append(F(cx + w2)).Append("\" y2=\"").Append(F(yy)).Append("\" stroke=\"").Append(col).Append("\" stroke-width=\"1.5\"").Append(dash).Append("/>");
                }
                break;
        }
        sb.Append("<title>").Append(Esc(s.Id + " " + s.Label)).Append("</title></g>");
    }

    private static void Label(StringBuilder sb, SldBox x, string label)
        => sb.Append("<text x=\"").Append(F(x.Right + 3)).Append("\" y=\"").Append(F(x.CentreY + 3)).Append("\" fill=\"").Append(SldColouring.Ink).Append("\">").Append(Esc(label)).Append("</text>");

    private static string F(double v) => (v == 0 ? 0 : v).ToString("0.##", CultureInfo.InvariantCulture);
    private static string Esc(string s) => WebUtility.HtmlEncode(s);
}
