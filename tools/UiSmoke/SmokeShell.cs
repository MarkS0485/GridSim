using GridSim.App.Assets;
using GridSim.App.Shell;
using GridSim.Ui.Assets.Editing;
using GridSim.Ui.Shell;

namespace GridSim.UiSmoke;

// The parts of the shell that would reach outside the window (dialogs, OS pickers, Author mode's questions), replaced
// for the smoke run. UiSmoke never clicks a menu item that opens one, so none of these is expected to run; each one that
// does is recorded and answers "no" (cancel, discard), so a stray call can neither block the run on an invisible dialog
// nor write a file. The recorded calls are listed in the run's output and in manifest.json.

/// <summary>Shell actions that open no dialog: each call is recorded and does nothing.</summary>
internal sealed class SmokeShellActions(List<string> calls) : IShellActions
{
    public Task OpenCaseAsync() { calls.Add("shell: Open case..."); return Task.CompletedTask; }

    public Task OpenRunFolderAsync() { calls.Add("shell: Open run folder..."); return Task.CompletedTask; }

    public Task ExitAsync() { calls.Add("shell: Exit"); return Task.CompletedTask; }

    public Task ShowAboutAsync() { calls.Add("shell: About"); return Task.CompletedTask; }

    public Task ShowThirdPartyNoticesAsync() { calls.Add("shell: Third-party notices"); return Task.CompletedTask; }

    public Task ShowLicenceAsync() { calls.Add("shell: Licence"); return Task.CompletedTask; }
}

/// <summary>OS pickers that open nothing: every pick is recorded and cancelled.</summary>
internal sealed class SmokePickers(List<string> calls) : IShellPickers
{
    public Task<PickResult> PickFolderAsync(string title, string? startFolder)
    {
        calls.Add("picker: " + title);
        return Task.FromResult(PickResult.Cancelled);
    }

    public Task<PickResult> PickFileAsync(string title, string? startFolder, string typeName, IReadOnlyList<string> patterns)
    {
        calls.Add("picker: " + title);
        return Task.FromResult(PickResult.Cancelled);
    }
}

/// <summary>
/// Author mode's questions, answered without a dialog: unsaved changes are discarded (UiSmoke edits nothing, so there
/// are none), a yes / no is no, and a text or a new record's fields are cancelled. So Author mode can never save.
/// </summary>
internal sealed class SmokeAuthorPrompts(List<string> calls) : IAuthorPrompts
{
    public Task<DirtyChoice> AskSaveDiscardCancelAsync(string message)
    {
        calls.Add("author: save / discard / cancel (answered Discard)");
        return Task.FromResult(DirtyChoice.Discard);
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        calls.Add("author: confirm " + title + " (answered no)");
        return Task.FromResult(false);
    }

    public Task<string?> AskTextAsync(string title, string message, string initial)
    {
        calls.Add("author: text " + title + " (cancelled)");
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
    {
        calls.Add("author: fields " + title + " (cancelled)");
        return Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>?>(null);
    }
}
