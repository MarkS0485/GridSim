using System.Text;
using System.Text.RegularExpressions;

namespace GridSim.Cli;

// ---------------------------------------------------------------------------------------------------------------------
// F09 (October 2026): per-verb help, single-sourced with the CLI reference (user manual ch. 19).
//
// `gridsim <verb> --help`, `gridsim <verb> -h` and `gridsim help <verb>` print that verb's section of
// docs/manual/0019 CLI reference.md: its usage line(s), every flag with its default, and the exit codes the section
// states, followed by the general exit-code rule. The chapter itself is compiled into the binary as an embedded
// resource (GridSim.Cli.csproj), so there is exactly ONE copy of each verb's flags, defaults and exit codes: the help
// cannot drift from the manual, because it is the manual.
//
// Why this and not a table in code that a test compares with ch. 19 (the other option considered): ch. 19 already holds
// every flag, default and exit code, verified against Program.cs and kept current by both the code and the docs
// sessions. A second copy in code (about seventy verbs and several hundred flag rows) would need a parity test that
// either compares only names and defaults (leaving the meanings and exit-code prose to drift) or compares everything
// (so every docs-session wording edit also needs a code edit). Printing the chapter itself has neither problem, and the
// tests (VerbHelpTests) check that every dispatched verb resolves to a section and renders as plain ASCII.
//
// The renderer is deliberately small: it turns the Markdown of one section into plain console text (links become their
// text, code spans lose their backticks, tables become one entry per row with "default:" shown, a few non-ASCII signs
// become ASCII words). It does not interpret anything else.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Finds and renders one verb's section of the CLI reference (manual ch. 19) as plain console text.</summary>
internal static class VerbHelp
{
    /// <summary>The manifest name of the embedded chapter (see GridSim.Cli.csproj).</summary>
    public const string ResourceName = "GridSim.Cli.19-cli-reference.md";

    /// <summary>The repository path of the chapter, printed in the help so the reader can find the full text.</summary>
    public const string ChapterPath = "docs/manual/0019 CLI reference.md";

    /// <summary>Console width the table meanings are wrapped to.</summary>
    private const int WrapWidth = 100;

    /// <summary>The general exit-code rule printed under every verb's help (manual ch. 19, "Parsing rules and exit codes").</summary>
    public const string ExitCodeFooter =
        "Exit codes (every verb): 0 success; 1 input error or a failed check; 2 usage error; 3 non-convergence or a\n" +
        "validation / verdict failure; 77 skipped (crossval only). The cases this verb adds are stated above.";

    /// <summary>
    /// The embedded chapter text. A build without it is a broken build (the csproj embeds the file and fails when it is
    /// missing), so a missing resource is a defect and throws.
    /// </summary>
    public static string LoadEmbedded()
    {
        using Stream? s = typeof(VerbHelp).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"the CLI reference is not embedded in this build (resource {ResourceName}).");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>A heading of the chapter: its level (2 or 3), its title, and its line range [Start, End).</summary>
    internal sealed record Section(int Level, string Title, int Start, int End);

    /// <summary>Every "## " and "### " heading of the chapter, with the line range each covers (up to the next heading of
    /// the same or a higher level).</summary>
    internal static List<Section> Sections(string[] lines)
    {
        var heads = new List<(int Level, string Title, int Line)>();
        bool inFence = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;
            if (lines[i].StartsWith("### ", StringComparison.Ordinal)) heads.Add((3, lines[i][4..].Trim(), i));
            else if (lines[i].StartsWith("## ", StringComparison.Ordinal)) heads.Add((2, lines[i][3..].Trim(), i));
        }
        var sections = new List<Section>();
        for (int k = 0; k < heads.Count; k++)
        {
            int end = lines.Length;
            for (int j = k + 1; j < heads.Count; j++)
                if (heads[j].Level <= heads[k].Level) { end = heads[j].Line; break; }
            sections.Add(new Section(heads[k].Level, heads[k].Title, heads[k].Line, end));
        }
        return sections;
    }

    /// <summary>
    /// The section for a verb (and an optional mode token such as "--estimate" for "bench --estimate"), or null. A verb
    /// matches a heading equal to its name (case-insensitive), or a section that names it as an alias with
    /// "(alias `name`)" (for example n-1 is the nminus1 section).
    /// </summary>
    internal static Section? Find(List<Section> sections, string[] lines, string verb, string? mode = null)
    {
        string v = verb.Trim();
        if (v.Length == 0 || v.StartsWith('-')) return null;
        if (mode is not null)
        {
            Section? withMode = sections.FirstOrDefault(s => s.Title.Equals(v + " " + mode, StringComparison.OrdinalIgnoreCase));
            if (withMode is not null) return withMode;
        }
        Section? exact = sections.FirstOrDefault(s => s.Title.Equals(v, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        string alias = $"(alias `{v.ToLowerInvariant()}`)";
        foreach (Section s in sections.Where(s => s.Level == 3))
            for (int i = s.Start; i < s.End; i++)
                if (lines[i].Contains(alias, StringComparison.Ordinal)) return s;
        return null;
    }

    /// <summary>True when the chapter has help for this verb.</summary>
    public static bool HasTopic(string markdown, string verb)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return Find(Sections(lines), lines, verb) is not null;
    }

    /// <summary>
    /// The plain-text help for a verb, or null when the chapter has no section for it. The text is: a title line with
    /// the chapter anchor; the section body; the shared notes of its group (the text between a group heading such as
    /// "## Network analysis verbs" and its first verb); other modes of the same verb; and the exit-code footer.
    /// </summary>
    public static string? Render(string markdown, string verb, string? mode = null)
    {
        // A link whose text wraps onto the next line ("[Solver\nselection](#solver-selection)") cannot be seen line by
        // line, so links are reduced to their text over the whole chapter first. The line breaks stay where they were,
        // so every heading keeps its line number.
        string text0 = Regex.Replace(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), @"\[([^\]]*)\]\([^)\s]*\)", "$1");
        string[] lines = text0.Split('\n');
        List<Section> sections = Sections(lines);
        Section? s = Find(sections, lines, verb, mode);
        if (s is null) return null;

        var sb = new StringBuilder();
        sb.Append("gridsim ").Append(Ascii(s.Title)).Append("  (manual ch. 19: ").Append(ChapterPath).Append('#').Append(Anchor(s.Title)).Append(")\n");
        sb.Append('\n');
        RenderBody(sb, lines, s.Start + 1, BodyEnd(sections, s));

        // The group's shared notes: the text under the enclosing "## ..." heading before its first "### " section
        // (for example the evidence verbs' shared forward-inference rule). Only for a "###" verb section.
        if (s.Level == 3)
        {
            Section? group = sections.LastOrDefault(g => g.Level == 2 && g.Start < s.Start);
            if (group is not null)
            {
                Section? firstChild = sections.FirstOrDefault(c => c.Level == 3 && c.Start > group.Start);
                int introEnd = firstChild is null ? group.End : Math.Min(firstChild.Start, group.End);
                if (lines.Skip(group.Start + 1).Take(introEnd - group.Start - 1).Any(l => l.Trim().Length > 0))
                {
                    sb.Append('\n').Append("Shared notes for ").Append(Ascii(group.Title)).Append(":\n");
                    RenderBody(sb, lines, group.Start + 1, introEnd);
                }
            }
        }

        // Other modes of the same verb (bench has "bench --estimate" and "bench --perf").
        string prefix = s.Title + " ";
        List<string> modes = sections.Where(m => m.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(m => m.Title).ToList();
        if (modes.Count > 0)
            sb.Append('\n').Append("Other modes: ").Append(string.Join(", ", modes.Select(m => $"gridsim {Ascii(m)} --help"))).Append('\n');

        sb.Append('\n').Append(ExitCodeFooter).Append('\n');
        return CollapseBlankLines(sb.ToString());
    }

    // The body of a section stops at its first child heading (a "##" section with "###" children shows only its own text).
    private static int BodyEnd(List<Section> sections, Section s)
    {
        Section? child = sections.FirstOrDefault(c => c.Start > s.Start && c.Start < s.End);
        return child?.Start ?? s.End;
    }

    // Render Markdown lines [from, to) as console text.
    private static void RenderBody(StringBuilder sb, string[] lines, int from, int to)
    {
        bool inFence = false;
        for (int i = from; i < to; i++)
        {
            string line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) { sb.Append("    ").Append(Ascii(line)).Append('\n'); continue; }
            if (line.TrimStart().StartsWith('|'))
            {
                // A table: header, separator, rows. Collect the block and render it.
                int j = i;
                var block = new List<string>();
                while (j < to && lines[j].TrimStart().StartsWith('|')) block.Add(lines[j++]);
                RenderTable(sb, block);
                i = j - 1;
                continue;
            }
            sb.Append(Inline(line)).Append('\n');
        }
    }

    // A table becomes one entry per row: the first cell on its own line, then "<header>: <cell>" for short columns
    // ("default: 20") and the wrapped text for a Meaning / Does column.
    private static void RenderTable(StringBuilder sb, List<string> block)
    {
        if (block.Count < 2) { foreach (string l in block) sb.Append(Inline(l)).Append('\n'); return; }
        List<string> header = Cells(block[0]);
        foreach (string rowLine in block.Skip(2))
        {
            List<string> cells = Cells(rowLine);
            if (cells.Count == 0) continue;
            sb.Append("  ").Append(Inline(cells[0])).Append('\n');
            for (int c = 1; c < cells.Count; c++)
            {
                string head = c < header.Count ? Inline(header[c]) : "";
                string text = Inline(cells[c]);
                if (head is "Meaning" or "Does" or "Loads") Wrap(sb, text, "      ");
                else sb.Append("      ").Append(head.ToLowerInvariant()).Append(": ").Append(text).Append('\n');
            }
        }
        sb.Append('\n');
    }

    // Split a table row into its cells (an escaped "\|" stays inside a cell).
    private static List<string> Cells(string row)
    {
        string t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t[..^1];
        var cells = new List<string>();
        var cur = new StringBuilder();
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cur.Append('|'); i++; continue; }
            if (t[i] == '|') { cells.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(t[i]);
        }
        cells.Add(cur.ToString().Trim());
        return cells;
    }

    // Word-wrap text at WrapWidth with the given indent.
    private static void Wrap(StringBuilder sb, string text, string indent)
    {
        var line = new StringBuilder(indent);
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > indent.Length && line.Length + 1 + word.Length > WrapWidth)
            {
                sb.Append(line).Append('\n');
                line.Clear().Append(indent);
            }
            if (line.Length > indent.Length) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > indent.Length) sb.Append(line).Append('\n');
    }

    // Inline Markdown to plain text: [text](link) -> text, **bold** -> bold, `code` -> code, "\|" -> "|", then ASCII.
    private static string Inline(string s)
    {
        string t = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        t = t.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Replace("\\|", "|", StringComparison.Ordinal);
        return Ascii(t);
    }

    /// <summary>
    /// Plain ASCII for the console (the chapter has a few signs such as the pound sign and sigma): known signs become
    /// words or ASCII look-alikes; anything else outside ASCII becomes '?', so the help never prints mojibake.
    /// </summary>
    internal static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (ch < 128) { sb.Append(ch); continue; }
            sb.Append(ch switch
            {
                '\u00A3' => "GBP ",      // the pound sign: "(pound)/MWh" becomes "GBP/MWh" (the space is dropped below)
                '\u03C3' => "sigma",
                '\u00D7' => "x",
                '\u2013' or '\u2014' => "-",
                '\u2018' or '\u2019' => "'",
                '\u201C' or '\u201D' => "\"",
                '\u2265' => ">=",
                '\u2264' => "<=",
                '\u2192' => "->",
                '\u00B0' => " deg",
                '\u00B5' or '\u03BC' => "u",
                '\u00A0' => " ",
                _ => "?",
            });
        }
        return sb.ToString().Replace("GBP /", "GBP/", StringComparison.Ordinal);
    }

    /// <summary>The GitHub anchor of a heading: lower case, punctuation other than '-' dropped, spaces to '-'.</summary>
    internal static string Anchor(string title)
    {
        var sb = new StringBuilder();
        foreach (char ch in title.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_') sb.Append(ch);
            else if (ch == ' ') sb.Append('-');
        }
        return sb.ToString();
    }

    // At most one blank line in a row, and none at the very start.
    private static string CollapseBlankLines(string s)
    {
        var sb = new StringBuilder(s.Length);
        int blanks = 0;
        foreach (string line in s.Split('\n'))
        {
            if (line.Trim().Length == 0) { if (++blanks > 1) continue; }
            else blanks = 0;
            sb.Append(line.TrimEnd()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
