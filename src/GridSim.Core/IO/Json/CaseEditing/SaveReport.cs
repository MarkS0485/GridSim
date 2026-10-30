namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>How a save (or a check) ended. Only <see cref="Saved"/> wrote anything.</summary>
internal enum SaveOutcome
{
    /// <summary>No document differs from its baseline: nothing was written ("No changes to save").</summary>
    NothingToSave,

    /// <summary>Every dirty file was written, verified and renamed into place.</summary>
    Saved,

    /// <summary>The gate found errors: NOT SAVED, the disk is unchanged.</summary>
    Refused,

    /// <summary>The edit introduces model warnings that pre-flight did not have; the user must say yes explicitly. Nothing written.</summary>
    NeedsConfirmation,

    /// <summary>A target file changed on disk since the case was opened (or last saved): refused, nothing written.</summary>
    ChangedOnDisk,

    /// <summary>Writing failed part-way; already-replaced files were restored from memory (see the report text).</summary>
    CommitFailed,

    /// <summary>A check (no write) found no error.</summary>
    CheckPassed,

    /// <summary>A check (no write) found errors.</summary>
    CheckFailed,
}

/// <summary>One problem the gate found: the file, the JSON pointer inside it ("" when not tied to a value) and the message.</summary>
internal sealed record SaveProblem(string File, string Pointer, string Message)
{
    public override string ToString() => File + (Pointer.Length > 0 ? " " + Pointer : "") + ": " + Message;
}

/// <summary>
/// What the save gate reports (SCHEMA-EDITOR-PLAN.md section 7). A refused save is never shown as a success: the
/// <see cref="Headline"/> of anything but <see cref="SaveOutcome.Saved"/> starts with what did NOT happen.
/// </summary>
internal sealed record SaveReport(
    SaveOutcome Outcome,
    string Headline,
    IReadOnlyList<SaveProblem> Errors,
    IReadOnlyList<string> NewWarnings,
    IReadOnlyList<string> FilesWritten,
    IReadOnlyList<string> Notes)
{
    /// <summary>True only when the files were written.</summary>
    public bool Saved => Outcome == SaveOutcome.Saved;

    /// <summary>The whole report as plain text lines (the --check output and the error list's source).</summary>
    public IEnumerable<string> Lines()
    {
        yield return Headline;
        foreach (SaveProblem e in Errors) yield return "  error: " + e;
        foreach (string w in NewWarnings) yield return "  new warning: " + w;
        foreach (string f in FilesWritten) yield return "  written: " + f;
        foreach (string n in Notes) yield return "  note: " + n;
    }
}
