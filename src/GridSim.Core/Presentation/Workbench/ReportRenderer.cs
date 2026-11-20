using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>A resolved report block, ready to render.</summary>
public abstract record ReportOut;

public sealed record ReportOutText(string Text) : ReportOut;
public sealed record ReportOutValue(string Label, string Value, bool Gap) : ReportOut;
/// <summary>A resolved table. <see cref="GapNote"/> is set (and the rows empty) when the binding found no array.</summary>
public sealed record ReportOutTable(string Title, IReadOnlyList<string> Headings, IReadOnlyList<IReadOnlyList<string>> Rows, string? GapNote) : ReportOut;
/// <summary>A resolved plot; <see cref="Plot"/> is null, and <see cref="GapNote"/> set, when the binding could not be drawn.</summary>
public sealed record ReportOutPlot(string Title, PlotSpec? Plot, string? GapNote) : ReportOut;

public sealed record ReportOutSection(string Heading, IReadOnlyList<ReportOut> Blocks);

/// <summary>What a report records about how it was made: the SHA-256 of the template bytes and of the result bytes, and the
/// generator (and, when the caller gives it, the product version). There is no timestamp: the report is reproducible.</summary>
public sealed record ReportProvenance(string TemplateSha256, string ResultSha256, string Generator, string? ProductVersion);

/// <summary>A report ready to render: the title, the resolved sections and the provenance.</summary>
public sealed record ReportDocument(string Title, IReadOnlyList<ReportOutSection> Sections, ReportProvenance Provenance);

/// <summary>
/// The report generator (R4-10). Order of operations, as the work order states it: load the template; verify the result's hash
/// (when the template names one); resolve the bindings in template order; build the tables and plots; render; the caller writes
/// once. A binding that finds nothing is a visible gap (a note, or "n/a" in a cell), never a zero. Markdown, self-contained HTML
/// (inline SVG, no script) and CSV are produced from the one <see cref="ReportDocument"/>, so they agree.
/// </summary>
public static class ReportRenderer
{
    public const string Generator = "gridsim-report/1";
    public const string GapText = "n/a";

    /// <summary>Build the document from the template and result bytes.</summary>
    public static ReportDocument Build(byte[] templateBytes, byte[] resultBytes, string? productVersion = null)
    {
        ArgumentNullException.ThrowIfNull(templateBytes);
        ArgumentNullException.ThrowIfNull(resultBytes);
        ReportTemplate template = ReportTemplate.Parse(templateBytes);
        string resultSha = WorkbenchText.Sha256(resultBytes);
        if (template.ResultSha256 is not null && template.ResultSha256 != resultSha)
            throw new ReportException($"The result does not match the template: the template names SHA-256 {template.ResultSha256} and the result is {resultSha}. Nothing was written.");
        ReportTemplate.CheckDuplicates(resultBytes, "result");
        JsonElement result;
        try { result = JsonDocument.Parse(resultBytes).RootElement.Clone(); }
        catch (JsonException ex) { throw new ReportException("The result is not valid JSON: " + ex.Message); }

        var sections = new List<ReportOutSection>();
        int plotNo = 0;
        foreach (ReportSection sec in template.Sections)
        {
            var blocks = new List<ReportOut>();
            foreach (ReportBlock b in sec.Blocks)
            {
                switch (b)
                {
                    case ReportTextBlock t: blocks.Add(new ReportOutText(t.Text)); break;
                    case ReportValueBlock v:
                        {
                            bool found = TryResolve(result, v.Path, out JsonElement e);
                            string text = found ? Cell(e, v.Decimals) : GapText;
                            blocks.Add(new ReportOutValue(v.Label, text, !found || e.ValueKind == JsonValueKind.Null));
                            break;
                        }
                    case ReportTableBlock tb: blocks.Add(Table(result, tb)); break;
                    case ReportPlotBlock pb: blocks.Add(Plot(result, pb, "plot-" + (++plotNo).ToString(CultureInfo.InvariantCulture))); break;
                }
            }
            sections.Add(new ReportOutSection(sec.Heading, blocks));
        }
        return new ReportDocument(template.Title, sections,
            new ReportProvenance(WorkbenchText.Sha256(templateBytes), resultSha, Generator, productVersion));
    }

    // ------------------------------------------------------------------ bindings

    /// <summary>Resolve an RFC 6901 pointer; false when any step is absent.</summary>
    internal static bool TryResolve(JsonElement root, string pointer, out JsonElement found)
    {
        found = root;
        if (pointer.Length == 0) return true;
        foreach (string raw in pointer[1..].Split('/'))
        {
            string key = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (found.ValueKind == JsonValueKind.Object)
            {
                if (!found.TryGetProperty(key, out found)) return false;
            }
            else if (found.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int i) || i >= found.GetArrayLength()) return false;
                found = found[i];
            }
            else return false;
        }
        return true;
    }

    private static string Cell(JsonElement e, int? decimals)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined: return GapText;
            case JsonValueKind.String: return e.GetString()!;
            case JsonValueKind.True: return "yes";
            case JsonValueKind.False: return "no";
            case JsonValueKind.Number:
                {
                    double d = e.GetDouble();
                    if (!double.IsFinite(d)) return GapText;
                    if (decimals is int n) return WorkbenchText.Show(d, n);
                    if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return WorkbenchText.Show(d, 0);
                    return WorkbenchText.Show(d, 4);
                }
            default: return e.GetRawText();
        }
    }

    private static ReportOutTable Table(JsonElement result, ReportTableBlock tb)
    {
        if (!TryResolve(result, tb.Path, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return new ReportOutTable(tb.Title, [], [], $"No table data at {tb.Path}.");
        IReadOnlyList<ReportColumn> cols;
        if (tb.Columns is not null) cols = tb.Columns;
        else
        {
            var keys = new List<ReportColumn>();
            foreach (JsonElement row in arr.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty p in row.EnumerateObject().Where(p => keys.All(k => k.Key != p.Name)))
                        keys.Add(new ReportColumn(p.Name, p.Name, "", null));
                break;
            }
            cols = keys;
        }
        var headings = cols.Select(c => c.Unit.Length > 0 ? $"{c.Title} ({c.Unit})" : c.Title).ToList();
        var rows = new List<IReadOnlyList<string>>();
        foreach (JsonElement row in arr.EnumerateArray())
        {
            var cells = new List<string>(cols.Count);
            foreach (ReportColumn c in cols)
                cells.Add(row.ValueKind == JsonValueKind.Object && row.TryGetProperty(c.Key, out JsonElement e) ? Cell(e, c.Decimals) : GapText);
            rows.Add(cells);
        }
        return new ReportOutTable(tb.Title, headings, rows, null);
    }

    private static ReportOutPlot Plot(JsonElement result, ReportPlotBlock pb, string id)
    {
        if (!TryResolve(result, pb.XPath, out JsonElement xe) || xe.ValueKind != JsonValueKind.Array)
            return new ReportOutPlot(pb.Title, null, $"No plot data at {pb.XPath}.");
        if (!TryResolve(result, pb.YPath, out JsonElement ye) || ye.ValueKind != JsonValueKind.Array)
            return new ReportOutPlot(pb.Title, null, $"No plot data at {pb.YPath}.");
        if (xe.GetArrayLength() != ye.GetArrayLength())
            return new ReportOutPlot(pb.Title, null, "The x and y arrays differ in length.");
        var x = new List<double>();
        var y = new List<double>();
        foreach (JsonElement e in xe.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number) return new ReportOutPlot(pb.Title, null, "An x value is missing, so the point cannot be placed.");
            x.Add(e.GetDouble());
        }
        foreach (JsonElement e in ye.EnumerateArray()) y.Add(e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.NaN);
        try
        {
            PlotSpec spec = pb.Kind switch
            {
                "spectrum" => PlotBuilders.Spectrum(id, pb.Title, x, y, pb.YLabel.Length > 0 ? pb.YLabel : "Amplitude"),
                "eigen" => PlotBuilders.EigenvaluePlane(id, pb.Title, x, y),
                _ => PlotBuilders.Lines(id, pb.Title, pb.XLabel, pb.YLabel, [new PlotSeriesData(pb.Title, x, y)], pb.LogX, pb.LogY),
            };
            return new ReportOutPlot(pb.Title, spec, null);
        }
        catch (ArgumentException ex) { return new ReportOutPlot(pb.Title, null, ex.Message); }
    }

    // ------------------------------------------------------------------ Markdown

    public static string ToMarkdown(ReportDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var sb = new StringBuilder();
        sb.Append("# ").Append(MdLine(doc.Title)).Append("\n\n");
        foreach (ReportOutSection s in doc.Sections)
        {
            sb.Append("## ").Append(MdLine(s.Heading)).Append("\n\n");
            bool prevValue = false;
            foreach (ReportOut b in s.Blocks)
            {
                if (prevValue && b is not ReportOutValue) sb.Append('\n');
                prevValue = b is ReportOutValue;
                switch (b)
                {
                    case ReportOutText t: sb.Append(t.Text).Append("\n\n"); break;
                    case ReportOutValue v: sb.Append("- **").Append(MdLine(v.Label)).Append(":** ").Append(MdLine(v.Value)).Append('\n'); break;
                    case ReportOutTable t: MdTable(sb, t); break;
                    case ReportOutPlot p:
                        sb.Append("*Plot: ").Append(MdLine(p.Title)).Append(" - ");
                        if (p.Plot is null) sb.Append("not drawn: ").Append(MdLine(p.GapNote ?? "no data"));
                        else sb.Append("plot SHA-256 ").Append(p.Plot.Sha256()).Append("; drawn in the HTML report");
                        sb.Append("*\n\n");
                        break;
                }
            }
            if (s.Blocks.Count > 0 && s.Blocks[^1] is ReportOutValue) sb.Append('\n');
        }
        sb.Append("---\n\n");
        sb.Append("Template SHA-256: ").Append(doc.Provenance.TemplateSha256).Append("  \n");
        sb.Append("Result SHA-256: ").Append(doc.Provenance.ResultSha256).Append("  \n");
        sb.Append("Generator: ").Append(doc.Provenance.Generator);
        if (doc.Provenance.ProductVersion is not null) sb.Append(" (GridSim ").Append(doc.Provenance.ProductVersion).Append(')');
        sb.Append('\n');
        return sb.ToString();
    }

    private static void MdTable(StringBuilder sb, ReportOutTable t)
    {
        sb.Append("**").Append(MdLine(t.Title)).Append("**\n\n");
        if (t.GapNote is not null) { sb.Append('*').Append(MdLine(t.GapNote)).Append("*\n\n"); return; }
        if (t.Headings.Count == 0) { sb.Append("*No columns.*\n\n"); return; }
        sb.Append("| ").AppendJoin(" | ", t.Headings.Select(MdLine)).Append(" |\n");
        sb.Append("| ").AppendJoin(" | ", t.Headings.Select(_ => "---")).Append(" |\n");
        foreach (IReadOnlyList<string> r in t.Rows) sb.Append("| ").AppendJoin(" | ", r.Select(MdLine)).Append(" |\n");
        sb.Append('\n');
    }

    private static string MdLine(string s) => s.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

    // ------------------------------------------------------------------ HTML

    public static string ToHtml(ReportDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en-GB\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(E(doc.Title)).Append("</title>\n<style>\n");
        sb.Append("body{font-family:sans-serif;margin:2rem auto;max-width:60rem;padding:0 1rem;color:#222;background:#fff}\n");
        sb.Append("table{border-collapse:collapse;margin:0.5rem 0 1.5rem}th,td{border:1px solid #bbb;padding:0.2rem 0.5rem;text-align:right}th{background:#f0f0f0}td:first-child,th:first-child{text-align:left}\n");
        sb.Append(".gap{color:#b00020;font-style:italic}footer{margin-top:2rem;border-top:1px solid #bbb;padding-top:0.5rem;font-size:0.85rem;color:#555}code{word-break:break-all}\n");
        sb.Append("</style>\n</head>\n<body>\n<h1>").Append(E(doc.Title)).Append("</h1>\n");
        foreach (ReportOutSection s in doc.Sections)
        {
            sb.Append("<section>\n<h2>").Append(E(s.Heading)).Append("</h2>\n");
            bool inList = false;
            foreach (ReportOut b in s.Blocks)
            {
                if (b is not ReportOutValue && inList) { sb.Append("</ul>\n"); inList = false; }
                switch (b)
                {
                    case ReportOutText t: sb.Append("<p>").Append(E(t.Text)).Append("</p>\n"); break;
                    case ReportOutValue v:
                        if (!inList) { sb.Append("<ul>\n"); inList = true; }
                        sb.Append("<li><strong>").Append(E(v.Label)).Append(":</strong> ").Append(v.Gap ? "<span class=\"gap\">" : "<span>").Append(E(v.Value)).Append("</span></li>\n");
                        break;
                    case ReportOutTable t:
                        sb.Append("<h3>").Append(E(t.Title)).Append("</h3>\n");
                        if (t.GapNote is not null) { sb.Append("<p class=\"gap\">").Append(E(t.GapNote)).Append("</p>\n"); break; }
                        sb.Append("<table>\n<thead><tr>");
                        foreach (string h in t.Headings) sb.Append("<th>").Append(E(h)).Append("</th>");
                        sb.Append("</tr></thead>\n<tbody>\n");
                        foreach (IReadOnlyList<string> r in t.Rows)
                        {
                            sb.Append("<tr>");
                            foreach (string c in r) sb.Append(c == GapText ? "<td class=\"gap\">" : "<td>").Append(E(c)).Append("</td>");
                            sb.Append("</tr>\n");
                        }
                        sb.Append("</tbody>\n</table>\n");
                        break;
                    case ReportOutPlot p:
                        sb.Append("<h3>").Append(E(p.Title)).Append("</h3>\n");
                        if (p.Plot is null) sb.Append("<p class=\"gap\">Not drawn: ").Append(E(p.GapNote ?? "no data")).Append("</p>\n");
                        else sb.Append("<figure>").Append(PlotSvg.Render(p.Plot)).Append("<figcaption>Plot SHA-256 <code>").Append(p.Plot.Sha256()).Append("</code></figcaption></figure>\n");
                        break;
                }
            }
            if (inList) sb.Append("</ul>\n");
            sb.Append("</section>\n");
        }
        sb.Append("<footer>\n<div>Template SHA-256: <code>").Append(doc.Provenance.TemplateSha256).Append("</code></div>\n");
        sb.Append("<div>Result SHA-256: <code>").Append(doc.Provenance.ResultSha256).Append("</code></div>\n");
        sb.Append("<div>Generator: ").Append(E(doc.Provenance.Generator));
        if (doc.Provenance.ProductVersion is not null) sb.Append(" (GridSim ").Append(E(doc.Provenance.ProductVersion)).Append(')');
        sb.Append("</div>\n</footer>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ CSV

    /// <summary>
    /// Every table of the report as CSV (RFC 4180 quoting, LF line ends): a "# title" line, the heading row, the rows, and a blank
    /// line between tables. A gap is an empty field. Values and plots are not tables and are left out; the provenance is the last
    /// comment lines.
    /// </summary>
    public static string ToCsv(ReportDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var sb = new StringBuilder();
        foreach (ReportOutSection s in doc.Sections)
            foreach (ReportOutTable t in s.Blocks.OfType<ReportOutTable>())
            {
                sb.Append("# ").Append(CsvComment(s.Heading == t.Title ? t.Title : s.Heading + ": " + t.Title)).Append('\n');
                if (t.GapNote is not null) { sb.Append("# no data: ").Append(CsvComment(t.GapNote)).Append("\n\n"); continue; }
                sb.AppendJoin(',', t.Headings.Select(Csv)).Append('\n');
                foreach (IReadOnlyList<string> r in t.Rows) sb.AppendJoin(',', r.Select(c => c == GapText ? "" : Csv(c))).Append('\n');
                sb.Append('\n');
            }
        sb.Append("# template-sha256 ").Append(doc.Provenance.TemplateSha256).Append('\n');
        sb.Append("# result-sha256 ").Append(doc.Provenance.ResultSha256).Append('\n');
        sb.Append("# generator ").Append(doc.Provenance.Generator).Append('\n');
        return sb.ToString();
    }

    private static string CsvComment(string s) => s.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Csv(string s) =>
        s.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
}
