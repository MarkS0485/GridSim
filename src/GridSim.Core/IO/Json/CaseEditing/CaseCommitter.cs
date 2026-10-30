using System.Globalization;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>
/// Writes a session's dirty documents to disk as safely as plain files allow (SCHEMA-EDITOR-PLAN.md section 7,
/// step 5; risk R5): first every new file is written beside its target as <c>&lt;file&gt;.gridsim-new</c>, flushed
/// and read back to verify its bytes; only when ALL are verified is each renamed over its target. A failure while
/// writing the new files leaves every target untouched; a failure while renaming rewrites the targets already
/// replaced from the in-memory baselines and reports loudly. A multi-file save is still not atomic if the process
/// dies mid-rename; the leftover <c>.gridsim-new</c> files are then reported the next time the case is opened.
/// Files the case does not read are never opened.
/// </summary>
internal static class CaseCommitter
{
    /// <summary>One file to commit: the document and what its file must become (null = the file must not exist).</summary>
    private sealed record Target(CaseDocument Doc, byte[]? NewBytes, byte[]? Baseline)
    {
        public string NewPath => Doc.Path + CaseSession.NewFileSuffix;
    }

    /// <summary>
    /// Commit the dirty documents of <paramref name="session"/>. Returns the files written, or throws
    /// <see cref="CaseCommitException"/> (with the targets restored) when anything fails. <paramref name="beforeReplace"/>
    /// is a test hook called with each file name just before it is renamed into place (it may throw to simulate a
    /// failure); production passes null.
    /// </summary>
    public static IReadOnlyList<string> Commit(CaseSession session, Action<string>? beforeReplace = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var targets = session.DirtyDocuments.Select(d => new Target(d, d.Current?.ToArray(), d.BaselineCopy())).ToList();
        if (targets.Count == 0) return Array.Empty<string>();

        // Last check for an external change, immediately before writing (the gate checked too, earlier).
        List<string> changed = ChangedOnDisk(targets.Select(t => t.Doc));
        if (changed.Count > 0)
            throw new CaseCommitException($"NOT SAVED: changed on disk since opened: {string.Join(", ", changed)}. Nothing was written.");

        // Phase 1: write and verify every new file. Nothing is replaced yet.
        try
        {
            foreach (Target t in targets.Where(t => t.NewBytes is not null))
            {
                WriteDurably(t.NewPath, t.NewBytes!);
                byte[] back = File.ReadAllBytes(t.NewPath);
                if (!back.AsSpan().SequenceEqual(t.NewBytes))
                    throw new IOException($"{Path.GetFileName(t.NewPath)}: the bytes read back differ from the bytes written ({back.Length.ToString(CultureInfo.InvariantCulture)} versus {t.NewBytes!.Length.ToString(CultureInfo.InvariantCulture)} bytes).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteNewFiles(targets);
            throw new CaseCommitException($"NOT SAVED: writing the new files failed, every target is unchanged: {ex.Message}", ex);
        }

        // Phase 2: rename each verified file over its target (or delete a target that must no longer exist).
        var replaced = new List<Target>();
        try
        {
            foreach (Target t in targets)
            {
                beforeReplace?.Invoke(t.Doc.FileName);
                if (t.NewBytes is null) { if (File.Exists(t.Doc.Path)) File.Delete(t.Doc.Path); }
                else File.Move(t.NewPath, t.Doc.Path, overwrite: true);
                replaced.Add(t);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string restore = Restore(replaced);
            DeleteNewFiles(targets);
            throw new CaseCommitException(
                $"NOT SAVED: replacing {targets[replaced.Count].Doc.FileName} failed ({ex.Message}). {restore}", ex);
        }

        return targets.Select(t => t.Doc.FileName + (t.NewBytes is null ? " (removed)" : "")).ToList();
    }

    /// <summary>The documents whose file on disk no longer matches their baseline (existence or bytes).</summary>
    public static List<string> ChangedOnDisk(IEnumerable<CaseDocument> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        var changed = new List<string>();
        foreach (CaseDocument d in docs)
        {
            bool exists = File.Exists(d.Path);
            if (exists != d.BaselineExists) { changed.Add(d.FileName); continue; }
            if (exists && !File.ReadAllBytes(d.Path).AsSpan().SequenceEqual(d.Baseline)) changed.Add(d.FileName);
        }
        return changed;
    }

    /// <summary>Write bytes and flush them through the OS cache before returning.</summary>
    private static void WriteDurably(string path, byte[] bytes)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Put already-replaced targets back from their in-memory baselines; returns a sentence saying how it went.</summary>
    private static string Restore(List<Target> replaced)
    {
        var failed = new List<string>();
        foreach (Target t in replaced)
        {
            try
            {
                if (t.Baseline is null) { if (File.Exists(t.Doc.Path)) File.Delete(t.Doc.Path); }
                else WriteDurably(t.Doc.Path, t.Baseline);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{t.Doc.FileName} ({ex.Message})");
            }
        }
        if (replaced.Count == 0) return "No file had been replaced; every target is unchanged.";
        return failed.Count == 0
            ? $"The {replaced.Count.ToString(CultureInfo.InvariantCulture)} file(s) already replaced were restored from memory: {string.Join(", ", replaced.Select(t => t.Doc.FileName))}."
            : $"RESTORE FAILED for {string.Join(", ", failed)}: those files now hold the NEW content; the editor still holds the original bytes in memory.";
    }

    private static void DeleteNewFiles(IEnumerable<Target> targets)
    {
        foreach (Target t in targets)
        {
            try { if (File.Exists(t.NewPath)) File.Delete(t.NewPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* reported as a leftover at the next open */ }
        }
    }
}

/// <summary>A commit failed; the message says what was restored. The editor keeps every edit in memory.</summary>
internal sealed class CaseCommitException : Exception
{
    public CaseCommitException() { }
    public CaseCommitException(string message) : base(message) { }
    public CaseCommitException(string message, Exception inner) : base(message, inner) { }
}
