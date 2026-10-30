using System.Globalization;
using System.Text;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// D-DOC-062 (October 2026): the anchor of an audit log kept in a small file of its own, so the next run that appends
/// to the log can check the log still holds everything the last run left in it.
///
/// <para><b>Why.</b> The hash chain on its own cannot tell a log that lost its last entries (tail truncation) from a
/// log that never had them: the shorter file still verifies. <see cref="AuditRunRecorder.Start"/> used to open the log
/// with no anchor, so the CLI (<c>--audit-log</c>) appended new entries to a tail-truncated log without noticing, and
/// the truncation was then buried under fresh, valid entries. Now every run writes the anchor (entry count and head
/// hash) after it appends, and the next run opens the log against it: a log with fewer entries than the anchor, or a
/// different hash at the anchored entry, is refused with <see cref="AuditLogTamperException"/> before anything is
/// appended.</para>
///
/// <para><b>Where.</b> By default beside the log (<c>&lt;log&gt;.anchor</c>), which catches accidental truncation, a
/// restore of an older copy, and a naive edit. An operator who wants protection against someone who can also edit the
/// anchor keeps it elsewhere (CLI <c>--audit-anchor &lt;file&gt;</c>, web <c>Audit:AnchorPath</c>: a different
/// account's share, write-once media) and also keeps the anchor line printed at the end of every run.</para>
///
/// <para><b>Format</b> (one ASCII line, newline-terminated): <c>gridsim-audit-anchor/1 count=&lt;N&gt; head=&lt;hash&gt;</c>,
/// where the hash is <see cref="AuditLog.Genesis"/> for an empty log, else 64 upper-case hexadecimal digits. Anything
/// else is refused (fail loud: a damaged anchor must not silently turn the check off).</para>
/// </summary>
public static class AuditAnchorFile
{
    /// <summary>The format tag at the start of the line.</summary>
    public const string FormatTag = "gridsim-audit-anchor/1";

    /// <summary>The default anchor file for a log: the log's full path plus <c>.anchor</c>.</summary>
    public static string DefaultPathFor(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        return Path.GetFullPath(logPath) + ".anchor";
    }

    /// <summary>The one-line text of <paramref name="anchor"/> (without the newline).</summary>
    public static string Format(AuditAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        Validate(anchor.Count, anchor.HeadHash, "anchor");
        return string.Create(CultureInfo.InvariantCulture, $"{FormatTag} count={anchor.Count} head={anchor.HeadHash}");
    }

    /// <summary>
    /// Read the anchor at <paramref name="anchorPath"/>, or null when the file does not exist. A file that exists but
    /// is not exactly one well-formed anchor line throws <see cref="InvalidOperationException"/> naming the file.
    /// </summary>
    public static AuditAnchor? ReadIfExists(string anchorPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorPath);
        if (!File.Exists(anchorPath)) return null;
        string text = File.ReadAllText(anchorPath, Encoding.ASCII);
        return Parse(text, anchorPath);
    }

    /// <summary>Parse anchor-file text (one line, optional trailing newline). Throws <see cref="InvalidOperationException"/>
    /// naming <paramref name="source"/> when it is not a well-formed anchor line.</summary>
    public static AuditAnchor Parse(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        string line = text.EndsWith('\n') ? text[..^1] : text;
        if (line.EndsWith('\r')) line = line[..^1];
        string[] parts = line.Split(' ');
        if (parts.Length != 3 || parts[0] != FormatTag
            || !parts[1].StartsWith("count=", StringComparison.Ordinal)
            || !parts[2].StartsWith("head=", StringComparison.Ordinal)
            || !int.TryParse(parts[1].AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            throw Malformed(source, $"expected one line '{FormatTag} count=<N> head=<hash>'");
        string head = parts[2][5..];
        Validate(count, head, source);
        return new AuditAnchor(count, head);
    }

    /// <summary>
    /// Write <paramref name="anchor"/> to <paramref name="anchorPath"/>, replacing any earlier anchor in one step (a
    /// temporary file in the same folder, flushed to disk, then moved over the old one), so a crash leaves either the
    /// old anchor or the new one, never half a line.
    /// </summary>
    public static void Write(string anchorPath, AuditAnchor anchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorPath);
        string line = Format(anchor) + "\n";
        string full = Path.GetFullPath(anchorPath);
        string temp = full + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Encoding.ASCII.GetBytes(line));
            fs.Flush(flushToDisk: true);
        }
        File.Move(temp, full, overwrite: true);
    }

    // Count non-negative; head GENESIS exactly when the count is 0, otherwise 64 upper-case hex digits (the
    // AuditLog.ComputeHash form).
    private static void Validate(int count, string? head, string source)
    {
        if (count < 0) throw Malformed(source, $"count {count} is negative");
        if (head is null) throw Malformed(source, "head hash is missing");
        if (count == 0)
        {
            if (head != AuditLog.Genesis) throw Malformed(source, $"an empty log's head must be {AuditLog.Genesis}, not '{head}'");
            return;
        }
        if (head.Length != 64 || !head.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F')))
            throw Malformed(source, $"head '{head}' is not 64 upper-case hexadecimal digits");
    }

    private static InvalidOperationException Malformed(string source, string why) => new(
        $"Audit anchor '{source}' is not a valid anchor ({why}). A damaged anchor must not switch the truncation " +
        "check off: restore it from the anchor line a run printed, or investigate the log before appending to it.");
}
