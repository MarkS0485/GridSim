using System.Globalization;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Presentation;

/// <summary>
/// What a shell shows for one audit log (N151): the verdict line and, only when the WHOLE log verified, one line per
/// entry. Built by <see cref="AuditLogDisplay"/>; the web and desktop shells render it as given, so neither words an
/// entry or a verdict differently from the other or from the CLI.
/// </summary>
/// <param name="Verified">True only when every entry verified (and the anchor, when one was given, matched).</param>
/// <param name="EntryLines">One <see cref="AuditLogFile.Describe"/> line per entry, in order. ALWAYS empty when
/// <see cref="Verified"/> is false: nothing after a broken link can be trusted, and showing the clean prefix next to
/// a tampered tail invites reading it as evidence (the same rule as <c>gridsim audit show</c>).</param>
/// <param name="Verdict">The CLI's own verdict line: <c>OK &lt;label&gt;: &lt;n&gt; entries, head &lt;hash&gt;</c>
/// or <c>TAMPERED &lt;label&gt;: entry &lt;i&gt;: &lt;reason&gt; (&lt;n&gt; entries verified before it)</c>.</param>
/// <param name="Anchor">The anchor of the verified log (entry count and head hash) to record elsewhere, or null when
/// the log did not verify.</param>
public sealed record AuditLogDisplayResult(bool Verified, IReadOnlyList<string> EntryLines, string Verdict, AuditAnchor? Anchor);

/// <summary>
/// The one display path for a hash-chained audit log in the presentation shells (N151, following N064's
/// <c>gridsim audit show</c>). It runs exactly the verification <see cref="AuditLogFile.Verify"/> runs and words the
/// result exactly as the CLI does: on success every entry through <see cref="AuditLogFile.Describe"/> and the
/// <c>OK</c> line; on any failure NO entries and only the <c>TAMPERED</c> verdict. Presentation only: it computes
/// nothing and changes nothing on disk.
/// </summary>
public static class AuditLogDisplay
{
    /// <summary>The largest log the shells accept (64 MiB). A log is one short JSON line per run, so a real log is
    /// many orders of magnitude smaller; the cap stops a stray upload from filling the web server's memory.</summary>
    public const long MaxLogBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Verify and describe the log file at <paramref name="path"/> (the desktop shell). A missing or unreadable file
    /// throws (<see cref="FileNotFoundException"/>, <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>):
    /// a deleted audit log must never read as an empty, clean one. The verdict names the file as given.
    /// </summary>
    public static AuditLogDisplayResult FromFile(string path, AuditAnchor? anchor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxLogBytes)
            throw new IOException(string.Create(CultureInfo.InvariantCulture,
                $"'{path}' is {info.Length} bytes, more than the {MaxLogBytes}-byte limit for an audit log shown in a shell; check it with 'gridsim audit show'."));
        return Describe(AuditLogFile.Verify(path, anchor), path);
    }

    /// <summary>
    /// Verify and describe a log held in memory (the web shell, where the browser uploads the file; the server never
    /// opens a path the viewer typed). <paramref name="label"/> names the log in the verdict (the uploaded file name).
    /// Throws <see cref="ArgumentException"/> when the bytes exceed <see cref="MaxLogBytes"/>.
    /// </summary>
    public static AuditLogDisplayResult FromBytes(ReadOnlySpan<byte> bytes, string label, AuditAnchor? anchor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (bytes.Length > MaxLogBytes)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"'{label}' is {bytes.Length} bytes, more than the {MaxLogBytes}-byte limit for an audit log shown in a shell."));
        return Describe(AuditLogFile.VerifyBytes(bytes, anchor), label);
    }

    /// <summary>
    /// Read the optional external anchor from two text boxes, with the CLI's rules (<c>--anchor-count</c> and
    /// <c>--anchor-head</c> go together; the count is a non-negative whole number). Both blank = no anchor (true,
    /// <paramref name="anchor"/> null). Returns false with a plain-English <paramref name="error"/> otherwise.
    /// </summary>
    public static bool TryParseAnchor(string? countText, string? headText, out AuditAnchor? anchor, out string? error)
    {
        anchor = null;
        error = null;
        bool hasCount = !string.IsNullOrWhiteSpace(countText), hasHead = !string.IsNullOrWhiteSpace(headText);
        if (!hasCount && !hasHead) return true;
        if (hasCount != hasHead)
        {
            error = "The anchor count and the anchor head hash go together (the anchor is the pair).";
            return false;
        }
        if (!int.TryParse(countText!.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            error = $"The anchor count '{countText.Trim()}' is not a non-negative whole number.";
            return false;
        }
        anchor = new AuditAnchor(count, headText!.Trim());
        return true;
    }

    // Word a verification exactly as RunAudit in the CLI does ('show' form): entries plus OK when valid; only the
    // TAMPERED line when not. The verdict strings are kept character for character equal to the CLI's (a test
    // compares them against the built CLI's output on the same file).
    private static AuditLogDisplayResult Describe(AuditLogVerification v, string label)
    {
        if (v.IsValid)
        {
            var lines = new List<string>(v.Entries.Count);
            foreach (AuditLogEntry entry in v.Entries) lines.Add(AuditLogFile.Describe(entry));
            return new AuditLogDisplayResult(true, lines,
                string.Create(CultureInfo.InvariantCulture, $"OK {label}: {v.VerifiedCount} entries, head {v.HeadHash}"),
                v.Anchor);
        }
        return new AuditLogDisplayResult(false, Array.Empty<string>(),
            string.Create(CultureInfo.InvariantCulture,
                $"TAMPERED {label}: entry {v.FirstBrokenIndex}: {v.Reason} ({v.VerifiedCount} entries verified before it)"),
            null);
    }
}
