using System.Globalization;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Licensing;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>Options for one run of the <see cref="SaveGate"/>.</summary>
/// <param name="CheckOnly">Run every stage but write nothing (F9 / K, and the --check mode).</param>
/// <param name="ConfirmNewWarnings">The user has said yes to the new model warnings listed by an earlier run.</param>
/// <param name="BeforeReplace">Test hook passed to <see cref="CaseCommitter.Commit"/> (may throw to simulate a failure).</param>
internal sealed record SaveOptions(bool CheckOnly = false, bool ConfirmNewWarnings = false, Action<string>? BeforeReplace = null);

/// <summary>
/// The staged, fail-loud save of a case (SCHEMA-EDITOR-PLAN.md section 7). In order:
/// <list type="number">
/// <item>no dirty file: "No changes to save", nothing written;</item>
/// <item>external change: any target that differs on disk from its baseline refuses the save;</item>
/// <item>stage: the case's own files (current state) are written to a fresh temporary folder, then
/// (a) every dirty file is validated against its schema, (b) the folder is loaded with
/// <see cref="JsonGridLoader.LoadDirectory"/> (dispatch on) exactly as every verb loads a case, (c) the licence gate
/// checks the bus count against the case's real path, and (d) model warnings that pre-flight did not have need an
/// explicit yes;</item>
/// <item>any error: "NOT SAVED: n error(s)" with every problem listed; the disk is unchanged;</item>
/// <item>commit (<see cref="CaseCommitter"/>); on success the baselines advance and the undo history is kept.</item>
/// </list>
/// A check runs the same stages over ALL files (not only dirty ones) and writes nothing.
/// </summary>
internal static class SaveGate
{
    /// <summary>Run the gate (and, unless checking, commit). Never throws for bad case content; the report says it all.</summary>
    public static SaveReport Run(CaseSession session, SaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        options ??= new SaveOptions();
        var dirty = session.DirtyDocuments.ToList();
        var notes = new List<string>();
        foreach (string leftover in session.LeftoverFiles)
            notes.Add($"{leftover} was left by an interrupted save before this case was opened; check it and delete it.");

        if (!options.CheckOnly && dirty.Count == 0)
            return new SaveReport(SaveOutcome.NothingToSave, "No changes to save.", Array.Empty<SaveProblem>(), Array.Empty<string>(), Array.Empty<string>(), notes);

        // 2. External change. GR-262: every document of the case, not only the edited ones: a save validates the edited
        // files together with the others as they were when opened, so another writer's change to an unedited file would
        // make the saved combination one that was never checked.
        List<string> changed = CaseCommitter.ChangedOnDisk(session.Documents);
        if (changed.Count > 0)
        {
            var problems = changed.Select(f => new SaveProblem(f, "", "changed on disk since the case was opened (or last saved); reopen it to see the new content.")).ToList();
            return new SaveReport(options.CheckOnly ? SaveOutcome.CheckFailed : SaveOutcome.ChangedOnDisk,
                (options.CheckOnly ? "CHECK FAILED: " : "NOT SAVED: ") + $"{changed.Count.ToString(CultureInfo.InvariantCulture)} file(s) changed on disk since opened: {string.Join(", ", changed)}.",
                problems, Array.Empty<string>(), Array.Empty<string>(), notes);
        }

        // 3. Stage and check.
        (List<SaveProblem> errors, List<string> newWarnings, List<string> allWarnings) = session.Format == CaseFormat.V1Folder
            ? StageV1(session, options.CheckOnly ? session.Documents.Where(d => d.Exists).ToList() : dirty)
            : StageV2(session, notes);

        if (options.CheckOnly)
        {
            return errors.Count == 0
                ? new SaveReport(SaveOutcome.CheckPassed, "CHECK PASSED: the case " + (session.Format == CaseFormat.V1Folder ? "loads, validates" : "validates (v2 schemas and semantic checks)")
                    + $" and is within the licence ({session.BusCount.ToString(CultureInfo.InvariantCulture)} buses)." + (dirty.Count > 0 ? $" Unsaved changes in {string.Join(", ", dirty.Select(d => d.FileName))}." : ""),
                    Array.Empty<SaveProblem>(), newWarnings, Array.Empty<string>(), notes)
                : new SaveReport(SaveOutcome.CheckFailed, $"CHECK FAILED: {errors.Count.ToString(CultureInfo.InvariantCulture)} error(s).", errors, newWarnings, Array.Empty<string>(), notes);
        }

        // 4. Any error refuses the save.
        if (errors.Count > 0)
            return new SaveReport(SaveOutcome.Refused, $"NOT SAVED: {errors.Count.ToString(CultureInfo.InvariantCulture)} error(s). The files on disk are unchanged.",
                errors, newWarnings, Array.Empty<string>(), notes);
        if (newWarnings.Count > 0 && !options.ConfirmNewWarnings)
            return new SaveReport(SaveOutcome.NeedsConfirmation, $"NOT SAVED YET: the change adds {newWarnings.Count.ToString(CultureInfo.InvariantCulture)} model warning(s) the case did not have. Save anyway?",
                Array.Empty<SaveProblem>(), newWarnings, Array.Empty<string>(), notes);

        // 5. Commit.
        IReadOnlyList<string> changes = session.ChangesSinceSave;
        IReadOnlyList<string> written;
        try
        {
            written = CaseCommitter.Commit(session, options.BeforeReplace);
        }
        catch (CaseCommitException ex)
        {
            return new SaveReport(SaveOutcome.CommitFailed, ex.Message, Array.Empty<SaveProblem>(), Array.Empty<string>(), Array.Empty<string>(), notes);
        }
        session.MarkSaved();
        session.AcceptWarnings(allWarnings);
        var lines = new List<string>(notes);
        lines.AddRange(changes.Select(c => "changed: " + c));
        return new SaveReport(SaveOutcome.Saved, $"Saved {written.Count.ToString(CultureInfo.InvariantCulture)} file(s): {string.Join(", ", written)}.",
            Array.Empty<SaveProblem>(), newWarnings, written, lines);
    }

    /// <summary>
    /// Stage a v1 case and run checks (a)-(d). Returns the errors, the warnings that pre-flight did not have, and all
    /// the model's warnings (which become the accepted set after a save).
    /// </summary>
    private static (List<SaveProblem> Errors, List<string> NewWarnings, List<string> AllWarnings) StageV1(CaseSession session, List<CaseDocument> validate)
    {
        var errors = new List<SaveProblem>();

        // (a) Schema: every file to validate, whole document against its schema.
        foreach (CaseDocument d in validate.Where(d => d.Exists))
            foreach (SchemaError e in BundledSchemas.V1Validate.Validate(d.Text.ToElement(), d.SchemaFile))
                errors.Add(new SaveProblem(d.FileName, e.Pointer, e.Message));
        // N327 part 2 (CF5): the provenance trust rules, in code (an output tag written as a case input is refused).
        foreach (CaseDocument d in validate.Where(d => d.Exists))
            errors.AddRange(ProvenanceProblems(d));

        // Stage the current state of the case's own files in a fresh temporary folder.
        string stage = Path.Combine(Path.GetTempPath(), "gridsim-edit-" + Guid.NewGuid().ToString("N"));
        var newWarnings = new List<string>();
        var allWarnings = new List<string>();
        try
        {
            Directory.CreateDirectory(stage);
            foreach (CaseDocument d in session.Documents.Where(d => d.Exists))
                File.WriteAllBytes(Path.Combine(stage, d.FileName), d.Text.ToArray());

            // (b) The loader, exactly as every verb runs it.
            GridModel? model = null;
            try
            {
                model = JsonGridLoader.LoadDirectory(stage, dispatch: true);
            }
            catch (Exception ex) when (CaseSession.IsLoaderFailure(ex))
            {
                errors.Add(new SaveProblem(FileOf(ex.Message), "", "the loader refuses the case: " + ex.Message));

                // (c) The licence too, on the session's own bus count: a refused case is still checked, so every reason
                // the save fails is listed at once. (Since D-DOC-082 the loader refuses an island with no slack, so a
                // bus added with no branch yet is refused by the loader; its licence verdict used to be lost with it.)
                GateResult early = session.Gate.Check(session.BusCount, session.IsBundled);
                if (!early.Allowed) errors.Add(new SaveProblem("buses.json", "", "licence: " + early.Message));
            }

            if (model is not null)
            {
                // (c) The licence, against the case's real path (a staged copy is never bundled).
                GateResult gate = session.Gate.Check(model.Buses.Count, session.IsBundled);
                if (!gate.Allowed) errors.Add(new SaveProblem("buses.json", "", "licence: " + gate.Message));

                // (d) Model warnings the case did not have at open (or at the last save).
                allWarnings.AddRange(CaseSession.WarningsOf(model));
                var before = new HashSet<string>(session.Preflight.ModelWarnings, StringComparer.Ordinal);
                newWarnings.AddRange(allWarnings.Where(w => !before.Contains(w)));
            }
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp folder is harmless */ }
        }
        return (errors, newWarnings, allWarnings);
    }

    /// <summary>
    /// The v2 stages. A v2 save is "schema-valid plus the editor's semantic checks": (a) the manifest and every network
    /// file against their schemas, (b) <see cref="V2CaseChecks"/>, (c) the licence for the case's bus count, (d)
    /// semantic warnings pre-flight did not have. Per Q3 it does not project to v1 or run JsonGridLoader.
    /// N327 (1.95.238): when (a)-(c) pass, the gate also runs the v2 loader (<see cref="V2CaseLoader"/>) over a staged
    /// copy of a case manifest that imports nothing, and reports its verdict as a note: "the v2 loader reads the case"
    /// or "WARNING: the v2 loader refuses the case: ...". The verdict is a note, not an error, because refusing the save
    /// on it changes what saves (an existing editor test saves a two-bus case the loader refuses for an island with no
    /// slack); that ruling was Q32 (QUESTIONS-FOR-MARK.md, N327; see CF5 below). A case that imports library templates is not loaded:
    /// imports resolve against the shipped data folder beside the program that loads the case, which the editor cannot
    /// know, so the note keeps saying the loader is not run and how to check a load. A lone network file is not loaded
    /// either (the loader reads a case manifest).
    /// D-DOC-162 (DOC-17): this comment and the note used to say "there is no production v2 loader yet (roadmap P2)".
    /// CF5 (N327, Q32 recommendation accepted by main): the loader's refusal now REFUSES the save (an error naming the
    /// loader's message), as the v1 gate refuses what JsonGridLoader refuses; the WARNING note is kept beside it so the
    /// report reads the same way. Part 2: a provenance tag that is an output tag (Counterfactual, UnvalidatedAtScale)
    /// written anywhere in a case file is refused (<see cref="ProvenanceProblems"/>).
    /// Phase 5b (2026-10-20): a case's asset overlay files are session documents, so (a) validates them against
    /// assets.schema.json, (b) runs <see cref="Overlay.V2AssetChecker"/> on them against the case (every problem listed,
    /// in <see cref="V2CaseChecks"/>), and the loader stage stages them beside the rest, so the v2 loader reads the case
    /// WITH its overlay exactly as every verb will; a commit writes them with the other files.
    /// </summary>
    private static (List<SaveProblem> Errors, List<string> NewWarnings, List<string> AllWarnings) StageV2(CaseSession session, List<string> notes)
    {
        var errors = new List<SaveProblem>();
        foreach (CaseDocument d in session.Documents.Where(d => d.Exists))
            foreach (SchemaError e in BundledSchemas.V2Validate.Validate(d.Text.ToElement(), d.SchemaFile))
                errors.Add(new SaveProblem(d.FileName, e.Pointer, e.Message));
        foreach (CaseDocument d in session.Documents.Where(d => d.Exists))
            errors.AddRange(ProvenanceProblems(d));   // N327 part 2 (CF5)
        (List<SaveProblem> semantic, List<string> warnings) = V2CaseChecks.Run(session);
        errors.AddRange(semantic);
        GateResult gate = session.Gate.Check(session.BusCount, session.IsBundled);
        if (!gate.Allowed) errors.Add(new SaveProblem("case", "", "licence: " + gate.Message));
        (string note, string? refusal) = LoaderNote(session, errors.Count == 0);
        notes.Add(note);
        // CF5 (N327): a case the v2 loader refuses is not saved (the v1 gate's rule for JsonGridLoader).
        if (refusal is not null) errors.Add(new SaveProblem("case", "", refusal));
        var before = new HashSet<string>(session.Preflight.ModelWarnings, StringComparer.Ordinal);
        return (errors, warnings.Where(w => !before.Contains(w)).ToList(), warnings);
    }

    /// <summary>
    /// The v2 loader stage of <see cref="StageV2"/> (N327), as the note it adds. When the case can be loaded here (a
    /// case manifest, no imports, and the earlier stages passed) the case's own files (current state, at their paths
    /// relative to the case folder) and the embedded v2 schemas are written to a fresh temporary folder and the staged
    /// manifest is loaded with <see cref="V2CaseLoader"/>; nothing is written to the case. Returns the note and, when the
    /// loader refuses the case (CF5, N327), the refusal the gate adds as an error; null when it read the case or was not run.
    /// </summary>
    private static (string Note, string? Refusal) LoaderNote(CaseSession session, bool checksPassed)
    {
        const string NotRun = "a v2 save is checked against the v2 schemas and the editor's semantic checks only; the v2 loader is not run on save";
        const string HowTo = "load the case (for example gridsim validate or pf) to confirm GridSim reads it.";
        if (session.Format != CaseFormat.V2Case)
            return ($"{NotRun} of a lone network file (the loader reads a case manifest), so {HowTo}", null);
        CaseDocument manifest = session.Documents[0];
        if (RecordRead.Member(manifest.Text.Root, "imports", StringComparer.Ordinal) is { Kind: JsonSourceKind.Array } imports && imports.Elements.Count > 0)
            return ($"{NotRun} of a case that imports library templates (imports resolve against the data folder of the program that loads the case), so {HowTo}", null);
        if (!checksPassed)
            return ($"{NotRun} while the schema, semantic or licence checks fail; fix those, then save or check again.", null);

        string stage = Path.Combine(Path.GetTempPath(), "gridsim-edit-" + Guid.NewGuid().ToString("N"));
        string caseDir = Path.Combine(stage, "case");
        try
        {
            string schemas = Path.Combine(stage, "schemas");
            Directory.CreateDirectory(caseDir);
            BundledSchemas.WriteV2SchemaFolder(schemas);
            foreach (CaseDocument d in session.Documents.Where(d => d.Exists))
            {
                string target = Path.Combine(caseDir, Path.GetRelativePath(session.RootPath, d.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, d.Text.ToArray());
            }
            V2CaseLoader.Load(Path.Combine(caseDir, Path.GetRelativePath(session.RootPath, manifest.Path)), schemas, dataRoot: null, dispatch: true);
            return ("the v2 loader reads the case (staged copy, dispatch on, as every verb loads it).", null);
        }
        catch (Exception ex) when (CaseSession.IsLoaderFailure(ex))
        {
            // The staged folder's path means nothing to the user: show the case folder in its place.
            string why = ex.Message.Replace(caseDir, session.RootPath, StringComparison.Ordinal);
            return ("WARNING: the v2 loader refuses the case, so GridSim will not run it as saved: " + why,
                "the v2 loader refuses the case: " + why);
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp folder is harmless */ }
        }
    }

    /// <summary>
    /// N327 part 2 (CF5): every provenance tag in <paramref name="d"/> that is an output tag
    /// (<see cref="Diagnostics.ProvenanceTrust.IsOutputOnly"/>) is a problem: a case file is an input, and a counterfactual
    /// or an unvalidated-at-scale result is never an input (technical ch. 8). A tag the schema does not know is left to the
    /// schema check. A document that does not parse yields nothing here (the schema check reports it).
    /// </summary>
    private static IEnumerable<SaveProblem> ProvenanceProblems(CaseDocument d)
    {
        System.Text.Json.JsonElement root;
        try { root = d.Text.ToElement(); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { yield break; }
        foreach ((string pointer, string tag) in Diagnostics.ProvenanceTrust.OutputTagsIn(root))
            yield return new SaveProblem(d.FileName, pointer,
                $"provenance {tag} is an output tag and cannot be written as a case input (a counterfactual is never an input to another replay; an unvalidated-at-scale result is never fed back as data).");
    }

    /// <summary>The case file a loader message names at its start ("generation.json: ..."), or "case" when none.</summary>
    private static string FileOf(string message)
    {
        int colon = message.IndexOf(':', StringComparison.Ordinal);
        string head = colon > 0 ? message[..colon] : "";
        return CaseFileSpec.ForFile(head) is not null ? head : "case";
    }
}
