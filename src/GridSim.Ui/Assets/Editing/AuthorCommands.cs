using GridSim.Ui.Commands;

namespace GridSim.Ui.Assets.Editing;

// UI programme Phase 4.1 (ASSETS plan section 3.5, modes and the command registry): the Author-mode commands, each
// declared once. They are Author mode only and desktop only (mobile has no Author mode; the registry refuses an Author
// command offered on mobile, so a mobile head can register none of these). The menu bar gains Edit (Undo Ctrl+Z, Redo
// Ctrl+Y) and Case (Check, Save Ctrl+S, Save As... Ctrl+Shift+S, Switch to Run) in Author mode only, so the Run-mode
// menu bar and its shortcuts are exactly as before. The context-menu commands act on the node of the ASSETS tree that
// was right-clicked (the view selects it first), by target kind:
//   Add record...   on a class node (AssetClassNode);
//   Duplicate, Delete on every record kind;
//   Rename key..., Where used on the kinds with a key (a bus id; a line, transformer or asset-class name).
// No gesture clashes: Ctrl+Z, Ctrl+Y, Ctrl+S and Ctrl+Shift+S are taken by nothing else in either mode (the registry
// refuses a clash per mode, so a later command that took one would fail loudly at start-up).

/// <summary>What the Author-mode commands act on: the shell implements it over its Author mode controller.</summary>
public interface IAuthorCommandActions
{
    /// <summary>The Author mode's model while the tab is in AUTHOR, else null.</summary>
    AssetAuthorModel? Author { get; }

    /// <summary>Undo the last change.</summary>
    Task UndoAsync();

    /// <summary>Redo the last undone change.</summary>
    Task RedoAsync();

    /// <summary>Check the case (no write, no solve).</summary>
    Task CheckAsync();

    /// <summary>Save in place (a bundled case goes to Save As).</summary>
    Task SaveAsync();

    /// <summary>Save As into a folder of the user's own.</summary>
    Task SaveAsAsync();

    /// <summary>Leave AUTHOR (Save / Discard / Cancel when there are unsaved changes) and reload the case in RUN.</summary>
    Task SwitchToRunAsync();

    /// <summary>Add a record to the selected class (asks for its required fields).</summary>
    Task AddRecordAsync();

    /// <summary>Duplicate the selected record.</summary>
    Task DuplicateAsync();

    /// <summary>Delete the selected record (refused, listing the referrers, while anything refers to it).</summary>
    Task DeleteAsync();

    /// <summary>Rename the selected record's key (asks for the new key, says what follows, then renames everywhere).</summary>
    Task RenameAsync();

    /// <summary>Show who refers to the selected record (CONNECTIONS).</summary>
    Task WhereUsedAsync();
}

/// <summary>The Author-mode registry commands (UI programme Phase 4.1).</summary>
public static class AuthorCommands
{
    /// <summary>Edit > Undo (Ctrl+Z).</summary>
    public const string Undo = "edit.undo";

    /// <summary>Edit > Redo (Ctrl+Y).</summary>
    public const string Redo = "edit.redo";

    /// <summary>Case > Check.</summary>
    public const string Check = "case.check";

    /// <summary>Case > Save (Ctrl+S).</summary>
    public const string Save = "case.save";

    /// <summary>Case > Save As... (Ctrl+Shift+S).</summary>
    public const string SaveAs = "case.saveAs";

    /// <summary>Case > Switch to Run.</summary>
    public const string SwitchToRun = "case.switchToRun";

    /// <summary>Context menu on a class node: Add record...</summary>
    public const string AddRecord = "author.addRecord";

    /// <summary>Context menu on a record: Duplicate.</summary>
    public const string Duplicate = "author.duplicate";

    /// <summary>Context menu on a record: Delete.</summary>
    public const string Delete = "author.delete";

    /// <summary>Context menu on a keyed record: Rename key...</summary>
    public const string Rename = "author.rename";

    /// <summary>Context menu on a keyed record: Where used.</summary>
    public const string WhereUsed = "author.whereUsed";

    /// <summary>Every record kind of the Author tree (Duplicate and Delete act on each). Phase 5b: an asset overlay record
    /// too (its duplicate takes a fresh id; its delete is refused while children or record references point at it).</summary>
    public const CommandTargets RecordKinds = CommandTargets.Bus | CommandTargets.Generator | CommandTargets.Demand | CommandTargets.Branch
        | CommandTargets.Transformer | CommandTargets.Oltc | CommandTargets.Boundary | CommandTargets.Protection
        | CommandTargets.AssetClass | CommandTargets.AssetBinding | CommandTargets.OverlayRecord;

    /// <summary>The record kinds with a key (a bus id; a line, transformer or asset-class name): Rename and Where used.
    /// Phase 5b: an asset overlay record's id too (its children and record references follow a rename).</summary>
    public const CommandTargets KeyedKinds = CommandTargets.Bus | CommandTargets.Branch | CommandTargets.Transformer | CommandTargets.AssetClass
        | CommandTargets.OverlayRecord;

    /// <summary>Register the Author-mode commands into <paramref name="registry"/>, each running a member of
    /// <paramref name="actions"/>, and return them in registration order. Throws (from the registry) on a clash.</summary>
    public static IReadOnlyList<AppCommand> Register(CommandRegistry registry, IAuthorCommandActions actions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        bool Authoring() => actions.Author is not null;
        AppCommand Add(AppCommand c) => registry.Register(c);
        return new[]
        {
            Add(new AppCommand
            {
                Id = Undo, Title = "Undo", MenuPath = "Edit/Undo", Gesture = "Ctrl+Z", Modes = CommandModes.Author,
                CanExecute = () => actions.Author?.Authoring.CanUndo == true, Execute = actions.UndoAsync,
            }),
            Add(new AppCommand
            {
                Id = Redo, Title = "Redo", MenuPath = "Edit/Redo", Gesture = "Ctrl+Y", Modes = CommandModes.Author,
                CanExecute = () => actions.Author?.Authoring.CanRedo == true, Execute = actions.RedoAsync,
            }),
            Add(new AppCommand
            {
                Id = Check, Title = "Check", MenuPath = "Case/Check", Modes = CommandModes.Author,
                CanExecute = Authoring, Execute = actions.CheckAsync,
            }),
            Add(new AppCommand
            {
                Id = Save, Title = "Save", MenuPath = "Case/Save", Gesture = "Ctrl+S", Modes = CommandModes.Author,
                CanExecute = Authoring, Execute = actions.SaveAsync,
            }),
            Add(new AppCommand
            {
                Id = SaveAs, Title = "Save As...", MenuPath = "Case/Save As...", Gesture = "Ctrl+Shift+S", Modes = CommandModes.Author,
                CanExecute = Authoring, Execute = actions.SaveAsAsync,
            }),
            Add(new AppCommand
            {
                Id = SwitchToRun, Title = "Switch to Run", MenuPath = "Case/Switch to Run", Modes = CommandModes.Author,
                CanExecute = Authoring, Execute = actions.SwitchToRunAsync,
            }),
            Add(new AppCommand
            {
                Id = AddRecord, Title = "Add record...", Modes = CommandModes.Author, Targets = CommandTargets.AssetClassNode,
                CanExecute = () => actions.Author?.AddTarget is not null, Execute = actions.AddRecordAsync,
            }),
            Add(new AppCommand
            {
                Id = Duplicate, Title = "Duplicate", Modes = CommandModes.Author, Targets = RecordKinds,
                CanExecute = () => actions.Author?.HasSelectedRecord == true, Execute = actions.DuplicateAsync,
            }),
            Add(new AppCommand
            {
                Id = Delete, Title = "Delete", Modes = CommandModes.Author, Targets = RecordKinds,
                CanExecute = () => actions.Author?.HasSelectedRecord == true, Execute = actions.DeleteAsync,
            }),
            Add(new AppCommand
            {
                Id = Rename, Title = "Rename key...", Modes = CommandModes.Author, Targets = KeyedKinds,
                CanExecute = () => actions.Author?.SelectedHasKey == true, Execute = actions.RenameAsync,
            }),
            Add(new AppCommand
            {
                Id = WhereUsed, Title = "Where used", Modes = CommandModes.Author, Targets = KeyedKinds,
                CanExecute = () => actions.Author?.SelectedHasKey == true, Execute = actions.WhereUsedAsync,
            }),
        };
    }
}

/// <summary>What the overlay command acts on (UI programme Phase 5b): the shell's Author mode controller implements it.</summary>
public interface IOverlayAuthorActions
{
    /// <summary>The Author mode's model while the tab is in AUTHOR, else null.</summary>
    AssetAuthorModel? Author { get; }

    /// <summary>Add an overlay record at the selected node (asks for its kind and id, then its required fields).</summary>
    Task AddOverlayRecordAsync();
}

/// <summary>
/// UI programme Phase 5b (2026-10-20): the asset overlay's Author-mode command, declared once. "Add overlay record..."
/// is a context-menu command (no menu bar entry, no shortcut), Author mode and desktop only like every Author command,
/// on an overlay record (adds a child of it), on a model asset an overlay record can be attached to and on the "Asset
/// overlay" class node (<see cref="CommandTargets.OverlayAnchor"/>). It is registered apart from
/// <see cref="AuthorCommands"/> (by the Author mode controller), so the Author command set of Phase 4.1 is unchanged.
/// </summary>
public static class OverlayAuthorCommands
{
    /// <summary>Context menu on an overlay record, a model asset or the overlay class node: Add overlay record...</summary>
    public const string AddOverlayRecord = "author.addOverlayRecord";

    /// <summary>Where Add overlay record... is offered.</summary>
    public const CommandTargets Targets = CommandTargets.OverlayRecord | CommandTargets.OverlayAnchor;

    /// <summary>Register the overlay command into <paramref name="registry"/>, running <paramref name="actions"/>, and
    /// return it (in a list, as the other Register methods do). Throws (from the registry) on a clash.</summary>
    public static IReadOnlyList<AppCommand> Register(CommandRegistry registry, IOverlayAuthorActions actions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        return new[]
        {
            registry.Register(new AppCommand
            {
                Id = AddOverlayRecord, Title = "Add overlay record...", Modes = CommandModes.Author, Targets = Targets,
                CanExecute = () => actions.Author?.CanAddOverlay == true, Execute = actions.AddOverlayRecordAsync,
            }),
        };
    }
}

/// <summary>What the star-legs command does, so the registry entry stays a plain declaration (Phase 5f follow-up).</summary>
public interface IStarLegsAuthorActions
{
    /// <summary>The Author mode's model while the tab is in AUTHOR, else null.</summary>
    AssetAuthorModel? Author { get; }

    /// <summary>Derive star legs on the selected transformer3W overlay record: propose the six leg edits on its OVERVIEW
    /// (nothing is changed until the user applies them there).</summary>
    Task DeriveStarLegsAsync();
}

/// <summary>
/// UI programme Phase 5f follow-up (2026-10-24): "Derive star legs..." as an Author-mode context command, declared once.
/// It is offered only on a transformer3W overlay record (<see cref="CommandTargets.StarTransformer"/>), Author mode and
/// desktop only like every Author command, and runs the same proposal as the record's OVERVIEW button: it proposes, the
/// page's Apply writes. Registered apart from <see cref="OverlayAuthorCommands"/> (by the Author mode controller), so
/// the overlay command set of Phase 5b is unchanged.
/// </summary>
public static class StarLegsAuthorCommands
{
    /// <summary>Context menu on a transformer3W overlay record: Derive star legs...</summary>
    public const string DeriveStarLegs = "author.deriveStarLegs";

    /// <summary>Register the star-legs command into <paramref name="registry"/>, running <paramref name="actions"/>, and
    /// return it (in a list, as the other Register methods do). Throws (from the registry) on a clash.</summary>
    public static IReadOnlyList<AppCommand> Register(CommandRegistry registry, IStarLegsAuthorActions actions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        return new[]
        {
            registry.Register(new AppCommand
            {
                Id = DeriveStarLegs, Title = "Derive star legs...", Modes = CommandModes.Author, Targets = CommandTargets.StarTransformer,
                CanExecute = () => actions.Author?.CanDeriveStarLegs == true, Execute = actions.DeriveStarLegsAsync,
            }),
        };
    }
}
