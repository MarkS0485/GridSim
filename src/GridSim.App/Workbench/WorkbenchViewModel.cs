using CommunityToolkit.Mvvm.ComponentModel;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Presentation.Workbench.Sld;

namespace GridSim.App.Workbench;

/// <summary>One line of the flattened project tree: the node, its depth and its display text.</summary>
public sealed record WorkbenchTreeRow(WorkbenchNode Node, int Depth, string Text);

/// <summary>The report formats the workbench offers.</summary>
public enum WorkbenchReportFormat { Markdown, Html, Csv }

/// <summary>
/// The view model of the Avalonia workbench window (R4-10). It holds one shared <see cref="WorkbenchProject"/> and shows it:
/// the project tree, the model table (sort, filter, group), a study's result with its plot, and the report. It computes
/// nothing itself: the tables, plots and reports are Core's (<see cref="ModelTable"/>, <see cref="PlotBuilders"/>,
/// <see cref="ReportRenderer"/>) and the studies are Core's existing entry points (<see cref="WorkbenchStudies"/>), so the
/// numbers and geometry are those the web client shows.
/// </summary>
public sealed class WorkbenchViewModel : ObservableObject
{
    private WorkbenchProject? _project;
    private string? _sourcePath;
    private long _projectRevision = -1;
    private bool _loadingDiagram;

    public WorkbenchViewModel()
    {
        Diagram.DocumentChanged += OnDiagramDocumentChanged;
    }
    private bool _busy;

    /// <summary>True while a study is running off the UI thread.</summary>
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    /// <summary>R4-18: the single-line diagram pane's view model (the diagram of the loaded project, editable when its source is a JSON case folder or a node-breaker file).</summary>
    public SldViewModel Diagram { get; } = new();

    /// <summary>The project, or null before a model is loaded.</summary>
    public WorkbenchProject? Project => _project;

    private string _statusText = "No model loaded. Open a case: MATPOWER, PSS/E RAW, UCTE, CGMES, a GridSim case folder or v2 case.";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    private string _selectedNodeId = "";
    public string SelectedNodeId { get => _selectedNodeId; set => SetProperty(ref _selectedNodeId, value); }
    private ModelElementKind _tableKind = ModelElementKind.Bus;
    public ModelElementKind TableKind { get => _tableKind; set => SetProperty(ref _tableKind, value); }
    private string? _sortKey;
    public string? SortKey { get => _sortKey; set => SetProperty(ref _sortKey, value); }
    private bool _sortDescending;
    public bool SortDescending { get => _sortDescending; set => SetProperty(ref _sortDescending, value); }
    private string? _filterColumn;
    public string? FilterColumn { get => _filterColumn; set => SetProperty(ref _filterColumn, value); }
    private ModelFilterOp _filterOp = ModelFilterOp.Contains;
    public ModelFilterOp FilterOp { get => _filterOp; set => SetProperty(ref _filterOp, value); }
    private string _filterValue = "";
    public string FilterValue { get => _filterValue; set => SetProperty(ref _filterValue, value); }
    private GroupKind? _groupBy;
    public GroupKind? GroupBy { get => _groupBy; set => SetProperty(ref _groupBy, value); }
    private string? _selectedStudyId;
    public string? SelectedStudyId { get => _selectedStudyId; set => SetProperty(ref _selectedStudyId, value); }
    private WorkbenchReportFormat _reportFormat = WorkbenchReportFormat.Markdown;
    public WorkbenchReportFormat ReportFormat { get => _reportFormat; set => SetProperty(ref _reportFormat, value); }

    /// <summary>The model table for the current kind, filter, sort and grouping (null before a model is loaded).</summary>
    public ModelTableView? Table { get; private set; }

    /// <summary>The model table as monospace lines (header, rule, rows).</summary>
    public IReadOnlyList<string> TableLines { get; private set; } = [];

    /// <summary>The flattened project tree, in display order.</summary>
    public IReadOnlyList<WorkbenchTreeRow> TreeRows { get; private set; } = [];

    /// <summary>The result of the selected study, if it has been run.</summary>
    public WorkbenchResult? Result { get; private set; }

    /// <summary>The selected study's result as monospace text: status, each table.</summary>
    public IReadOnlyList<string> ResultLines { get; private set; } = [];

    /// <summary>The ids of the plots of the selected result.</summary>
    public IReadOnlyList<string> PlotIds { get; private set; } = [];

    /// <summary>The plot shown (the first of the result unless <see cref="SelectPlot"/> chose another), or null.</summary>
    public PlotSpec? Plot { get; private set; }

    /// <summary>The last generated report text (empty before one is generated).</summary>
    public string ReportText { get; private set; } = "";

    /// <summary>The columns of the current table kind (for the sort and filter pickers).</summary>
    public IReadOnlyList<ModelColumn> Columns => ModelTable.Columns(TableKind);

    /// <summary>Raised when the shown content (tree, table, result, plot or report) was rebuilt.</summary>
    public event EventHandler? Refreshed;

    // ------------------------------------------------------------------ loading

    /// <summary>Use a project (the default studies on <paramref name="model"/>).</summary>
    public void SetProject(string name, GridModel model, BoundGroups? groups = null)
    {
        SetProject(WorkbenchProject.WithDefaultStudies(name, model, groups));
    }

    public void SetProject(WorkbenchProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        Result = null;
        ResultLines = [];
        PlotIds = [];
        Plot = null;
        ReportText = "";
        SelectedStudyId = project.Studies.Count > 0 ? project.Studies[0].Id : null;
        StatusText = $"{project.Name}: {project.Model.Buses.Count} buses, {project.Model.Branches.Count} branches, {project.Model.AllGenerators.Count} units.";
        LoadDiagram(project);
        RebuildAll();
    }

    /// <summary>R4-18: give the diagram pane the project's source: an editable document when the project came from a JSON case folder or a node-breaker file, else the model read-only.</summary>
    private void LoadDiagram(WorkbenchProject project)
    {
        string? source = _sourcePath;
        _sourcePath = null;
        bool opened = false;
        _loadingDiagram = true;
        try
        {
            if (source is not null && Directory.Exists(source) && File.Exists(Path.Combine(source, "buses.json"))) opened = Diagram.TryOpenCase(source);
            else if (source is not null && File.Exists(source) && source.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) opened = Diagram.TryOpenSubstations(source);
            if (!opened) Diagram.LoadReadOnly(project.Model);
        }
        finally { _loadingDiagram = false; }
        _projectRevision = Diagram.Document?.Revision ?? -1;
    }

    /// <summary>
    /// A3: the diagram's document changed (an edit, undo, redo or save). Bring the project's model in line with it, so the tables,
    /// the studies, the reports and the diagram all describe the same network. The studies stay; their results, which were of the
    /// old network, are dropped, and the status line says so.
    /// </summary>
    private void OnDiagramDocumentChanged(object? sender, EventArgs e)
    {
        if (_loadingDiagram || _project is null || Diagram.Document is not { } doc || doc.Revision == _projectRevision) return;
        GridModel? model;
        try
        {
            model = doc switch
            {
                SldCaseDocument c => c.LoadStudyModel() ?? throw new InvalidOperationException("The edited case no longer loads."),
                SldSubstationDocument s => SubstationProjection.ProjectNormal(s.Substations).Model,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or SubstationTopologyException or ArgumentException)
        {
            StatusText = "The tables and studies still show the previous network: the edited model could not be rebuilt: " + ex.Message;
            Refreshed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _projectRevision = doc.Revision;
        if (model is null || ReferenceEquals(model, _project.Model)) return;
        var next = new WorkbenchProject(_project.Name, model, _project.Groups);
        foreach (WorkbenchStudyDefinition def in _project.Studies) next.AddStudy(def);
        bool hadResults = _project.Results.Count > 0;
        _project = next;
        Result = null;
        ResultLines = [];
        PlotIds = [];
        Plot = null;
        ReportText = "";
        RebuildAll();
        StatusText = $"{next.Name}: model rebuilt from the diagram edit ({model.Buses.Count} buses, {model.Branches.Count} branches, {model.AllGenerators.Count} units)"
            + (hadResults ? "; earlier study results were of the previous network and were cleared." : ".");
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Load a MATPOWER file or a GridSim case folder. Returns false (and says why in <see cref="StatusText"/>) on failure.</summary>
    public bool TryLoad(string path, LicenceGate? gate = null)
    {
        try
        {
            string full = Path.GetFullPath(path);
            // A4: every format the main app and the CLI read, through their readers (WorkbenchCaseReader).
            // A1: the same licence check as the main app (SimHost.LoadLive) and the CLI (Gated): the bus cap, with the bundled test
            // made before and after the read (bug #178). The node-breaker route is checked on its projected model.
            bool bundledBefore = BundledNetworks.IsBundledV2Case(full);
            WorkbenchCaseRead? read = WorkbenchCaseReader.Read(full);
            if (read is null) { StatusText = $"Cannot open '{path}': give {WorkbenchCaseReader.Supported}."; return false; }
            GridModel model = read.Model;
            string name = read.Name;
            _sourcePath = read.EditableSource;
            GateResult lic = (gate ?? LicenceGate.Resolve()).Check(model.BusCount, bundledBefore && BundledNetworks.IsBundledV2Case(full));
            if (!lic.Allowed) { _sourcePath = null; StatusText = lic.Message; return false; }
            SetProject(name, model);
            if (read.Notes.Count > 0) StatusText += $" {read.Notes.Count} reader note(s): {read.Notes[0]}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException or FormatException or InvalidOperationException or SubstationTopologyException or System.Xml.XmlException or NotSupportedException)
        {
            _sourcePath = null;
            StatusText = "Could not load the case: " + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ selection and actions

    /// <summary>Select a tree node by id: a model node shows its table, a study node selects the study (and shows its result if run).</summary>
    public void SelectNode(string id)
    {
        SelectedNodeId = id;
        if (id.StartsWith("model/", StringComparison.Ordinal) && Enum.TryParse(id[6..], true, out ModelElementKind k))
        {
            TableKind = k;
            SortKey = null;
            FilterColumn = null;
            RebuildTable();
        }
        else if (id.StartsWith("study/", StringComparison.Ordinal) || id.StartsWith("report/", StringComparison.Ordinal))
        {
            SelectedStudyId = id[(id.IndexOf('/') + 1)..];
            ShowResult();
        }
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Apply the current sort, filter and grouping to the table.</summary>
    public void RebuildTable()
    {
        if (_project is null) { Table = null; TableLines = []; return; }
        var filters = new List<ModelFilter>();
        if (FilterColumn is not null && FilterValue.Length > 0) filters.Add(new ModelFilter(FilterColumn, FilterOp, FilterValue));
        try
        {
            Table = _project.Table(new ModelTableQuery(TableKind, filters, SortKey, SortDescending, GroupBy));
            TableLines = WorkbenchTextTables.Lines(Table);
            StatusText = $"{TableKind}: {Table.ShownRows} of {Table.TotalRows} shown.";
        }
        catch (ArgumentException ex)
        {
            StatusText = "Filter not applied: " + ex.Message;
        }
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Run the selected study (or the one given) on the calling thread, keep its result and show it. A failure is said in <see cref="StatusText"/>; see <see cref="RunSelectedAsync"/> for the form the window uses.</summary>
    public WorkbenchResult? RunSelected(string? studyId = null)
    {
        if (!TryStart(studyId, out WorkbenchProject? project, out string id)) return null;
        try
        {
            WorkbenchResult r = project!.Run(id);
            return Finish(r);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the status line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Failed(id, ex);
        }
    }

    /// <summary>
    /// Run the selected study (or the one given) on a worker thread and keep and show its result back on the calling (UI) context.
    /// Refused while another study is running; a result for a project that was replaced meanwhile (a diagram edit, a new load) is
    /// dropped and said. Any failure is said in <see cref="StatusText"/>, never thrown to the crash handler.
    /// </summary>
    public async Task<WorkbenchResult?> RunSelectedAsync(string? studyId = null)
    {
        if (IsBusy) { StatusText = "A study is already running."; Refreshed?.Invoke(this, EventArgs.Empty); return null; }
        if (!TryStart(studyId, out WorkbenchProject? project, out string id)) return null;
        IsBusy = true;
        StatusText = $"Running {id}...";
        Refreshed?.Invoke(this, EventArgs.Empty);
        try
        {
            // ForceYielding: the call always returns to the caller (the UI thread) before the run completes, even when
            // the study finishes before the await is reached; a plain await would then run on and return a completed task.
            WorkbenchResult r = await Task.Run(() => project!.Evaluate(id))
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
            if (!ReferenceEquals(project, _project))
            {
                StatusText = $"{id}: result dropped, the model changed while it ran.";
                Refreshed?.Invoke(this, EventArgs.Empty);
                return null;
            }
            project!.Keep(r);
            return Finish(r);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the status line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Failed(id, ex);
        }
        finally { IsBusy = false; }
    }

    private bool TryStart(string? studyId, out WorkbenchProject? project, out string id)
    {
        project = _project;
        id = studyId ?? SelectedStudyId ?? "";
        if (project is null) { StatusText = "Load a model first."; return false; }
        if (id.Length == 0) { StatusText = "Select a study first."; return false; }
        SelectedStudyId = id;
        return true;
    }

    private WorkbenchResult Finish(WorkbenchResult r)
    {
        StatusText = $"{r.StudyId}: {r.Status}" + (r.Reason is null ? "" : " - " + r.Reason);
        ShowResult();
        RebuildTree();
        Refreshed?.Invoke(this, EventArgs.Empty);
        return r;
    }

    private WorkbenchResult? Failed(string id, Exception ex)
    {
        StatusText = $"{id} failed: {ex.GetType().Name}: {ex.Message}";
        Refreshed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Show a different plot of the selected result.</summary>
    public void SelectPlot(string plotId)
    {
        Plot = Result?.Plots.FirstOrDefault(p => p.Id == plotId)?.Spec;
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Generate the report for the selected study's result with the default template, in the chosen format.</summary>
    public string? GenerateReport()
    {
        if (_project is null || SelectedStudyId is null || !_project.Results.ContainsKey(SelectedStudyId))
        {
            StatusText = "Run the study first: a report is made from its result.";
            return null;
        }
        byte[] result = System.Text.Encoding.UTF8.GetBytes(_project.ResultJson(SelectedStudyId));
        byte[] template = System.Text.Encoding.UTF8.GetBytes(ReportTemplate.DefaultJson(result));
        ReportDocument doc = ReportRenderer.Build(template, result);
        ReportText = ReportFormat switch
        {
            WorkbenchReportFormat.Html => ReportRenderer.ToHtml(doc),
            WorkbenchReportFormat.Csv => ReportRenderer.ToCsv(doc),
            _ => ReportRenderer.ToMarkdown(doc),
        };
        StatusText = $"Report for {SelectedStudyId} ({ReportFormat}): {ReportText.Length} characters.";
        Refreshed?.Invoke(this, EventArgs.Empty);
        return ReportText;
    }

    /// <summary>Write the last report to a new file (an existing path is refused).</summary>
    public bool SaveReport(string path)
    {
        if (ReportText.Length == 0) { StatusText = "Generate a report first."; return false; }
        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full) || Directory.Exists(full)) { StatusText = "Not saved: that path already exists."; return false; }
            File.WriteAllText(full, ReportText, new System.Text.UTF8Encoding(false));
            StatusText = "Report saved to " + full;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = "Not saved: " + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ rebuilding

    private void RebuildAll()
    {
        RebuildTree();
        RebuildTable();
    }

    private void RebuildTree()
    {
        if (_project is null) { TreeRows = []; return; }
        var rows = new List<WorkbenchTreeRow>();
        void Walk(WorkbenchNode n, int depth)
        {
            string text = n.Detail is null ? n.Title : $"{n.Title}  ({n.Detail})";
            rows.Add(new WorkbenchTreeRow(n, depth, new string(' ', depth * 2) + text));
            foreach (WorkbenchNode c in n.Children) Walk(c, depth + 1);
        }
        Walk(_project.Tree(), 0);
        TreeRows = rows;
    }

    private void ShowResult()
    {
        Result = null;
        ResultLines = [];
        PlotIds = [];
        Plot = null;
        if (_project is not null && SelectedStudyId is not null && _project.Results.TryGetValue(SelectedStudyId, out WorkbenchResult? r))
        {
            Result = r;
            var lines = new List<string> { $"Study {r.StudyId} ({r.Kind})", $"Status:  {r.Status}", $"Outcome: {r.Outcome}" };
            if (r.Reason is not null) lines.Add("Reason:  " + r.Reason);
            foreach (WorkbenchTable t in r.Tables)
            {
                lines.Add("");
                lines.Add(t.Title);
                lines.AddRange(WorkbenchTextTables.Lines(t));
            }
            ResultLines = lines;
            PlotIds = r.Plots.Select(p => p.Id).ToList();
            Plot = r.Plots.Count > 0 ? r.Plots[0].Spec : null;
        }
    }
}
