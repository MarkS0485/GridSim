using GridSim.Core.Diagnostics;
using GridSim.Core.Presentation;
using GridSim.Ui.Dashboard;

namespace GridSim.Ui.Tabs.AuditLog;

/// <summary>The colour the audit-log verdict is shown in: the theme's text colour before any check, its green for a
/// log that verified, its red for anything else (a tampered log, a bad anchor, an unreadable file).</summary>
public enum AuditVerdictTone
{
    /// <summary>No verdict shown yet: the theme's Text colour (the WPF window's starting foreground).</summary>
    Neutral,

    /// <summary>The whole log verified: the theme's Green.</summary>
    Verified,

    /// <summary>Not verified, or not checked at all: the theme's Red.</summary>
    Failed,
}

/// <summary>
/// The desktop audit-log viewer's logic (N151): read a <c>gridsim-audit/1</c> file, optionally against an external
/// anchor (entry count and head hash), and show the result of Core's <see cref="AuditLogDisplay"/>, the one display
/// path the web AUDIT LOG tab also uses and whose lines match <c>gridsim audit show</c>. A log that verifies shows every
/// entry and the OK line; a log that does NOT verify shows only the TAMPERED verdict, never the clean prefix (nothing
/// after a broken link can be trusted). A missing or unreadable file shows the error and no entries. Presentation
/// only: the file is read, never written.
///
/// <para>UI programme Phase 1.6 (third batch): this is the logic of the WPF <c>AuditLogWindow.cs</c> (its
/// <c>OpenAndShow</c> and <c>ShowVerdict</c>), moved here line for line so the Avalonia window shows the same verdicts
/// from the same code. The WPF window keeps its WPF shell (the controls built in code, the WPF file dialog) and asks
/// this model what to show. The two changes are at the framework boundary: the verdict colour, which the WPF window
/// looked up as the theme brush "Green" or "Red", is the <see cref="Tone"/> each front end turns into its own theme
/// brush; and the file dialog, which sat in the middle of <c>OpenAndShow</c>, is the chooser a front end passes to
/// <see cref="Open"/> or <see cref="OpenAsync"/> (it returns the chosen path, or null when the user cancels).</para>
/// </summary>
public sealed class AuditLogModel : ObservableModel
{
    // ---- the window's fixed texts (the WPF window's, verbatim) ----

    /// <summary>The window title.</summary>
    public const string WindowTitle = "GridSim - audit log (verified)";

    /// <summary>The label before the anchor's entry-count box.</summary>
    public const string AnchorCountLabel = "anchor count";

    /// <summary>The label before the anchor's head-hash box.</summary>
    public const string AnchorHeadLabel = "anchor head";

    /// <summary>The open button's text.</summary>
    public const string OpenButtonText = " OPEN LOG... ";

    /// <summary>The note under the anchor bar.</summary>
    public const string Note = "The whole hash chain is checked before anything is shown. The optional anchor (count and head " +
                               "hash recorded elsewhere) also catches a log cut short at the end or rewritten end to end.";

    /// <summary>The file dialog's title.</summary>
    public const string DialogTitle = "Choose a gridsim-audit/1 log";

    /// <summary>The file dialog's filter, in the WPF filter syntax ("name|patterns|name|patterns").</summary>
    public const string DialogFilter = "Audit logs (*.jsonl;*.log;*.json)|*.jsonl;*.log;*.json|All files (*.*)|*.*";

    /// <summary>
    /// The same filter as name and patterns, for a front end whose dialog takes them apart (the Avalonia storage
    /// provider): read from <see cref="DialogFilter"/>, so the two cannot drift.
    /// </summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Patterns)> DialogFileTypes { get; } = ParseFilter(DialogFilter);

    // ---- what the window shows ----

    /// <summary>The anchor's entry count, as typed (empty = no anchor).</summary>
    private string _anchorCount = "";
    public string AnchorCount { get => _anchorCount; set => Set(ref _anchorCount, value ?? ""); }

    /// <summary>The anchor's head hash, as typed (empty = no anchor).</summary>
    private string _anchorHead = "";
    public string AnchorHead { get => _anchorHead; set => Set(ref _anchorHead, value ?? ""); }

    /// <summary>The verdict line (empty before the first check).</summary>
    private string _verdict = "";
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }

    /// <summary>The colour the verdict is shown in.</summary>
    private AuditVerdictTone _tone = AuditVerdictTone.Neutral;
    public AuditVerdictTone Tone { get => _tone; private set => Set(ref _tone, value); }

    /// <summary>Every entry line of a log that verified, one per line; otherwise empty.</summary>
    private string _entries = "";
    public string Entries { get => _entries; private set => Set(ref _entries, value); }

    /// <summary>
    /// The first half of the WPF <c>OpenAndShow</c>, before its file dialog: clear the entries and parse the anchor.
    /// A bad anchor shows the NOT CHECKED verdict and returns false, and no dialog is to be opened.
    /// </summary>
    public bool TryBeginOpen(out AuditAnchor? anchor)
    {
        Entries = "";
        if (!AuditLogDisplay.TryParseAnchor(AnchorCount, AnchorHead, out anchor, out string? anchorError))
        {
            ShowVerdict($"NOT CHECKED: {anchorError}", ok: false);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The second half of the WPF <c>OpenAndShow</c>, after its file dialog returned <paramref name="fileName"/>: verify
    /// the file through Core and show exactly what <see cref="AuditLogDisplay"/> returns: entries only when the whole
    /// log verified; otherwise the verdict alone.
    /// </summary>
    public void ShowFile(string fileName, AuditAnchor? anchor)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        AuditLogDisplayResult result;
        try
        {
            result = AuditLogDisplay.FromFile(fileName, anchor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Missing, unreadable or oversized: an error, never an empty clean log.
            ShowVerdict($"NOT CHECKED: cannot read '{fileName}': {ex.Message}", ok: false);
            return;
        }
        if (result.Verified) Entries = string.Join(Environment.NewLine, result.EntryLines);
        ShowVerdict(result.Verdict, result.Verified);
    }

    /// <summary>
    /// The whole of the WPF <c>OpenAndShow</c> (the OPEN LOG... button), with the file dialog as
    /// <paramref name="chooseFile"/>: it is asked only when the anchor parsed, and a null answer (cancelled) leaves the
    /// verdict as it was, with the entries cleared, as before.
    /// </summary>
    public void Open(Func<string?> chooseFile)
    {
        ArgumentNullException.ThrowIfNull(chooseFile);
        if (!TryBeginOpen(out AuditAnchor? anchor)) return;
        string? file = chooseFile();
        if (file is null) return;
        ShowFile(file, anchor);
    }

    /// <summary><see cref="Open"/> with a file dialog that answers later (the Avalonia storage provider).</summary>
    public async Task OpenAsync(Func<Task<string?>> chooseFile)
    {
        ArgumentNullException.ThrowIfNull(chooseFile);
        if (!TryBeginOpen(out AuditAnchor? anchor)) return;
        string? file = await chooseFile().ConfigureAwait(true);   // back on the caller's (UI) thread to show the result
        if (file is null) return;
        ShowFile(file, anchor);
    }

    // The WPF ShowVerdict: the text, and the theme colour of the outcome.
    private void ShowVerdict(string text, bool ok)
    {
        Verdict = text;
        Tone = ok ? AuditVerdictTone.Verified : AuditVerdictTone.Failed;
    }

    /// <summary>Split a WPF filter ("name|a;b|name|c") into its names and patterns; a malformed one is a coding error.</summary>
    private static IReadOnlyList<(string Name, IReadOnlyList<string> Patterns)> ParseFilter(string filter)
    {
        string[] parts = filter.Split('|');
        if (parts.Length % 2 != 0) throw new InvalidOperationException($"The file filter has an odd number of parts: {filter}");
        var types = new List<(string, IReadOnlyList<string>)>();
        for (int i = 0; i < parts.Length; i += 2)
        {
            // The display name drops its "(*.x;...)" suffix: the dialog shows the patterns itself.
            string name = parts[i];
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            types.Add((paren >= 0 ? name[..paren] : name, parts[i + 1].Split(';')));
        }
        return types;
    }
}
