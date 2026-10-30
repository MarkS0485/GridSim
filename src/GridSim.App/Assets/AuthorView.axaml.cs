// HOW TO FEED (UI programme Phase 4.1).
// The view shows the navigator's Author model (AssetNavigatorModel.Author), bound in AssetsView.axaml:
//     <assets:AuthorView x:Name="AuthorPane" DataContext="{Binding Author}" />
// It is shown only while the ASSETS tab is in AUTHOR. Its buttons and the tree's context menu run the window's Author
// mode controller (the same actions as the Edit and Case menu items), which AssetsView hands it.
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GridSim.App.Controls.Diagram;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Editing.Elements;
using GridSim.Ui.Assets.Editing.Templates;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Commands;

namespace GridSim.App.Assets;

/// <summary>
/// The ASSETS tab in AUTHOR mode (UI programme Phase 4.1): the case file's records in the split layout, the field grid,
/// the where-used list and the last Check. Every action is the Author mode controller's (the registry commands' own), so
/// a button, a menu item and a shortcut can never do different things.
/// </summary>
public sealed partial class AuthorView : UserControl
{
    private bool _syncingTree;
    private AssetAuthorModel? _watched;

    public AuthorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch();
    }

    /// <summary>The window's Author mode (set by the ASSETS view when the window wires it).</summary>
    public AuthorModeController? AuthorMode { get; set; }

    /// <summary>Builds the context menu for a target kind in the current mode (the window's ContextMenuFor).</summary>
    public Func<CommandTargets, ContextMenu?>? ContextMenuProvider { get; set; }

    /// <summary>The Author model behind the view (fails loud when there is none: AUTHOR is not on).</summary>
    public AssetAuthorModel Model => DataContext as AssetAuthorModel
        ?? throw new InvalidOperationException("The AUTHOR view has no AssetAuthorModel as its DataContext (the tab is not in AUTHOR).");

    private AuthorModeController Controller => AuthorMode
        ?? throw new InvalidOperationException("The AUTHOR view has no Author mode controller (the window did not wire it).");

    // ---- the tree follows the model's selection (a where-used link, a new or moved record) ----

    private void Watch()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnModelChanged;
        _watched = DataContext as AssetAuthorModel;
        if (_watched is not null) _watched.PropertyChanged += OnModelChanged;
        SyncTree();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssetAuthorModel.Selected) or nameof(AssetAuthorModel.Roots)) SyncTree();
    }

    private void SyncTree()
    {
        _syncingTree = true;
        try { AuthorTree.SelectedItem = _watched?.Selected; }
        finally { _syncingTree = false; }
    }

    private void Tree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTree || !ReferenceEquals(e.Source, AuthorTree)) return;
        if (AuthorTree.SelectedItem is AuthorNode node && _watched is { } m && ReferenceEquals(m.ById(node.Id), node)) m.Select(node);
    }

    // A right-click on a node: select it, then open the registry's context menu for its kind (Author mode).
    private void Tree_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not AuthorNode node || _watched is not { } m) return;
        if (ReferenceEquals(m.ById(node.Id), node)) m.Select(node);
        if (MenuFor(node) is not ContextMenu menu) return;
        menu.Open(e.Source as Control ?? AuthorTree);
        e.Handled = true;
    }

    /// <summary>
    /// The context menu of a node: the registry's commands for each of its target kinds (<see cref="AuthorNode.MenuTargets"/>)
    /// in one menu, a separator between the kinds (UI programme Phase 5b: a model asset of a case with an overlay file
    /// adds the overlay anchor's "Add overlay record..." after its own commands). Null when no command acts on the node.
    /// </summary>
    public ContextMenu? MenuFor(AuthorNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ContextMenu? merged = null;
        foreach (CommandTargets target in node.MenuTargets)
        {
            if (ContextMenuProvider?.Invoke(target) is not ContextMenu part) continue;
            if (merged is null) { merged = part; continue; }
            // Move the items over (an item has one parent), after a separator.
            var items = part.Items.Cast<object>().ToList();
            part.Items.Clear();
            merged.Items.Add(new Separator());
            foreach (object item in items) merged.Items.Add(item);
        }
        return merged;
    }

    // ---- the action strip: the controller's actions (the Edit and Case menu items' own) ----

    private void Undo_Click(object? sender, RoutedEventArgs e) => Run(Controller.UndoAsync);

    private void Redo_Click(object? sender, RoutedEventArgs e) => Run(Controller.RedoAsync);

    private void Check_Click(object? sender, RoutedEventArgs e) => Run(Controller.CheckAsync);

    private void Save_Click(object? sender, RoutedEventArgs e) => Run(Controller.SaveAsync);

    private void SaveAs_Click(object? sender, RoutedEventArgs e) => Run(Controller.SaveAsAsync);

    // Run an action; a failure is shown in the status line, never swallowed.
    private async void Run(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Not swallowed: shown in the Author status line in the alarm colour.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _watched?.Note("Failed: " + ex.Message, isError: true);
        }
    }

    // ---- the pages ----

    private void ClassRow_Click(object? sender, RoutedEventArgs e)
    {
        AuthorNode node = (sender as Control)?.DataContext as AuthorNode
            ?? throw new InvalidOperationException("An AUTHOR class row is not on an AuthorNode.");
        Model.Select(node);
    }

    // Phase 5c: a click on a symbol of a substation's topology diagram selects its overlay record (or, on a bay's far end,
    // the model record the bay switches).
    // Phase 5f: Derive star legs on a three-winding transformer record. DERIVE only proposes (shown on the page); APPLY is
    // the user's confirmation and writes the six leg values as one undo step; CANCEL drops the proposal.
    private void DeriveStarLegs_Click(object? sender, RoutedEventArgs e) => Model.DeriveStarLegs();

    private void ApplyStarLegs_Click(object? sender, RoutedEventArgs e) => Model.ApplyStarLegs();

    private void CancelStarLegs_Click(object? sender, RoutedEventArgs e) => Model.CancelStarLegs();

    private void TopologyDiagram_SymbolClicked(object? sender, DiagramHitEventArgs e)
    {
        if (e.Hit is { Kind: DiagramHitKind.Symbol } hit) Model.OpenFromDiagram(hit.Id);
    }

    private void WhereUsed_Click(object? sender, RoutedEventArgs e)
    {
        WhereUsedRow row = (sender as Control)?.DataContext as WhereUsedRow
            ?? throw new InvalidOperationException("An AUTHOR where-used row is not on a WhereUsedRow.");
        if (!Model.OpenWhereUsed(row)) Model.Note(row.What + " has no node of its own in the tree (it is nested in another record).", isError: false);
    }

    private static FieldRow RowOf(object? sender) => (sender as Control)?.DataContext as FieldRow
        ?? throw new InvalidOperationException("An AUTHOR field control is not on a FieldRow.");

    private void FieldSet_Click(object? sender, RoutedEventArgs e) => Model.CommitField(RowOf(sender));

    private void FieldReset_Click(object? sender, RoutedEventArgs e) => Model.ResetField(RowOf(sender));

    // ---- UI programme Phase 4.3: the per-element editor (its fields are the grid's own rows) ----

    private static EditorField EditorFieldOf(object? sender) => (sender as Control)?.DataContext as EditorField
        ?? throw new InvalidOperationException("An AUTHOR editor control is not on an EditorField.");

    private void EditorSet_Click(object? sender, RoutedEventArgs e) => Model.CommitEditorField(EditorFieldOf(sender));

    private void EditorReset_Click(object? sender, RoutedEventArgs e) => Model.ResetEditorField(EditorFieldOf(sender));

    // Enter in an editor field's text box commits it (Escape puts the literal back), as in the generic grid.
    private void EditorBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Model.CommitEditorField(EditorFieldOf(sender));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EditorField field = EditorFieldOf(sender);
            field.EditText = field.Literal;
            e.Handled = true;
        }
    }

    // A pick in a field's picker (a bus, a branch, an enum value, true or false) commits that value at once (one undo step).
    private void EditorPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: EditorChoice choice } || e.AddedItems.Count == 0) return;
        Model.PickEditorChoice(EditorFieldOf(sender), choice);
    }

    // ---- UI programme Phase 4.4: the templates (every action is one Author model operation: one undo step, or refused
    // with the reason in the status line) ----

    private static T ContextOf<T>(object? sender, string what) where T : class => (sender as Control)?.DataContext as T
        ?? throw new InvalidOperationException("An AUTHOR " + what + " control is not on a " + typeof(T).Name + ".");

    // A generator row (a template's bound generators, the TEMPLATES page): select that generator's record.
    private void BoundGenerator_Click(object? sender, RoutedEventArgs e)
    {
        GeneratorBindingInfo g = ContextOf<GeneratorBindingInfo>(sender, "generator row");
        if (!Model.SelectRecord(g.Record)) Model.Note(g.Label + " has no node in the tree.", isError: false);
    }

    private void RuleRemove_Click(object? sender, RoutedEventArgs e)
        => Model.RemoveRule(ContextOf<TemplateBindingRule>(sender, "binding rule").Index);

    private void RuleAdd_Click(object? sender, RoutedEventArgs e)
    {
        TemplateEditorModel ed = ContextOf<TemplateEditorModel>(sender, "template");
        Model.AddBindingRule(ed.Ref, ed.NewRuleMatchType);
    }

    private void OverrideReset_Click(object? sender, RoutedEventArgs e)
    {
        TemplateOverrideRow row = ContextOf<TemplateOverrideRow>(sender, "override");
        Model.ResetOverride(row.Template, row.Param);
    }

    private void OverridePageReset_Click(object? sender, RoutedEventArgs e) => Model.ResetSelectedOverride();

    private void OverrideSet_Click(object? sender, RoutedEventArgs e)
    {
        TemplateEditorModel ed = ContextOf<TemplateEditorModel>(sender, "template");
        if (ed.OverrideParam is not string param)
        {
            Model.Note("Pick the parameter to override first.", isError: true);
            return;
        }
        Model.SetOverride(ed.Ref, param, ed.OverrideText, ed.OverrideReason);
    }

    private void OpenTemplate_Click(object? sender, RoutedEventArgs e)
    {
        GeneratorTemplateModel link = ContextOf<GeneratorTemplateModel>(sender, "template link");
        if (link.Generator.Template is not string r || !Model.OpenTemplate(r)) Model.Note("The template this generator names is not in the case's TEMPLATES.", isError: true);
    }

    // A pick in the generator's template picker binds it at once (its own template member; one undo step).
    private void BindTemplatePicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string reference } || e.AddedItems.Count == 0) return;
        GeneratorTemplateModel link = ContextOf<GeneratorTemplateModel>(sender, "template link");
        Model.BindGenerator(link.Generator.Record, reference);
    }

    private void UnbindTemplate_Click(object? sender, RoutedEventArgs e)
        => Model.UnbindGenerator(ContextOf<GeneratorTemplateModel>(sender, "template link").Generator.Record);

    private void ShowEffective_Click(object? sender, RoutedEventArgs e) => Model.ShowEffectiveParameters();

    // The switch between a record's editor and the generic field grid.
    private void EditorViewToggle_Click(object? sender, RoutedEventArgs e) => Model.ShowFieldGrid = !Model.ShowFieldGrid;

    // Enter in a field's text box commits it (Escape puts the literal back).
    private void FieldBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Model.CommitField(RowOf(sender));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FieldRow row = RowOf(sender);
            row.EditText = row.Literal;
            e.Handled = true;
        }
    }
}
