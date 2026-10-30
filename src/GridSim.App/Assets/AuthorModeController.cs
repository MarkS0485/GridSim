using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Assets.Editing.Templates;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Commands;
using GridSim.Ui.Models;

namespace GridSim.App.Assets;

// UI programme Phase 4.1: the window's side of Author mode. The ASSETS tab's RUN / AUTHOR toggle, the Edit and Case
// menus and the tree's context menus all come here; this class does what the plan says around the editing itself:
//   - entering AUTHOR is offered only for a case file (a v1 folder or a v2 manifest) on the desktop (plan Q9: MATPOWER,
//     a replay and the bridge show the reason instead); it PAUSES the run, opens the case through the public
//     CaseAuthoring wrapper and switches the menus and shortcuts to Author mode;
//   - leaving AUTHOR with unsaved changes asks Save / Discard / Cancel (a bundled case's Save is Save As); Cancel, or a
//     save the gate refuses, keeps the session in AUTHOR;
//   - back in RUN, a case that was saved is RELOADED through the run pipeline (the window's LoadCaseAsync, the same path
//     as File > Open case), so the run is always of the clean, passing, saved case; a case left unchanged simply carries
//     on (the run resumes if it was playing).
// The questions are asked through IAuthorPrompts, so a test answers them without a dialog.
// UI programme Phase 5b (2026-10-20): the asset overlay. This controller also registers and runs "Add overlay record..."
// (OverlayAuthorCommands; Author mode, desktop only): two prompts, the kind (only the kinds allowed at the selected node)
// and then the id and the fields that kind requires; the add itself is the Author model's (one engine operation).

/// <summary>The answer to "there are unsaved changes".</summary>
public enum DirtyChoice
{
    /// <summary>Save them (a bundled case: Save As), then go on.</summary>
    Save,

    /// <summary>Throw them away (nothing is written), then go on.</summary>
    Discard,

    /// <summary>Stay in AUTHOR with the changes kept.</summary>
    Cancel,
}

/// <summary>The questions Author mode asks the user (the window shows dialogs; a test answers them itself).</summary>
public interface IAuthorPrompts
{
    /// <summary>Unsaved changes: Save, Discard or Cancel? <paramref name="message"/> lists the dirty files.</summary>
    Task<DirtyChoice> AskSaveDiscardCancelAsync(string message);

    /// <summary>A yes / no question (new model warnings, what a rename will change). True means yes.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>One line of text (a Save As folder, a new key), starting from <paramref name="initial"/>; null when cancelled.</summary>
    Task<string?> AskTextAsync(string title, string message, string initial);

    /// <summary>
    /// Shell slice (v2 + pickers, 2026-10-20): the folder Save As writes the copy into, null when cancelled.
    /// <paramref name="suggested"/> is the folder Save As would suggest (in the user's own case folder; it need not exist
    /// yet). The window's dialogs show the OS folder picker (<see cref="AuthorDialogs"/>); the default here asks for the
    /// folder as one line of text, as before, so a test's scripted answers (and a platform without a folder picker) keep
    /// working. Whatever is picked, the Save As refusal rules are checked afterwards, unchanged
    /// (<see cref="AssetAuthorModel.SaveAs"/>: not a bundled folder, not inside the case's own folder, new or empty).
    /// </summary>
    Task<string?> PickSaveAsFolderAsync(string title, string message, string suggested) => AskTextAsync(title, message, suggested);

    /// <summary>The values of a new record's required fields; null when cancelled.</summary>
    Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields);
}

/// <summary>
/// Author mode for the window (UI programme Phase 4.1): entering and leaving AUTHOR (pause, prompts, reload) and the
/// Author-mode commands of the registry (<see cref="AuthorCommands"/>).
/// </summary>
public sealed class AuthorModeController : IAuthorCommandActions, IOverlayAuthorActions, ITemplateAuthorActions, IStarLegsAuthorActions
{
    private readonly MainWindowViewModel _vm;
    private readonly IAuthorPrompts _prompts;
    private bool _wasPlaying;            // the run was playing when AUTHOR was entered (it resumes on a plain leave)
    private ModelSource? _entrySource;    // the run pipeline's source when AUTHOR was entered (a save in place reloads it)
    private string? _entryCasePath;       // the case path when AUTHOR was entered (a Save As moves it)

    /// <summary>Author mode over the window's view model, asking <paramref name="prompts"/>.</summary>
    public AuthorModeController(MainWindowViewModel vm, IAuthorPrompts prompts)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        // UI programme Phase 5b: the overlay's context command ("Add overlay record...") is this controller's own, so it
        // is registered here, once per window, apart from the Phase 4.1 Author commands the window registers.
        OverlayCommands = OverlayAuthorCommands.Register(_vm.Registry, this);
        // UI programme Phase 4.4: the template commands (Add template..., Duplicate, Delete, Bind to template..., Reset
        // override) are this controller's own too, registered the same way, apart from the Phase 4.1 Author commands.
        TemplateCommands = TemplateAuthorCommands.Register(_vm.Registry, this);
        // Phase 5f follow-up: Derive star legs... on a transformer3W overlay record's context menu, registered the same way.
        StarLegsCommands = StarLegsAuthorCommands.Register(_vm.Registry, this);
    }

    /// <summary>Phase 5f follow-up: the star-legs command registered for this controller (refreshed with <see cref="Commands"/>).</summary>
    public IReadOnlyList<AppCommand> StarLegsCommands { get; }

    /// <summary>Phase 4.4: the template commands registered for this controller (refreshed with <see cref="Commands"/>).</summary>
    public IReadOnlyList<AppCommand> TemplateCommands { get; }

    /// <summary>UI programme Phase 4.2: the prompts Author mode asks through (the map editor asks its Place bus and Connect
    /// questions through the same ones, so a test's scripted answers cover both).</summary>
    public IAuthorPrompts Prompts => _prompts;

    /// <summary>Phase 5b: the overlay command registered for this controller (refreshed with <see cref="Commands"/>).</summary>
    public IReadOnlyList<AppCommand> OverlayCommands { get; }

    /// <summary>The shipped data roots a case is bundled under (null: the real ones; a test passes its own).</summary>
    public IReadOnlyList<string>? BundledRoots { get; init; }

    /// <summary>The folder Save As suggests a copy in (null: the per-user default, CaseAuthoring.DefaultUserCasesFolder).</summary>
    public string? UserCasesFolder { get; init; }

    /// <summary>The Author-mode commands registered for this controller (their enabled state is refreshed after every
    /// change).</summary>
    public IReadOnlyList<AppCommand> Commands { get; set; } = Array.Empty<AppCommand>();

    private AssetNavigatorModel Navigator => _vm.Tabs.Assets;

    /// <inheritdoc />
    public AssetAuthorModel? Author => Navigator.Author;

    /// <summary>The last time leaving AUTHOR reloaded the case (null when it did not), so a test can wait for it.</summary>
    public Task? PendingReload { get; private set; }

    // Every command's enabled state may have changed (a change, a selection, the mode).
    private void RefreshCommands()
    {
        foreach (AppCommand c in Commands) c.RaiseCanExecuteChanged();
        foreach (AppCommand c in OverlayCommands) c.RaiseCanExecuteChanged();
        foreach (AppCommand c in TemplateCommands) c.RaiseCanExecuteChanged();
        foreach (AppCommand c in StarLegsCommands) c.RaiseCanExecuteChanged();
    }

    // ======================================== entering and leaving ========================================

    /// <summary>The header toggle: enter AUTHOR from RUN, or leave it.</summary>
    public Task<bool> ToggleAsync() => Navigator.IsAuthoring ? LeaveAsync() : EnterAsync();

    /// <summary>
    /// Enter AUTHOR: refused with the reason (shown beside the toggle and in the status bar) for a source that is not a
    /// case file or a case that cannot be opened; otherwise the run is paused, the case is opened for editing and the
    /// menus switch to Author mode. True when AUTHOR was entered (or already was).
    /// </summary>
    public async Task<bool> EnterAsync()
    {
        AssetNavigatorModel nav = Navigator;
        if (nav.IsAuthoring) return true;
        CaseAuthoring authoring;
        try
        {
            authoring = nav.OpenAuthoring(BundledRoots, UserCasesFolder);
        }
        catch (CaseAuthoringException ex)
        {
            nav.ModeNote = ex.Message;
            _vm.StatusText = ex.Message;
            return false;
        }
        // Pause the run first: AUTHOR shows no live value, and nothing runs while the case is being changed.
        _wasPlaying = _vm.Run.HasRun && !_vm.Run.IsPaused;
        if (_wasPlaying) await _vm.Run.TogglePlayAsync().ConfigureAwait(true);
        _entrySource = _vm.Run.CurrentSource;
        _entryCasePath = authoring.CasePath;
        var author = new AssetAuthorModel(authoring);
        author.PropertyChanged += (_, _) => RefreshCommands();
        nav.EnterAuthor(author);
        _vm.Mode = CommandModes.Author;
        _vm.StatusText = "AUTHOR: editing " + authoring.CaseName + " (the run is paused). Case > Switch to Run, or the RUN toggle, goes back.";
        RefreshCommands();
        return true;
    }

    /// <summary>
    /// Leave AUTHOR for RUN: Save / Discard / Cancel when there are unsaved changes (false, staying in AUTHOR, on Cancel or
    /// a refused save); then a saved case is reloaded through the run pipeline, an unchanged one carries on.
    /// </summary>
    public async Task<bool> LeaveAsync()
    {
        if (!Navigator.IsAuthoring) return true;
        if (!await ResolveDirtyAsync().ConfigureAwait(true)) return false;
        AssetAuthorModel author = Author!;
        bool saved = author.Authoring.SavedThisSession;
        bool moved = !string.Equals(author.Authoring.CasePath, _entryCasePath, StringComparison.Ordinal);
        ModelSource? reload = !saved ? null
            : moved || _entrySource is null ? author.Authoring.RunSource(author.Authoring.CaseName + "  (your copy)")
            : _entrySource;
        Exit();
        if (reload is not null)
        {
            // RUN is always of the saved case: reload it through the run pipeline, as File > Open case loads a case.
            PendingReload = _vm.LoadCaseAsync(reload);
            await PendingReload.ConfigureAwait(true);
        }
        else if (_wasPlaying && _vm.Run.IsPaused)
            await _vm.Run.TogglePlayAsync().ConfigureAwait(true);   // nothing changed on disk: carry on as before
        return true;
    }

    /// <summary>
    /// The window's guard before another case is loaded while in AUTHOR (MainWindowViewModel.LeaveAuthorBeforeLoad):
    /// Save / Discard / Cancel for unsaved changes, then leave AUTHOR without reloading (the new load follows). False
    /// cancels the load.
    /// </summary>
    public async Task<bool> LeaveForLoadAsync()
    {
        if (!Navigator.IsAuthoring) return true;
        if (!await ResolveDirtyAsync().ConfigureAwait(true)) return false;
        Exit();
        return true;
    }

    // Back to Run mode in the tab and the menus.
    private void Exit()
    {
        Navigator.LeaveAuthor();
        _vm.Mode = CommandModes.Run;
        RefreshCommands();
    }

    // Unsaved changes: ask, and save when asked to. True when the session may be left (clean, saved or discarded).
    private async Task<bool> ResolveDirtyAsync()
    {
        AssetAuthorModel author = Author!;
        if (!author.IsDirty) return true;
        string message = "Unsaved changes in " + string.Join(", ", author.Authoring.DirtyFiles) + "."
            + (author.Authoring.IsBundled ? " This is a bundled case: Save saves your copy with Save As." : "")
            + " Save them, discard them (nothing is written), or cancel and stay in AUTHOR?";
        switch (await _prompts.AskSaveDiscardCancelAsync(message).ConfigureAwait(true))
        {
            case DirtyChoice.Discard:
                author.Note("Discarded the unsaved changes; nothing was written.", isError: false);
                return true;
            case DirtyChoice.Save:
                return author.Authoring.IsBundled ? await SaveAsFlowAsync().ConfigureAwait(true) : await SaveFlowAsync().ConfigureAwait(true);
            default:
                author.Note("Stayed in AUTHOR: the unsaved changes are kept.", isError: false);
                return false;
        }
    }

    // Save in place; new model warnings need a yes. True when the case is now saved (or there was nothing to save).
    private async Task<bool> SaveFlowAsync()
    {
        AssetAuthorModel author = Author!;
        AuthoringReport r = author.Save();
        if (r.Outcome == AuthoringOutcome.NeedsConfirmation
            && await _prompts.ConfirmAsync("Save anyway?", string.Join(Environment.NewLine, r.Lines())).ConfigureAwait(true))
            r = author.Save(confirmNewWarnings: true);
        RefreshCommands();
        return r.Saved || r.Outcome == AuthoringOutcome.NothingToSave;
    }

    // Save As into a folder the user picks (the suggestion is in the user's own case folder). True when saved. Shell
    // slice (v2 + pickers, 2026-10-20): asked through PickSaveAsFolderAsync (the OS folder picker in the window); the
    // refusal rules are SaveAs's own, checked after the pick exactly as they were after the typed path.
    private async Task<bool> SaveAsFlowAsync()
    {
        AssetAuthorModel author = Author!;
        string? folder = await _prompts.PickSaveAsFolderAsync("Save As",
            "Save the case as a new case of your own. Folder (it must not exist yet, or be empty; never a bundled folder):",
            author.Authoring.SuggestSaveAsFolder()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            author.Note("Save As cancelled; nothing was written.", isError: false);
            return false;
        }
        AuthoringReport r = author.SaveAs(folder);
        if (r.Outcome == AuthoringOutcome.NeedsConfirmation
            && await _prompts.ConfirmAsync("Save As anyway?", string.Join(Environment.NewLine, r.Lines())).ConfigureAwait(true))
            r = author.SaveAs(folder, confirmNewWarnings: true);
        RefreshCommands();
        return r.Saved;
    }

    // ======================================== the commands (IAuthorCommandActions) ========================================

    private AssetAuthorModel Need() => Author ?? throw new InvalidOperationException("The ASSETS tab is not in AUTHOR mode.");

    /// <inheritdoc />
    public Task UndoAsync()
    {
        Need().Undo();
        RefreshCommands();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RedoAsync()
    {
        Need().Redo();
        RefreshCommands();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CheckAsync()
    {
        Need().Check();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SaveAsync()
    {
        // A bundled case is never written in place: Save is Save As for it (the plan's rule), said in the status line.
        if (Need().Authoring.IsBundled) await SaveAsFlowAsync().ConfigureAwait(true);
        else await SaveFlowAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async Task SaveAsAsync()
    {
        Need();
        await SaveAsFlowAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async Task SwitchToRunAsync() => await LeaveAsync().ConfigureAwait(true);

    /// <inheritdoc />
    public async Task AddRecordAsync()
    {
        AssetAuthorModel author = Need();
        AuthorNode target = author.AddTarget ?? throw new InvalidOperationException("Select a class (Buses, Generation, ...) to add a record to.");
        AuthoringRecordSet set = AssetAuthorModel.CollectionOf(target);
        IReadOnlyList<NewRecordField> fields = RecordActions.RequiredFields(author.Authoring, set);
        IReadOnlyList<KeyValuePair<string, string>>? values = await _prompts.AskFieldsAsync("Add record",
            "A new record of " + target.Label + " in " + set.FileName + ". Its required fields (every other field is absent, so it takes its default):",
            fields).ConfigureAwait(true);
        if (values is null)
        {
            author.Note("Add cancelled; nothing changed.", isError: false);
            return;
        }
        author.AddRecord(target, values);
        RefreshCommands();
    }

    /// <inheritdoc />
    public Task DuplicateAsync()
    {
        Need().DuplicateSelected();
        RefreshCommands();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync()
    {
        Need().DeleteSelected();
        RefreshCommands();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RenameAsync()
    {
        AssetAuthorModel author = Need();
        string? typed = await _prompts.AskTextAsync("Rename key", "The new key (every reference to it follows, in one step):",
            author.SelectedKeyText).ConfigureAwait(true);
        if (typed is null)
        {
            author.Note("Rename cancelled; nothing changed.", isError: false);
            return;
        }
        // First say what would follow (nothing changes), then rename on a yes.
        AuthoringResult dry = author.RenameSelected(typed, dryRun: true);
        if (!dry.Ok) return;   // refused: the status line says why
        if (!dry.Changed && dry.Message.EndsWith("unchanged.", StringComparison.Ordinal)) return;
        if (!await _prompts.ConfirmAsync("Rename?", dry.Message).ConfigureAwait(true))
        {
            author.Note("Rename cancelled; nothing changed.", isError: false);
            return;
        }
        author.RenameSelected(typed);
        RefreshCommands();
    }

    /// <inheritdoc />
    public Task WhereUsedAsync()
    {
        Need().ShowWhereUsed();
        return Task.CompletedTask;
    }

    // ======================================== the star-legs command (IStarLegsAuthorActions, Phase 5f follow-up) ========================================

    /// <inheritdoc />
    public Task DeriveStarLegsAsync()
    {
        // The same proposal as the OVERVIEW button: shown on the record's page, nothing changed until Apply there.
        Need().DeriveStarLegs();
        return Task.CompletedTask;
    }

    // ======================================== the overlay command (IOverlayAuthorActions, Phase 5b) ========================================

    /// <summary>
    /// Add an overlay record at the selected node: first its kind (the kinds allowed there) and id, then the fields that
    /// kind needs (parent, file, required references and parameters). Cancelling either prompt changes nothing; a refusal
    /// shows in the Author status line with the reason.
    /// </summary>
    public async Task AddOverlayRecordAsync()
    {
        AssetAuthorModel author = Need();
        IReadOnlyList<OverlayKindChoice> kinds = author.OverlayKindChoices();
        if (kinds.Count == 0)
        {
            author.Note("No overlay record can be added here: select the Asset overlay class, an overlay record with child kinds, or a model asset an overlay kind attaches to.", isError: true);
            return;
        }
        string where = author.OverlayAddAt is null ? "the asset overlay" : author.Authoring.Describe(author.OverlayAddAt);
        IReadOnlyList<KeyValuePair<string, string>>? first = await _prompts.AskFieldsAsync("Add overlay record",
            "A new asset overlay record at " + where + ". Its kind (" + string.Join(", ", kinds.Select(k => k.Kind)) + "):",
            new[] { new NewRecordField("kind", "kind", "Enum", kinds.Select(k => k.Kind).ToList()) }).ConfigureAwait(true);
        string? kind = first?.FirstOrDefault(kv => kv.Key == "kind").Value?.Trim();
        if (string.IsNullOrEmpty(kind))
        {
            author.Note("Add overlay record cancelled; nothing changed.", isError: false);
            return;
        }
        if (!kinds.Any(k => k.Kind == kind))
        {
            author.Note("Add overlay record refused: '" + kind + "' is not one of the kinds that can be added here (" + string.Join(", ", kinds.Select(k => k.Kind)) + ").", isError: true);
            return;
        }
        IReadOnlyList<KeyValuePair<string, string>>? values = await _prompts.AskFieldsAsync("Add overlay record: " + kind,
            "A new '" + kind + "' record at " + where + ". Its id and required fields (every optional field is absent; set it afterwards in PROPERTIES):",
            author.OverlayFieldsFor(kind)).ConfigureAwait(true);
        if (values is null)
        {
            author.Note("Add overlay record cancelled; nothing changed.", isError: false);
            return;
        }
        author.AddOverlayRecord(kind, values);
        RefreshCommands();
    }

    // ======================================== the template commands (ITemplateAuthorActions, Phase 4.4) ========================================

    /// <summary>Add a local inverter template: one prompt for its kind (the inverter template kinds), name (the id is
    /// gridsim.local.NAME) and optional title. Cancelling changes nothing; a refusal shows in the Author status line.</summary>
    public async Task AddTemplateAsync()
    {
        AssetAuthorModel author = Need();
        IReadOnlyList<string> kinds = TemplateKinds.InverterKinds;
        IReadOnlyList<KeyValuePair<string, string>>? values = await _prompts.AskFieldsAsync("Add template",
            "A new inverter template of this case (written in its manifest's localTemplates; id gridsim.local.NAME, revision 1, no parameter set yet: "
            + "type them in PROPERTIES). Its kind, name and (optional) title:",
            new[]
            {
                new NewRecordField("kind", "kind", "Enum", kinds),
                new NewRecordField("name", "name (letters, digits, '_' and '-')", "String", Array.Empty<string>()),
                new NewRecordField("title", "title (optional)", "String", Array.Empty<string>()),
            }).ConfigureAwait(true);
        if (values is null)
        {
            author.Note("Add template cancelled; nothing changed.", isError: false);
            return;
        }
        string Value(string key) => values.FirstOrDefault(kv => kv.Key == key).Value ?? "";
        author.AddTemplate(Value("kind"), Value("name"), Value("title"));
        RefreshCommands();
    }

    /// <inheritdoc />
    public Task DuplicateTemplateAsync()
    {
        Need().DuplicateSelectedTemplate();
        RefreshCommands();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteTemplateAsync()
    {
        Need().DeleteSelectedTemplate();
        RefreshCommands();
        return Task.CompletedTask;
    }

    /// <summary>Bind to the selected template: one prompt for EITHER a generator (by name: that generator's own template
    /// member) OR a type (a binding rule for every generator of that type; a trailing * makes it a prefix).</summary>
    public async Task BindToTemplateAsync()
    {
        AssetAuthorModel author = Need();
        string target = author.Selected?.TemplateRef ?? throw new InvalidOperationException("Select a template to bind generators to.");
        IReadOnlyList<KeyValuePair<string, string>>? values = await _prompts.AskFieldsAsync("Bind to template",
            "Bind to " + target + " EITHER one generator (by its name: it names the template itself, which wins over every rule) "
            + "OR every generator of a type (a binding rule; a trailing * makes it a prefix, for example wind*). Give one of the two:",
            new[]
            {
                new NewRecordField("generator", "generator name", "String", author.GeneratorNames()),
                new NewRecordField("matchType", "or generator type", "String", Array.Empty<string>()),
            }).ConfigureAwait(true);
        if (values is null)
        {
            author.Note("Bind cancelled; nothing changed.", isError: false);
            return;
        }
        string Value(string key) => values.FirstOrDefault(kv => kv.Key == key).Value ?? "";
        author.BindToSelectedTemplate(Value("generator"), Value("matchType"));
        RefreshCommands();
    }

    /// <inheritdoc />
    public Task ResetOverrideAsync()
    {
        Need().ResetSelectedOverride();
        RefreshCommands();
        return Task.CompletedTask;
    }
}
