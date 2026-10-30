using System.Text.RegularExpressions;

namespace GridSim.Cli;

// ---------------------------------------------------------------------------------------------------------------------
// N103 (October 2026): one shared per-verb flag check, single-sourced with the CLI reference (user manual ch. 19).
//
// The process-wide gate in Program.cs refuses a flag NO verb knows (a typo). It could not refuse a flag that some OTHER
// verb knows, so "trip --top 5", "stability --out x", "interpose --catalog f" ran with the flag silently ignored: the
// run "succeeded" without doing what was asked. A few verbs had their own accepted-flag list (RequireVerbFlags: the
// evidence verbs, pf, dcpf, zonal, convert, validate); most had none.
//
// The accepted flags of a verb are the flags its own section of ch. 19 names: the same text `gridsim <verb> --help`
// prints (see VerbHelp.cs), compiled into the binary. So there is one list per verb, the documented one, and a flag the
// documentation does not give a verb is refused for it. A verb's set is every `--flag` written anywhere in:
//   * its section (usage line, flag table, prose), and its other modes' sections ("bench --estimate", "bench --perf");
//   * the shared notes of its group (the text between the group heading and its first verb);
//   * the "Global switches" section (usable with any command), and the "Result files" flags for a writer verb.
// Taking every flag the section mentions errs on the side of accepting (a flag named in passing in a verb's prose is
// accepted for it), never of refusing a flag the documentation gives the verb. The test VerbFlagsTests checks the other
// direction against the source: every flag a verb's code reads is in the verb's set.
//
// The check runs once, in Program.cs, after the global switches and the result-writer flags have been taken off the
// argument list and before the verb runs. A first token that is not a verb (a case name, a .m file, --dir) is the
// no-verb run and is not checked here (its flags are the global switches).
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>The flags each verb accepts, read from the embedded CLI reference (manual ch. 19).</summary>
internal static class VerbFlags
{
    private static readonly Regex FlagToken = new(@"--[A-Za-z][A-Za-z0-9-]*", RegexOptions.CultureInvariant);

    /// <summary>
    /// The flags <paramref name="verb"/> accepts, or null when ch. 19 has no section for it (not a verb). Aliases resolve
    /// as the help does (n-1 is nminus1).
    /// </summary>
    public static IReadOnlySet<string>? Accepted(string markdown, string verb, bool writerVerb)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<VerbHelp.Section> sections = VerbHelp.Sections(lines);
        VerbHelp.Section? own = VerbHelp.Find(sections, lines, verb);
        if (own is null) return null;

        var accepted = new HashSet<string>(StringComparer.Ordinal);
        void Take(int from, int to)
        {
            for (int i = from; i < to && i < lines.Length; i++)
                foreach (Match mt in FlagToken.Matches(lines[i])) accepted.Add(mt.Value);
        }

        // The verb's own section, and its other modes ("bench" with "bench --estimate" and "bench --perf").
        Take(own.Start, own.End);
        foreach (VerbHelp.Section mode in sections.Where(s => s.Title.StartsWith(own.Title + " ", StringComparison.OrdinalIgnoreCase)))
            Take(mode.Start, mode.End);

        // The group's shared notes (only for a "###" verb under a "##" group).
        if (own.Level == 3)
        {
            VerbHelp.Section? group = sections.LastOrDefault(g => g.Level == 2 && g.Start < own.Start);
            if (group is not null)
            {
                VerbHelp.Section? firstChild = sections.FirstOrDefault(c => c.Level == 3 && c.Start > group.Start);
                Take(group.Start, firstChild is null ? group.End : Math.Min(firstChild.Start, group.End));
            }
        }

        // The global switches, usable with any command.
        VerbHelp.Section? global = sections.FirstOrDefault(s => s.Title.Equals("Global switches", StringComparison.OrdinalIgnoreCase));
        if (global is not null) Take(global.Start, global.End);

        // The shared result-writer flags on the writer verbs (CliOutput has already taken them off by the time the check
        // runs; listed so the set is complete when read on its own).
        if (writerVerb) { accepted.Add("--json"); accepted.Add("--csv"); accepted.Add("--out"); }

        // Help is always answered (it is handled before any verb runs).
        accepted.Add("--help");
        return accepted;
    }

    /// <summary>
    /// Check the remaining arguments of a verb run: the first flag the verb does not accept, with the error message to
    /// print (exit 2), or null when every flag is accepted or the first token is not a verb.
    /// </summary>
    public static string? Check(string markdown, IReadOnlyList<string> rest, bool writerVerb)
    {
        if (rest.Count == 0 || rest[0].StartsWith('-')) return null;
        IReadOnlySet<string>? accepted = Accepted(markdown, rest[0], writerVerb);
        if (accepted is null) return null;   // the no-verb run (a case name, a .m file): not a verb
        foreach (string tok in rest.Skip(1))
        {
            if (!tok.StartsWith("--", StringComparison.Ordinal)) continue;
            int eq = tok.IndexOf('=');
            string name = eq > 0 ? tok[..eq] : tok;
            if (accepted.Contains(name)) continue;
            return $"error: {rest[0]} does not take {name} (it is not one of {rest[0]}'s flags, so it would be silently ignored). "
                + $"Run 'gridsim {rest[0]} --help' for the flags {rest[0]} takes, or see {VerbHelp.ChapterPath}.";
        }
        return null;
    }
}
