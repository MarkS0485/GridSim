using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.Core.Analysis.StudyCases;
using GridSim.Core.Model;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Presentation.Workbench.Sld;

namespace GridSim.App.Workbench;

/// <summary>
/// The workbench window (RC4 R4-10): the project tree, the model table, the study runner with its result, the plot and the
/// report. All content comes from <see cref="WorkbenchViewModel"/>, which shows Core's workbench layer; the window only
/// lists it. Open it with <see cref="Open"/>.
/// </summary>
public sealed partial class WorkbenchWindow : Window
{
    private const string NoGroup = "(none)";
    private bool _updating;

    public WorkbenchViewModel ViewModel { get; }

    /// <summary>For the AXAML loader and the designer only.</summary>
    public WorkbenchWindow() : this(new WorkbenchViewModel()) { }

    public WorkbenchWindow(WorkbenchViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
        FilterOpPicker.ItemsSource = Enum.GetNames<ModelFilterOp>();
        FilterOpPicker.SelectedItem = ModelFilterOp.Contains.ToString();
        GroupPicker.ItemsSource = new[] { NoGroup }.Concat(Enum.GetNames<GroupKind>()).ToList();
        GroupPicker.SelectedIndex = 0;
        FormatPicker.ItemsSource = Enum.GetNames<WorkbenchReportFormat>();
        FormatPicker.SelectedItem = WorkbenchReportFormat.Markdown.ToString();
        PlotPicker.SelectionChanged += (_, _) =>
        {
            if (!_updating && PlotPicker.SelectedItem is string id) ViewModel.SelectPlot(id);
        };
        ColourPicker.ItemsSource = Enum.GetNames<SldColourMode>();
        ColourPicker.SelectedItem = viewModel.Diagram.ColourMode.ToString();
        ColourPicker.SelectionChanged += (_, _) =>
        {
            if (!_updating && Enum.TryParse(ColourPicker.SelectedItem as string, out SldColourMode cm)) ViewModel.Diagram.ColourMode = cm;
        };
        CaseLayoutBox.IsCheckedChanged += (_, _) =>
        {
            if (!_updating) ViewModel.Diagram.RequestedLayout = CaseLayoutBox.IsChecked == true ? SldLayoutMode.Case : SldLayoutMode.Auto;
        };
        DiagramView.ViewModel = ViewModel.Diagram;
        ViewModel.Diagram.PropertyChanged += (_, _) => ShowDiagramState();
        ViewModel.Refreshed += (_, _) => ShowState();
        ShowState();
    }

    /// <summary>The command id of File / Workbench... (A6).</summary>
    public const string CommandId = "file.workbench";

    /// <summary>File / Workbench...: opens the workbench on the model the main window has loaded (the same as Ctrl+Shift+B; the shortcut is the key handler's, so the window's key bindings are unchanged).</summary>
    public static GridSim.Ui.Commands.AppCommand Command(Func<MainWindow> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new GridSim.Ui.Commands.AppCommand
        {
            Id = CommandId,
            Title = "Workbench...",
            MenuPath = "File/Workbench...",
            Modes = GridSim.Ui.Commands.CommandModes.Both,
            Execute = () => { owner().OpenWorkbench(); return Task.CompletedTask; },
        };
    }

    /// <summary>Open a workbench window on a model (the one loaded in the main window), or empty when there is none.</summary>
    public static WorkbenchWindow Open(Window? owner, string name, GridModel? model)
    {
        var vm = new WorkbenchViewModel();
        if (model is not null) vm.SetProject(name, model);
        var w = new WorkbenchWindow(vm);
        if (owner is not null) w.Show(owner); else w.Show();
        return w;
    }

    // ---------------------------------------------------------------- showing the view model

    private void ShowState()
    {
        _updating = true;
        try
        {
            WorkbenchViewModel vm = ViewModel;
            StatusLine.Text = vm.StatusText;
            TreeList.ItemsSource = vm.TreeRows.Select(r => r.Text).ToList();
            int sel = vm.TreeRows.ToList().FindIndex(r => r.Node.Id == vm.SelectedNodeId);
            TreeList.SelectedIndex = sel;

            IReadOnlyList<ModelColumn> cols = vm.Columns;
            SortPicker.ItemsSource = new[] { "(file order)" }.Concat(cols.Select(c => c.Key)).ToList();
            SortPicker.SelectedIndex = vm.SortKey is null ? 0 : cols.ToList().FindIndex(c => c.Key == vm.SortKey) + 1;
            FilterColumnPicker.ItemsSource = cols.Select(c => c.Key).ToList();
            FilterColumnPicker.SelectedIndex = vm.FilterColumn is null ? -1 : cols.ToList().FindIndex(c => c.Key == vm.FilterColumn);
            DescendingBox.IsChecked = vm.SortDescending;
            TableList.ItemsSource = vm.TableLines;

            StudyPicker.ItemsSource = vm.Project?.Studies.Select(s => s.Id).ToList();
            StudyPicker.SelectedItem = vm.SelectedStudyId;
            ResultList.ItemsSource = vm.ResultLines;
            PlotPicker.ItemsSource = vm.PlotIds;
            PlotPicker.SelectedIndex = vm.Plot is null ? -1 : vm.PlotIds.ToList().FindIndex(id => vm.Result?.Plots.FirstOrDefault(p => p.Id == id)?.Spec == vm.Plot);
            PlotView.Spec = vm.Plot;
            ReportBox.Text = vm.ReportText;
            DiagramView.ViewModel = vm.Diagram;
            ShowDiagramState();
        }
        finally { _updating = false; }
    }

    /// <summary>The diagram pane's buttons and status line from the diagram view model.</summary>
    private void ShowDiagramState()
    {
        SldViewModel d = ViewModel.Diagram;
        bool was = _updating;
        _updating = true;
        try
        {
            DiagramStatus.Text = d.StatusText;
            CaseLayoutBox.IsChecked = d.RequestedLayout == SldLayoutMode.Case;
            ColourPicker.SelectedItem = d.ColourMode.ToString();
            UndoButton.IsEnabled = d.CanUndo;
            RedoButton.IsEnabled = d.CanRedo;
            AdoptButton.IsEnabled = d.CanEdit && d.Document is SldCaseDocument;
            AddBusButton.IsEnabled = d.CanMove;
            AddGenButton.IsEnabled = d.CanEdit && d.Document is SldCaseDocument;
            AddLoadButton.IsEnabled = d.CanEdit && d.Document is SldCaseDocument;
            AddBranchButton.IsEnabled = d.CanEdit && d.Document is SldCaseDocument;
            DeleteButton.IsEnabled = d.CanEdit && d.Document is SldCaseDocument;
            ToggleButton.IsEnabled = d.Document is SldSubstationDocument;
            DiagramSaveButton.IsEnabled = d.IsDirty;
        }
        finally { _updating = was; }
        DiagramView.InvalidateVisual();
    }

    private void Fit_Click(object? sender, RoutedEventArgs e) => DiagramView.FitToView();
    // The power flow runs off the UI thread; the view model catches and reports every failure, so nothing escapes this handler.
    private async void ResultColour_Click(object? sender, RoutedEventArgs e)
    {
        ResultColourButton.IsEnabled = false;
        try { await ViewModel.Diagram.ColourByPowerFlowAsync(); }
        finally { ResultColourButton.IsEnabled = true; }
    }
    private void Adopt_Click(object? sender, RoutedEventArgs e) => ViewModel.Diagram.AdoptLayout();
    private void Undo_Click(object? sender, RoutedEventArgs e) => ViewModel.Diagram.Undo();
    private void Redo_Click(object? sender, RoutedEventArgs e) => ViewModel.Diagram.Redo();
    private void AddBus_Click(object? sender, RoutedEventArgs e) => ViewModel.Diagram.AddBusBelowSelected();
    /// <summary>How the entry dialogs are shown (modal over this window); a test replaces it to fill the dialog without a user.</summary>
    public Func<EntryDialog, Task<string[]?>> ShowEntryDialog { get; set; } = null!;

    // A5: the edit values are typed into a small validated dialog (the defaults are the values these buttons used to apply unasked).
    // The dialogs are handled here; the view model's edit methods report every refusal in the diagram status line.
    private async void AddGen_Click(object? sender, RoutedEventArgs e) => await AddGeneratorAsync();
    private async void AddLoad_Click(object? sender, RoutedEventArgs e) => await AddLoadAsync();
    private async void AddBranch_Click(object? sender, RoutedEventArgs e) => await AddBranchAsync();

    private Task<string[]?> Ask(EntryDialog d) => (ShowEntryDialog ??= x => x.ShowDialog<string[]?>(this))(d);

    public async Task AddGeneratorAsync()
    {
        if (ViewModel.Diagram.SelectedBus is null) { ViewModel.Diagram.AddGeneratorToSelected(0, 0); return; }   // says "Select a busbar"
        var dlg = new EntryDialog("Add generator", [new("Capacity (MVA)", "100"), new("Output P (MW)", "50"), new("Voltage setpoint (pu)", "1.0")],
            v => SldEntryForms.TryGenerator(v, out _));
        if (await Ask(dlg).ConfigureAwait(true) is { } values && SldEntryForms.TryGenerator(values, out GeneratorEntry? g) is null && g is not null)
            ViewModel.Diagram.AddGeneratorToSelected(g.CapacityMva, g.OutputMw, g.VoltageSetpointPu);
    }

    public async Task AddLoadAsync()
    {
        if (ViewModel.Diagram.SelectedBus is null) { ViewModel.Diagram.AddLoadToSelected(0); return; }   // says "Select a busbar"
        var dlg = new EntryDialog("Add load", [new("P (MW)", "10"), new("Q (MVAr)", "3")], v => SldEntryForms.TryLoad(v, out _));
        if (await Ask(dlg).ConfigureAwait(true) is { } values && SldEntryForms.TryLoad(values, out LoadEntry? l) is null && l is not null)
            ViewModel.Diagram.AddLoadToSelected(l.PMw, l.QMvar);
    }

    public async Task AddBranchAsync()
    {
        string from = ViewModel.Diagram.SelectedBus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var dlg = new EntryDialog("Add branch between two buses",
            [new("Kind (line or transformer)", "line"), new("From bus", from), new("To bus", ""), new("R (pu)", "0.01"), new("X (pu)", "0.1"),
             new("B (pu, line)", "0"), new("Rating (MVA, 0 = none)", "0"), new("Tap ratio (transformer)", "1")],
            v => SldEntryForms.TryBranch(v, out _));
        if (await Ask(dlg).ConfigureAwait(true) is { } values && SldEntryForms.TryBranch(values, out BranchEntry? b) is null && b is not null)
            ViewModel.Diagram.AddBranchBetween(b);
    }
    private void Toggle_Click(object? sender, RoutedEventArgs e) => ViewModel.Diagram.ToggleSelectedSwitch();
    private void Delete_Click(object? sender, RoutedEventArgs e) => ViewModel.Diagram.DeleteSelected();
    private void DiagramSave_Click(object? sender, RoutedEventArgs e) => ViewModel.Diagram.Save();

    private void Svg_Click(object? sender, RoutedEventArgs e)
    {
        string path = SvgPathBox.Text?.Trim() ?? "";
        string? svg = ViewModel.Diagram.ToSvg();
        if (path.Length == 0 || svg is null) { ViewModel.Diagram.StatusText = "Type a file path to export the diagram to."; return; }
        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full) || Directory.Exists(full)) { ViewModel.Diagram.StatusText = "Not exported: that path already exists."; return; }
            File.WriteAllText(full, svg, new System.Text.UTF8Encoding(false));
            ViewModel.Diagram.StatusText = "Diagram exported to " + full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ViewModel.Diagram.StatusText = "Not exported: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- events

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        string path = PathBox.Text?.Trim() ?? "";
        if (path.Length == 0) { ViewModel.StatusText = "Type a path first."; ShowState(); return; }
        ViewModel.TryLoad(path);
        ShowState();
    }

    private void Tree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        int i = TreeList.SelectedIndex;
        if (i < 0 || i >= ViewModel.TreeRows.Count) return;
        string id = ViewModel.TreeRows[i].Node.Id;
        ViewModel.SelectNode(id);
        if (id.StartsWith("model/", StringComparison.Ordinal)) WorkbenchTabs.SelectedItem = ModelTabItem;
        else if (id.StartsWith("study/", StringComparison.Ordinal)) WorkbenchTabs.SelectedItem = StudyTabItem;
        else if (id.StartsWith("report/", StringComparison.Ordinal)) WorkbenchTabs.SelectedItem = ReportTabItem;
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        WorkbenchViewModel vm = ViewModel;
        vm.SortKey = SortPicker.SelectedIndex > 0 ? SortPicker.SelectedItem as string : null;
        vm.SortDescending = DescendingBox.IsChecked == true;
        vm.FilterColumn = FilterColumnPicker.SelectedItem as string;
        vm.FilterOp = Enum.TryParse(FilterOpPicker.SelectedItem as string, out ModelFilterOp op) ? op : ModelFilterOp.Contains;
        vm.FilterValue = FilterValueBox.Text?.Trim() ?? "";
        vm.GroupBy = Enum.TryParse(GroupPicker.SelectedItem as string, out GroupKind g) ? g : null;
        vm.RebuildTable();
    }

    // The study runs off the UI thread; the view model catches and reports every failure, so nothing escapes this handler.
    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        try
        {
            WorkbenchTabs.SelectedItem = StudyTabItem;
            await ViewModel.RunSelectedAsync(StudyPicker.SelectedItem as string);
        }
        finally { RunButton.IsEnabled = true; }
    }

    private void Generate_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.ReportFormat = Enum.TryParse(FormatPicker.SelectedItem as string, out WorkbenchReportFormat f) ? f : WorkbenchReportFormat.Markdown;
        ViewModel.GenerateReport();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        string path = SavePathBox.Text?.Trim() ?? "";
        if (path.Length == 0) { ViewModel.StatusText = "Type a file path to save to."; ShowState(); return; }
        ViewModel.SaveReport(path);
        ShowState();
    }
}
