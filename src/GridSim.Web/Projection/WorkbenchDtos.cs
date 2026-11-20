using GridSim.Core.Presentation.Workbench;

namespace GridSim.Web.Projection;

/// <summary>
/// R4-10: the web client's view of the Core workbench layer (<c>GridSim.Core.Presentation.Workbench</c>). These are plain
/// projections: every cell, total and coordinate is copied from Core, so the web page and the Avalonia view show the same
/// values (the parity tests compare both to the Core result). Nothing here computes.
/// </summary>
public sealed record WorkbenchCellDto(string Text, double? Number, bool Missing);

public sealed record WorkbenchColumnDto(string Key, string Title, string Unit, string Type, bool Summable);

public sealed record WorkbenchRowDto(int Index, string Label, IReadOnlyList<WorkbenchCellDto> Cells, IReadOnlyList<string> Groups);

public sealed record WorkbenchSectionDto(string? GroupId, string Title, IReadOnlyList<WorkbenchRowDto> Rows, IReadOnlyList<double?> Totals);

/// <summary>A model table: columns, sections of rows, and the counts.</summary>
public sealed record ModelTableDto(string Kind, IReadOnlyList<WorkbenchColumnDto> Columns, IReadOnlyList<WorkbenchSectionDto> Sections, int TotalRows, int ShownRows)
{
    public static ModelTableDto From(ModelTableView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new ModelTableDto(v.Kind.ToString(),
            v.Columns.Select(c => new WorkbenchColumnDto(c.Key, c.Title, c.Unit, c.Type.ToString(), c.Summable)).ToList(),
            v.Sections.Select(s => new WorkbenchSectionDto(s.GroupId, s.Title,
                s.Rows.Select(r => new WorkbenchRowDto(r.Index, r.Label, r.Cells.Select(c => new WorkbenchCellDto(c.Text, c.Number, c.Missing)).ToList(), r.Groups)).ToList(),
                s.Totals)).ToList(),
            v.TotalRows, v.ShownRows);
    }

    /// <summary>The cell texts of every row in order (what the page prints), for comparing two clients.</summary>
    public IEnumerable<IReadOnlyList<string>> CellTexts() => Sections.SelectMany(s => s.Rows).Select(r => (IReadOnlyList<string>)r.Cells.Select(c => c.Text).ToList());
}

public sealed record PlotPointDto(double DataX, double DataY, double Px, double Py);
public sealed record PlotTickDto(double Value, double Position, string Label);
public sealed record PlotAxisDto(string Label, bool Log, double Min, double Max, IReadOnlyList<PlotTickDto> Ticks);
public sealed record PlotPolylineDto(string Series, int Style, bool Closed, IReadOnlyList<PlotPointDto> Points);
public sealed record PlotMarkerDto(string Series, string Shape, double DataX, double DataY, double Px, double Py, string? Label);
public sealed record PlotBarDto(string Series, double DataX, double DataY, double Px, double Py, double Width, double Height);
public sealed record PlotGapDto(string Series, double DataX, double Px, string Reason);
public sealed record PlotPanelDto(double Left, double Top, double Width, double Height, PlotAxisDto X, PlotAxisDto Y,
    IReadOnlyList<PlotPolylineDto> Polylines, IReadOnlyList<PlotMarkerDto> Markers, IReadOnlyList<PlotBarDto> Bars, IReadOnlyList<PlotGapDto> Gaps);

/// <summary>A plot: the geometry (panels, axes, ticks, polylines, markers, bars, gaps), its canonical-text SHA-256 and the SVG the page shows.</summary>
public sealed record PlotDto(string Id, string Title, double Width, double Height, string Sha256, IReadOnlyList<PlotPanelDto> Panels, string Svg)
{
    public static PlotDto From(PlotSpec s)
    {
        ArgumentNullException.ThrowIfNull(s);
        static PlotAxisDto Axis(PlotAxis a) => new(a.Label, a.Log, a.Min, a.Max, a.Ticks.Select(t => new PlotTickDto(t.Value, t.Position, t.Label)).ToList());
        return new PlotDto(s.Id, s.Title, s.Width, s.Height, s.Sha256(),
            s.Panels.Select(p => new PlotPanelDto(p.Left, p.Top, p.Width, p.Height, Axis(p.X), Axis(p.Y),
                p.Items.OfType<PlotPolyline>().Select(l => new PlotPolylineDto(l.Series, l.Style, l.Closed, l.Points.Select(q => new PlotPointDto(q.DataX, q.DataY, q.Px, q.Py)).ToList())).ToList(),
                p.Items.OfType<PlotMarker>().Select(m => new PlotMarkerDto(m.Series, m.Shape.ToString(), m.DataX, m.DataY, m.Px, m.Py, m.Label)).ToList(),
                p.Items.OfType<PlotBar>().Select(b => new PlotBarDto(b.Series, b.DataX, b.DataY, b.Px, b.Py, b.Width, b.Height)).ToList(),
                p.Items.OfType<PlotGap>().Select(g => new PlotGapDto(g.Series, g.DataX, g.Px, g.Reason)).ToList())).ToList(),
            PlotSvg.Render(s));
    }
}

public sealed record WorkbenchResultTableDto(string Id, string Title, IReadOnlyList<WorkbenchColumnDto> Columns, IReadOnlyList<IReadOnlyList<WorkbenchCellDto>> Rows);

/// <summary>A study result: status, outcome, reason, its tables and plots.</summary>
public sealed record WorkbenchResultDto(string StudyId, string Kind, string Status, string Outcome, string? Reason,
    IReadOnlyList<WorkbenchResultTableDto> Tables, IReadOnlyList<PlotDto> Plots)
{
    public static WorkbenchResultDto From(WorkbenchResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new WorkbenchResultDto(r.StudyId, r.Kind, r.Status, r.Outcome, r.Reason,
            r.Tables.Select(t => new WorkbenchResultTableDto(t.Id, t.Title,
                t.Columns.Select(c => new WorkbenchColumnDto(c.Key, c.Title, c.Unit, c.Type.ToString(), c.Summable)).ToList(),
                t.Rows.Select(row => (IReadOnlyList<WorkbenchCellDto>)row.Select(c => new WorkbenchCellDto(c.Text, c.Number, c.Missing)).ToList()).ToList())).ToList(),
            r.Plots.Select(p => PlotDto.From(p.Spec)).ToList());
    }
}

/// <summary>One node of the project tree.</summary>
public sealed record WorkbenchNodeDto(string Id, string Title, string Kind, string? Detail, IReadOnlyList<WorkbenchNodeDto> Children)
{
    public static WorkbenchNodeDto From(WorkbenchNode n) =>
        new(n.Id, n.Title, n.Kind, n.Detail, n.Children.Select(From).ToList());
}
