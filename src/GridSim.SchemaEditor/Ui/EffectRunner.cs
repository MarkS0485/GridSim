using GridSim.Core.IO.Json.CaseEditing;
using GridSim.SchemaEditor.Ui.Views;

namespace GridSim.SchemaEditor.Ui;

/// <summary>
/// Runs the effects the editor asks for (save, check) through Core's <see cref="SaveGate"/> and feeds the report back
/// into the editor: the headline on the message line, and the error list screen when there are errors (Enter on an
/// error opens the record it names). Shared by the console host and the tests, so both exercise the same path.
/// </summary>
internal static class EffectRunner
{
    /// <summary>Run one effect. <paramref name="beforeReplace"/> is the committer's test hook (null in production).</summary>
    public static void Run(Editor editor, EditorEffect effect, Action<string>? beforeReplace = null)
    {
        ArgumentNullException.ThrowIfNull(editor);
        switch (effect)
        {
            case EditorEffect.Save:
            case EditorEffect.SaveConfirmed:
                Save(editor, new SaveOptions(ConfirmNewWarnings: effect == EditorEffect.SaveConfirmed, BeforeReplace: beforeReplace));
                break;
            case EditorEffect.Check:
            {
                SaveReport r = SaveGate.Run(editor.Session, new SaveOptions(CheckOnly: true));
                editor.LastReport = r;
                editor.Say(r.Headline + (r.NewWarnings.Count > 0 ? $" {r.NewWarnings.Count} new warning(s): {r.NewWarnings[0]}" : ""), r.Outcome != SaveOutcome.CheckPassed);
                if (r.Errors.Count > 0) editor.Push(ErrorList(editor.Session, r));
                break;
            }
        }
    }

    private static void Save(Editor editor, SaveOptions options)
    {
        SaveReport r = SaveGate.Run(editor.Session, options);
        editor.LastReport = r;
        switch (r.Outcome)
        {
            case SaveOutcome.Saved:
                editor.Say(r.Headline + (r.Notes.Count(n => n.StartsWith("changed: ", StringComparison.Ordinal)) is int n and > 0 ? $" {n} change(s)." : ""));
                editor.AfterChange(null);
                if (editor.QuitAfterSave) editor.Finished = true;
                break;
            case SaveOutcome.NothingToSave:
                editor.Say(r.Headline);
                if (editor.QuitAfterSave) editor.Finished = true;
                break;
            case SaveOutcome.NeedsConfirmation:
                editor.AskConfirm($"{r.Headline} First: {r.NewWarnings[0]} (Y saves, N does not)", "YN", c =>
                {
                    if (c == 'Y') return EditorEffect.SaveConfirmed;
                    editor.QuitAfterSave = false;
                    editor.Say("Not saved.");
                    return EditorEffect.None;
                });
                break;
            default:
                // Refused, changed on disk, commit failed: never shown as success; the disk is as the report says.
                editor.QuitAfterSave = false;
                editor.Say(r.Headline, error: true);
                if (r.Errors.Count > 0) editor.Push(ErrorList(editor.Session, r));
                break;
        }
    }

    /// <summary>The error list screen: one row per problem; Enter opens the record the problem's pointer is in.</summary>
    public static View ErrorList(CaseSession session, SaveReport report)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>();
        var targets = new List<(int Doc, string Pointer)?>();
        foreach (SaveProblem p in report.Errors)
        {
            lines.Add(p.ToString());
            CaseDocument? d = session.Documents.FirstOrDefault(x => string.Equals(x.FileName, p.File, StringComparison.Ordinal));
            targets.Add(d is null || !d.Exists ? null : (d.Id, CaseOps.TopRecordPointer(session, d, p.Pointer)));
        }
        return new TextListView(session, "Errors", $"{report.Errors.Count} problem(s); Enter opens the record", lines, targets);
    }
}
