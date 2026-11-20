using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>A node of the project tree: an id (stable, used to select it), a title, a kind (project, folder, model, group,
/// study, result, report) and an optional detail text (a count, or a study's status).</summary>
public sealed record WorkbenchNode(string Id, string Title, string Kind, string? Detail, IReadOnlyList<WorkbenchNode> Children);

/// <summary>
/// The workbench's shared project state (R4-10): the loaded model, its R4-01 groups (optional), the studies defined on it and
/// the results of those run. Both clients (the Avalonia app and the web) hold one of these and show it: the tree, the model
/// tables, the study results and the report. It holds no study logic: <see cref="Run"/> calls
/// <see cref="WorkbenchStudies"/>, which calls the existing Core entry points.
/// </summary>
public sealed class WorkbenchProject
{
    private readonly List<WorkbenchStudyDefinition> _studies = [];
    private readonly Dictionary<string, WorkbenchResult> _results = new(StringComparer.Ordinal);

    public WorkbenchProject(string name, GridModel model, BoundGroups? groups = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(model);
        Name = name;
        Model = model;
        Groups = groups;
    }

    public string Name { get; }
    public GridModel Model { get; }
    public BoundGroups? Groups { get; }
    public IReadOnlyList<WorkbenchStudyDefinition> Studies => _studies;
    public IReadOnlyDictionary<string, WorkbenchResult> Results => _results;

    /// <summary>A project with the default studies: a power flow, the preventive N-1 security study, the group summary (when the
    /// project has groups) and the QV curve at the first load bus.</summary>
    public static WorkbenchProject WithDefaultStudies(string name, GridModel model, BoundGroups? groups = null)
    {
        var p = new WorkbenchProject(name, model, groups);
        p.AddStudy(WorkbenchStudyDefinition.Of("power-flow", WorkbenchStudies.PowerFlow));
        p.AddStudy(WorkbenchStudyDefinition.Of("n-1-security", WorkbenchStudies.TransmissionSecurity));
        if (groups is not null) p.AddStudy(WorkbenchStudyDefinition.Of("group-summary", WorkbenchStudies.GroupSummary));
        if (model.Buses.Any(b => b.Type == BusType.Pq)) p.AddStudy(WorkbenchStudyDefinition.Of("qv-curve", WorkbenchStudies.QvCurveKind));
        return p;
    }

    /// <summary>Add a study. Its id must be new; a duplicate is refused.</summary>
    public void AddStudy(WorkbenchStudyDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        if (string.IsNullOrWhiteSpace(def.Id)) throw new ArgumentException("A study needs an id.", nameof(def));
        if (_studies.Any(s => s.Id == def.Id)) throw new ArgumentException($"Study '{def.Id}' already exists.", nameof(def));
        _studies.Add(def);
    }

    /// <summary>Run a study by id and keep its result (replacing the earlier result of that study).</summary>
    public WorkbenchResult Run(string studyId)
    {
        WorkbenchResult r = Evaluate(studyId);
        Keep(r);
        return r;
    }

    /// <summary>Run a study by id and return its result without keeping it, so a client can compute off its UI thread and
    /// <see cref="Keep"/> the result back on it. The model, groups and definition are only read.</summary>
    public WorkbenchResult Evaluate(string studyId)
    {
        WorkbenchStudyDefinition def = _studies.FirstOrDefault(s => s.Id == studyId)
            ?? throw new ArgumentException($"No study '{studyId}' in the project.", nameof(studyId));
        return WorkbenchStudies.Run(Model, Groups, def);
    }

    /// <summary>Keep a result made by <see cref="Evaluate"/> (replacing the earlier result of that study).</summary>
    public void Keep(WorkbenchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _results[result.StudyId] = result;
    }

    /// <summary>The model table of a query on this project.</summary>
    public ModelTableView Table(ModelTableQuery query) => ModelTable.Build(Model, query, Groups);

    /// <summary>The project tree: model (with element counts), groups, studies (with their status when run) and reports.</summary>
    public WorkbenchNode Tree()
    {
        var model = new List<WorkbenchNode>();
        foreach (ModelElementKind k in Enum.GetValues<ModelElementKind>())
            model.Add(new WorkbenchNode("model/" + k.ToString().ToLowerInvariant(), k + "s", "model",
                WorkbenchText.Canon(ModelTable.Count(Model, k)), []));
        var children = new List<WorkbenchNode> { new("model", "Model", "folder", null, model) };
        if (Groups is not null)
            children.Add(new WorkbenchNode("groups", "Groups", "folder", WorkbenchText.Canon(Groups.Groups.Count),
                Groups.Groups.Select(g => new WorkbenchNode("group/" + g.Id, g.Id, "group", ModelGroup.KindName(g.Group.Kind), [])).ToList()));
        children.Add(new WorkbenchNode("studies", "Studies", "folder", WorkbenchText.Canon(_studies.Count),
            _studies.Select(s => new WorkbenchNode("study/" + s.Id, s.Id, "study",
                _results.TryGetValue(s.Id, out WorkbenchResult? r) ? r.Status : "not run", [])).ToList()));
        children.Add(new WorkbenchNode("reports", "Reports", "folder", null,
            _results.Keys.Select(id => new WorkbenchNode("report/" + id, "Report: " + id, "report", null, [])).ToList()));
        return new WorkbenchNode("project", Name, "project", null, children);
    }

    /// <summary>The result document (<c>gridsim-workbench-result/1</c>) of a study that has been run: the text a report binds to.</summary>
    public string ResultJson(string studyId)
    {
        WorkbenchStudyDefinition def = _studies.FirstOrDefault(s => s.Id == studyId) ?? throw new ArgumentException($"No study '{studyId}'.", nameof(studyId));
        if (!_results.TryGetValue(studyId, out WorkbenchResult? r)) throw new InvalidOperationException($"Study '{studyId}' has not been run.");
        return WorkbenchResultDocument.ToJson(Name, Model.Buses.Count, def, r);
    }
}

/// <summary>
/// The workbench result document, <c>gridsim-workbench-result/1</c>: a study's status, its tables (rows as objects keyed by
/// column key, a missing value as JSON null) and its plots (the data series and the plot's SHA-256). It is plain JSON written
/// in one fixed order with LF line ends, so the same result always gives the same bytes; a report binds to it with JSON
/// pointers, as it binds to any other result JSON.
/// </summary>
public static class WorkbenchResultDocument
{
    public const string Schema = "gridsim-workbench-result/1";

    public static string ToJson(string caseName, int buses, WorkbenchStudyDefinition def, WorkbenchResult r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteStartObject("study");
            w.WriteString("id", def.Id);
            w.WriteString("kind", def.Kind);
            w.WriteStartObject("parameters");
            foreach (KeyValuePair<string, string> p in def.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteString(p.Key, p.Value);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("case");
            w.WriteString("name", caseName);
            w.WriteNumber("buses", buses);
            w.WriteEndObject();
            w.WriteString("status", r.Status);
            w.WriteString("outcome", r.Outcome);
            if (r.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", r.Reason);
            w.WriteStartObject("tables");
            foreach (WorkbenchTable t in r.Tables)
            {
                w.WriteStartObject(t.Id);
                w.WriteString("title", t.Title);
                w.WriteStartArray("columns");
                foreach (ModelColumn c in t.Columns)
                {
                    w.WriteStartObject();
                    w.WriteString("key", c.Key);
                    w.WriteString("title", c.Title);
                    w.WriteString("unit", c.Unit);
                    w.WriteNumber("decimals", c.Decimals);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("rows");
                foreach (IReadOnlyList<ModelCell> row in t.Rows)
                {
                    w.WriteStartObject();
                    for (int i = 0; i < t.Columns.Count; i++)
                    {
                        ModelCell cell = row[i];
                        if (cell.Missing) w.WriteNull(t.Columns[i].Key);
                        else if (cell.Number is double d && t.Columns[i].Type != ModelColumnType.Text && double.IsFinite(d)) w.WriteNumber(t.Columns[i].Key, d);
                        else w.WriteString(t.Columns[i].Key, cell.Text);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("plots");
            foreach (WorkbenchPlot p in r.Plots)
            {
                w.WriteStartObject(p.Id);
                w.WriteString("title", p.Spec.Title);
                w.WriteString("xLabel", p.XLabel);
                w.WriteString("yLabel", p.YLabel);
                w.WriteString("sha256", p.Spec.Sha256());
                w.WriteStartArray("series");
                foreach (PlotSeriesData s in p.Series)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.Name);
                    w.WriteStartArray("x");
                    foreach (double v in s.X) Num(w, v);
                    w.WriteEndArray();
                    w.WriteStartArray("y");
                    foreach (double v in s.Y) Num(w, v);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetString(ms.ToArray()) + "\n";
    }

    private static void Num(Utf8JsonWriter w, double v)
    {
        if (double.IsFinite(v)) w.WriteNumberValue(v); else w.WriteNullValue();
    }
}
