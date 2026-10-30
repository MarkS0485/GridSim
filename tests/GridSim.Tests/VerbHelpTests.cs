using System.Text.RegularExpressions;
using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F09 (October 2026): per-verb help is the verb's section of the CLI reference (manual ch. 19), compiled into the
/// binary and rendered as plain text by src/GridSim.Cli/VerbHelp.cs (compiled into this test project as well). These
/// tests read the chapter from disk, as the build embeds it, and check that every verb the CLI dispatches has help, that
/// the help carries the flags, defaults and exit codes, and that it prints as plain ASCII.
/// </summary>
public sealed class VerbHelpTests
{
    private static string Root() => CliProcess.Root();

    private static string Chapter() => File.ReadAllText(Path.Combine(Root(), "docs", "manual", "0019 CLI reference.md")).Replace("\r\n", "\n");

    private static string ProgramSource() => File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");

    /// <summary>Every verb Program.cs dispatches on, found the same way CliHelpContractTests finds them.</summary>
    private static SortedSet<string> DispatchedVerbs()
    {
        string src = ProgramSource();
        var verbs = new SortedSet<string>(StringComparer.Ordinal) { "licence" };
        foreach (Match m in Regex.Matches(src, @"rest\[0\]\.Equals\(""([a-z][\w-]*)""")) verbs.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(src, @"rest\[0\] is ""([a-z][\w-]*)""")) verbs.Add(m.Groups[1].Value);
        Assert.True(verbs.Count > 30, $"verb scan found only {verbs.Count} verbs; the dispatch pattern may have changed");
        return verbs;
    }

    [Fact]
    public void Every_dispatched_verb_and_alias_has_help()
    {
        string md = Chapter();
        var missing = DispatchedVerbs().Where(v => VerbHelp.Render(md, v) is null).ToList();
        Assert.True(missing.Count == 0, "verbs with no help section in ch. 19: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_help_text_is_plain_ascii_with_a_title_and_the_exit_code_rule()
    {
        string md = Chapter();
        foreach (string v in DispatchedVerbs())
        {
            string text = VerbHelp.Render(md, v)!;
            Assert.True(text.All(ch => ch == '\n' || (ch >= ' ' && ch < 127)), $"help for {v} is not plain printable ASCII");
            Assert.StartsWith("gridsim ", text, StringComparison.Ordinal);
            Assert.Contains("(manual ch. 19: docs/manual/0019 CLI reference.md#", text, StringComparison.Ordinal);
            Assert.Contains(VerbHelp.ExitCodeFooter, text, StringComparison.Ordinal);
            Assert.DoesNotContain("](", text, StringComparison.Ordinal);    // no Markdown link syntax left
            Assert.DoesNotContain("`", text, StringComparison.Ordinal);     // no code-span backticks left
            Assert.DoesNotContain("|---", text, StringComparison.Ordinal);  // tables are rendered, not dumped
        }
    }

    [Fact]
    public void Help_shows_usage_flags_defaults_and_exit_codes()
    {
        string text = VerbHelp.Render(Chapter(), "nminus2")!;
        Assert.StartsWith("gridsim nminus2  (manual ch. 19: docs/manual/0019 CLI reference.md#nminus2)", text, StringComparison.Ordinal);
        // Moved 2026-10 (N092, Ruling R1, Mark delegated 2026-10-04): per-bus voltage limits are now the default, so the
        // usage line offers the flag that restores the old view instead of the old opt-in (still accepted as a no-op
        // alias). Before: "... [--top N] [--bus-vlimits] (alias n-2)"; after: "... [--top N] [--global-vlimits] (alias n-2)".
        Assert.Contains("gridsim nminus2 [<case>] [--dir <path>] [--candidates N] [--exhaustive] [--top N] [--global-vlimits] (alias n-2)", text, StringComparison.Ordinal);
        Assert.Contains("  --candidates\n      default: 20\n", text, StringComparison.Ordinal);
        Assert.Contains("  --exhaustive\n      default: off\n", text, StringComparison.Ordinal);
        Assert.Contains("for nminus1.", text, StringComparison.Ordinal);   // "Exit codes as for [nminus1](#nminus1)." rendered
        Assert.Contains("Result files: --json <file.json>", text, StringComparison.Ordinal);

        string dcpf = VerbHelp.Render(Chapter(), "dcpf")!;
        Assert.Contains("Exit 0 on a solve; 1 when the case does not load", dcpf, StringComparison.Ordinal);
    }

    [Fact]
    public void Aliases_resolve_to_their_verb_section()
    {
        string md = Chapter();
        Assert.Equal(VerbHelp.Render(md, "nminus1"), VerbHelp.Render(md, "n-1"));
        Assert.Equal(VerbHelp.Render(md, "nminus2"), VerbHelp.Render(md, "N-2"));
        Assert.Equal(VerbHelp.Render(md, "n-1-1"), VerbHelp.Render(md, "nminus1minus1"));
    }

    [Fact]
    public void A_verb_with_modes_takes_the_mode_and_lists_the_others()
    {
        string md = Chapter();
        string estimate = VerbHelp.Render(md, "bench", "--estimate")!;
        Assert.StartsWith("gridsim bench --estimate  (manual ch. 19: docs/manual/0019 CLI reference.md#bench---estimate)", estimate, StringComparison.Ordinal);
        string bench = VerbHelp.Render(md, "bench")!;
        Assert.StartsWith("gridsim bench  (", bench, StringComparison.Ordinal);
        Assert.Contains("Other modes: gridsim bench --estimate --help, gridsim bench --perf --help", bench, StringComparison.Ordinal);
        // A token that is not a mode falls back to the verb itself.
        Assert.Equal(bench, VerbHelp.Render(md, "bench", "--runs"));
        // The anchor matches the chapter's own link to that section.
        Assert.Contains("(#bench---estimate)", md, StringComparison.Ordinal);
        Assert.Equal("bench---estimate", VerbHelp.Anchor("bench --estimate"));
        Assert.Equal("running-a-case-no-verb", VerbHelp.Anchor("Running a case (no verb)"));
    }

    [Fact]
    public void A_group_note_shared_by_its_verbs_is_shown_with_each_of_them()
    {
        // The evidence verbs share the forward-inference rule (exit 2), written once under the group heading.
        string text = VerbHelp.Render(Chapter(), "fleet")!;
        Assert.Contains("Shared notes for Evidence verbs:", text, StringComparison.Ordinal);
        Assert.Contains("rejected with exit 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Words_that_are_not_verbs_have_no_help()
    {
        string md = Chapter();
        Assert.Null(VerbHelp.Render(md, "no-such-verb"));
        Assert.Null(VerbHelp.Render(md, "case9"));
        Assert.Null(VerbHelp.Render(md, "--dir"));
        Assert.Null(VerbHelp.Render(md, ""));
        Assert.False(VerbHelp.HasTopic(md, "case14"));
        Assert.True(VerbHelp.HasTopic(md, "dcopf"));
    }

    [Fact]
    public void Every_writer_verb_documents_the_result_flags_and_no_other_verb_claims_them()
    {
        string md = Chapter();
        foreach (string v in CliOutput.WriterVerbs.Select(CliOutput.Canonical).Distinct())
            Assert.Contains("--json", VerbHelp.Render(md, v)!, StringComparison.Ordinal);
        foreach (string v in DispatchedVerbs().Where(v => !CliOutput.WriterVerbs.Contains(v)))
            Assert.DoesNotContain("Result files: --json", VerbHelp.Render(md, v)!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chapter_is_embedded_in_the_cli_build()
    {
        string csproj = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "GridSim.Cli.csproj"));
        Assert.Contains($"<EmbeddedResource Include=\"..\\..\\docs\\manual\\0019 CLI reference.md\" LogicalName=\"{VerbHelp.ResourceName}\"", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_ascii_signs_become_ascii_words()
    {
        Assert.Equal("30 GBP/MWh, 1-sigma, a x b, 5 deg, ?", VerbHelp.Ascii("30 \u00A3/MWh, 1-\u03C3, a \u00D7 b, 5\u00B0, \u4E2D"));
    }

    [Fact]
    public void N055_the_cli_reference_stub_is_gone_and_nothing_points_at_it()
    {
        Assert.False(File.Exists(Path.Combine(Root(), "docs", "cli-reference.md")), "docs/cli-reference.md (a redirect stub) should be removed");
        string src = ProgramSource();
        Assert.DoesNotContain("see docs/cli-reference for", src, StringComparison.Ordinal);
        Assert.Contains("or see {VerbHelp.ChapterPath}.", src, StringComparison.Ordinal);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(Root(), "docs"), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".txt", StringComparison.Ordinal)))
            Assert.DoesNotContain("docs/cli-reference.md`", File.ReadAllText(f), StringComparison.Ordinal);
    }
}
